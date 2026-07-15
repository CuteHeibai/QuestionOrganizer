using System.Diagnostics;
using System.Net;
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
            return await aiProvider.RedrawFiguresAsync(
                project.SourcePath, document, project.AiInstructions, cancellationToken);

        if (project.FigureMode == FigureProcessingMode.ExternalToolThenOriginalImage)
        {
            var externalFigures = await TryCreateFiguresWithExternalToolAsync(project, document, cancellationToken);
            if (externalFigures.Count > 0) return externalFigures;
            LogAdded?.Invoke("外部图形工具不可用或未产出 SVG，已回退为原图保留。");
        }

        return await CreateOriginalImageFiguresAsync(project, document, cancellationToken);
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
        var sourceSvg = await CreateSourceImageSvgAsync(project.SourcePath, cancellationToken);
        return GetFigureIds(document)
            .Select(id => new FigureDocument
            {
                Id = id,
                Description = "原图保留，未进行 AI 重绘",
                Svg = sourceSvg
            })
            .ToList();
    }

    private static async Task<string> CreateSourceImageSvgAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".png" and not ".jpg" and not ".jpeg")
        {
            return """
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0,0,640,160">
                  <rect width="640" height="160" fill="#ffffff"/>
                  <text x="24" y="84" font-family="SimSun, 宋体, serif" font-size="20" fill="#202020">原文件为 PDF，当前无法直接嵌入裁切图，请改用 AI 重绘或外部工具。</text>
                </svg>
                """;
        }

        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        var bounds = ReadImageBounds(sourcePath);
        var mediaType = extension == ".png" ? "image/png" : "image/jpeg";
        var encoded = Convert.ToBase64String(bytes);
        var safeName = WebUtility.HtmlEncode(Path.GetFileName(sourcePath));
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}" overflow="hidden">
              <title>{safeName}</title>
              <image href="data:{mediaType};base64,{encoded}" x="0" y="0" width="{bounds.SourceWidth}" height="{bounds.SourceHeight}" preserveAspectRatio="none"/>
            </svg>
            """;
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

        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
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
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < 0) return new SourceImageBounds(0, 0, width, height, width, height);

        const int padding = 8;
        minX = Math.Max(0, minX - padding);
        minY = Math.Max(0, minY - padding);
        maxX = Math.Min(width - 1, maxX + padding);
        maxY = Math.Min(height - 1, maxY + padding);
        return new SourceImageBounds(minX, minY, maxX - minX + 1, maxY - minY + 1, width, height);
    }

    private sealed record SourceImageBounds(int X, int Y, int Width, int Height, int SourceWidth, int SourceHeight);

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
        var corrected = review.CorrectedDocument;
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
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadGeneratedFilesAsync(
        QuestionProject project,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in new[] { "document.json", "metadata.json", "question.html", "question.tex" })
        {
            var path = Path.Combine(project.DirectoryPath, fileName);
            if (File.Exists(path))
                result[fileName] = await File.ReadAllTextAsync(path, cancellationToken);
        }

        foreach (var path in Directory.EnumerateFiles(project.DirectoryPath, "*.svg").Take(12))
        {
            result[Path.GetFileName(path)] = await File.ReadAllTextAsync(path, cancellationToken);
        }

        foreach (var fileName in new[] { "question.docx", "question.pdf" })
        {
            var path = Path.Combine(project.DirectoryPath, fileName);
            if (File.Exists(path))
                result[fileName] = $"文件存在，大小 {new FileInfo(path).Length} 字节。";
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
