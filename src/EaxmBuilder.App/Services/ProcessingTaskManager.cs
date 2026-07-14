using EaxmBuilder.AI;
using EaxmBuilder.Core;
using EaxmBuilder.Export;
using EaxmBuilder.Infrastructure;

namespace EaxmBuilder.Services;

public sealed class ProcessingTaskManager(
    ProjectRepository repository,
    IAiProvider aiProvider,
    IReadOnlyList<IQuestionExporter> exporters)
{
    private static readonly TaskStep[] OrderedSteps =
    [
        TaskStep.Ocr,
        TaskStep.FormulaRecognition,
        TaskStep.FigureRedraw,
        TaskStep.WordExport,
        TaskStep.PdfExport,
        TaskStep.LatexExport,
        TaskStep.JsonExport,
        TaskStep.AiReview
    ];

    public event Action<string>? LogAdded;
    public event Action<QuestionProject>? ProjectChanged;

    public async Task RunPendingAsync(QuestionProject project, CancellationToken cancellationToken = default)
    {
        foreach (var step in OrderedSteps)
        {
            if (project.Steps[step].State == StepState.Completed) continue;
            var succeeded = await RunStepAsync(project, step, cancellationToken);
            if (!succeeded) return;
        }
    }

    public async Task<bool> RunStepAsync(
        QuestionProject project,
        TaskStep step,
        CancellationToken cancellationToken = default)
    {
        if (IsDisabledExport(project, step))
        {
            await MarkSkippedAsync(project, step);
            return true;
        }

        EnsurePrerequisites(project, step);
        var record = project.Steps[step];
        record.State = StepState.Running;
        record.Error = string.Empty;
        record.Attempts++;
        await repository.SaveAsync(project);
        ProjectChanged?.Invoke(project);
        LogAdded?.Invoke(StartMessage(step));

        try
        {
            await ExecuteAsync(project, step, cancellationToken);
            record.State = StepState.Completed;
            record.CompletedAt = DateTimeOffset.Now;
            LogAdded?.Invoke(CompleteMessage(step));
            return true;
        }
        catch (OperationCanceledException)
        {
            record.State = StepState.Failed;
            record.Error = $"{DisplayName(step)}已取消或请求超时，可以单独重试。";
            LogAdded?.Invoke($"{DisplayName(step)}已停止");
            return false;
        }
        catch (Exception exception)
        {
            record.State = StepState.Failed;
            record.Error = exception.Message;
            LogAdded?.Invoke($"{DisplayName(step)}失败：{exception.Message}");
            return false;
        }
        finally
        {
            await repository.SaveAsync(project);
            ProjectChanged?.Invoke(project);
        }
    }

    private async Task ExecuteAsync(QuestionProject project, TaskStep step, CancellationToken cancellationToken)
    {
        switch (step)
        {
            case TaskStep.Ocr:
                var ocr = await aiProvider.RecognizeTextAsync(
                    project.SourcePath, project.AiInstructions, cancellationToken);
                await repository.SaveDataAsync(project, "ocr.json", ocr);
                break;

            case TaskStep.FormulaRecognition:
                var ocrData = await RequireDataAsync<OcrResult>(project, "ocr.json");
                var document = await aiProvider.StructureQuestionAsync(
                    project.SourcePath, ocrData, project.AiInstructions, cancellationToken);
                await repository.SaveDataAsync(project, "document.json", document);
                break;

            case TaskStep.FigureRedraw:
                var figureDocument = await RequireDataAsync<QuestionDocument>(project, "document.json");
                var figures = await aiProvider.RedrawFiguresAsync(
                    project.SourcePath, figureDocument, project.AiInstructions, cancellationToken);
                figureDocument.Figures = figures.ToList();
                await SvgWriter.WriteAllAsync(project, figures, cancellationToken);
                await repository.SaveDataAsync(project, "document.json", figureDocument);
                break;

            case TaskStep.AiReview:
                await ReviewOutputsAsync(project, cancellationToken);
                break;

            default:
                var exporter = exporters.FirstOrDefault(item => item.Step == step)
                    ?? throw new InvalidOperationException($"未注册 {step} 导出器。");
                var exportDocument = await RequireDataAsync<QuestionDocument>(project, "document.json");
                await exporter.ExportAsync(project, exportDocument, cancellationToken);
                break;
        }
    }

    private async Task ReviewOutputsAsync(QuestionProject project, CancellationToken cancellationToken)
    {
        var document = await RequireDataAsync<QuestionDocument>(project, "document.json");
        var generatedFiles = await ReadGeneratedFilesAsync(project, cancellationToken);
        var review = await aiProvider.ReviewOutputsAsync(
            project.SourcePath,
            document,
            generatedFiles,
            project.AiInstructions,
            cancellationToken);
        await repository.SaveDataAsync(project, "review.json", review);
        LogAdded?.Invoke(string.IsNullOrWhiteSpace(review.Summary)
            ? "AI 复核完成"
            : $"AI 复核：{review.Summary}");

        if (review.Passed || review.CorrectedDocument is null) return;

        LogAdded?.Invoke("AI 复核发现问题，正在应用修正并重新导出...");
        var corrected = review.CorrectedDocument;
        await SvgWriter.WriteAllAsync(project, corrected.Figures, cancellationToken);
        await repository.SaveDataAsync(project, "document.json", corrected);
        foreach (var exporter in exporters.Where(item => !IsDisabledExport(project, item.Step)))
        {
            await exporter.ExportAsync(project, corrected, cancellationToken);
            var exportRecord = project.Steps[exporter.Step];
            exportRecord.State = StepState.Completed;
            exportRecord.Error = string.Empty;
            exportRecord.CompletedAt = DateTimeOffset.Now;
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadGeneratedFilesAsync(
        QuestionProject project,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in new[] { "document.json", "metadata.json", "question.html", "question.tex" })
        {
            var path = Path.Combine(project.DirectoryPath, fileName);
            if (File.Exists(path))
                result[fileName] = await File.ReadAllTextAsync(path, cancellationToken);
        }

        foreach (var path in Directory.EnumerateFiles(project.DirectoryPath, "*.svg").Take(12))
        {
            result[Path.GetFileName(path)] = await File.ReadAllTextAsync(path, cancellationToken);
        }

        foreach (var fileName in new[] { "question.docx", "question.pdf" })
        {
            var path = Path.Combine(project.DirectoryPath, fileName);
            if (File.Exists(path))
                result[fileName] = $"文件存在，大小 {new FileInfo(path).Length} 字节。";
        }
        return result;
    }

    private async Task<T> RequireDataAsync<T>(QuestionProject project, string fileName)
    {
        return await repository.LoadDataAsync<T>(project, fileName)
               ?? throw new InvalidOperationException($"项目缺少 {fileName}，请先执行前置步骤。");
    }

    private static void EnsurePrerequisites(QuestionProject project, TaskStep step)
    {
        var index = Array.IndexOf(OrderedSteps, step);
        if (index <= 0) return;
        var required = step switch
        {
            TaskStep.FormulaRecognition => TaskStep.Ocr,
            TaskStep.FigureRedraw => TaskStep.FormulaRecognition,
            TaskStep.AiReview => TaskStep.FigureRedraw,
            _ => TaskStep.FigureRedraw
        };
        if (project.Steps[required].State != StepState.Completed)
            throw new InvalidOperationException($"请先完成{DisplayName(required)}。");
    }

    private async Task MarkSkippedAsync(QuestionProject project, TaskStep step)
    {
        var record = project.Steps[step];
        record.State = StepState.Skipped;
        record.Error = string.Empty;
        record.CompletedAt = DateTimeOffset.Now;
        await repository.SaveAsync(project);
        ProjectChanged?.Invoke(project);
        LogAdded?.Invoke($"{DisplayName(step)}已跳过");
    }

    private static bool IsDisabledExport(QuestionProject project, TaskStep step) => step switch
    {
        TaskStep.WordExport => !project.OutputSelection.Word && !project.OutputSelection.AppendToWord,
        TaskStep.PdfExport => !project.OutputSelection.Pdf,
        TaskStep.LatexExport => !project.OutputSelection.Latex,
        TaskStep.JsonExport => !project.OutputSelection.Json,
        TaskStep.AiReview => !project.OutputSelection.HasAnyOutput,
        _ => false
    };

    public static string DisplayName(TaskStep step) => step switch
    {
        TaskStep.Ocr => "OCR",
        TaskStep.FormulaRecognition => "公式识别",
        TaskStep.FigureRedraw => "图形重绘",
        TaskStep.WordExport => "Word",
        TaskStep.PdfExport => "PDF",
        TaskStep.LatexExport => "LaTeX",
        TaskStep.JsonExport => "JSON",
        TaskStep.AiReview => "AI 复核",
        _ => step.ToString()
    };

    private static string StartMessage(TaskStep step) => step switch
    {
        TaskStep.Ocr => "开始 OCR...",
        TaskStep.FormulaRecognition => "识别公式...",
        TaskStep.FigureRedraw => "生成 SVG...",
        TaskStep.AiReview => "AI 检查生成文件...",
        _ => $"生成 {DisplayName(step)}..."
    };

    private static string CompleteMessage(TaskStep step) => $"{DisplayName(step)}完成";
}
