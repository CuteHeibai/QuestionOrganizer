using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using EaxmBuilder.Core;

namespace EaxmBuilder.Export;

public sealed class DocxExporter(WordExportOptions? options = null) : IQuestionExporter
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly Regex InlineLatexPattern = new(
        @"\$[^$]+\$|\\\([^)]+\\\)|[A-Za-z0-9_{}^()+\-*/=,.\s]{0,24}\\(?:sqrt|frac|dfrac|tfrac|angle|triangle|perp|parallel|overline|mathrm|mathbf|mathit|sin|cos|tan|csc|sec|cot|arcsin|arccos|arctan|sinh|cosh|tanh|csch|sech|coth|exp|ln|log|lg|int|sum|prod|cdot|times|leq?|geq?|neq|mean|median|quartile|quantile|stdevp?|varp?|covp?|mad|corr|spearman|stats|count|total|normaldist|tdist|chisqdist|uniformdist|binomialdist|poissondist|geodist|discretedist|pdf|cdf|inversecdf|random|ztest|ttest|zproptest|chisqtest|chisqgof|pvalue|pleft|pright|score|dof|stderr|conf|lower|upper|estimate|polygon|distance|midpoint|lcm|gcd|mod|ceil|floor|round|sign|nPr|nCr)[^，。；;：:\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public TaskStep Step => TaskStep.WordExport;

    public async Task ExportAsync(
        QuestionProject project,
        QuestionDocument document,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exportOptions = options ?? new WordExportOptions();
        var templatePath = ResolveTemplatePath();
        var outputPath = ProjectOutputPaths.GetFilePath(project, ".docx");
        var renderedFigures = await RenderFiguresAsync(document, cancellationToken);
        try
        {
            if (exportOptions.CreateStandalone)
            {
                EnsureOutputWritable(outputPath);
                File.Copy(templatePath, outputPath, true);
                RemoveMarkOfTheWeb(outputPath);
                using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Update);
                var relationships = LoadXml(archive, "word/_rels/document.xml.rels");
                var contentTypes = LoadXml(archive, "[Content_Types].xml");
                var assets = AddFigureAssets(archive, relationships, contentTypes, renderedFigures);
                var outputDocument = CreateDocument(LoadXml(archive, "word/document.xml"), document, assets);

                ReplaceXml(archive, "word/document.xml", outputDocument);
                ReplaceXml(archive, "word/_rels/document.xml.rels", relationships);
                ReplaceXml(archive, "[Content_Types].xml", contentTypes);
            }

            if (!string.IsNullOrWhiteSpace(exportOptions.AppendToWordPath))
                AppendToExistingDocument(exportOptions.AppendToWordPath, document, renderedFigures);
        }
        finally
        {
            foreach (var renderedFigure in renderedFigures)
            {
                TryDelete(renderedFigure.HtmlPath);
                TryDelete(renderedFigure.PngPath);
            }
        }
    }

    private static void EnsureOutputWritable(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new IOException($"{Path.GetFileName(path)} 正在被 Word 或其他程序打开。请关闭该文件后，重新执行 Word 步骤。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException($"{Path.GetFileName(path)} 当前不可写。请检查文件权限或关闭正在使用它的程序。", exception);
        }
    }

    private string ResolveTemplatePath()
    {
        if (!string.IsNullOrWhiteSpace(options?.TemplatePath))
        {
            if (File.Exists(options.TemplatePath)) return options.TemplatePath;
            throw new FileNotFoundException("找不到所选 Word 模板。", options.TemplatePath);
        }

        var builtInPath = Path.Combine(AppContext.BaseDirectory, "Assets", "default-template.docx");
        if (File.Exists(builtInPath)) return builtInPath;
        return ExtractEmbeddedTemplate();
    }

    private static string ExtractEmbeddedTemplate()
    {
        const string resourceName = "EaxmBuilder.Assets.default-template.docx";
        var templatePath = Path.Combine(Path.GetTempPath(), "QuestionOrganizer-default-template.docx");
        if (File.Exists(templatePath) && new FileInfo(templatePath).Length > 0) return templatePath;

        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                          ?? throw new FileNotFoundException("内置 Word 模板缺失，请重新安装应用。", resourceName);
        using var output = File.Create(templatePath);
        input.CopyTo(output);
        return templatePath;
    }

    private static void AppendToExistingDocument(
        string targetPath,
        QuestionDocument document,
        IReadOnlyList<RenderedFigure> renderedFigures)
    {
        if (!File.Exists(targetPath))
            throw new FileNotFoundException("找不到要追加的 Word 文档。", targetPath);
        EnsureOutputWritable(targetPath);
        RemoveMarkOfTheWeb(targetPath);

        using var archive = ZipFile.Open(targetPath, ZipArchiveMode.Update);
        var targetDocument = LoadXml(archive, "word/document.xml");
        var relationships = LoadXml(archive, "word/_rels/document.xml.rels");
        var contentTypes = LoadXml(archive, "[Content_Types].xml");
        var assets = AddFigureAssets(archive, relationships, contentTypes, renderedFigures);
        var generatedDocument = CreateDocument(new XDocument(targetDocument), document, assets);
        var generatedBody = generatedDocument.Root?.Element(W + "body")
                            ?? throw new InvalidOperationException("生成的 Word 内容缺少正文区域。");
        var generatedElements = generatedBody.Elements()
            .Where(element => element.Name != W + "sectPr")
            .Select(element => new XElement(element))
            .ToList();

        var targetBody = targetDocument.Root?.Element(W + "body")
                         ?? throw new InvalidOperationException("要追加的 Word 文档缺少正文区域。");
        var sectionProperties = targetBody.Elements(W + "sectPr").LastOrDefault();
        var separator = CreateParagraph(string.Empty);
        if (sectionProperties is null)
        {
            targetBody.Add(separator);
            targetBody.Add(generatedElements);
        }
        else
        {
            sectionProperties.AddBeforeSelf(separator);
            sectionProperties.AddBeforeSelf(generatedElements);
        }

        ReplaceXml(archive, "word/document.xml", targetDocument);
        ReplaceXml(archive, "word/_rels/document.xml.rels", relationships);
        ReplaceXml(archive, "[Content_Types].xml", contentTypes);
    }

    private static async Task<List<RenderedFigure>> RenderFiguresAsync(
        QuestionDocument document,
        CancellationToken cancellationToken)
    {
        var rendered = new List<RenderedFigure>();
        foreach (var figure in document.Figures.Where(item => !string.IsNullOrWhiteSpace(item.Svg)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (pixelWidth, pixelHeight) = ReadSvgPixelSize(figure.Svg);
            if (IsEmbeddableVectorSvg(figure.Svg))
            {
                rendered.Add(new RenderedFigure(figure.Id, string.Empty, string.Empty, pixelWidth, pixelHeight, figure.Svg, "svg"));
                continue;
            }

            var edgePath = PdfExporter.FindEdge()
                           ?? throw new InvalidOperationException("未找到 Microsoft Edge，无法为 Word 渲染图形。");
            var token = Guid.NewGuid().ToString("N");
            var htmlPath = Path.Combine(Path.GetTempPath(), $"QuestionOrganizer-{token}.html");
            var pngPath = Path.Combine(Path.GetTempPath(), $"QuestionOrganizer-{token}.png");
            var html = "<!doctype html><meta charset=\"utf-8\"><style>html,body{margin:0;width:100%;height:100%;overflow:hidden;background:white}svg{display:block;width:100%;height:100%}</style>" + figure.Svg;
            await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(false), cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = edgePath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--headless");
            startInfo.ArgumentList.Add("--disable-gpu");
            startInfo.ArgumentList.Add("--hide-scrollbars");
            startInfo.ArgumentList.Add($"--window-size={pixelWidth},{pixelHeight}");
            startInfo.ArgumentList.Add($"--screenshot={pngPath}");
            startInfo.ArgumentList.Add(new Uri(htmlPath).AbsoluteUri);

            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("无法启动 Word 图形渲染进程。");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 || !File.Exists(pngPath))
                throw new InvalidOperationException($"图形 {figure.Id} 无法渲染为 Word 图片。");
            EnsureNonBlankPng(pngPath, figure.Id);
            rendered.Add(new RenderedFigure(figure.Id, htmlPath, pngPath, pixelWidth, pixelHeight, string.Empty, "png"));
        }
        return rendered;
    }

    private static Dictionary<string, FigureAsset> AddFigureAssets(
        ZipArchive archive,
        XDocument relationships,
        XDocument contentTypes,
        IReadOnlyList<RenderedFigure> renderedFigures)
    {
        var relationshipRoot = relationships.Root
                               ?? throw new InvalidOperationException("Word 模板的关系文件无效。");
        var contentTypeRoot = contentTypes.Root
                              ?? throw new InvalidOperationException("Word 模板的内容类型文件无效。");
        if (!contentTypeRoot.Elements(Ct + "Default")
                .Any(item => string.Equals((string?)item.Attribute("Extension"), "png", StringComparison.OrdinalIgnoreCase)))
        {
            contentTypeRoot.Add(new XElement(Ct + "Default",
                new XAttribute("Extension", "png"), new XAttribute("ContentType", "image/png")));
        }
        if (!contentTypeRoot.Elements(Ct + "Default")
                .Any(item => string.Equals((string?)item.Attribute("Extension"), "svg", StringComparison.OrdinalIgnoreCase)))
        {
            contentTypeRoot.Add(new XElement(Ct + "Default",
                new XAttribute("Extension", "svg"), new XAttribute("ContentType", "image/svg+xml")));
        }

        var documentOverride = contentTypeRoot.Elements(Ct + "Override")
            .FirstOrDefault(item => (string?)item.Attribute("PartName") == "/word/document.xml");
        documentOverride?.SetAttributeValue("ContentType",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");

        var relationshipNumber = relationshipRoot.Elements(Rel + "Relationship")
            .Select(item => (string?)item.Attribute("Id"))
            .Where(id => id?.StartsWith("rId", StringComparison.Ordinal) == true && int.TryParse(id[3..], out _))
            .Select(id => int.Parse(id![3..], CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max() + 1;
        var assets = new Dictionary<string, FigureAsset>(StringComparer.OrdinalIgnoreCase);
        var usedMediaNames = archive.Entries
            .Where(entry => entry.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase))
            .Select(entry => Path.GetFileName(entry.FullName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var index = 1;
        var mediaIndex = 1;
        foreach (var renderedFigure in renderedFigures)
        {
            string fileName;
            do
            {
                fileName = $"question-figure-{mediaIndex++}.{renderedFigure.Extension}";
            } while (!usedMediaNames.Add(fileName));
            var relationshipId = $"rId{relationshipNumber++}";
            var mediaEntry = archive.CreateEntry($"word/media/{fileName}", CompressionLevel.Optimal);
            using (var output = mediaEntry.Open())
            {
                if (renderedFigure.Extension == "svg")
                {
                    using var writer = new StreamWriter(output, new UTF8Encoding(false));
                    writer.Write(renderedFigure.Svg);
                }
                else
                {
                    using var input = File.OpenRead(renderedFigure.PngPath);
                    input.CopyTo(output);
                }
            }

            relationshipRoot.Add(new XElement(Rel + "Relationship",
                new XAttribute("Id", relationshipId),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"),
                new XAttribute("Target", $"media/{fileName}")));
            var widthEmu = 3_840_480L;
            var heightEmu = Math.Max(457_200L,
                (long)(widthEmu * (double)renderedFigure.PixelHeight / renderedFigure.PixelWidth));
            assets[renderedFigure.Id] = new FigureAsset(
                relationshipId, fileName, widthEmu, heightEmu, index);
            index++;
        }
        return assets;
    }

    private static XDocument CreateDocument(
        XDocument templateDocument,
        QuestionDocument document,
        IReadOnlyDictionary<string, FigureAsset> assets)
    {
        var body = templateDocument.Root?.Element(W + "body")
                   ?? throw new InvalidOperationException("Word 模板缺少正文区域。");
        var sectionProperties = body.Elements(W + "sectPr").LastOrDefault();
        body.RemoveNodes();

        var numberPlaced = false;
        var startIndex = 0;
        if (TryGetPromptTargetLayout(document.Blocks, assets, out var targetFigureIndex, out var targetAsset))
        {
            var promptBlocks = document.Blocks.Take(targetFigureIndex).ToList();
            if (promptBlocks.Count > 0)
            {
                body.Add(CreatePromptTargetTable(
                    promptBlocks,
                    document.QuestionNumber,
                    targetAsset,
                    document.LatexSymbolMap));
                numberPlaced = true;
                startIndex = targetFigureIndex + 1;
            }
        }

        for (var index = startIndex; index < document.Blocks.Count; index++)
        {
            var block = document.Blocks[index];
            if (IsChoicePair(document.Blocks, index))
            {
                var choices = new List<(string Label, FigureAsset Asset)>();
                while (IsChoicePair(document.Blocks, index))
                {
                    var label = document.Blocks[index].Text.Trim();
                    var figureBlock = document.Blocks[index + 1];
                    if (!assets.TryGetValue(figureBlock.FigureId, out var choiceAsset))
                        throw new InvalidOperationException($"图形 {figureBlock.FigureId} 尚未生成，无法导出 Word。");
                    choices.Add((label, choiceAsset));
                    index += 2;
                }
                index--;
                body.Add(CreateChoiceTable(choices));
                continue;
            }

            switch (block.Type)
            {
                case QuestionBlockType.Paragraph:
                case QuestionBlockType.Formula:
                    var inlineBlocks = new List<QuestionBlock>();
                    while (index < document.Blocks.Count &&
                           document.Blocks[index].Type is QuestionBlockType.Paragraph or QuestionBlockType.Formula)
                    {
                        if (inlineBlocks.Count > 0 && StartsNewParagraph(document.Blocks[index])) break;
                        inlineBlocks.Add(document.Blocks[index]);
                        index++;
                    }
                    index--;
                    body.Add(CreateInlineParagraph(
                        inlineBlocks,
                        document.QuestionNumber,
                        document.LatexSymbolMap,
                        ref numberPlaced));
                    break;
                case QuestionBlockType.Figure:
                    if (!assets.TryGetValue(block.FigureId, out var asset))
                        throw new InvalidOperationException($"图形 {block.FigureId} 尚未生成，无法导出 Word。");
                    var hasImageChoices = Enumerable.Range(index + 1, document.Blocks.Count - index - 1)
                        .Any(candidate => IsChoicePair(document.Blocks, candidate));
                    body.Add(CreateImageParagraph(asset,
                        hasImageChoices ? 1_554_480L : 3_657_600L,
                        hasImageChoices ? 1_371_600L : 2_743_200L));
                    break;
            }
        }

        body.Add(sectionProperties is null ? CreateDefaultSectionProperties() : new XElement(sectionProperties));
        return templateDocument;
    }

    private static bool TryGetPromptTargetLayout(
        IReadOnlyList<QuestionBlock> blocks,
        IReadOnlyDictionary<string, FigureAsset> assets,
        out int targetFigureIndex,
        out FigureAsset targetAsset)
    {
        targetFigureIndex = -1;
        targetAsset = default!;
        var firstChoiceIndex = Enumerable.Range(0, blocks.Count).FirstOrDefault(index => IsChoicePair(blocks, index), -1);
        if (firstChoiceIndex <= 0) return false;
        targetFigureIndex = Enumerable.Range(0, firstChoiceIndex)
            .FirstOrDefault(index => blocks[index].Type == QuestionBlockType.Figure, -1);
        if (targetFigureIndex <= 0 ||
            !assets.TryGetValue(blocks[targetFigureIndex].FigureId, out var asset))
            return false;
        targetAsset = asset;
        return true;
    }

    private static XElement CreatePromptTargetTable(
        IReadOnlyList<QuestionBlock> promptBlocks,
        string questionNumber,
        FigureAsset targetAsset,
        IReadOnlyDictionary<string, string> latexSymbolMap)
    {
        const int totalWidth = 8640;
        const int promptWidth = 5940;
        const int figureWidth = totalWidth - promptWidth;
        var leftCell = new XElement(W + "tc",
            new XElement(W + "tcPr",
                new XElement(W + "tcW", new XAttribute(W + "w", promptWidth), new XAttribute(W + "type", "dxa")),
                new XElement(W + "vAlign", new XAttribute(W + "val", "center"))));
        var numberPlaced = false;
        AddInlineParagraphs(leftCell, promptBlocks, questionNumber, latexSymbolMap, ref numberPlaced);

        var rightCell = new XElement(W + "tc",
            new XElement(W + "tcPr",
                new XElement(W + "tcW", new XAttribute(W + "w", figureWidth), new XAttribute(W + "type", "dxa")),
                new XElement(W + "vAlign", new XAttribute(W + "val", "center"))),
            CreateImageParagraph(targetAsset, 1_280_160L, 1_188_720L));
        return new XElement(W + "tbl",
            new XElement(W + "tblPr",
                new XElement(W + "tblW", new XAttribute(W + "w", totalWidth), new XAttribute(W + "type", "dxa")),
                new XElement(W + "tblLayout", new XAttribute(W + "type", "fixed")),
                new XElement(W + "tblBorders",
                    Border("top"), Border("left"), Border("bottom"), Border("right"),
                    Border("insideH"), Border("insideV"))),
            new XElement(W + "tblGrid",
                new XElement(W + "gridCol", new XAttribute(W + "w", promptWidth)),
                new XElement(W + "gridCol", new XAttribute(W + "w", figureWidth))),
            new XElement(W + "tr", leftCell, rightCell));
    }

    private static bool IsChoicePair(IReadOnlyList<QuestionBlock> blocks, int index) =>
        index + 1 < blocks.Count &&
        blocks[index].Type == QuestionBlockType.Paragraph &&
        IsChoiceLabel(blocks[index].Text) &&
        blocks[index + 1].Type == QuestionBlockType.Figure;

    private static bool IsChoiceLabel(string value)
    {
        var text = value.Trim();
        return text.Length is 2 or 3 && text[0] is >= 'A' and <= 'H' &&
               text[1] is '.' or '．' or '、';
    }

    private static XElement CreateChoiceTable(IReadOnlyList<(string Label, FigureAsset Asset)> choices)
    {
        const int columns = 4;
        const int cellWidth = 2160;
        var table = new XElement(W + "tbl",
            new XElement(W + "tblPr",
                new XElement(W + "tblW", new XAttribute(W + "w", cellWidth * columns),
                    new XAttribute(W + "type", "dxa")),
                new XElement(W + "tblLayout", new XAttribute(W + "type", "fixed")),
                new XElement(W + "tblBorders",
                    Border("top"), Border("left"), Border("bottom"), Border("right"),
                    Border("insideH"), Border("insideV"))),
            new XElement(W + "tblGrid", Enumerable.Range(0, columns)
                .Select(_ => new XElement(W + "gridCol", new XAttribute(W + "w", cellWidth)))));

        for (var offset = 0; offset < choices.Count; offset += columns)
        {
            var row = new XElement(W + "tr");
            for (var column = 0; column < columns; column++)
            {
                var choiceIndex = offset + column;
                var cell = new XElement(W + "tc",
                    new XElement(W + "tcPr",
                        new XElement(W + "tcW", new XAttribute(W + "w", cellWidth),
                            new XAttribute(W + "type", "dxa")),
                        new XElement(W + "vAlign", new XAttribute(W + "val", "center"))));
                if (choiceIndex < choices.Count)
                {
                    var choice = choices[choiceIndex];
                    cell.Add(CreateCompactLabel(choice.Label));
                    cell.Add(CreateImageParagraph(choice.Asset, 914_400L, 1_097_280L));
                }
                else
                {
                    cell.Add(new XElement(W + "p"));
                }
                row.Add(cell);
            }
            table.Add(row);
        }
        return table;
    }

    private static XElement Border(string name) =>
        new(W + name, new XAttribute(W + "val", "nil"));

    private static XElement CreateCompactLabel(string text) =>
        new(W + "p",
            new XElement(W + "pPr",
                new XElement(W + "spacing", new XAttribute(W + "before", "0"),
                    new XAttribute(W + "after", "0"), new XAttribute(W + "line", "240"),
                    new XAttribute(W + "lineRule", "auto"))),
            new XElement(W + "r", RunProperties(),
                new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));

    private static XElement CreateParagraph(string text) =>
        new(W + "p", ParagraphProperties(), CreateTextRun(text));

    private static XElement CreateFormula(string latex, IReadOnlyDictionary<string, string> latexSymbolMap) =>
        new(W + "p", ParagraphProperties(), CreateMathRun(latex, latexSymbolMap));

    private static void AddInlineParagraphs(
        XElement parent,
        IReadOnlyList<QuestionBlock> blocks,
        string questionNumber,
        IReadOnlyDictionary<string, string> latexSymbolMap,
        ref bool numberPlaced)
    {
        for (var index = 0; index < blocks.Count; index++)
        {
            if (blocks[index].Type is not (QuestionBlockType.Paragraph or QuestionBlockType.Formula)) continue;
            var inlineBlocks = new List<QuestionBlock>();
            while (index < blocks.Count &&
                   blocks[index].Type is QuestionBlockType.Paragraph or QuestionBlockType.Formula)
            {
                if (inlineBlocks.Count > 0 && StartsNewParagraph(blocks[index])) break;
                inlineBlocks.Add(blocks[index]);
                index++;
            }
            index--;
            parent.Add(CreateInlineParagraph(inlineBlocks, questionNumber, latexSymbolMap, ref numberPlaced));
        }
    }

    private static XElement CreateInlineParagraph(
        IReadOnlyList<QuestionBlock> blocks,
        string questionNumber,
        IReadOnlyDictionary<string, string> latexSymbolMap,
        ref bool numberPlaced)
    {
        var paragraph = new XElement(W + "p", ParagraphProperties());
        foreach (var block in blocks)
        {
            if (block.Type == QuestionBlockType.Formula)
            {
                paragraph.Add(CreateMathRun(block.Latex, latexSymbolMap));
                continue;
            }

            var text = block.Text;
            if (!numberPlaced && !string.IsNullOrWhiteSpace(questionNumber) &&
                !text.TrimStart().StartsWith(questionNumber, StringComparison.Ordinal))
            {
                text = $"{questionNumber.TrimEnd('.', '．', '、')}. {text}";
            }
            if (!string.IsNullOrWhiteSpace(text)) numberPlaced = true;
            paragraph.Add(CreateTextRuns(text, latexSymbolMap));
        }
        return paragraph;
    }

    private static XElement CreateTextRun(string text) =>
        new(W + "r", RunProperties(),
            new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));

    private static IEnumerable<XElement> CreateTextRuns(
        string text,
        IReadOnlyDictionary<string, string> latexSymbolMap)
    {
        foreach (var inlineSegment in SplitInlineLatex(text))
        {
            if (inlineSegment.IsMath)
            {
                yield return CreateMathRun(inlineSegment.Text, latexSymbolMap);
                continue;
            }

            foreach (var segment in MathTextFormatter.ToMathSegments(inlineSegment.Text, latexSymbolMap))
            {
                yield return segment.Kind switch
                {
                    MathTextFormatter.SegmentKind.Fraction => CreateMathObject(CreateMathFraction(segment, latexSymbolMap)),
                    MathTextFormatter.SegmentKind.Radical => CreateMathObject(CreateMathRadical(segment, latexSymbolMap)),
                    _ => CreateTextRun(segment.Text)
                };
            }
        }
    }

    private static IEnumerable<(bool IsMath, string Text)> SplitInlineLatex(string text)
    {
        var cursor = 0;
        foreach (Match match in InlineLatexPattern.Matches(text))
        {
            if (match.Index > cursor) yield return (false, text[cursor..match.Index]);
            yield return (true, match.Value.Trim());
            cursor = match.Index + match.Length;
        }
        if (cursor < text.Length) yield return (false, text[cursor..]);
    }

    private static XElement CreateMathRun(string latex, IReadOnlyDictionary<string, string> latexSymbolMap) =>
        CreateMathObject(CreateMathElements(latex, latexSymbolMap));

    private static XElement CreateMathObject(params object[] content) =>
        new(M + "oMath", content);

    private static object[] CreateMathElements(string value, IReadOnlyDictionary<string, string> latexSymbolMap) =>
        MathTextFormatter.ToMathSegments(value, latexSymbolMap, stripMathDelimiters: true)
            .Select<MathTextFormatter.MathSegment, object>(segment => segment.Kind switch
            {
                MathTextFormatter.SegmentKind.Fraction => CreateMathFraction(segment, latexSymbolMap),
                MathTextFormatter.SegmentKind.Radical => CreateMathRadical(segment, latexSymbolMap),
                _ => CreateMathTextRun(segment.Text)
            })
            .ToArray();

    private static XElement CreateMathTextRun(string text) =>
        new(M + "r",
            new XElement(M + "rPr", new XElement(M + "sty", new XAttribute(M + "val", "p"))),
            new XElement(M + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));

    private static XElement CreateMathFraction(
        MathTextFormatter.MathSegment segment,
        IReadOnlyDictionary<string, string> latexSymbolMap) =>
        new(M + "f",
            new XElement(M + "num", CreateMathElements(segment.Numerator, latexSymbolMap)),
            new XElement(M + "den", CreateMathElements(segment.Denominator, latexSymbolMap)));

    private static XElement CreateMathRadical(
        MathTextFormatter.MathSegment segment,
        IReadOnlyDictionary<string, string> latexSymbolMap)
    {
        var properties = string.IsNullOrWhiteSpace(segment.Degree)
            ? new XElement(M + "radPr", new XElement(M + "degHide", new XAttribute(M + "val", "1")))
            : new XElement(M + "radPr");
        var radical = new XElement(M + "rad", properties);
        if (!string.IsNullOrWhiteSpace(segment.Degree))
            radical.Add(new XElement(M + "deg", CreateMathElements(segment.Degree, latexSymbolMap)));
        radical.Add(new XElement(M + "e", CreateMathElements(segment.Radicand, latexSymbolMap)));
        return radical;
    }

    private static bool StartsNewParagraph(QuestionBlock block)
    {
        if (block.Type != QuestionBlockType.Paragraph) return false;
        var text = block.Text.TrimStart();
        return text.StartsWith('（') && text.Length > 2 && char.IsDigit(text[1]);
    }

    private static XElement CreateImageParagraph(FigureAsset asset, long maxWidth, long maxHeight) =>
        new(W + "p",
            new XElement(W + "pPr",
                new XElement(W + "jc", new XAttribute(W + "val", "center")),
                new XElement(W + "spacing", new XAttribute(W + "before", "0"),
                    new XAttribute(W + "after", "0"), new XAttribute(W + "line", "240"),
                    new XAttribute(W + "lineRule", "auto"))),
            new XElement(W + "r", CreateImageDrawing(asset, maxWidth, maxHeight)));

    private static XElement CreateImageDrawing(FigureAsset asset, long maxWidth, long maxHeight)
    {
        var scale = Math.Min((double)maxWidth / asset.WidthEmu, (double)maxHeight / asset.HeightEmu);
        scale = Math.Min(scale, 1d);
        var width = Math.Max(1L, (long)(asset.WidthEmu * scale));
        var height = Math.Max(1L, (long)(asset.HeightEmu * scale));
        var picture = new XElement(Pic + "pic",
            new XElement(Pic + "nvPicPr",
                new XElement(Pic + "cNvPr", new XAttribute("id", 2000 + asset.Index),
                    new XAttribute("name", asset.FileName)),
                new XElement(Pic + "cNvPicPr")),
            new XElement(Pic + "blipFill",
                new XElement(A + "blip", new XAttribute(R + "embed", asset.RelationshipId)),
                new XElement(A + "stretch", new XElement(A + "fillRect"))),
            new XElement(Pic + "spPr",
                new XElement(A + "xfrm",
                    new XElement(A + "off", new XAttribute("x", "0"), new XAttribute("y", "0")),
                    new XElement(A + "ext", new XAttribute("cx", width), new XAttribute("cy", height))),
                new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))));
        var inline = new XElement(Wp + "inline",
            new XAttribute("distT", "0"), new XAttribute("distB", "0"),
            new XAttribute("distL", "0"), new XAttribute("distR", "0"),
            new XElement(Wp + "extent", new XAttribute("cx", width), new XAttribute("cy", height)),
            new XElement(Wp + "effectExtent", new XAttribute("l", "0"), new XAttribute("t", "0"),
                new XAttribute("r", "0"), new XAttribute("b", "0")),
            new XElement(Wp + "docPr", new XAttribute("id", 1000 + asset.Index),
                new XAttribute("name", asset.FileName)),
            new XElement(Wp + "cNvGraphicFramePr",
                new XElement(A + "graphicFrameLocks", new XAttribute("noChangeAspect", "1"))),
            new XElement(A + "graphic",
                new XElement(A + "graphicData",
                    new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/picture"), picture)));
        return new XElement(W + "drawing", inline);
    }

    private static XElement RunProperties() =>
        new(W + "rPr",
            new XElement(W + "rFonts", new XAttribute(W + "ascii", "宋体"),
                new XAttribute(W + "eastAsia", "宋体"), new XAttribute(W + "hAnsi", "宋体")),
            new XElement(W + "sz", new XAttribute(W + "val", "21")),
            new XElement(W + "szCs", new XAttribute(W + "val", "21")));

    private static XElement ParagraphProperties() =>
        new(W + "pPr",
            new XElement(W + "spacing", new XAttribute(W + "before", "0"),
                new XAttribute(W + "after", "0"), new XAttribute(W + "line", "480"),
                new XAttribute(W + "lineRule", "auto")));

    private static XElement CreateDefaultSectionProperties() =>
        new(W + "sectPr",
            new XElement(W + "pgSz", new XAttribute(W + "w", "12240"), new XAttribute(W + "h", "15840")),
            new XElement(W + "pgMar", new XAttribute(W + "top", "1440"), new XAttribute(W + "right", "1800"),
                new XAttribute(W + "bottom", "1440"), new XAttribute(W + "left", "1800"),
                new XAttribute(W + "header", "720"), new XAttribute(W + "footer", "720"),
                new XAttribute(W + "gutter", "0")));

    private static (int Width, int Height) ReadSvgPixelSize(string svg)
    {
        try
        {
            var values = XDocument.Parse(svg).Root?.Attribute("viewBox")?.Value
                .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => double.Parse(value, CultureInfo.InvariantCulture))
                .ToArray();
            if (values is not { Length: 4 } || values[2] <= 0 || values[3] <= 0)
                throw new InvalidOperationException("SVG viewBox 必须包含四个用空格分隔的数字。");
            const int width = 1000;
            return (width, Math.Clamp((int)Math.Round(width * values[3] / values[2]), 320, 1200));
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or FormatException)
        {
            throw new InvalidOperationException("AI 返回的 SVG 尺寸格式无效。", exception);
        }
    }

    private static bool IsEmbeddableVectorSvg(string svg)
    {
        try
        {
            var document = XDocument.Parse(svg);
            var root = document.Root;
            if (root is null || root.Name.LocalName != "svg") return false;
            return !root.Descendants().Any(element => element.Name.LocalName == "image");
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static void EnsureNonBlankPng(string path, string figureId)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        var darkPixels = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index + 3] > 0 && pixels[index] < 235 && pixels[index + 1] < 235 && pixels[index + 2] < 235)
                darkPixels++;
        }
        if (darkPixels < Math.Max(100, source.PixelWidth * source.PixelHeight / 5000))
            throw new InvalidOperationException($"图形 {figureId} 渲染为空白，请重新执行图形重绘。");
    }

    private static XDocument LoadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidOperationException($"Word 模板缺少 {path}。");
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static void ReplaceXml(ZipArchive archive, string path, XDocument document)
    {
        archive.GetEntry(path)?.Delete();
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        document.Save(writer, SaveOptions.DisableFormatting);
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void RemoveMarkOfTheWeb(string path)
    {
        if (OperatingSystem.IsWindows()) TryDelete(path + ":Zone.Identifier");
    }

    private sealed record RenderedFigure(
        string Id,
        string HtmlPath,
        string PngPath,
        int PixelWidth,
        int PixelHeight,
        string Svg,
        string Extension);

    private sealed record FigureAsset(
        string RelationshipId,
        string FileName,
        long WidthEmu,
        long HeightEmu,
        int Index);
}

public sealed record WordExportOptions(
    string? TemplatePath = null,
    bool CreateStandalone = true,
    string AppendToWordPath = "");
