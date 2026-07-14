using System.Text.Json.Serialization;

namespace EaxmBuilder.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskStep
{
    Ocr,
    FormulaRecognition,
    FigureRedraw,
    WordExport,
    PdfExport,
    LatexExport,
    JsonExport,
    AiReview
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepState
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped
}

public sealed class StepRecord
{
    public StepState State { get; set; } = StepState.Pending;
    public string Error { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class QuestionProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public string AiInstructions { get; set; } = string.Empty;
    public OutputSelection OutputSelection { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public Dictionary<TaskStep, StepRecord> Steps { get; set; } = Enum
        .GetValues<TaskStep>()
        .ToDictionary(step => step, _ => new StepRecord());

    [JsonIgnore]
    public string SourcePath => Path.Combine(DirectoryPath, SourceFileName);

    [JsonIgnore]
    public bool IsComplete => Steps.Values.All(step => step.State is StepState.Completed or StepState.Skipped);

    [JsonIgnore]
    public int CompletedStepCount => Steps.Values.Count(step => step.State == StepState.Completed);

    [JsonIgnore]
    public string StatusText => IsComplete ? "已完成" : $"{CompletedStepCount}/{Steps.Count} 步";
}

public sealed class OutputSelection
{
    public bool Word { get; set; } = true;
    public bool Pdf { get; set; } = true;
    public bool Latex { get; set; } = true;
    public bool Json { get; set; } = true;
    public string AppendToWordPath { get; set; } = string.Empty;

    [JsonIgnore]
    public bool AppendToWord => !string.IsNullOrWhiteSpace(AppendToWordPath);

    [JsonIgnore]
    public bool HasAnyOutput => Word || Pdf || Latex || Json || AppendToWord;
}
