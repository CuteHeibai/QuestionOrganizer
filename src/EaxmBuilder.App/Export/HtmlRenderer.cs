using System.Net;
using System.Text;
using EaxmBuilder.Core;

namespace EaxmBuilder.Export;

public static class HtmlRenderer
{
    public static string Render(QuestionDocument document)
    {
        var body = new StringBuilder();
        var numberPlaced = false;
        var startIndex = 0;
        if (TryGetPromptTargetLayout(document, out var targetFigureIndex, out var targetFigure))
        {
            body.AppendLine("<div class=\"prompt-row\"><div class=\"prompt-text\">");
            AppendInlineParagraphs(
                body,
                document.Blocks.Take(targetFigureIndex).ToList(),
                document.QuestionNumber,
                document.LatexSymbolMap,
                ref numberPlaced);
            body.AppendLine("</div><div class=\"target-figure\">");
            body.AppendLine(SanitizeSvg(targetFigure.Svg));
            body.AppendLine("</div></div>");
            startIndex = targetFigureIndex + 1;
        }

        for (var index = startIndex; index < document.Blocks.Count; index++)
        {
            var block = document.Blocks[index];
            if (IsChoicePair(document.Blocks, index))
            {
                body.AppendLine("<div class=\"choices\">");
                while (IsChoicePair(document.Blocks, index))
                {
                    var label = document.Blocks[index].Text.Trim();
                    var figure = document.Figures.FirstOrDefault(item => item.Id == document.Blocks[index + 1].FigureId);
                    body.Append("<div class=\"choice\"><div class=\"choice-label\">")
                        .Append(WebUtility.HtmlEncode(label))
                        .AppendLine("</div>");
                    if (figure is not null) body.AppendLine(SanitizeSvg(figure.Svg));
                    body.AppendLine("</div>");
                    index += 2;
                }
                body.AppendLine("</div>");
                index--;
                continue;
            }

            switch (block.Type)
            {
                case QuestionBlockType.Paragraph:
                case QuestionBlockType.Formula:
                    var inlineBlocks = new List<QuestionBlock>();
                    while (index < document.Blocks.Count &&
                           document.Blocks[index].Type is QuestionBlockType.Paragraph or QuestionBlockType.Formula)
                    {
                        if (inlineBlocks.Count > 0 && StartsNewParagraph(document.Blocks[index])) break;
                        inlineBlocks.Add(document.Blocks[index]);
                        index++;
                    }
                    index--;
                    AppendInlineParagraph(
                        body,
                        inlineBlocks,
                        document.QuestionNumber,
                        document.LatexSymbolMap,
                        ref numberPlaced);
                    break;
                case QuestionBlockType.Figure:
                    var figure = document.Figures.FirstOrDefault(item => item.Id == block.FigureId);
                    if (figure is not null) body.AppendLine(SanitizeSvg(figure.Svg));
                    break;
            }
        }

        return $$"""
            <!doctype html>
            <html lang="zh-CN"><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(document.Title)}}</title>
            <style>
            @page{size:A4;margin:25.4mm 31.8mm}body{font-family:"SimSun","宋体",serif;font-size:10.5pt;line-height:2;color:#111}
            p{margin:0}.formula{font-family:"Cambria Math","Times New Roman",serif}
            svg{display:block;max-width:68%;height:auto;margin:10pt auto}
            .prompt-row{display:grid;grid-template-columns:minmax(0,1fr) 122px;gap:10pt;align-items:center}
            .target-figure svg{max-width:118px;margin:0 auto}
            .choices{display:grid;grid-template-columns:repeat(4,1fr);gap:8pt;margin-top:18pt;align-items:start}
            .choice{break-inside:avoid}.choice-label{font-weight:600;line-height:1.4;margin-bottom:3pt}
            .choice svg{max-width:100%;margin:0 auto}
            </style></head><body>{{body}}</body></html>
            """;
    }

    private static void AppendInlineParagraphs(
        StringBuilder body,
        IReadOnlyList<QuestionBlock> blocks,
        string questionNumber,
        IReadOnlyDictionary<string, string> latexSymbolMap,
        ref bool numberPlaced)
    {
        for (var index = 0; index < blocks.Count; index++)
        {
            if (blocks[index].Type is not (QuestionBlockType.Paragraph or QuestionBlockType.Formula)) continue;
            var inlineBlocks = new List<QuestionBlock>();
            while (index < blocks.Count &&
                   blocks[index].Type is QuestionBlockType.Paragraph or QuestionBlockType.Formula)
            {
                if (inlineBlocks.Count > 0 && StartsNewParagraph(blocks[index])) break;
                inlineBlocks.Add(blocks[index]);
                index++;
            }
            index--;
            AppendInlineParagraph(body, inlineBlocks, questionNumber, latexSymbolMap, ref numberPlaced);
        }
    }

    private static void AppendInlineParagraph(
        StringBuilder body,
        IReadOnlyList<QuestionBlock> blocks,
        string questionNumber,
        IReadOnlyDictionary<string, string> latexSymbolMap,
        ref bool numberPlaced)
    {
        if (blocks.Count == 0) return;
        body.Append("<p>");
        foreach (var block in blocks)
        {
            if (block.Type == QuestionBlockType.Formula)
            {
                body.Append("<span class=\"formula\">")
                    .Append(WebUtility.HtmlEncode(MathTextFormatter.ToDisplayText(block.Latex, latexSymbolMap)))
                    .Append("</span>");
                continue;
            }

            var text = block.Text;
            if (!numberPlaced && !string.IsNullOrWhiteSpace(questionNumber) &&
                !text.TrimStart().StartsWith(questionNumber, StringComparison.Ordinal))
            {
                text = $"{questionNumber.TrimEnd('.', '．', '、')}. {text}";
            }
            if (!string.IsNullOrWhiteSpace(text)) numberPlaced = true;
            body.Append(WebUtility.HtmlEncode(text));
        }
        body.AppendLine("</p>");
    }

    private static bool StartsNewParagraph(QuestionBlock block)
    {
        if (block.Type != QuestionBlockType.Paragraph) return false;
        var text = block.Text.TrimStart();
        return text.StartsWith('（') && text.Length > 2 && char.IsDigit(text[1]);
    }

    private static bool TryGetPromptTargetLayout(
        QuestionDocument document,
        out int targetFigureIndex,
        out FigureDocument targetFigure)
    {
        targetFigureIndex = -1;
        targetFigure = default!;
        var firstChoiceIndex = Enumerable.Range(0, document.Blocks.Count)
            .FirstOrDefault(index => IsChoicePair(document.Blocks, index), -1);
        if (firstChoiceIndex <= 0) return false;
        targetFigureIndex = Enumerable.Range(0, firstChoiceIndex)
            .FirstOrDefault(index => document.Blocks[index].Type == QuestionBlockType.Figure, -1);
        if (targetFigureIndex <= 0) return false;
        var figureId = document.Blocks[targetFigureIndex].FigureId;
        targetFigure = document.Figures.FirstOrDefault(item => item.Id == figureId)!;
        return targetFigure is not null;
    }

    private static bool IsChoicePair(IReadOnlyList<QuestionBlock> blocks, int index) =>
        index + 1 < blocks.Count &&
        blocks[index].Type == QuestionBlockType.Paragraph &&
        IsChoiceLabel(blocks[index].Text) &&
        blocks[index + 1].Type == QuestionBlockType.Figure;

    private static bool IsChoiceLabel(string value)
    {
        var text = value.Trim();
        return text.Length is 2 or 3 && text[0] is >= 'A' and <= 'H' &&
               text[1] is '.' or '．' or '、';
    }

    private static string SanitizeSvg(string svg)
    {
        return svg
            .Replace("<script", "<!-- blocked-script", StringComparison.OrdinalIgnoreCase)
            .Replace("</script>", "-->", StringComparison.OrdinalIgnoreCase)
            .Replace("javascript:", string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
