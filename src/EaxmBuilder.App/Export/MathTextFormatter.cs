using System.Text.RegularExpressions;

namespace EaxmBuilder.Export;

internal static partial class MathTextFormatter
{
    private static readonly Dictionary<char, char> Superscripts = new()
    {
        ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴',
        ['5'] = '⁵', ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸', ['9'] = '⁹',
        ['+'] = '⁺', ['-'] = '⁻', ['='] = '⁼', ['('] = '⁽', [')'] = '⁾',
        ['n'] = 'ⁿ'
    };

    private static readonly Dictionary<char, char> Subscripts = new()
    {
        ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃', ['4'] = '₄',
        ['5'] = '₅', ['6'] = '₆', ['7'] = '₇', ['8'] = '₈', ['9'] = '₉',
        ['+'] = '₊', ['-'] = '₋', ['='] = '₌', ['('] = '₍', [')'] = '₎'
    };

    public static string ToDisplayText(string latex)
    {
        var value = latex.Trim();
        if (value.StartsWith('$') && value.EndsWith('$') && value.Length >= 2)
            value = value[1..^1];
        value = value
            .Replace(@"\left", string.Empty, StringComparison.Ordinal)
            .Replace(@"\right", string.Empty, StringComparison.Ordinal)
            .Replace(@"\,", " ", StringComparison.Ordinal)
            .Replace(@"\;", " ", StringComparison.Ordinal)
            .Replace(@"\quad", " ", StringComparison.Ordinal)
            .Replace(@"\cdot", "·", StringComparison.Ordinal)
            .Replace(@"\times", "×", StringComparison.Ordinal)
            .Replace(@"\div", "÷", StringComparison.Ordinal)
            .Replace(@"\leq", "≤", StringComparison.Ordinal)
            .Replace(@"\le", "≤", StringComparison.Ordinal)
            .Replace(@"\geq", "≥", StringComparison.Ordinal)
            .Replace(@"\ge", "≥", StringComparison.Ordinal)
            .Replace(@"\neq", "≠", StringComparison.Ordinal)
            .Replace(@"\ne", "≠", StringComparison.Ordinal)
            .Replace(@"\infty", "∞", StringComparison.Ordinal)
            .Replace(@"\pi", "π", StringComparison.Ordinal)
            .Replace(@"\theta", "θ", StringComparison.Ordinal)
            .Replace(@"\alpha", "α", StringComparison.Ordinal)
            .Replace(@"\beta", "β", StringComparison.Ordinal)
            .Replace(@"\gamma", "γ", StringComparison.Ordinal);

        value = FractionRegex().Replace(value, match =>
            $"({ToDisplayText(match.Groups[1].Value)})/({ToDisplayText(match.Groups[2].Value)})");
        value = ScriptRegex().Replace(value, match =>
        {
            var map = match.Groups[1].Value == "^" ? Superscripts : Subscripts;
            var content = match.Groups[2].Value.Length > 0 ? match.Groups[2].Value : match.Groups[3].Value;
            var converted = new string(content.Select(character =>
                map.TryGetValue(character, out var mapped) ? mapped : character).ToArray());
            return converted;
        });

        return value
            .Replace("{", string.Empty, StringComparison.Ordinal)
            .Replace("}", string.Empty, StringComparison.Ordinal)
            .Replace("\\", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    [GeneratedRegex(@"\\frac\{([^{}]+)\}\{([^{}]+)\}")]
    private static partial Regex FractionRegex();

    [GeneratedRegex(@"([\^_])(?:\{([^{}]+)\}|([A-Za-z0-9+\-=()]))")]
    private static partial Regex ScriptRegex();
}
