using EaxmBuilder.Core;

namespace EaxmBuilder.AI;

public interface IAiProvider
{
    Task TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<OcrResult> RecognizeTextAsync(
        string sourcePath,
        string additionalInstructions,
        CancellationToken cancellationToken = default);
    Task<QuestionDocument> StructureQuestionAsync(
        string sourcePath,
        OcrResult ocr,
        string additionalInstructions,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FigureDocument>> RedrawFiguresAsync(
        string sourcePath,
        QuestionDocument document,
        string additionalInstructions,
        CancellationToken cancellationToken = default);
}
