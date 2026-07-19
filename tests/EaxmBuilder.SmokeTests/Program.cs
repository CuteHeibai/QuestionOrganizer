using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Net;
using System.Windows.Media.Animation;
using EaxmBuilder.Controls;
using EaxmBuilder.AI;
using EaxmBuilder.Core;
using EaxmBuilder.Export;
using EaxmBuilder.Infrastructure;
using EaxmBuilder.Services;

var output = Path.Combine(Path.GetTempPath(), "QuestionOrganizer-Smoke-" + Guid.NewGuid().ToString("N"));
var finalOutput = Path.Combine(output, "final-output");
Directory.CreateDirectory(output);
var previousFigureTool = Environment.GetEnvironmentVariable("QUESTION_ORGANIZER_FIGURE_TOOL");
var previousGeoGebraPath = Environment.GetEnvironmentVariable("QUESTION_ORGANIZER_GEOGEBRA_PATH");

try
{
    var project = new QuestionProject
    {
        Name = "Question21",
        DirectoryPath = output,
        AiInstructions = "保留原题编号，不生成答案。",
        OutputSelection =
        {
            FileName = "custom-question",
            OutputDirectory = finalOutput
        }
    };
    var document = new QuestionDocument
    {
        Title = "圆的方程",
        QuestionNumber = "21",
        LatexSymbolMap = new Dictionary<string, string>
        {
            [@"\customstar"] = "★",
            [@"\widearc"] = "⌒#1"
        },
        Blocks =
        [
            new QuestionBlock { Type = QuestionBlockType.Paragraph, Text = "已知圆 C 的方程为" },
            new QuestionBlock { Type = QuestionBlockType.Formula, Latex = "x^2+y^2=1" },
            new QuestionBlock { Type = QuestionBlockType.Paragraph, Text = "其中" },
            new QuestionBlock { Type = QuestionBlockType.Formula, Latex = @"\triangle ABC,\ \mathrm{AB}\perp CD,\ \sqrt{x_1^2}+\angle A+\sin\theta" },
            new QuestionBlock { Type = QuestionBlockType.Formula, Latex = @"\customstar+\widearc{AB}" },
            new QuestionBlock { Type = QuestionBlockType.Paragraph, Text = "如图，angle ABC和angle ADC的角平分线分别交AD、BC于E、F。过F作FG perp BE于G。" },
            new QuestionBlock { Type = QuestionBlockType.Paragraph, Text = "（2）若GF = sqrt2，HF = (1)/(2)，求triangle DHF的面积；" },
            new QuestionBlock { Type = QuestionBlockType.Paragraph, Text = @"补充：若 AB=\sqrt{2}，求 \triangle ABC 的面积。" },
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
    await SvgWriter.WriteFinalOutputsAsync(project, document.Figures, CancellationToken.None);
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
    File.Copy(Path.Combine(finalOutput, "custom-question.docx"), appendTarget);
    await new DocxExporter(new WordExportOptions(
        CreateStandalone: false,
        AppendToWordPath: appendTarget)).ExportAsync(project, document, CancellationToken.None);
    using (var archive = ZipFile.OpenRead(appendTarget))
    {
        var xml = await ReadEntryAsync(archive, "word/document.xml");
        if (CountOccurrences(xml, "已知圆 C 的方程为") < 2)
            throw new InvalidOperationException("追加到现有 Word 文档未写入题目正文。");
        if (archive.GetEntry("word/media/question-figure-2.svg") is null)
            throw new InvalidOperationException("追加到现有 Word 文档时未避开已有矢量图文件名。");
    }

    var repository = new ProjectRepository();
    await File.WriteAllBytesAsync(
        Path.Combine(output, "create-source.png"),
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));
    var createdProject = await repository.CreateAsync(Path.Combine(output, "create-source.png"), output);
    if (!createdProject.OutputSelection.OutputDirectory.StartsWith(
            Path.Combine(output, "最终输出"), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("新项目未使用设置目录下的最终输出文件夹。");
    await repository.SaveAsync(project);
    var reloaded = (await repository.GetRecentAsync(output)).Single(item => item.Id == project.Id);
    if (reloaded.AiInstructions != project.AiInstructions)
        throw new InvalidOperationException("项目级 AI 要求未能持久化。");

    project.Steps[TaskStep.Ocr].State = StepState.Running;
    await repository.SaveAsync(project);
    reloaded = (await repository.GetRecentAsync(output, activeProjectIds: new HashSet<Guid> { project.Id }))
        .Single(item => item.Id == project.Id);
    if (reloaded.Steps[TaskStep.Ocr].State != StepState.Running)
        throw new InvalidOperationException("当前仍在运行的项目被错误显示为任务中断。");
    reloaded = (await repository.GetRecentAsync(output)).Single(item => item.Id == project.Id);
    if (reloaded.Steps[TaskStep.Ocr].State != StepState.Failed)
        throw new InvalidOperationException("中断的运行状态未恢复为可重试状态。");

    var selectedOutputDirectory = Path.Combine(output, "selected-output");
    var selectedFinalOutputDirectory = Path.Combine(selectedOutputDirectory, "final");
    Directory.CreateDirectory(selectedOutputDirectory);
    var selectedOutputProject = new QuestionProject
    {
        Name = "SelectedOutput",
        DirectoryPath = selectedOutputDirectory,
        SourceFileName = "source.png",
        OutputSelection = new OutputSelection
        {
            Word = true,
            Pdf = false,
            Latex = false,
            Json = false,
            Svg = false,
            FileName = "OnlyWord",
            OutputDirectory = selectedFinalOutputDirectory
        }
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
    if (!File.Exists(Path.Combine(selectedFinalOutputDirectory, "OnlyWord.docx")) ||
        File.Exists(Path.Combine(selectedFinalOutputDirectory, "OnlyWord.pdf")) ||
        File.Exists(Path.Combine(selectedFinalOutputDirectory, "OnlyWord.json")) ||
        File.Exists(Path.Combine(selectedFinalOutputDirectory, "OnlyWord-figure1.svg")) ||
        selectedOutputProject.Steps[TaskStep.PdfExport].State != StepState.Skipped ||
        selectedOutputProject.Steps[TaskStep.LatexExport].State != StepState.Skipped ||
        selectedOutputProject.Steps[TaskStep.JsonExport].State != StepState.Skipped ||
        selectedOutputProject.Steps[TaskStep.AiReview].State != StepState.Completed)
        throw new InvalidOperationException("按输出选项跳过导出步骤失败。");
    if (!File.Exists(Path.Combine(selectedOutputDirectory, "review.json")))
        throw new InvalidOperationException("AI 复核步骤未生成 review.json。");

    var regenerateProject = new QuestionProject
    {
        Name = "Regenerate",
        DirectoryPath = Path.Combine(output, "regenerate"),
        SourceFileName = "source.png",
        OutputSelection = new OutputSelection
        {
            Word = true,
            Pdf = true,
            Latex = true,
            Json = true,
            Svg = true,
            FileName = "Regenerate",
            OutputDirectory = Path.Combine(output, "regenerate-final")
        }
    };
    foreach (var step in regenerateProject.Steps.Values)
        step.State = StepState.Completed;
    QuestionProjectWorkflow.ResetFinalGenerationSteps(regenerateProject);
    if (regenerateProject.Steps[TaskStep.Ocr].State != StepState.Completed ||
        regenerateProject.Steps[TaskStep.FormulaRecognition].State != StepState.Completed ||
        regenerateProject.Steps[TaskStep.FigureRedraw].State != StepState.Completed ||
        regenerateProject.Steps[TaskStep.WordExport].State != StepState.Pending ||
        regenerateProject.Steps[TaskStep.PdfExport].State != StepState.Pending ||
        regenerateProject.Steps[TaskStep.LatexExport].State != StepState.Pending ||
        regenerateProject.Steps[TaskStep.JsonExport].State != StepState.Pending ||
        regenerateProject.Steps[TaskStep.AiReview].State != StepState.Pending)
        throw new InvalidOperationException("再次生成未重置所有已选择的最终输出步骤。");

    var originalFigureDirectory = Path.Combine(output, "original-figure");
    Directory.CreateDirectory(originalFigureDirectory);
    await File.WriteAllBytesAsync(
        Path.Combine(originalFigureDirectory, "source.png"),
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));
    var originalFigureProject = new QuestionProject
    {
        Name = "OriginalFigure",
        DirectoryPath = originalFigureDirectory,
        SourceFileName = "source.png",
        FigureMode = FigureProcessingMode.OriginalImage
    };
    originalFigureProject.Steps[TaskStep.Ocr].State = StepState.Completed;
    originalFigureProject.Steps[TaskStep.FormulaRecognition].State = StepState.Completed;
    await repository.SaveDataAsync(originalFigureProject, "document.json", document);
    var figureFakeAi = new FakeAiProvider();
    var figureSucceeded = await new ProcessingTaskManager(
            repository,
            figureFakeAi,
            [new DocxExporter(), new PdfExporter(), new LatexExporter(), new JsonExporter()])
        .RunStepAsync(originalFigureProject, TaskStep.FigureRedraw);
    var originalFigureSvg = await File.ReadAllTextAsync(Path.Combine(originalFigureDirectory, "figure1.svg"));
    if (!figureSucceeded ||
        figureFakeAi.RedrawCallCount != 0 ||
        !originalFigureSvg.Contains("data:image/png;base64", StringComparison.Ordinal))
        throw new InvalidOperationException("原图保留模式未正确跳过 AI 重绘并生成可导出的 SVG。");
    Environment.SetEnvironmentVariable("QUESTION_ORGANIZER_FIGURE_TOOL", Path.Combine(output, "missing-tool.exe"));
    var localGeoGebraPath = Path.Combine(Directory.GetCurrentDirectory(), "src", "EaxmBuilder.App", "Assets", "GeoGebra");
    Environment.SetEnvironmentVariable("QUESTION_ORGANIZER_GEOGEBRA_PATH",
        Directory.Exists(localGeoGebraPath) ? localGeoGebraPath : string.Empty);
    var externalFallbackProject = new QuestionProject
    {
        Name = "ExternalFallbackFigure",
        DirectoryPath = originalFigureDirectory,
        SourceFileName = "source.png",
        FigureMode = FigureProcessingMode.ExternalToolThenOriginalImage
    };
    externalFallbackProject.Steps[TaskStep.Ocr].State = StepState.Completed;
    externalFallbackProject.Steps[TaskStep.FormulaRecognition].State = StepState.Completed;
    await repository.SaveDataAsync(externalFallbackProject, "document.json", document);
    var externalFakeAi = new FakeAiProvider();
    var externalSucceeded = await new ProcessingTaskManager(
            repository,
            externalFakeAi,
            [new DocxExporter(), new PdfExporter(), new LatexExporter(), new JsonExporter()])
        .RunStepAsync(externalFallbackProject, TaskStep.FigureRedraw);
    if (!externalSucceeded ||
        externalFakeAi.RedrawCallCount != 0 ||
        externalFakeAi.GeoGebraCallCount == 0 ||
        !File.Exists(Path.Combine(originalFigureDirectory, "figure1.geogebra.txt")))
        throw new InvalidOperationException("外部工具模式未优先生成 GeoGebra 命令并在失败时回退。");
    if (Directory.Exists(localGeoGebraPath))
    {
        var externalDocument = await repository.LoadDataAsync<QuestionDocument>(externalFallbackProject, "document.json")
                               ?? throw new InvalidOperationException("外部工具模式未保存 document.json。");
        if (externalDocument.Figures.FirstOrDefault()?.Description.Contains("GeoGebra", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("本地 GeoGebra bundle 存在时未使用 GeoGebra 命令绘制图形。");
    }

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
    var deserializeJson = typeof(OpenAiProvider).GetMethod("DeserializeJsonContent",
            BindingFlags.Static | BindingFlags.NonPublic)
        ?.MakeGenericMethod(typeof(OutputReviewResult))
        ?? throw new InvalidOperationException("无法检查 AI JSON 容错解析。");
    var wrappedReview = (OutputReviewResult?)deserializeJson.Invoke(null,
        ["说明：下面是复核结果。\n```json\n{\"passed\":true,\"summary\":\"ok\",\"issues\":[],\"correctedDocument\":null}\n```\n已完成。"]);
    if (wrappedReview is null || !wrappedReview.Passed || wrappedReview.Summary != "ok")
        throw new InvalidOperationException("AI 复核 JSON 外包文字未能被容错解析。");
    var structuredReview = (OutputReviewResult?)deserializeJson.Invoke(null,
        ["{\"passed\":false,\"summary\":\"发现问题\",\"issues\":[{\"severity\":\"high\",\"type\":\"figure_missing\",\"message\":\"端点未连接\",\"correction\":\"复用同一命名点\"}],\"correctedDocument\":{\"Blocks\":[{\"Type\":\"FigureRef\",\"FigureId\":\"fig1\",\"Caption\":\"图1\"}]}}"]);
    if (structuredReview?.Issues.Count != 1 ||
        structuredReview.Issues[0].Severity != "high" ||
        structuredReview.Issues[0].Description != "端点未连接" ||
        structuredReview.CorrectedDocument?.Blocks.FirstOrDefault()?.Type != QuestionBlockType.Figure)
        throw new InvalidOperationException("AI 复核中的结构化 issues 无法解析。");
    var reversedSymbolDocument = new QuestionDocument
    {
        LatexSymbolMap = new Dictionary<string, string>
        {
            ["√"] = @"\sqrt{}",
            ["triangle"] = "△",
            [@"\perp"] = "⊥"
        }
    };
    QuestionDocumentNormalizer.NormalizeLatexSymbolMap(reversedSymbolDocument);
    if (!reversedSymbolDocument.LatexSymbolMap.TryGetValue(@"\sqrt", out var sqrtSymbol) ||
        sqrtSymbol != "√" ||
        !reversedSymbolDocument.LatexSymbolMap.ContainsKey(@"\triangle") ||
        !reversedSymbolDocument.LatexSymbolMap.ContainsKey(@"\perp"))
        throw new InvalidOperationException("AI 返回的反向 latexSymbolMap 未能自动纠正。");
    var rendererType = typeof(ProcessingTaskManager).Assembly.GetType("EaxmBuilder.Services.GeoGebraRenderer")
                       ?? throw new InvalidOperationException("无法检查 GeoGebra 矢量转换器。");
    var vectorMethod = rendererType.GetMethod("TryCreateVectorSvg",
        BindingFlags.Static | BindingFlags.NonPublic)
                       ?? throw new InvalidOperationException("无法检查直角 Polyline 矢量转换器。");
    var vectorArguments = new object?[]
    {
        "right-angle",
        new List<string>
        {
            "A=(0,0)", "B=(2,0)", "C=(2,2)",
            "P=(1.7,0)", "Q=(1.7,0.3)", "R=(2,0.3)",
            "Segment(A,B)", "Segment(B,C)", "Polyline(P,Q,R)"
        },
        null
    };
    if (vectorMethod.Invoke(null, vectorArguments) is not true ||
        vectorArguments[2] is not string vectorSvg ||
        vectorSvg.Count(character => character == '<') < 6 ||
        vectorSvg.Contains("stroke=\"#ffffff\"", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("直角 Polyline 未转换为可用的纯 SVG 矢量图。");
    var computedPointArguments = new object?[]
    {
        "computed-points",
        new List<string>
        {
            "A=(0,4)", "B=(0,0)", "C=(4,0)", "D=(4,4)",
            "O=Intersect(Segment(A,C),Segment(B,D))",
            "E=Midpoint(O,D)",
            "Segment(A,C)", "Segment(B,D)", "Segment(A,E)",
            "Text(\"O\",O+(0.12,-0.12))", "Text(\"E\",E+(0.12,0.12))", "Text(\"图1\",(2,-0.45))"
        },
        null
    };
    if (vectorMethod.Invoke(null, computedPointArguments) is not true ||
        computedPointArguments[2] is not string computedPointSvg ||
        !computedPointSvg.Contains(">O<", StringComparison.Ordinal) ||
        !computedPointSvg.Contains(">E<", StringComparison.Ordinal) ||
        !computedPointSvg.Contains(">图1<", StringComparison.Ordinal))
        throw new InvalidOperationException("GeoGebra 计算点或坐标文字未转换为可用 SVG。");
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
    var profileSettings = new AppSettings { Provider = AiProviderKind.Doubao };
    profileSettings.ProviderProfiles[AiProviderKind.OpenAi] = new AiProviderSettings
    {
        ProtectedApiKey = WindowsDataProtector.Protect("openai-key"),
        BaseUrl = "https://api.openai.com/v1",
        Model = "gpt-openai"
    };
    profileSettings.ProviderProfiles[AiProviderKind.Doubao] = new AiProviderSettings
    {
        ProtectedApiKey = WindowsDataProtector.Protect("doubao-key"),
        BaseUrl = "https://ark.example/v3",
        Model = "doubao-model"
    };
    var settingsStore = new SettingsStore();
    if (settingsStore.ReadApiKey(profileSettings, AiProviderKind.OpenAi) != "openai-key" ||
        settingsStore.ReadApiKey(profileSettings, AiProviderKind.Doubao) != "doubao-key")
        throw new InvalidOperationException("每个 AI 供应商未能保存独立 API Key。");
    if (typeof(DocxExporter).Assembly.GetManifestResourceStream("EaxmBuilder.Assets.default-template.docx") is not { } templateResource)
        throw new InvalidOperationException("内置 Word 模板未嵌入程序，ZIP 包会缺少默认模板。");
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
    try
    {
        using var failedPayload = JsonDocument.Parse(
            "{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"failed\",\"error\":{\"message\":\"Upstream request failed\"}}");
        _ = extractResponsesText.Invoke(null, [failedPayload.RootElement]);
        throw new InvalidOperationException("Responses failed 状态没有抛出明确错误。");
    }
    catch (TargetInvocationException exception)
        when (exception.InnerException?.Message.Contains("AI Responses API 返回失败：Upstream request failed", StringComparison.Ordinal) == true)
    {
        // Expected: failed Responses payloads should expose the upstream error directly.
    }

    RequireProjectFile("figure1.svg");
    RequireProjectFile("question.html");
    RequireFinalFile("custom-question-figure1.svg");
    RequireFinalFile("custom-question.json");
    RequireFinalFile("custom-question.tex");
    RequireFinalFile("custom-question.docx");
    RequireFinalFile("custom-question.pdf");
    if (File.Exists(Path.Combine(finalOutput, "question.html")) ||
        File.Exists(Path.Combine(finalOutput, "document.json")) ||
        File.Exists(Path.Combine(finalOutput, "review.json")))
        throw new InvalidOperationException("最终输出目录不应包含过渡文件。");

    XDocument.Parse(await File.ReadAllTextAsync(Path.Combine(finalOutput, "custom-question-figure1.svg")));
    using (var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(finalOutput, "custom-question.json"))))
    {
        if (!metadata.RootElement.TryGetProperty("LatexSymbolMap", out var symbolMap) ||
            !symbolMap.TryGetProperty(@"\customstar", out var customStar) ||
            customStar.GetString() != "★")
            throw new InvalidOperationException("题目级 LaTeX 符号映射未写入 metadata.json。");
    }

    using (var archive = ZipFile.OpenRead(Path.Combine(finalOutput, "custom-question.docx")))
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
        if (archive.GetEntry("word/media/question-figure-1.svg") is null)
            throw new InvalidOperationException("DOCX 缺少嵌入的 SVG 矢量图。");
        var relationshipEntry = archive.GetEntry("word/_rels/document.xml.rels")
            ?? throw new InvalidOperationException("DOCX 缺少正文关系文件。");
        if (!(await ReadEntryAsync(archive, "word/_rels/document.xml.rels")).Contains("relationships/image", StringComparison.Ordinal))
            throw new InvalidOperationException("DOCX 缺少 SVG 图形关系。");
        if (xml.Contains("angle ABC", StringComparison.Ordinal) ||
            xml.Contains("perp BE", StringComparison.Ordinal) ||
            xml.Contains("sqrt2", StringComparison.Ordinal) ||
            xml.Contains("(1)/(2)", StringComparison.Ordinal) ||
            xml.Contains("triangle DHF", StringComparison.Ordinal) ||
            !xml.Contains("∠ ABC", StringComparison.Ordinal) ||
            !xml.Contains("FG ⊥ BE", StringComparison.Ordinal) ||
            !xml.Contains("<m:rad>", StringComparison.Ordinal) ||
            !xml.Contains("<m:f>", StringComparison.Ordinal) ||
            !xml.Contains("△ DHF", StringComparison.Ordinal) ||
            xml.Contains(@"\sqrt", StringComparison.Ordinal) ||
            xml.Contains(@"\triangle", StringComparison.Ordinal))
            throw new InvalidOperationException("DOCX 正文中的裸 LaTeX/OCR 命令未能解析为数学符号或结构化公式。");
    }

    var html = await File.ReadAllTextAsync(Path.Combine(output, "question.html"));
    if (html.Contains("$x^2", StringComparison.Ordinal) ||
        !html.Contains("x&#178;+y&#178;=1", StringComparison.Ordinal) ||
        !html.Contains("<span class=\"formula\">", StringComparison.Ordinal))
        throw new InvalidOperationException("PDF/HTML 导出仍存在块级公式、原始 LaTeX 或公式间距风险。");
    var decodedHtml = WebUtility.HtmlDecode(html);
    if (decodedHtml.Contains(@"\triangle", StringComparison.Ordinal) ||
        decodedHtml.Contains(@"\mathrm", StringComparison.Ordinal) ||
        decodedHtml.Contains(@"\sqrt", StringComparison.Ordinal) ||
        decodedHtml.Contains(@"\customstar", StringComparison.Ordinal) ||
        decodedHtml.Contains(@"\widearc", StringComparison.Ordinal) ||
        !decodedHtml.Contains("△ ABC", StringComparison.Ordinal) ||
        !decodedHtml.Contains("AB⊥ CD", StringComparison.Ordinal) ||
        !decodedHtml.Contains("<msqrt>", StringComparison.Ordinal) ||
        !decodedHtml.Contains("∠ A+sinθ", StringComparison.Ordinal) ||
        !decodedHtml.Contains("★+⌒AB", StringComparison.Ordinal))
        throw new InvalidOperationException("LaTeX 常见命令未能解析为可读数学符号或结构化公式。");
    if (decodedHtml.Contains("angle ABC", StringComparison.Ordinal) ||
        decodedHtml.Contains("perp BE", StringComparison.Ordinal) ||
        decodedHtml.Contains("sqrt2", StringComparison.Ordinal) ||
        decodedHtml.Contains("(1)/(2)", StringComparison.Ordinal) ||
        decodedHtml.Contains("triangle DHF", StringComparison.Ordinal) ||
        !decodedHtml.Contains("∠ ABC", StringComparison.Ordinal) ||
        !decodedHtml.Contains("FG ⊥ BE", StringComparison.Ordinal) ||
        !decodedHtml.Contains("<msqrt>", StringComparison.Ordinal) ||
        !decodedHtml.Contains("<mfrac>", StringComparison.Ordinal) ||
        !decodedHtml.Contains("△ DHF", StringComparison.Ordinal))
        throw new InvalidOperationException("段落正文中的裸 LaTeX/OCR 命令未能解析为数学符号或结构化公式。");

    var pdfHeader = new byte[4];
    await using (var stream = File.OpenRead(Path.Combine(finalOutput, "custom-question.pdf")))
        _ = await stream.ReadAsync(pdfHeader);
    if (Encoding.ASCII.GetString(pdfHeader) != "%PDF")
        throw new InvalidOperationException("PDF 文件头无效。");

    Console.WriteLine("PASS: API routing, selected outputs, append Word, recovery, spinner, persistence and all exporters");
    return 0;

    void RequireProjectFile(string fileName)
    {
        var path = Path.Combine(output, fileName);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException($"缺少导出文件：{fileName}");
    }

    void RequireFinalFile(string fileName)
    {
        var path = Path.Combine(finalOutput, fileName);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException($"缺少最终导出文件：{fileName}");
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
    Environment.SetEnvironmentVariable("QUESTION_ORGANIZER_FIGURE_TOOL", previousFigureTool);
    Environment.SetEnvironmentVariable("QUESTION_ORGANIZER_GEOGEBRA_PATH", previousGeoGebraPath);
    Directory.Delete(output, true);
}

internal sealed class FakeAiProvider : IAiProvider
{
    public int RedrawCallCount { get; private set; }
    public int GeoGebraCallCount { get; private set; }

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
        Task.FromResult<IReadOnlyList<FigureDocument>>(OnRedrawFigures());

    private IReadOnlyList<FigureDocument> OnRedrawFigures()
    {
        RedrawCallCount++;
        return [];
    }

    public Task<IReadOnlyList<FigureDocument>> CreateGeoGebraFiguresAsync(
        string sourcePath,
        QuestionDocument document,
        string additionalInstructions,
        CancellationToken cancellationToken = default)
    {
        GeoGebraCallCount++;
        return Task.FromResult<IReadOnlyList<FigureDocument>>([
            new FigureDocument
            {
                Id = "figure1",
                Description = "测试 GeoGebra 图形",
                GeoGebraCommands =
                [
                    "A=(0,0)",
                    "B=(4,0)",
                    "Segment(A,B)",
                    "Text(\"A\",A+(-0.2,0.2))",
                    "Text(\"B\",B+(0.2,0.2))"
                ]
            }
        ]);
    }

    public Task<OutputReviewResult> ReviewOutputsAsync(
        string sourcePath,
        QuestionDocument document,
        IReadOnlyDictionary<string, string> generatedFiles,
        string additionalInstructions,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new OutputReviewResult
        {
            Passed = true,
            Summary = $"检查了 {generatedFiles.Count} 个生成文件。",
            Issues = []
        });
}
