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

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FigureProcessingMode
{
    AiRedraw,
    ExternalToolThenOriginalImage,
    OriginalImage
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
    public FigureProcessingMode FigureMode { get; set; } = FigureProcessingMode.AiRedraw;
    public GenerationSummary LastGeneration { get; set; } = new();
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

public sealed class GenerationSummary
{
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool? Succeeded { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ReviewSummary { get; set; } = string.Empty;
    public List<string> Files { get; set; } = [];
}

public sealed class OutputSelection
{
    public bool Word { get; set; } = true;
    public bool Pdf { get; set; } = true;
    public bool Latex { get; set; } = true;
    public bool Json { get; set; } = true;
    public bool Svg { get; set; } = true;
    public string OutputDirectory { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string AppendToWordPath { get; set; } = string.Empty;

    [JsonIgnore]
    public bool AppendToWord => !string.IsNullOrWhiteSpace(AppendToWordPath);

    [JsonIgnore]
    public bool HasAnyOutput => Word || Pdf || Latex || Json || Svg || AppendToWord;
}

public static class QuestionProjectWorkflow
{
    public static void ResetFinalGenerationSteps(QuestionProject project)
    {
        if (project.OutputSelection.Word || project.OutputSelection.AppendToWord)
            ResetStep(project, TaskStep.WordExport);
        if (project.OutputSelection.Pdf) ResetStep(project, TaskStep.PdfExport);
        if (project.OutputSelection.Latex) ResetStep(project, TaskStep.LatexExport);
        if (project.OutputSelection.Json) ResetStep(project, TaskStep.JsonExport);
        if (project.OutputSelection.HasAnyOutput) ResetStep(project, TaskStep.AiReview);
    }

    private static void ResetStep(QuestionProject project, TaskStep step)
    {
        var record = project.Steps[step];
        if (record.State == StepState.Running) return;
        record.State = StepState.Pending;
        record.Error = string.Empty;
        record.CompletedAt = null;
    }
}

public static class ProjectOutputPaths
{
    private static readonly string[] FinalExtensions = [".docx", ".pdf", ".tex", ".json"];

    public static string GetFinalDirectory(QuestionProject project) =>
        string.IsNullOrWhiteSpace(project.OutputSelection.OutputDirectory)
            ? Path.Combine(project.DirectoryPath, "output")
            : project.OutputSelection.OutputDirectory.Trim();

    public static string GetBaseFileName(QuestionProject project)
    {
        var value = string.IsNullOrWhiteSpace(project.OutputSelection.FileName)
            ? project.Name
            : project.OutputSelection.FileName;
        var safe = SanitizeFileName(Path.GetFileNameWithoutExtension(value));
        return string.IsNullOrWhiteSpace(safe) ? "Question" : safe;
    }

    public static string GetFilePath(QuestionProject project, string extension) =>
        Path.Combine(EnsureFinalDirectory(project), GetBaseFileName(project) + extension);

    public static string GetFigurePath(QuestionProject project, string figureId) =>
        Path.Combine(EnsureFinalDirectory(project), $"{GetBaseFileName(project)}-{SanitizeFileName(figureId)}.svg");

    public static string EnsureFinalDirectory(QuestionProject project)
    {
        var directory = GetFinalDirectory(project);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static void ClearFinalOutputs(QuestionProject project)
    {
        var directory = EnsureFinalDirectory(project);
        var baseName = GetBaseFileName(project);
        foreach (var extension in FinalExtensions)
            TryDelete(Path.Combine(directory, baseName + extension));
        foreach (var path in Directory.EnumerateFiles(directory, baseName + "-*.svg"))
            TryDelete(path);
    }

    public static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Where(character => !invalid.Contains(character)).ToArray()).Trim();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A locked final file will be reported by the exporter that tries to overwrite it.
        }
    }
}
