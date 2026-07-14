using System.Text.Json;
using System.Text.Json.Serialization;
using EaxmBuilder.Core;

namespace EaxmBuilder.Export;

public sealed class JsonExporter : IQuestionExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public TaskStep Step => TaskStep.JsonExport;

    public async Task ExportAsync(
        QuestionProject project,
        QuestionDocument document,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Create(Path.Combine(project.DirectoryPath, "metadata.json"));
        await JsonSerializer.SerializeAsync(stream, document, Options, cancellationToken);
    }
}

