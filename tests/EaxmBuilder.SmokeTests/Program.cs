using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Windows.Media.Animation;
using EaxmBuilder.Controls;
using EaxmBuilder.AI;
using EaxmBuilder.Core;
using EaxmBuilder.Export;
using EaxmBuilder.Infrastructure;
using EaxmBuilder.Services;

var output = Path.Combine(Path.GetTempPath(), "QuestionOrganizer-Smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);

try
{
    var project = new QuestionProject
    {
        Name = "Question21",
        DirectoryPath = output,
        AiInstructions = "保留原题编号，不生成答案。"
    };
    var document = new QuestionDocument
    {
        Title = "圆的方程",
        QuestionNumber = "21",
        Blocks =
        [
            new QuestionBlock { Type = QuestionBlockType.Paragraph, Text = "已知圆 C 的方程为" },
            new QuestionBlock { Type = QuestionBlockType.Formula, Latex = "x^2+y^2=1" },
            new QuestionBlock { Type = QuestionBlockType.Figure, FigureId = "figure1" }
        ],
        Figures =
        [
            new FigureDocument
            {
                Id = "figure1",
                Description = "单位圆",
                Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 120 120\"><circle cx=\"60\" cy=\"60\" r=\"42\" fill=\"none\" stroke=\"#202020\"/></svg>"
            }
        ]
    };

    await SvgWriter.WriteAllAsync(project, document.Figures, CancellationToken.None);
    var compactFigure = new FigureDocument
    {
        Id = "compact",
        Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"00260220\"><path d=\"M7884 C88251261015826\"/></svg>"
    };
    await SvgWriter.WriteAllAsync(project, [compactFigure], CancellationToken.None);
    if (!compactFigure.Svg.Contains("viewBox=\"0 0 260 220\"", StringComparison.Ordinal) ||
        !compactFigure.Svg.Contains("M 78 84 C 88 25 126 10 158 26", StringComparison.Ordinal))
        throw new InvalidOperationException("SVG 粘连坐标未能恢复为有效格式：" + compactFigure.Svg);
    await new JsonExporter().ExportAsync(project, document, CancellationToken.None);
    await new LatexExporter().ExportAsync(project, document, CancellationToken.None);
    await new DocxExporter().ExportAsync(project, document, CancellationToken.None);
    await new PdfExporter().ExportAsync(project, document, CancellationToken.None);

    var appendTarget = Path.Combine(output, "append-target.docx");
    File.Copy(Path.Combine(output, "question.docx"), appendTarget);
    await new DocxExporter(new WordExportOptions(
        CreateStandalone: false,
        AppendToWordPath: appendTarget)).ExportAsync(project, document, CancellationToken.None);
    using (var archive = ZipFile.OpenRead(appendTarget))
    {
        var xml = await ReadEntryAsync(archive, "word/document.xml");
        if (CountOccurrences(xml, "已知圆 C 的方程为") < 2)
            throw new InvalidOperationException("追加到现有 Word 文档未写入题目正文。");
        if (archive.GetEntry("word/media/question-figure-2.png") is null)
            throw new InvalidOperationException("追加到现有 Word 文档时未避开已有图片文件名。");
    }

    var repository = new ProjectRepository();
    await repository.SaveAsync(project);
    var reloaded = (await repository.GetRecentAsync(output)).Single();
    if (reloaded.AiInstructions != project.AiInstructions)
        throw new InvalidOperationException("项目级 AI 要求未能持久化。");

    project.Steps[TaskStep.Ocr].State = StepState.Running;
    await repository.SaveAsync(project);
    reloaded = (await repository.GetRecentAsync(output)).Single();
    if (reloaded.Steps[TaskStep.Ocr].State != StepState.Failed)
        throw new InvalidOperationException("中断的运行状态未恢复为可重试状态。");

    var selectedOutputDirectory = Path.Combine(output, "selected-output");
    Directory.CreateDirectory(selectedOutputDirectory);
    var selectedOutputProject = new QuestionProject
    {
        Name = "SelectedOutput",
        DirectoryPath = selectedOutputDirectory,
        SourceFileName = "source.png",
        OutputSelection = new OutputSelection { Word = true, Pdf = false, Latex = false, Json = false }
    };
    selectedOutputProject.Steps[TaskStep.Ocr].State = StepState.Completed;
    selectedOutputProject.Steps[TaskStep.FormulaRecognition].State = StepState.Completed;
    selectedOutputProject.Steps[TaskStep.FigureRedraw].State = StepState.Completed;
    await repository.SaveDataAsync(selectedOutputProject, "document.json", document);
    await new ProcessingTaskManager(
        repository,
        new FakeAiProvider(),
        [new DocxExporter(), new PdfExporter(), new LatexExporter(), new JsonExporter()])
        .RunPendingAsync(selectedOutputProject);
    if (!File.Exists(Path.Combine(selectedOutputDirectory, "question.docx")) ||
        File.Exists(Path.Combine(selectedOutputDirectory, "question.pdf")) ||
        selectedOutputProject.Steps[TaskStep.PdfExport].State != StepState.Skipped ||
        selectedOutputProject.Steps[TaskStep.LatexExport].State != StepState.Skipped ||
        selectedOutputProject.Steps[TaskStep.JsonExport].State != StepState.Skipped)
        throw new InvalidOperationException("按输出选项跳过导出步骤失败。");

    Exception? animationError = null;
    var animationThread = new Thread(() =>
    {
        try
        {
            var icon = new FluentIcon();
            icon.BeginAnimation(FluentIcon.RotationAngleProperty, new DoubleAnimation(0, 360,
                TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        catch (Exception exception)
        {
            animationError = exception;
        }
    });
    animationThread.SetApartmentState(ApartmentState.STA);
    animationThread.Start();
    animationThread.Join();
    if (animationError is not null)
        throw new InvalidOperationException("运行状态动画无法启动。", animationError);

    var responsesField = typeof(OpenAiProvider).GetField("_useResponsesApi",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("无法检查 AI API 路由。");
    var clientField = typeof(OpenAiProvider).GetField("_client",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("无法检查 AI 请求超时。");
    using var officialProvider = new OpenAiProvider(
        AiProviderKind.OpenAi, "https://api.openai.com/v1", "test", "test");
    using var customProvider = new OpenAiProvider(
        AiProviderKind.OpenAi, "https://compatible.example/v1", "test", "test");
    using var chatProvider = new OpenAiProvider(
        AiProviderKind.OpenAiCompatible, "https://compatible.example/v1", "test", "test");
    using var doubaoProvider = new OpenAiProvider(
        AiProviderKind.Doubao, "https://ark.cn-beijing.volces.com/api/v3", "test", "test");
    if (responsesField.GetValue(officialProvider) is not true ||
        responsesField.GetValue(customProvider) is not true ||
        responsesField.GetValue(chatProvider) is not false ||
        responsesField.GetValue(doubaoProvider) is not false)
        throw new InvalidOperationException("官方、兼容与豆包 API 的路由规则不正确。");
    if (Enum.IsDefined(typeof(AiProviderKind), 2))
        throw new InvalidOperationException("DeepSeek 旧枚举值不应再作为可选 AI 提供商。");
    try
    {
        _ = new AiProviderFactory(new SettingsStore()).Create(new AppSettings
        {
            Provider = (AiProviderKind)2,
            BaseUrl = "https://api.deepseek.com",
            Model = "deepseek-chat",
            ProtectedApiKey = WindowsDataProtector.Protect("test")
        });
        throw new InvalidOperationException("已移除的 AI 提供商不应能创建 provider。");
    }
    catch (InvalidOperationException exception)
        when (exception.Message.Contains("不再支持", StringComparison.Ordinal))
    {
    }
    if (clientField.GetValue(doubaoProvider) is not HttpClient { Timeout.TotalMinutes: >= 20 })
        throw new InvalidOperationException("AI 长任务请求超时时间过短，公式识别容易提前失败。");
    if (typeof(DocxExporter).Assembly.GetManifestResourceStream("EaxmBuilder.Assets.default-template.docx") is not { } templateResource)
        throw new InvalidOperationException("内置 Word 模板未嵌入程序，单文件安装包会缺少默认模板。");
    templateResource.Dispose();

    var createResponsesRequest = typeof(OpenAiProvider).GetMethod("CreateResponsesRequest",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("无法检查 Responses 请求结构。");
    var responsesRequest = createResponsesRequest.Invoke(customProvider,
        ["question.png", "data:image/png;base64,AA==", "prompt"]);
    var responsesRequestJson = JsonSerializer.Serialize(responsesRequest);
    if (!responsesRequestJson.Contains("max_output_tokens", StringComparison.Ordinal) ||
        !responsesRequestJson.Contains("xhigh", StringComparison.Ordinal) ||
        !responsesRequestJson.Contains("json_object", StringComparison.Ordinal) ||
        !responsesRequestJson.Contains("stream", StringComparison.Ordinal))
        throw new InvalidOperationException("Responses 请求缺少已验证的推理或结构化输出参数。");

    var extractResponsesStreamText = typeof(OpenAiProvider).GetMethod("ExtractResponsesStreamText",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("无法检查 Responses 流解析器。");
    const string eventStream = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"{\\\"ok\\\":\"}\n\n" +
                               "data: {\"type\":\"response.output_text.delta\",\"delta\":\"true}\"}\n\n" +
                               "data: [DONE]\n\n";
    if ((string?)extractResponsesStreamText.Invoke(null, [eventStream]) != "{\"ok\":true}")
        throw new InvalidOperationException("Responses 流解析器未正确合并最终文本。");

    var extractChatText = typeof(OpenAiProvider).GetMethod("ExtractChatText",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("无法检查兼容响应解析器。");
    var compatiblePayloads = new[]
    {
        "{\"choices\":[{\"message\":{\"content\":\"{\\\"ok\\\":true}\"}}]}",
        "{\"choices\":[{\"text\":\"{\\\"ok\\\":true}\"}]}",
        "{\"choices\":[{\"content\":\"{\\\"ok\\\":true}\"}]}",
        "{\"choices\":[{\"delta\":{\"content\":\"{\\\"ok\\\":true}\"}}]}",
        "{\"choices\":[{\"message\":{\"reasoning_content\":\"{\\\"ok\\\":true}\"}}]}",
        "{\"response\":{\"output_text\":\"{\\\"ok\\\":true}\"}}"
    };
    foreach (var json in compatiblePayloads)
    {
        using var payload = JsonDocument.Parse(json);
        var extracted = (string?)extractChatText.Invoke(null, [payload.RootElement]);
        if (extracted != "{\"ok\":true}")
            throw new InvalidOperationException("兼容响应解析器未覆盖已知返回结构。");
    }

    var extractResponsesText = typeof(OpenAiProvider).GetMethod("ExtractResponsesText",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("无法检查 Responses 解析器。");
    var responsesPayloads = new[]
    {
        "{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"{\\\"ok\\\":true}\"}]}]}",
        "{\"output\":[{\"content\":\"{\\\"ok\\\":true}\"}]}",
        "{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"value\":\"{\\\"ok\\\":true}\"}]}]}",
        "{\"status\":\"completed\",\"output\":[{\"type\":\"reasoning\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"done\"}]},{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"{\\\"ok\\\":true}\"}]}]}",
        "{\"response\":{\"message\":{\"completion\":\"{\\\"ok\\\":true}\"}}}",
        "{\"response\":{\"output_text\":\"{\\\"ok\\\":true}\"}}",
        "{\"choices\":[{\"text\":\"{\\\"ok\\\":true}\"}]}"
    };
    foreach (var json in responsesPayloads)
    {
        using var payload = JsonDocument.Parse(json);
        var extracted = (string?)extractResponsesText.Invoke(null, [payload.RootElement]);
        if (extracted != "{\"ok\":true}")
            throw new InvalidOperationException("Responses 解析器未覆盖已知返回结构。");
    }

    RequireFile("figure1.svg");
    RequireFile("metadata.json");
    RequireFile("question.tex");
    RequireFile("question.docx");
    RequireFile("question.html");
    RequireFile("question.pdf");

    XDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "figure1.svg")));
    JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "metadata.json")));

    using (var archive = ZipFile.OpenRead(Path.Combine(output, "question.docx")))
    {
        var documentEntry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidOperationException("DOCX 缺少 word/document.xml");
        var xml = await ReadEntryAsync(archive, "word/document.xml");
        if (!xml.Contains("oMath", StringComparison.Ordinal) ||
            !xml.Contains("line=\"480\"", StringComparison.Ordinal) ||
            !xml.Contains("drawing", StringComparison.Ordinal) ||
            xml.Contains("2026", StringComparison.Ordinal) ||
            xml.Contains("oMathPara", StringComparison.Ordinal))
            throw new InvalidOperationException("DOCX 未正确替换模板正文、公式、图形或二倍行距设置。");
        if (archive.GetEntry("word/media/question-figure-1.png") is null)
            throw new InvalidOperationException("DOCX 缺少渲染后的图形图片。");
        var relationshipEntry = archive.GetEntry("word/_rels/document.xml.rels")
            ?? throw new InvalidOperationException("DOCX 缺少正文关系文件。");
        if (!(await ReadEntryAsync(archive, "word/_rels/document.xml.rels")).Contains("relationships/image", StringComparison.Ordinal))
            throw new InvalidOperationException("DOCX 缺少 SVG 图形关系。");
    }

    var html = await File.ReadAllTextAsync(Path.Combine(output, "question.html"));
    if (html.Contains("$x^2", StringComparison.Ordinal) ||
        !html.Contains("x&#178;+y&#178;=1", StringComparison.Ordinal) ||
        !html.Contains("<span class=\"formula\">", StringComparison.Ordinal))
        throw new InvalidOperationException("PDF/HTML 导出仍存在块级公式、原始 LaTeX 或公式间距风险。");

    var pdfHeader = new byte[4];
    await using (var stream = File.OpenRead(Path.Combine(output, "question.pdf")))
        _ = await stream.ReadAsync(pdfHeader);
    if (Encoding.ASCII.GetString(pdfHeader) != "%PDF")
        throw new InvalidOperationException("PDF 文件头无效。");

    Console.WriteLine("PASS: API routing, selected outputs, append Word, recovery, spinner, persistence and all exporters");
    return 0;

    void RequireFile(string fileName)
    {
        var path = Path.Combine(output, fileName);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException($"缺少导出文件：{fileName}");
    }

    static async Task<string> ReadEntryAsync(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName)
                    ?? throw new InvalidOperationException($"DOCX 缺少 {entryName}");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
finally
{
    Directory.Delete(output, true);
}

internal sealed class FakeAiProvider : IAiProvider
{
    public Task TestConnectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<OcrResult> RecognizeTextAsync(
        string sourcePath,
        string additionalInstructions,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new OcrResult());

    public Task<QuestionDocument> StructureQuestionAsync(
        string sourcePath,
        OcrResult ocr,
        string additionalInstructions,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new QuestionDocument());

    public Task<IReadOnlyList<FigureDocument>> RedrawFiguresAsync(
        string sourcePath,
        QuestionDocument document,
        string additionalInstructions,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FigureDocument>>([]);
}
