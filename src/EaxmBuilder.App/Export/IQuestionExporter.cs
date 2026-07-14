using EaxmBuilder.Core;

namespace EaxmBuilder.Export;

public interface IQuestionExporter
{
    TaskStep Step { get; }
    Task ExportAsync(QuestionProject project, QuestionDocument document, CancellationToken cancellationToken);
}

