using System.Text.Json;
using System.Text.Json.Serialization;

namespace EaxmBuilder.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuestionBlockType
{
    Paragraph,
    Formula,
    Figure
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
            Description = ReadString(root, "description"),
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
}
