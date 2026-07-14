using System.Xml.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using EaxmBuilder.Core;

namespace EaxmBuilder.Export;

public static class SvgWriter
{
    public static async Task WriteAllAsync(
        QuestionProject project,
        IReadOnlyList<FigureDocument> figures,
        CancellationToken cancellationToken)
    {
        foreach (var figure in figures)
        {
            var svg = Sanitize(figure.Svg);
            figure.Svg = svg;
            var fileName = SanitizeId(figure.Id) + ".svg";
            await File.WriteAllTextAsync(Path.Combine(project.DirectoryPath, fileName), svg, cancellationToken);
        }
    }

    private static string Sanitize(string svg)
    {
        var document = XDocument.Parse(svg, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException("AI 返回了空 SVG。");
        if (root.Name.LocalName != "svg") throw new InvalidOperationException("AI 返回的图形不是 SVG。");

        foreach (var node in root.Descendants().Where(node => node.Name.LocalName == "script").ToList())
            node.Remove();
        foreach (var attribute in root.DescendantsAndSelf().Attributes().ToList())
        {
            if (attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                attribute.Value.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
                attribute.Remove();
        }
        NormalizeNumericAttributes(root);
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static void NormalizeNumericAttributes(XElement root)
    {
        var viewBox = root.Attribute("viewBox")
                      ?? throw new InvalidOperationException("AI 返回的 SVG 缺少 viewBox。");
        var values = ParseViewBox(viewBox.Value);
        viewBox.Value = string.Join(" ", values.Select(FormatNumber));
        var width = values[2];
        var height = values[3];
        foreach (var path in root.Descendants().Where(item => item.Name.LocalName == "path"))
        {
            var data = path.Attribute("d");
            if (data is not null) data.Value = NormalizePathData(data.Value, width, height);
        }
    }

    private static double[] ParseViewBox(string value)
    {
        var separated = value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (separated.Length == 4 && separated.All(item =>
                double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            return separated.Select(item => double.Parse(item, CultureInfo.InvariantCulture)).ToArray();

        if (!value.All(char.IsDigit) || !value.StartsWith("00", StringComparison.Ordinal))
            throw new InvalidOperationException("SVG viewBox 必须包含四个可读取的数字。");
        var remaining = value[2..];
        var candidates = new List<(double Width, double Height, int Score)>();
        for (var split = 1; split < remaining.Length; split++)
        {
            if (!double.TryParse(remaining[..split], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
                !double.TryParse(remaining[split..], NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
                width < 32 || height < 32 || width > 4096 || height > 4096)
                continue;
            candidates.Add((width, height, Math.Abs(split - (remaining.Length - split))));
        }
        if (candidates.Count == 0)
            throw new InvalidOperationException("无法恢复 SVG viewBox 中粘连的数字。");
        var best = candidates.OrderBy(item => item.Score).ThenBy(item => Math.Abs(item.Width - item.Height)).First();
        return [0, 0, best.Width, best.Height];
    }

    private static string NormalizePathData(string value, double width, double height)
    {
        var normalized = Regex.Replace(value, "([A-Za-z])([^A-Za-z]*)", match =>
        {
            var command = match.Groups[1].Value[0];
            var raw = match.Groups[2].Value.Trim();
            if (raw.Length == 0 || raw.Any(character => char.IsWhiteSpace(character) || character == ',' ||
                                                       character == '-' || character == '.'))
                return command + (raw.Length == 0 ? string.Empty : " " + raw);
            if (!raw.All(char.IsDigit)) return match.Value;

            var parameterCount = char.ToUpperInvariant(command) switch
            {
                'M' or 'L' or 'T' => 2,
                'H' or 'V' => 1,
                'S' or 'Q' => 4,
                'C' => 6,
                'A' => 7,
                _ => 0
            };
            if (parameterCount == 0) return match.Value;
            var numbers = DecodeNumbers(raw, command, parameterCount, width, height)
                          ?? throw new InvalidOperationException($"无法恢复 SVG path 命令 {command} 中粘连的坐标。");
            return command + " " + string.Join(" ", numbers);
        });
        return Regex.Replace(normalized, "(?<=\\d)(?=[A-Za-z])", " ");
    }

    private static IReadOnlyList<string>? DecodeNumbers(
        string digits,
        char command,
        int parameterCount,
        double width,
        double height)
    {
        (double Cost, List<string> Values)? best = null;
        Search(0, 0, 0, []);
        return best?.Values;

        void Search(int position, int parameterIndex, double cost, List<string> values)
        {
            if (parameterIndex == parameterCount)
            {
                if (position == digits.Length && (best is null || cost < best.Value.Cost))
                    best = (cost, [.. values]);
                return;
            }
            var remainingParameters = parameterCount - parameterIndex - 1;
            var maxLength = Math.Min(4, digits.Length - position - remainingParameters);
            for (var length = 1; length <= maxLength; length++)
            {
                if (digits[position] == '0' && length > 1) break;
                var token = digits.Substring(position, length);
                var number = int.Parse(token, CultureInfo.InvariantCulture);
                if (number > ParameterLimit(command, parameterIndex, width, height)) continue;
                var tokenCost = length switch { 1 => 4d, 2 => 0d, 3 => 0.2d, _ => 1.5d };
                values.Add(token);
                Search(position + length, parameterIndex + 1, cost + tokenCost, values);
                values.RemoveAt(values.Count - 1);
            }
        }
    }

    private static double ParameterLimit(char command, int index, double width, double height)
    {
        return char.ToUpperInvariant(command) switch
        {
            'H' => width * 1.5,
            'V' => height * 1.5,
            'A' when index is 0 or 5 => width * 1.5,
            'A' when index is 1 or 6 => height * 1.5,
            'A' when index == 2 => 360,
            'A' when index is 3 or 4 => 1,
            _ => index % 2 == 0 ? width * 1.5 : height * 1.5
        };
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string SanitizeId(string value)
    {
        var safe = new string(value.Where(character => char.IsLetterOrDigit(character) || character == '-').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "figure" : safe;
    }
}
