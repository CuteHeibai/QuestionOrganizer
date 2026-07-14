using System.Text;
using EaxmBuilder.Core;

namespace EaxmBuilder.Export;

public sealed class LatexExporter : IQuestionExporter
{
    public TaskStep Step => TaskStep.LatexExport;

    public async Task ExportAsync(
        QuestionProject project,
        QuestionDocument document,
        CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        content.AppendLine("\\documentclass[12pt]{ctexart}");
        content.AppendLine("\\usepackage{amsmath,amssymb,graphicx}");
        content.AppendLine("\\begin{document}");
        foreach (var block in document.Blocks)
        {
            switch (block.Type)
            {
                case QuestionBlockType.Paragraph:
                    content.AppendLine(Escape(block.Text) + "\\par");
                    break;
                case QuestionBlockType.Formula:
                    content.AppendLine("\\[" + block.Latex + "\\]");
                    break;
                case QuestionBlockType.Figure:
                    content.AppendLine($"% SVG figure: {block.FigureId}.svg");
                    break;
            }
        }
        content.AppendLine("\\end{document}");
        await File.WriteAllTextAsync(
            Path.Combine(project.DirectoryPath, "question.tex"),
            content.ToString(),
            new UTF8Encoding(true),
            cancellationToken);
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\textbackslash{}", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("&", "\\&", StringComparison.Ordinal)
        .Replace("#", "\\#", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}

