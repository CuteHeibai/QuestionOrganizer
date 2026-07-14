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
}

public sealed class OcrResult
{
    public string RawText { get; set; } = string.Empty;
    public string Language { get; set; } = "zh-CN";
}

