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
        Converters = { new JsonStringEnumConverter() }
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

    public Task<QuestionDocument> StructureQuestionAsync(
        string sourcePath,
        OcrResult ocr,
        string additionalInstructions,
        CancellationToken cancellationToken = default)
    {
        var prompt = $$"""
            将下面 OCR 文本整理为结构化数学题目。对照原图修正明显识别错误，但不要求解或补写内容。
            整理目标是把这道数学题用 LaTeX 公式整理到 Word 文档中：
            正文最终排版为宋体、五号、不加粗、二倍行距、段落前后无空格。
            你只负责把文字、公式、图形占位整理成结构化内容；不要把这些排版要求写入题目正文。
            普通文字使用 Paragraph；每个独立或行内数学表达式使用 Formula，并在 latex 中给出规范 LaTeX；
            原图中存在几何图、坐标图、函数图、示意图、绳结图或依赖图像的选项时，在对应位置添加 Figure 块，
            并分配 figureId（figure1 起）。题干目标图和每个图像选项必须拆成独立 Figure：
            先放题干文字，再放目标图；每个选项按 Paragraph("A.") + Figure 的形式排列。
            不要把多个选项或目标图合并为一张 SVG，必须保留题目作答所需的全部图形。
            figures 暂时返回空数组。仅返回符合下列形状的 JSON：
            {"schemaVersion":"1.0","title":"","questionNumber":"","language":"zh-CN","blocks":[{"type":"Paragraph","text":"","latex":"","figureId":""}],"figures":[]}

            OCR 文本：
            {{ocr.RawText}}
            """ + FormatAdditionalInstructions(additionalInstructions);
        return RequestJsonAsync<QuestionDocument>(sourcePath, prompt, cancellationToken);
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
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            figures.Add(await RedrawSingleFigureAsync(sourcePath, document, id, additionalInstructions, cancellationToken));
        }
        return figures;
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

    private static string FormatAdditionalInstructions(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return $"\n\n当前项目的附加要求如下。只在不改变输出 JSON 结构的前提下执行：\n{value.Trim()}";
    }

    private async Task<T> RequestJsonAsync<T>(
        string sourcePath,
        string prompt,
        CancellationToken cancellationToken)
    {
        var dataUrl = await GetDataUrlAsync(sourcePath, cancellationToken);
        object request = _useResponsesApi
            ? CreateResponsesRequest(sourcePath, dataUrl, prompt)
            : CreateChatRequest(sourcePath, dataUrl, prompt);
        var endpoint = _useResponsesApi ? "responses" : "chat/completions";

        string content;
        try
        {
            if (_useResponsesApi)
            {
                content = await RequestResponsesTextAsync(request, cancellationToken);
            }
            else
            {
                using var response = await _client.PostAsJsonAsync(endpoint, request, JsonOptions, cancellationToken);
                await EnsureSuccessAsync(response, cancellationToken);
                var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                content = ExtractChatText(payload);
            }
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"AI 请求超过 {RequestTimeout.TotalMinutes:0} 分钟仍未完成。可以稍后单独重试本步骤，或换用响应更快的模型。",
                exception);
        }
        content = StripCodeFence(content);

        try
        {
            return JsonSerializer.Deserialize<T>(content, JsonOptions)
                   ?? throw new InvalidOperationException("AI 返回了空 JSON。");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("AI 返回的结构化数据无法解析。", exception);
        }
    }

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
        throw new InvalidOperationException("AI 流式响应中没有最终文本。");
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
                 { "output_text", "content", "text", "answer", "value", "message", "final_output", "completion" })
        {
            if (value.TryGetProperty(propertyName, out var property) &&
                TryReadContent(property, out result))
                return true;
        }
        return false;
    }

    private static bool LooksLikeJson(string value)
    {
        var candidate = StripCodeFence(value).Trim();
        return candidate.StartsWith('{') && candidate.EndsWith('}');
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
}
