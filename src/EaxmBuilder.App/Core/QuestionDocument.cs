using System.Text.Json;
using System.Text.Json.Serialization;

namespace EaxmBuilder.Core;

[JsonConverter(typeof(QuestionBlockTypeJsonConverter))]
public enum QuestionBlockType
{
    Paragraph,
    Formula,
    Figure
}

public sealed class QuestionBlockTypeJsonConverter : JsonConverter<QuestionBlockType>
{
    public override QuestionBlockType Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numericValue) &&
            Enum.IsDefined(typeof(QuestionBlockType), numericValue))
            return (QuestionBlockType)numericValue;

        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"题目区块 Type 必须是字符串或数字，实际为 {reader.TokenType}。");

        var value = reader.GetString()?.Trim();
        return value?.ToLowerInvariant() switch
        {
            "paragraph" or "text" or "question" or "题干" or "文本" => QuestionBlockType.Paragraph,
            "formula" or "equation" or "math" or "公式" or "数学公式" => QuestionBlockType.Formula,
            "figure" or "figureref" or "figure_ref" or "image" or "diagram" or "图形" or "图引用" =>
                QuestionBlockType.Figure,
            _ => throw new JsonException($"无法识别题目区块 Type：{value}")
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        QuestionBlockType value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

public sealed class QuestionDocument
{
    public string SchemaVersion { get; set; } = "1.0";
    public string Title { get; set; } = string.Empty;
    public string QuestionNumber { get; set; } = string.Empty;
    public string Language { get; set; } = "zh-CN";
    public Dictionary<string, string> LatexSymbolMap { get; set; } = [];
    public List<QuestionBlock> Blocks { get; set; } = [];
    public List<FigureDocument> Figures { get; set; } = [];
}

public static class QuestionDocumentNormalizer
{
    public static void NormalizeLatexSymbolMap(QuestionDocument document)
    {
        if (document.LatexSymbolMap.Count == 0) return;

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in document.LatexSymbolMap)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value)) continue;

            var key = item.Key.Trim();
            var value = item.Value.Trim();
            if (!key.StartsWith('\\') && value.StartsWith('\\'))
                (key, value) = (NormalizeCommand(value), key);
            else
                key = NormalizeCommand(key);

            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith('\\') || string.IsNullOrWhiteSpace(value)) continue;
            normalized[key] = value;
        }

        document.LatexSymbolMap = normalized;
    }

    private static string NormalizeCommand(string value)
    {
        var command = value.Trim();
        if (!command.StartsWith('\\')) command = "\\" + command;
        var braceIndex = command.IndexOf('{', StringComparison.Ordinal);
        return braceIndex > 0 ? command[..braceIndex] : command;
    }
}

public sealed class QuestionBlock
{
    public QuestionBlockType Type { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Latex { get; set; } = string.Empty;
    public string FigureId { get; set; } = string.Empty;
}

public sealed class FigureDocument
{
    public string Id { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Svg { get; set; } = string.Empty;
    public List<string> GeoGebraCommands { get; set; } = [];
}

public sealed class OcrResult
{
    public string RawText { get; set; } = string.Empty;
    public string Language { get; set; } = "zh-CN";
}

public sealed class OutputReviewResult
{
    public bool Passed { get; set; } = true;
    public string Summary { get; set; } = string.Empty;
    public List<OutputReviewIssue> Issues { get; set; } = [];
    public QuestionDocument? CorrectedDocument { get; set; }
}

[JsonConverter(typeof(OutputReviewIssueJsonConverter))]
public sealed class OutputReviewIssue
{
    public string Severity { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Correction { get; set; } = string.Empty;
}

public sealed class OutputReviewIssueJsonConverter : JsonConverter<OutputReviewIssue>
{
    public override OutputReviewIssue Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new OutputReviewIssue { Description = reader.GetString() ?? string.Empty };

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            using var value = JsonDocument.ParseValue(ref reader);
            return new OutputReviewIssue { Description = value.RootElement.GetRawText() };
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        return new OutputReviewIssue
        {
            Severity = ReadString(root, "severity"),
            Location = ReadString(root, "location"),
            Description = FirstString(root, "description", "message"),
            Correction = ReadString(root, "correction")
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        OutputReviewIssue value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("severity", value.Severity);
        writer.WriteString("location", value.Location);
        writer.WriteString("description", value.Description);
        writer.WriteString("correction", value.Correction);
        writer.WriteEndObject();
    }

    private static string ReadString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static string FirstString(JsonElement value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var result = ReadString(value, propertyName);
            if (!string.IsNullOrWhiteSpace(result)) return result;
        }
        return string.Empty;
    }
}
