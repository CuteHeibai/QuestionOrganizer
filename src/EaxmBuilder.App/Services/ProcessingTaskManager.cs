using System.Diagnostics;
using System.Net;
using System.Xml.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EaxmBuilder.AI;
using EaxmBuilder.Core;
using EaxmBuilder.Export;
using EaxmBuilder.Infrastructure;

namespace EaxmBuilder.Services;

public sealed class ProcessingTaskManager(
    ProjectRepository repository,
    IAiProvider aiProvider,
    IReadOnlyList<IQuestionExporter> exporters)
{
    private static readonly TaskStep[] OrderedSteps =
    [
        TaskStep.Ocr,
        TaskStep.FormulaRecognition,
        TaskStep.FigureRedraw,
        TaskStep.WordExport,
        TaskStep.PdfExport,
        TaskStep.LatexExport,
        TaskStep.JsonExport,
        TaskStep.AiReview
    ];

    public event Action<string>? LogAdded;
    public event Action<QuestionProject>? ProjectChanged;

    public async Task RunPendingAsync(QuestionProject project, CancellationToken cancellationToken = default)
    {
        ProjectOutputPaths.ClearFinalOutputs(project);
        foreach (var step in OrderedSteps)
        {
            if (project.Steps[step].State == StepState.Completed) continue;
            var succeeded = await RunStepAsync(project, step, cancellationToken);
            if (!succeeded) return;
        }
    }

    public async Task<bool> RunStepAsync(
        QuestionProject project,
        TaskStep step,
        CancellationToken cancellationToken = default)
    {
        if (IsDisabledExport(project, step))
        {
            await MarkSkippedAsync(project, step);
            return true;
        }

        EnsurePrerequisites(project, step);
        var record = project.Steps[step];
        record.State = StepState.Running;
        record.Error = string.Empty;
        record.Attempts++;
        await repository.SaveAsync(project);
        ProjectChanged?.Invoke(project);
        LogAdded?.Invoke(StartMessage(step));

        try
        {
            await ExecuteAsync(project, step, cancellationToken);
            record.State = StepState.Completed;
            record.CompletedAt = DateTimeOffset.Now;
            LogAdded?.Invoke(CompleteMessage(step));
            return true;
        }
        catch (OperationCanceledException)
        {
            record.State = StepState.Failed;
            record.Error = $"{DisplayName(step)}已取消或请求超时，可以单独重试。";
            LogAdded?.Invoke($"{DisplayName(step)}已停止");
            return false;
        }
        catch (Exception exception)
        {
            record.State = StepState.Failed;
            record.Error = exception.Message;
            LogAdded?.Invoke($"{DisplayName(step)}失败：{exception.Message}");
            return false;
        }
        finally
        {
            await repository.SaveAsync(project);
            ProjectChanged?.Invoke(project);
        }
    }

    private async Task ExecuteAsync(QuestionProject project, TaskStep step, CancellationToken cancellationToken)
    {
        switch (step)
        {
            case TaskStep.Ocr:
                var ocr = await aiProvider.RecognizeTextAsync(
                    project.SourcePath, project.AiInstructions, cancellationToken);
                await repository.SaveDataAsync(project, "ocr.json", ocr);
                break;

            case TaskStep.FormulaRecognition:
                var ocrData = await RequireDataAsync<OcrResult>(project, "ocr.json");
                var document = await aiProvider.StructureQuestionAsync(
                    project.SourcePath, ocrData, project.AiInstructions, cancellationToken);
                await repository.SaveDataAsync(project, "document.json", document);
                break;

            case TaskStep.FigureRedraw:
                var figureDocument = await RequireDataAsync<QuestionDocument>(project, "document.json");
                var figures = await CreateFiguresAsync(project, figureDocument, cancellationToken);
                figureDocument.Figures = figures.ToList();
                await SvgWriter.WriteAllAsync(project, figures, cancellationToken);
                await repository.SaveDataAsync(project, "document.json", figureDocument);
                break;

            case TaskStep.AiReview:
                await ReviewOutputsAsync(project, cancellationToken);
                break;

            default:
                var exporter = exporters.FirstOrDefault(item => item.Step == step)
                    ?? throw new InvalidOperationException($"未注册 {step} 导出器。");
                var exportDocument = await RequireDataAsync<QuestionDocument>(project, "document.json");
                await exporter.ExportAsync(project, exportDocument, cancellationToken);
                break;
        }
    }

    private async Task<IReadOnlyList<FigureDocument>> CreateFiguresAsync(
        QuestionProject project,
        QuestionDocument document,
        CancellationToken cancellationToken)
    {
        if (document.Blocks.All(block => block.Type != QuestionBlockType.Figure)) return [];

        if (project.FigureMode == FigureProcessingMode.AiRedraw)
        {
            var aiFigures = await aiProvider.RedrawFiguresAsync(
                project.SourcePath, document, project.AiInstructions, cancellationToken);
            return await EnsureCompleteFigureSetAsync(project, document, aiFigures, "AI 重绘", cancellationToken);
        }

        if (project.FigureMode == FigureProcessingMode.ExternalToolThenOriginalImage)
        {
            var externalFigures = await TryCreateFiguresWithGeoGebraAsync(project, document, cancellationToken);
            if (externalFigures.Count > 0)
                return await EnsureCompleteFigureSetAsync(project, document, externalFigures, "GeoGebra", cancellationToken);
            externalFigures = await TryCreateFiguresWithExternalToolAsync(project, document, cancellationToken);
            if (externalFigures.Count > 0)
                return await EnsureCompleteFigureSetAsync(project, document, externalFigures, "外部图形工具", cancellationToken);
            LogAdded?.Invoke("内嵌 GeoGebra/外部图形工具未产出 SVG，已回退为几何图裁剪。");
        }

        return await CreateOriginalImageFiguresAsync(project, document, cancellationToken);
    }

    private async Task<IReadOnlyList<FigureDocument>> EnsureCompleteFigureSetAsync(
        QuestionProject project,
        QuestionDocument document,
        IReadOnlyList<FigureDocument> figures,
        string sourceName,
        CancellationToken cancellationToken)
    {
        var ids = GetFigureIds(document);
        if (ids.Count == 0) return figures;

        var byId = figures
            .Where(figure => !string.IsNullOrWhiteSpace(figure.Id) && HasReadableSvg(figure.Svg))
            .GroupBy(figure => figure.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var missing = ids.Where(id => !byId.ContainsKey(id)).ToArray();
        if (missing.Length == 0) return ids.Select(id => byId[id]).ToList();

        var fallbackFigures = await CreateOriginalImageFiguresAsync(project, document, cancellationToken);
        var fallbackById = fallbackFigures
            .Where(figure => !string.IsNullOrWhiteSpace(figure.Id) && HasReadableSvg(figure.Svg))
            .GroupBy(figure => figure.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var id in missing)
        {
            if (fallbackById.TryGetValue(id, out var fallback))
                byId[id] = fallback;
        }

        var stillMissing = ids.Where(id => !byId.ContainsKey(id)).ToArray();
        if (stillMissing.Length > 0)
            LogAdded?.Invoke($"{sourceName}未生成 {string.Join("、", stillMissing)}，且原图裁剪也无法补齐。");
        else
            LogAdded?.Invoke($"{sourceName}未覆盖全部图形，缺失部分已用原图裁剪补齐。");
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    private async Task<IReadOnlyList<FigureDocument>> TryCreateFiguresWithGeoGebraAsync(
        QuestionProject project,
        QuestionDocument document,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FigureDocument> commandFigures;
        try
        {
            commandFigures = await aiProvider.CreateGeoGebraFiguresAsync(
                project.SourcePath, document, project.AiInstructions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogAdded?.Invoke($"GeoGebra 命令生成失败：{exception.Message}");
            return [];
        }

        var figures = new List<FigureDocument>();
        foreach (var figure in commandFigures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rendered = await GeoGebraRenderer.RenderAsync(project, figure, cancellationToken);
            if (rendered is not null) figures.Add(rendered);
        }
        if (figures.Count > 0) LogAdded?.Invoke("内嵌 GeoGebra 已生成图形。");
        return figures;
    }

    private async Task<IReadOnlyList<FigureDocument>> TryCreateFiguresWithExternalToolAsync(
        QuestionProject project,
        QuestionDocument document,
        CancellationToken cancellationToken)
    {
        var toolPath = Environment.GetEnvironmentVariable("QUESTION_ORGANIZER_FIGURE_TOOL");
        if (string.IsNullOrWhiteSpace(toolPath) || !File.Exists(toolPath)) return [];

        try
        {
            var documentPath = Path.Combine(project.DirectoryPath, "document.json");
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = toolPath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add(project.SourcePath);
            process.StartInfo.ArgumentList.Add(documentPath);
            process.StartInfo.ArgumentList.Add(project.DirectoryPath);
            process.Start();
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) return [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }

        var figures = new List<FigureDocument>();
        foreach (var id in GetFigureIds(document))
        {
            var path = Path.Combine(project.DirectoryPath, SanitizeFigureId(id) + ".svg");
            if (!File.Exists(path)) continue;
            figures.Add(new FigureDocument
            {
                Id = id,
                Description = "外部工具绘制",
                Svg = await File.ReadAllTextAsync(path, cancellationToken)
            });
        }
        return figures;
    }

    private static async Task<IReadOnlyList<FigureDocument>> CreateOriginalImageFiguresAsync(
        QuestionProject project,
        QuestionDocument document,
        CancellationToken cancellationToken)
    {
        var ids = GetFigureIds(document);
        var sourceSvgs = await CreateSourceImageSvgsAsync(project.SourcePath, ids, cancellationToken);
        return ids
            .Select(id => new FigureDocument
            {
                Id = id,
                Description = "原图几何图裁剪，未进行 AI 重绘",
                Svg = sourceSvgs.TryGetValue(id, out var svg) ? svg : sourceSvgs.Values.FirstOrDefault() ?? string.Empty
            })
            .ToList();
    }

    private static async Task<IReadOnlyDictionary<string, string>> CreateSourceImageSvgsAsync(
        string sourcePath,
        IReadOnlyList<string> figureIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".png" and not ".jpg" and not ".jpeg")
        {
            var fallbackSvg = """
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0,0,640,160">
                  <rect width="640" height="160" fill="#ffffff"/>
                  <text x="24" y="84" font-family="SimSun, 宋体, serif" font-size="20" fill="#202020">原文件为 PDF，当前无法直接嵌入裁切图，请改用 AI 重绘或外部工具。</text>
                </svg>
                """;
            foreach (var id in figureIds) result[id] = fallbackSvg;
            return result;
        }

        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        var mediaType = extension == ".png" ? "image/png" : "image/jpeg";
        var encoded = Convert.ToBase64String(bytes);
        var safeName = WebUtility.HtmlEncode(Path.GetFileName(sourcePath));
        var bounds = ReadFigureImageBounds(sourcePath, figureIds.Count);
        if (bounds.Count == 0) bounds = [ReadImageBounds(sourcePath)];

        for (var index = 0; index < figureIds.Count; index++)
        {
            var bound = bounds[Math.Min(index, bounds.Count - 1)];
            result[figureIds[index]] = $"""
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="{bound.X},{bound.Y},{bound.Width},{bound.Height}" overflow="hidden">
                  <title>{safeName}</title>
                  <image href="data:{mediaType};base64,{encoded}" x="0" y="0" width="{bound.SourceWidth}" height="{bound.SourceHeight}" preserveAspectRatio="none"/>
                </svg>
                """;
        }
        return result;
    }

    private static SourceImageBounds ReadImageBounds(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(path, UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = Math.Max(1, source.PixelWidth);
        var height = Math.Max(1, source.PixelHeight);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        var dark = new bool[width * height];
        var fullMinX = width;
        var fullMinY = height;
        var fullMaxX = -1;
        var fullMaxY = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var index = row + x * 4;
                var blue = pixels[index];
                var green = pixels[index + 1];
                var red = pixels[index + 2];
                var alpha = pixels[index + 3];
                if (alpha < 16 || red > 245 && green > 245 && blue > 245) continue;
                dark[y * width + x] = true;
                fullMinX = Math.Min(fullMinX, x);
                fullMinY = Math.Min(fullMinY, y);
                fullMaxX = Math.Max(fullMaxX, x);
                fullMaxY = Math.Max(fullMaxY, y);
            }
        }

        if (fullMaxX < 0) return new SourceImageBounds(0, 0, width, height, width, height);

        var relevant = FindRelevantComponentBounds(dark, width, height);
        var useRelevant = relevant.Area > 0 &&
                          relevant.Width >= width / 10 &&
                          relevant.Height >= height / 14;
        var minX = useRelevant ? relevant.X : fullMinX;
        var minY = useRelevant ? relevant.Y : fullMinY;
        var maxX = useRelevant ? relevant.X + relevant.Width - 1 : fullMaxX;
        var maxY = useRelevant ? relevant.Y + relevant.Height - 1 : fullMaxY;

        var horizontalPadding = Math.Max(28, Math.Min(width, height) / 26);
        var topPadding = Math.Max(56, Math.Min(width, height) / 14);
        var bottomPadding = Math.Max(32, Math.Min(width, height) / 24);
        minX = Math.Max(0, minX - horizontalPadding);
        minY = Math.Max(0, minY - topPadding);
        maxX = Math.Min(width - 1, maxX + horizontalPadding);
        maxY = Math.Min(height - 1, maxY + bottomPadding);
        return new SourceImageBounds(minX, minY, maxX - minX + 1, maxY - minY + 1, width, height);
    }

    private static IReadOnlyList<SourceImageBounds> ReadFigureImageBounds(string path, int expectedCount)
    {
        if (expectedCount <= 0) return [];

        var decoder = BitmapDecoder.Create(new Uri(path, UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = Math.Max(1, source.PixelWidth);
        var height = Math.Max(1, source.PixelHeight);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        var dark = new bool[width * height];
        var lowerStart = Math.Clamp(height * 52 / 100, 0, height - 1);
        for (var y = lowerStart; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var index = row + x * 4;
                var blue = pixels[index];
                var green = pixels[index + 1];
                var red = pixels[index + 2];
                var alpha = pixels[index + 3];
                var luminance = red * 0.299 + green * 0.587 + blue * 0.114;
                if (alpha >= 16 && luminance < 175) dark[y * width + x] = true;
            }
        }

        var components = FindComponents(dark, width, height, lowerStart)
            .Where(component =>
                component.Area >= Math.Max(60, width * height / 80_000) &&
                component.Width >= width / 10 &&
                component.Height >= height / 24 &&
                !IsLikelyEdgeArtifact(component, width, height))
            .OrderBy(component => component.Y)
            .ThenBy(component => component.X)
            .ToArray();
        if (components.Length == 0) return [];

        if (expectedCount == 1 && components.Length > 1)
        {
            var union = new ComponentBounds(
                components.Min(component => component.X),
                components.Min(component => component.Y),
                components.Max(component => component.X + component.Width) - components.Min(component => component.X),
                components.Max(component => component.Y + component.Height) - components.Min(component => component.Y),
                components.Sum(component => component.Area));
            components = [union];
        }
        else
        {
            components = components
                .Take(expectedCount)
                .OrderBy(component => component.X)
                .ToArray();
        }

        var horizontalPadding = Math.Max(48, width / 22);
        var topPadding = Math.Max(140, height / 10);
        var bottomPadding = Math.Max(80, height / 22);
        return components
            .Select(component =>
            {
                var minX = Math.Max(0, component.X - horizontalPadding);
                var minY = Math.Max(0, component.Y - topPadding);
                var maxX = Math.Min(width - 1, component.X + component.Width - 1 + horizontalPadding);
                var maxY = Math.Min(height - 1, component.Y + component.Height - 1 + bottomPadding);
                return new SourceImageBounds(minX, minY, maxX - minX + 1, maxY - minY + 1, width, height);
            })
            .ToArray();
    }

    private static ComponentBounds FindRelevantComponentBounds(bool[] dark, int width, int height)
    {
        var components = FindComponents(dark, width, height, 0);
        var minUnionX = width;
        var minUnionY = height;
        var maxUnionX = -1;
        var maxUnionY = -1;
        var unionArea = 0;
        var minArea = Math.Max(10, width * height / 120_000);
        foreach (var component in components)
        {
            if (component.Area < minArea || IsLikelyEdgeArtifact(component, width, height)) continue;

            minUnionX = Math.Min(minUnionX, component.X);
            minUnionY = Math.Min(minUnionY, component.Y);
            maxUnionX = Math.Max(maxUnionX, component.X + component.Width - 1);
            maxUnionY = Math.Max(maxUnionY, component.Y + component.Height - 1);
            unionArea += component.Area;
        }
        return unionArea == 0
            ? new ComponentBounds(0, 0, width, height, 0)
            : new ComponentBounds(
                minUnionX,
                minUnionY,
                maxUnionX - minUnionX + 1,
                maxUnionY - minUnionY + 1,
                unionArea);
    }

    private static IReadOnlyList<ComponentBounds> FindComponents(bool[] dark, int width, int height, int minStartY)
    {
        var visited = new bool[dark.Length];
        var queue = new Queue<int>();
        var components = new List<ComponentBounds>();
        for (var start = minStartY * width; start < dark.Length; start++)
        {
            if (!dark[start] || visited[start]) continue;

            var area = 0;
            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;
            visited[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var x = current % width;
                var y = current / width;
                area++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);

                Enqueue(x - 1, y);
                Enqueue(x + 1, y);
                Enqueue(x, y - 1);
                Enqueue(x, y + 1);
            }

            components.Add(new ComponentBounds(minX, minY, maxX - minX + 1, maxY - minY + 1, area));
        }
        return components;

        void Enqueue(int x, int y)
        {
            if (x < 0 || y < minStartY || x >= width || y >= height) return;
            var index = y * width + x;
            if (!dark[index] || visited[index]) return;
            visited[index] = true;
            queue.Enqueue(index);
        }
    }

    private static bool IsLikelyEdgeArtifact(ComponentBounds component, int width, int height)
    {
        var nearLeft = component.X <= 3;
        var nearRight = component.X + component.Width >= width - 4;
        var nearTop = component.Y <= 3;
        var nearBottom = component.Y + component.Height >= height - 4;
        var thinVertical = component.Width <= Math.Max(4, width / 220) &&
                           component.Height >= height / 5;
        var thinHorizontal = component.Height <= Math.Max(4, height / 220) &&
                             component.Width >= width / 5;
        return thinVertical && (nearLeft || nearRight) ||
               thinHorizontal && (nearTop || nearBottom);
    }

    private sealed record SourceImageBounds(int X, int Y, int Width, int Height, int SourceWidth, int SourceHeight);
    private sealed record ComponentBounds(int X, int Y, int Width, int Height, int Area);

    private static IReadOnlyList<string> GetFigureIds(QuestionDocument document) =>
        document.Blocks
            .Where(block => block.Type == QuestionBlockType.Figure)
            .Select(block => block.FigureId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string SanitizeFigureId(string value)
    {
        var safe = new string(value.Where(character => char.IsLetterOrDigit(character) || character == '-').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "figure" : safe;
    }

    private async Task ReviewOutputsAsync(QuestionProject project, CancellationToken cancellationToken)
    {
        var document = await RequireDataAsync<QuestionDocument>(project, "document.json");
        await SvgWriter.WriteFinalOutputsAsync(project, document.Figures, cancellationToken);
        var generatedFiles = await ReadGeneratedFilesAsync(project, cancellationToken);
        var review = await aiProvider.ReviewOutputsAsync(
            project.SourcePath,
            document,
            generatedFiles,
            project.AiInstructions,
            cancellationToken);
        await repository.SaveDataAsync(project, "review.json", review);
        LogAdded?.Invoke(string.IsNullOrWhiteSpace(review.Summary)
            ? "AI 复核完成"
            : $"AI 复核：{review.Summary}");

        if (review.Passed || review.CorrectedDocument is null) return;

        LogAdded?.Invoke("AI 复核发现问题，正在应用修正并重新导出...");
        var corrected = NormalizeReviewedDocument(document, review.CorrectedDocument);
        await SvgWriter.WriteAllAsync(project, corrected.Figures, cancellationToken);
        await repository.SaveDataAsync(project, "document.json", corrected);
        foreach (var exporter in exporters.Where(item => !IsDisabledExport(project, item.Step)))
        {
            await exporter.ExportAsync(project, corrected, cancellationToken);
            var exportRecord = project.Steps[exporter.Step];
            exportRecord.State = StepState.Completed;
            exportRecord.Error = string.Empty;
            exportRecord.CompletedAt = DateTimeOffset.Now;
        }
        await SvgWriter.WriteFinalOutputsAsync(project, corrected.Figures, cancellationToken);
    }

    private QuestionDocument NormalizeReviewedDocument(QuestionDocument original, QuestionDocument? corrected)
    {
        if (corrected is null) return original;

        corrected.SchemaVersion = string.IsNullOrWhiteSpace(corrected.SchemaVersion) ? original.SchemaVersion : corrected.SchemaVersion;
        corrected.Language = string.IsNullOrWhiteSpace(corrected.Language) ? original.Language : corrected.Language;
        corrected.Blocks ??= [];
        corrected.Figures ??= [];
        if (corrected.Blocks.Count == 0) corrected.Blocks = original.Blocks;

        QuestionDocumentNormalizer.NormalizeLatexSymbolMap(original);
        QuestionDocumentNormalizer.NormalizeLatexSymbolMap(corrected);
        foreach (var item in original.LatexSymbolMap)
            corrected.LatexSymbolMap.TryAdd(item.Key, item.Value);

        var originalFigures = original.Figures
            .Where(figure => !string.IsNullOrWhiteSpace(figure.Id))
            .ToDictionary(figure => figure.Id, StringComparer.OrdinalIgnoreCase);
        var mergedFigures = new List<FigureDocument>();
        foreach (var figure in corrected.Figures.Where(figure => !string.IsNullOrWhiteSpace(figure.Id)))
        {
            if (HasReadableSvg(figure.Svg))
            {
                mergedFigures.Add(figure);
                continue;
            }

            if (originalFigures.TryGetValue(figure.Id, out var originalFigure) && HasReadableSvg(originalFigure.Svg))
            {
                mergedFigures.Add(originalFigure);
                LogAdded?.Invoke($"AI 复核返回的 {figure.Id} 图形为空或不可解析，已保留原图形。");
            }
        }

        foreach (var id in GetFigureIds(corrected))
        {
            if (mergedFigures.Any(figure => string.Equals(figure.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
            if (originalFigures.TryGetValue(id, out var originalFigure) && HasReadableSvg(originalFigure.Svg))
                mergedFigures.Add(originalFigure);
        }
        corrected.Figures = mergedFigures;
        return corrected;
    }

    private static bool HasReadableSvg(string svg)
    {
        if (string.IsNullOrWhiteSpace(svg)) return false;
        try
        {
            var document = XDocument.Parse(svg);
            return string.Equals(document.Root?.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadGeneratedFilesAsync(
        QuestionProject project,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in new[] { "document.json", "question.html" })
        {
            var path = Path.Combine(project.DirectoryPath, fileName);
            if (File.Exists(path))
                result[fileName] = await File.ReadAllTextAsync(path, cancellationToken);
        }

        foreach (var finalTextFile in new[]
                 {
                     (Name: ProjectOutputPaths.GetBaseFileName(project) + ".json", Path: ProjectOutputPaths.GetFilePath(project, ".json")),
                     (Name: ProjectOutputPaths.GetBaseFileName(project) + ".tex", Path: ProjectOutputPaths.GetFilePath(project, ".tex"))
                 })
        {
            if (File.Exists(finalTextFile.Path))
                result[finalTextFile.Name] = await File.ReadAllTextAsync(finalTextFile.Path, cancellationToken);
        }

        var finalDirectory = ProjectOutputPaths.GetFinalDirectory(project);
        var baseName = ProjectOutputPaths.GetBaseFileName(project);
        if (Directory.Exists(finalDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(finalDirectory, baseName + "-*.svg").Take(12))
                result[Path.GetFileName(path)] = await File.ReadAllTextAsync(path, cancellationToken);
        }

        foreach (var finalFile in new[]
                 {
                     (Name: ProjectOutputPaths.GetBaseFileName(project) + ".docx", Path: ProjectOutputPaths.GetFilePath(project, ".docx")),
                     (Name: ProjectOutputPaths.GetBaseFileName(project) + ".pdf", Path: ProjectOutputPaths.GetFilePath(project, ".pdf"))
                 })
        {
            if (File.Exists(finalFile.Path))
                result[finalFile.Name] = $"文件存在，大小 {new FileInfo(finalFile.Path).Length} 字节。";
        }
        return result;
    }

    private async Task<T> RequireDataAsync<T>(QuestionProject project, string fileName)
    {
        return await repository.LoadDataAsync<T>(project, fileName)
               ?? throw new InvalidOperationException($"项目缺少 {fileName}，请先执行前置步骤。");
    }

    private static void EnsurePrerequisites(QuestionProject project, TaskStep step)
    {
        var index = Array.IndexOf(OrderedSteps, step);
        if (index <= 0) return;
        var required = step switch
        {
            TaskStep.FormulaRecognition => TaskStep.Ocr,
            TaskStep.FigureRedraw => TaskStep.FormulaRecognition,
            TaskStep.AiReview => TaskStep.FigureRedraw,
            _ => TaskStep.FigureRedraw
        };
        if (project.Steps[required].State != StepState.Completed)
            throw new InvalidOperationException($"请先完成{DisplayName(required)}。");
    }

    private async Task MarkSkippedAsync(QuestionProject project, TaskStep step)
    {
        var record = project.Steps[step];
        record.State = StepState.Skipped;
        record.Error = string.Empty;
        record.CompletedAt = DateTimeOffset.Now;
        await repository.SaveAsync(project);
        ProjectChanged?.Invoke(project);
        LogAdded?.Invoke($"{DisplayName(step)}已跳过");
    }

    private static bool IsDisabledExport(QuestionProject project, TaskStep step) => step switch
    {
        TaskStep.WordExport => !project.OutputSelection.Word && !project.OutputSelection.AppendToWord,
        TaskStep.PdfExport => !project.OutputSelection.Pdf,
        TaskStep.LatexExport => !project.OutputSelection.Latex,
        TaskStep.JsonExport => !project.OutputSelection.Json,
        TaskStep.AiReview => !project.OutputSelection.HasAnyOutput,
        _ => false
    };

    public static string DisplayName(TaskStep step) => step switch
    {
        TaskStep.Ocr => "OCR",
        TaskStep.FormulaRecognition => "公式识别",
        TaskStep.FigureRedraw => "图形处理",
        TaskStep.WordExport => "Word",
        TaskStep.PdfExport => "PDF",
        TaskStep.LatexExport => "LaTeX",
        TaskStep.JsonExport => "JSON",
        TaskStep.AiReview => "AI 复核",
        _ => step.ToString()
    };

    private static string StartMessage(TaskStep step) => step switch
    {
        TaskStep.Ocr => "开始 OCR...",
        TaskStep.FormulaRecognition => "识别公式...",
        TaskStep.FigureRedraw => "处理图形...",
        TaskStep.AiReview => "AI 检查生成文件...",
        _ => $"生成 {DisplayName(step)}..."
    };

    private static string CompleteMessage(TaskStep step) => $"{DisplayName(step)}完成";
}
