using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EaxmBuilder.Core;

namespace EaxmBuilder.AI;

public sealed class OpenAiProvider : IAiProvider, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new QuestionBlockTypeJsonConverter(),
            new JsonStringEnumConverter()
        }
    };

    private readonly HttpClient _client;
    private readonly string _model;
    private readonly bool _useResponsesApi;

    public OpenAiProvider(AiProviderKind kind, string baseUrl, string model, string apiKey)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri))
            throw new ArgumentException("Base URL 格式不正确。", nameof(baseUrl));
        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            throw new ArgumentException("Base URL 必须使用 HTTPS，本机地址除外。", nameof(baseUrl));

        _client = new HttpClient { BaseAddress = uri, Timeout = RequestTimeout };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("QuestionOrganizer/1.0");
        _model = model;
        _useResponsesApi = kind == AiProviderKind.OpenAi;
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync("models", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<OcrResult> RecognizeTextAsync(
        string sourcePath,
        string additionalInstructions,
        CancellationToken cancellationToken = default)
    {
        var prompt = """
            你是数学试题 OCR 引擎。逐字识别图片或 PDF 中的题干、选项、标点和公式，保持原顺序。
            不要求解，不要改写。公式暂时用 LaTeX 表示。
            仅返回 JSON：{"rawText":"...","language":"zh-CN"}
            """ + FormatAdditionalInstructions(additionalInstructions);
        return RequestJsonAsync<OcrResult>(sourcePath, prompt, cancellationToken);
    }

    public async Task<QuestionDocument> StructureQuestionAsync(
        string sourcePath,
        OcrResult ocr,
        string additionalInstructions,
        CancellationToken cancellationToken = default)
    {
        var prompt = $$$"""
            将下面 OCR 文本整理为结构化数学题目。对照原图修正明显识别错误，但不要求解或补写内容。
            整理目标是把这道数学题用 LaTeX 公式整理到 Word 文档中：
            正文最终排版为宋体、五号、不加粗、二倍行距、段落前后无空格。
            你只负责把文字、公式、图形占位整理成结构化内容；不要把这些排版要求写入题目正文。
            普通文字使用 Paragraph；每个独立或行内数学表达式都必须拆成 Formula 块，并在 latex 中给出规范 LaTeX；
            Paragraph 的 text 中不要残留 $...$、\(...\)、\sqrt、\frac、\angle、\triangle、_、^ 等 LaTeX/公式代码；
            如果一句话中穿插公式，请按 Paragraph + Formula + Paragraph + Formula 的顺序拆开，保持原文顺序；
            原图中存在几何图、坐标图、函数图、示意图、绳结图或依赖图像的选项时，在对应位置添加 Figure 块，
            并分配 figureId（figure1 起）。题干目标图和每个图像选项必须拆成独立 Figure：
            先放题干文字，再放目标图；每个选项按 Paragraph("A.") + Figure 的形式排列。
            不要把多个选项或目标图合并为一张 SVG，必须保留题目作答所需的全部图形。
            如果 latex 中使用了非常用、自定义或你不确定软件能否识别的 LaTeX 命令，
            必须在顶层 latexSymbolMap 中补充该命令的显示映射，键使用带反斜杠的命令名，值使用最终应显示的 Unicode 符号或普通文字。
            如果该命令带一个参数，值里可以用 #1 表示解析后的参数，例如 {"\\widearc":"⌒#1"}。
            figures 暂时返回空数组。仅返回符合下列形状的 JSON：
            {"schemaVersion":"1.0","title":"","questionNumber":"","language":"zh-CN","latexSymbolMap":{"\\custom":"显示符号"},"blocks":[{"type":"Paragraph","text":"","latex":"","figureId":""}],"figures":[]}

            OCR 文本：
            {{ocr.RawText}}
            """ + FormatAdditionalInstructions(additionalInstructions);
        var document = await RequestJsonAsync<QuestionDocument>(sourcePath, prompt, cancellationToken);
        QuestionDocumentNormalizer.NormalizeLatexSymbolMap(document);
        return document;
    }

    public async Task<IReadOnlyList<FigureDocument>> RedrawFiguresAsync(
        string sourcePath,
        QuestionDocument document,
        string additionalInstructions,
        CancellationToken cancellationToken = default)
    {
        if (document.Blocks.All(block => block.Type != QuestionBlockType.Figure)) return [];

        var ids = document.Blocks
            .Where(block => block.Type == QuestionBlockType.Figure)
            .Select(block => block.FigureId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var figures = new List<FigureDocument>();
        var failures = new List<string>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                figures.Add(await RedrawSingleFigureAsync(sourcePath, document, id, additionalInstructions, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add($"{id}: {exception.Message}");
            }
        }
        if (figures.Count == 0 && failures.Count > 0)
            throw new InvalidOperationException(string.Join("；", failures));
        return figures;
    }

    public async Task<IReadOnlyList<FigureDocument>> CreateGeoGebraFiguresAsync(
        string sourcePath,
        QuestionDocument document,
        string additionalInstructions,
        CancellationToken cancellationToken = default)
    {
        if (document.Blocks.All(block => block.Type != QuestionBlockType.Figure)) return [];

        var ids = document.Blocks
            .Where(block => block.Type == QuestionBlockType.Figure)
            .Select(block => block.FigureId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var figures = new List<FigureDocument>();
        var failures = new List<string>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                figures.Add(await CreateSingleGeoGebraFigureAsync(sourcePath, document, id, additionalInstructions, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add($"{id}: {exception.Message}");
            }
        }
        if (figures.Count == 0 && failures.Count > 0)
            throw new InvalidOperationException(string.Join("；", failures));
        return figures;
    }

    public Task<OutputReviewResult> ReviewOutputsAsync(
        string sourcePath,
        QuestionDocument document,
        IReadOnlyDictionary<string, string> generatedFiles,
        string additionalInstructions,
        CancellationToken cancellationToken = default)
    {
        var documentJson = JsonSerializer.Serialize(document, JsonOptions);
        var generatedText = string.Join("\n\n", generatedFiles.Select(item =>
            $"--- {item.Key} ---\n{TrimForPrompt(item.Value, 12_000)}"));
        var prompt = $$"""
            你是数学题目整理软件的质量检查 AI。请对照原始题目图片，检查软件生成的结构化内容和导出底稿是否有错误。

            重点检查：
            1. 题干、选项、编号、标点、数学公式是否与原图一致；
            2. LaTeX 是否仍残留为未解析代码，或需要补充 latexSymbolMap；
            3. 图形 SVG 是否缺失、错位、文字字体不符合宋体要求；如果图形 description 标明“原图保留”，不要把嵌入原图本身判为错误；
            4. Word/PDF/HTML/LaTeX 输出底稿是否有明显排版风险。

            如果发现错误，请在 correctedDocument 中返回完整修正后的 QuestionDocument；
            如果只需要新增符号解析，也请把 latexSymbolMap 合并到 correctedDocument。
            不要新增题目没有的答案或解析，不要改写题意。
            仅返回 JSON 对象，字段必须包括 passed、summary、issues、correctedDocument。
            通过时 correctedDocument 为 null；需要修正时 correctedDocument 为完整 QuestionDocument。

            当前结构化文档：
            {{{documentJson}}}

            已生成文件文本摘要：
            {{{generatedText}}}
            """ + FormatAdditionalInstructions(additionalInstructions);
        return RequestJsonAsync<OutputReviewResult>(sourcePath, prompt, cancellationToken);
    }

    private async Task<FigureDocument> RedrawSingleFigureAsync(
        string sourcePath,
        QuestionDocument document,
        string figureId,
        string additionalInstructions,
        CancellationToken cancellationToken)
    {
        var figureMap = DescribeFigureMap(document);
        var prompt = $$"""
            精确观察原图中的数学图形，并只为编号 {{figureId}} 重新绘制一张干净的 SVG。
            请像使用图像生成工具重新绘制图片一样生成矢量图：只保留题目需要的图形内容，不要截图、不要描摹成位图、不要嵌入原图。
            题目中的图形编号对应关系如下：
            {{figureMap}}

            保留点名、线型、角标、箭头、坐标轴及相对位置。不要把原图嵌入 SVG。
            只绘制 {{figureId}} 对应的几何图/函数图本体和图内点名、图号；不要绘制题干正文、问号、LaTeX 公式、条件说明或小问文字。
            图内点名必须避开线段和交点，优先放在线段外侧；禁止给文字加白底、白色描边或白色遮罩来盖住线段。
            如果标注会压线，请移动标注位置，不要用背景块解决。
            不要把数学符号替换成普通英文描述，也不要把题干里的公式集中绘制到 SVG 末尾。
            如果是绳结或交叉线图，必须保留拓扑关系：线条的每一次交叉都要通过断开、留白或白色遮罩表达上下穿过，
            不能把交叉画成普通相交线，不能省略局部弧线。
            如果是图像选项，只绘制该编号对应的一幅图，不要混入其他选项或题干目标图。
            SVG 必须使用 viewBox，白色或透明背景，图像中的全部文字和标注都使用宋体（font-family="SimSun, 宋体, serif"），
            线条使用 #202020，禁止脚本和外链。
            viewBox 与 path 中的所有数字必须逐个使用英文逗号分隔，即使删除全部空格后也必须保持有效。
            正确示例：viewBox="0,0,260,220"、d="M78,84C88,25,126,10,158,26"。
            错误示例：viewBox="00260220"、d="M7884C88251261015826"。绝不能把相邻数字粘连。
            SVG 中不要绘制 A、B、C、D 等选项标签，
            选项标签由文档排版引擎添加。
            仅返回 JSON：{"figures":[{"id":"{{figureId}}","description":"简短说明","svg":"<svg ...>...</svg>"}]}
            """ + FormatAdditionalInstructions(additionalInstructions);
        var result = await RequestJsonAsync<FigureEnvelope>(sourcePath, prompt, cancellationToken);
        var figure = result.Figures.FirstOrDefault(item =>
                         string.Equals(item.Id, figureId, StringComparison.OrdinalIgnoreCase))
                     ?? result.Figures.SingleOrDefault();
        if (figure is null || string.IsNullOrWhiteSpace(figure.Svg))
            throw new InvalidOperationException($"AI 未返回 {figureId} 的 SVG。");
        figure.Id = figureId;
        return figure;
    }

    private async Task<FigureDocument> CreateSingleGeoGebraFigureAsync(
        string sourcePath,
        QuestionDocument document,
        string figureId,
        string additionalInstructions,
        CancellationToken cancellationToken)
    {
        var figureMap = DescribeFigureMap(document);
        var questionContext = DescribeQuestionContext(document);
        var prompt = $$"""
            精确观察原图中的数学图形，并只为编号 {{figureId}} 生成 GeoGebra 绘图命令。
            题目中的图形编号对应关系如下：
            {{figureMap}}

            题目文字和公式上下文如下。绘图必须同时满足原图视觉位置和这些几何约束：
            {{questionContext}}

            只输出该图所需的二维 GeoGebra 命令，命令会被本地内嵌 GeoGebra 执行。
            要求：
            1. 默认只用 Segment 绘制有限线段，禁止把有限线段写成 Line 或 Ray；只有原图明确是无限直线时才可使用 Line；
            2. 禁止使用 Angle 命令自动生成角度标注；除非原图明确画了角弧或角度数值，否则不要标角；
            3. 直角标记优先用 Polyline(P1,P2,P3) 或 2 到 3 条短 Segment 手动画成方角；只有明确需要闭合小方框时才用四点 Polygon，不要用 Angle(A,B,C) 生成圆弧角标；
            4. 端点只作为构造点存在，不要依赖 GeoGebra 自动显示点标签或明显圆点；点名必须用 Text("A", A + (-0.2,0.2)) 手动标注；
            5. 所有线条和文字最终会被统一成黑色，请不要使用彩色样式；
            6. 点名、线段、垂线、角平分线、坐标轴、标注位置要尽量贴近原图；
            7. 每个可见交点或端点必须只定义一次命名点。所有经过该点的线段必须引用同一个点对象，例如 Segment(G,D)、Segment(G,F)，不要用相近坐标伪造连接；
            8. 同一直线上的点必须严格共线，例如 E 在 AD 上、H/F 在 BC 上时应使用相同的 y 坐标；竖线上的点应使用相同的 x 坐标；
            9. 如果题干说明垂直、角平分线、交于某点或连接某两点，必须优先满足这些拓扑关系，再微调坐标比例；
            10. 对矩形/平行四边形等基础图形，先定义外框点，再定义边上点、交点，最后用 Segment(已有点,已有点) 绘制全部可见线段；
            11. 坐标只用于保持原图的相对空间位置：建议把原图宽度映射到 x=0..10、高度映射到 y=0..6；不要为了美观移动点、改变线段长度、交换左右位置或上下关系；
            12. 先在内部完成“点坐标表 → 线段清单”的两遍检查，再输出命令。线段端点必须优先引用点坐标表中的命名点，禁止在 Segment 中重新写近似坐标；
            13. 输出前自查：原图中每一条可见线段都要有对应 Segment；所有应相交/连接的位置必须共享同一命名点；不要出现线段差一点没接上的情况；
            14. 不要输出 SVG、不要输出解释、不要输出 Markdown。

            仅返回 JSON：
            {"figures":[{"id":"{{figureId}}","description":"简短说明","geoGebraCommands":["A=(0,3)","B=(0,0)","Segment(A,B)"]}]}
            """ + FormatAdditionalInstructions(additionalInstructions);
        var result = await RequestJsonAsync<FigureEnvelope>(sourcePath, prompt, cancellationToken);
        var figure = result.Figures.FirstOrDefault(item =>
                         string.Equals(item.Id, figureId, StringComparison.OrdinalIgnoreCase))
                     ?? result.Figures.SingleOrDefault();
        if (figure is null || figure.GeoGebraCommands.Count == 0)
            throw new InvalidOperationException($"AI 未返回 {figureId} 的 GeoGebra 命令。");
        figure.Id = figureId;
        return figure;
    }

    private static string DescribeFigureMap(QuestionDocument document)
    {
        var lines = new List<string>();
        for (var index = 0; index < document.Blocks.Count; index++)
        {
            var block = document.Blocks[index];
            if (block.Type != QuestionBlockType.Figure) continue;
            var label = index > 0 && document.Blocks[index - 1].Type == QuestionBlockType.Paragraph
                ? document.Blocks[index - 1].Text.Trim()
                : string.Empty;
            var role = string.IsNullOrWhiteSpace(label) || !char.IsAsciiLetter(label[0])
                ? "题干目标图"
                : $"选项 {label}";
            lines.Add($"{block.FigureId}: {role}");
        }
        return lines.Count == 0 ? "无" : string.Join("\n", lines);
    }

    private static string DescribeQuestionContext(QuestionDocument document)
    {
        var lines = document.Blocks
            .Where(block => block.Type is QuestionBlockType.Paragraph or QuestionBlockType.Formula)
            .Select(block => block.Type == QuestionBlockType.Formula ? block.Latex : block.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text.Trim())
            .ToArray();
        return lines.Length == 0 ? "无" : TrimForPrompt(string.Join("\n", lines), 8_000);
    }

    private static string FormatAdditionalInstructions(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return $"\n\n当前项目的附加要求如下。只在不改变输出 JSON 结构的前提下执行：\n{value.Trim()}";
    }

    private static string TrimForPrompt(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "\n...（已截断）";

    private async Task<T> RequestJsonAsync<T>(
        string sourcePath,
        string prompt,
        CancellationToken cancellationToken)
    {
        var dataUrl = await GetDataUrlAsync(sourcePath, cancellationToken);
        string content;
        try
        {
            if (_useResponsesApi)
            {
                try
                {
                    content = await RequestResponsesTextAsync(
                        CreateResponsesRequest(sourcePath, dataUrl, prompt),
                        cancellationToken);
                }
                catch (ResponsesApiException exception) when (CanFallbackToChat(sourcePath, exception))
                {
                    content = await RequestChatTextAsync(
                        CreateChatRequest(sourcePath, dataUrl, prompt),
                        cancellationToken);
                }
            }
            else
            {
                content = await RequestChatTextAsync(
                    CreateChatRequest(sourcePath, dataUrl, prompt),
                    cancellationToken);
            }
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"AI 请求超过 {RequestTimeout.TotalMinutes:0} 分钟仍未完成。可以稍后单独重试本步骤，或换用响应更快的模型。",
                exception);
        }
        try
        {
            return DeserializeJsonContent<T>(content);
        }
        catch (InvalidJsonResponseException exception)
        {
            var savedPath = await SaveInvalidJsonResponseAsync(sourcePath, content, cancellationToken);
            try
            {
                return await RepairJsonResponseAsync<T>(content, cancellationToken);
            }
            catch (Exception repairException) when (repairException is not OperationCanceledException)
            {
                var parseDetail = exception.InnerException is null
                    ? string.Empty
                    : $" 原因：{exception.InnerException.Message}";
                throw new InvalidOperationException(
                    $"{exception.Message}{parseDetail} 已保存原始响应：{savedPath}。软件已自动尝试 JSON 修复但仍失败。",
                    repairException);
            }
        }
    }

    private static bool CanFallbackToChat(string sourcePath, ResponsesApiException exception) =>
        exception.AllowChatFallback &&
        !Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private object CreateResponsesRequest(string sourcePath, string dataUrl, string prompt)
    {
        object attachment = Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? new { type = "input_file", filename = Path.GetFileName(sourcePath), file_data = dataUrl, detail = "high" }
            : new { type = "input_image", image_url = dataUrl, detail = "high" };
        var input = new[]
        {
            new
            {
                role = "user",
                content = new object[] { new { type = "input_text", text = prompt }, attachment }
            }
        };
        var request = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["input"] = input,
            ["store"] = false,
            ["stream"] = true,
            ["max_output_tokens"] = 16_384,
            ["reasoning"] = new { effort = "xhigh", summary = "auto" },
            ["text"] = new { format = new { type = "json_object" } }
        };
        return request;
    }

    private async Task<string> RequestResponsesTextAsync(object request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "responses")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        using var response = await _client.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) == true ||
            body.TrimStart().StartsWith("data:", StringComparison.Ordinal))
            return ExtractResponsesStreamText(body);

        using var payload = JsonDocument.Parse(body);
        return ExtractResponsesText(payload.RootElement);
    }

    private async Task<string> RequestChatTextAsync(object request, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync("chat/completions", request, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        return ExtractChatText(payload);
    }

    private static string ExtractResponsesStreamText(string eventStream)
    {
        var text = new StringBuilder();
        JsonElement completedResponse = default;
        var hasCompletedResponse = false;
        using var reader = new StringReader(eventStream);
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;

            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;
            var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            if (type == "response.output_text.delta" && TryReadString(root, "delta", out var delta))
            {
                text.Append(delta);
            }
            else if (type == "response.output_text.done" && text.Length == 0 &&
                     TryReadString(root, "text", out var finalText))
            {
                text.Append(finalText);
            }
            else if (type is "response.completed" or "response.failed" or "response.incomplete" &&
                     root.TryGetProperty("response", out var responseValue))
            {
                completedResponse = responseValue.Clone();
                hasCompletedResponse = true;
            }
        }

        if (text.Length > 0) return text.ToString();
        if (hasCompletedResponse) return ExtractResponsesText(completedResponse);
        throw new ResponsesApiException("AI 流式响应中没有最终文本。", allowChatFallback: true);
    }

    private object CreateChatRequest(string sourcePath, string dataUrl, string prompt)
    {
        if (Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("当前兼容接口仅支持 PNG/JPG；PDF 需要支持 Responses API 的 OpenAI 提供商。");

        return new
        {
            model = _model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = dataUrl, detail = "high" } }
                    }
                }
            },
            response_format = new { type = "json_object" },
            max_tokens = 16_384
        };
    }

    private object CreateJsonRepairRequest(string content) => new
    {
        model = _model,
        messages = new[]
        {
            new
            {
                role = "user",
                content = $$"""
                    下面是一段 AI 对数学题整理任务的原始响应，但它不是严格合法 JSON。
                    请只输出一个语法有效的 JSON 对象，不要解释，不要 Markdown，不要代码块。
                    要保持原响应中的字段、题目文字、公式、图形信息和语义；不要新增答案或解析。
                    如果原响应前后有说明文字，请删除说明文字；如果只有少量逗号、引号、转义或括号错误，请修复。

                    原始响应：
                    {{content}}
                    """
            }
        },
        response_format = new { type = "json_object" },
        max_tokens = 16_384
    };

    private async Task<T> RepairJsonResponseAsync<T>(string content, CancellationToken cancellationToken)
    {
        var repaired = await RequestChatTextAsync(CreateJsonRepairRequest(TrimForPrompt(content, 60_000)), cancellationToken);
        return DeserializeJsonContent<T>(repaired);
    }

    private static async Task<string> SaveInvalidJsonResponseAsync(
        string sourcePath,
        string content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            directory = Directory.GetCurrentDirectory();
        var path = Path.Combine(directory, $"ai-invalid-json-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);
        return path;
    }

    private static async Task<string> GetDataUrlAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var mediaType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".pdf" => "application/pdf",
            _ => throw new NotSupportedException("仅支持 PNG、JPG 和 PDF 文件。")
        };
        return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string ExtractResponsesText(JsonElement payload)
    {
        if (TryReadContent(payload, out var directText)) return directText;
        foreach (var envelopeName in new[] { "response", "data", "result" })
        {
            if (payload.TryGetProperty(envelopeName, out var envelope) &&
                envelope.ValueKind == JsonValueKind.Object &&
                TryReadContent(envelope, out var envelopeText))
                return envelopeText;
        }
        if (payload.TryGetProperty("output", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var output in outputs.EnumerateArray().Where(IsFinalOutputItem))
            {
                ThrowIfRefused(output);
                if (TryReadContent(output, out var outputText)) return outputText;
            }
            foreach (var output in outputs.EnumerateArray())
            {
                ThrowIfRefused(output);
                if (TryReadContent(output, out var outputText) && LooksLikeJson(outputText)) return outputText;
            }
        }
        if (payload.TryGetProperty("choices", out _)) return ExtractChatText(payload);

        var status = payload.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
        var detail = ReadResponseDetail(payload);
        if (status is "failed" or "incomplete")
            throw new ResponsesApiException(
                $"AI Responses API 返回{(status == "failed" ? "失败" : "未完成")}{detail}。",
                allowChatFallback: status == "failed");
        throw new InvalidOperationException(
            $"AI 响应中没有可读取的文本{(string.IsNullOrWhiteSpace(status) ? string.Empty : $"（状态：{status}）")}{detail}" +
            $"（可用字段：{DescribeResponsesShape(payload)}）。");
    }

    private static bool IsFinalOutputItem(JsonElement output)
    {
        if (!TryReadString(output, "type", out var type)) return true;
        return type.Equals("message", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("output_text", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("text", StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowIfRefused(JsonElement output)
    {
        if (!output.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var part in content.EnumerateArray())
        {
            if (TryReadString(part, "refusal", out var refusal))
                throw new InvalidOperationException($"AI 拒绝了本次请求：{refusal}");
        }
    }

    private static string DescribeResponsesShape(JsonElement payload)
    {
        var description = DescribeFields(payload);
        if (payload.TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Array && output.GetArrayLength() > 0)
            description += $"; output[0]: {DescribeFields(output[0])}";
        return description;
    }

    private static string ExtractChatText(JsonElement payload)
    {
        if (TryReadContent(payload, out var topLevelText)) return topLevelText;
        foreach (var envelopeName in new[] { "response", "data", "result" })
        {
            if (payload.TryGetProperty(envelopeName, out var envelope) &&
                envelope.ValueKind == JsonValueKind.Object &&
                TryReadContent(envelope, out var envelopeText))
                return envelopeText;
        }

        if (!payload.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidOperationException(
                $"兼容 AI 响应中缺少 choices（可用字段：{DescribeFields(payload)}）。");

        var choice = choices[0];
        if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            if (TryReadContent(message, out var messageText)) return messageText;
            if (TryReadString(message, "reasoning_content", out var reasoning) && LooksLikeJson(reasoning))
                return StripCodeFence(reasoning);
        }
        if (choice.TryGetProperty("delta", out var delta) &&
            delta.ValueKind == JsonValueKind.Object &&
            TryReadContent(delta, out var deltaText))
            return deltaText;
        if (TryReadContent(choice, out var choiceText)) return choiceText;

        var finishReason = choice.TryGetProperty("finish_reason", out var finish) ? finish.GetString() : null;
        throw new InvalidOperationException(
            $"兼容 AI 响应没有最终文本{(string.IsNullOrWhiteSpace(finishReason) ? string.Empty : $"（结束原因：{finishReason}）")}" +
            $"（可用字段：{DescribeFields(choice)}）。");
    }

    private static bool TryReadContent(JsonElement value, out string result)
    {
        result = string.Empty;
        if (value.ValueKind == JsonValueKind.String)
        {
            result = value.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(result);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (TryReadContent(item, out var part)) parts.Add(part);
            }
            result = string.Join("\n", parts);
            return parts.Count > 0;
        }
        if (value.ValueKind != JsonValueKind.Object) return false;

        foreach (var propertyName in new[]
                 { "output_text", "content", "text", "answer", "value", "message", "final_output", "completion", "parsed", "json" })
        {
            if (!value.TryGetProperty(propertyName, out var property)) continue;
            if (TryReadContent(property, out result)) return true;
            if (property.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                result = property.GetRawText();
                return true;
            }
        }
        return false;
    }

    private static bool LooksLikeJson(string value)
    {
        var candidate = StripCodeFence(value).Trim();
        return (candidate.StartsWith('{') && candidate.EndsWith('}')) ||
               TryExtractJsonObject(candidate, out _);
    }

    private static T DeserializeJsonContent<T>(string value)
    {
        var primary = StripCodeFence(value);
        var candidates = new List<string> { primary };
        if (TryExtractJsonObject(primary, out var extracted) &&
            !string.Equals(primary, extracted, StringComparison.Ordinal))
            candidates.Add(extracted);

        JsonException? lastException = null;
        foreach (var candidate in candidates)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(candidate, JsonOptions)
                       ?? throw new InvalidOperationException("AI 返回了空 JSON。");
            }
            catch (JsonException exception)
            {
                lastException = exception;
            }
        }

        throw new InvalidJsonResponseException(
            "AI 返回的结构化数据无法解析。已尝试从响应中抽取完整 JSON，请重试本步骤；如果仍失败，请在 AI 要求中写明“只返回 JSON，不要解释”。",
            value,
            lastException);
    }

    private static bool TryExtractJsonObject(string value, out string json)
    {
        json = string.Empty;
        var text = StripCodeFence(value);
        var start = text.IndexOf('{');
        if (start < 0) return false;

        var depth = 0;
        var inString = false;
        var escaping = false;
        for (var index = start; index < text.Length; index++)
        {
            var current = text[index];
            if (inString)
            {
                if (escaping)
                {
                    escaping = false;
                }
                else if (current == '\\')
                {
                    escaping = true;
                }
                else if (current == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }
            if (current == '{')
            {
                depth++;
                continue;
            }
            if (current != '}') continue;

            depth--;
            if (depth != 0) continue;
            json = text[start..(index + 1)].Trim();
            return true;
        }
        return false;
    }

    private static string DescribeFields(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return value.ValueKind.ToString();
        var fields = value.EnumerateObject().Select(property => property.Name).Take(12).ToArray();
        return fields.Length == 0 ? "无" : string.Join(", ", fields);
    }

    private static bool TryReadString(JsonElement value, string propertyName, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        result = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(result);
    }

    private static string ReadResponseDetail(JsonElement payload)
    {
        if (payload.TryGetProperty("error", out var error) && TryReadString(error, "message", out var message))
            return $"：{message}";
        if (payload.TryGetProperty("incomplete_details", out var details) &&
            TryReadString(details, "reason", out var reason))
            return $"：{reason}";
        return string.Empty;
    }

    private static string StripCodeFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstLine = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine >= 0 && lastFence > firstLine
            ? trimmed[(firstLine + 1)..lastFence].Trim()
            : trimmed;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = $"AI 服务返回 {(int)response.StatusCode} {response.ReasonPhrase}";
        try
        {
            var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var detail))
                message += $"：{detail.GetString()}";
        }
        catch (JsonException)
        {
            // Keep the status-based message when the provider returns non-JSON content.
        }
        throw new HttpRequestException(message);
    }

    public void Dispose() => _client.Dispose();

    private sealed class FigureEnvelope
    {
        public List<FigureDocument> Figures { get; set; } = [];
    }

    private sealed class ResponsesApiException(string message, bool allowChatFallback)
        : InvalidOperationException(message)
    {
        public bool AllowChatFallback { get; } = allowChatFallback;
    }

    private sealed class InvalidJsonResponseException(
        string message,
        string content,
        Exception? innerException)
        : InvalidOperationException(message, innerException)
    {
        public string Content { get; } = content;
    }
}
