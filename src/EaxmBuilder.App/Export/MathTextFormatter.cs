using System.Text;
using System.Text.RegularExpressions;

namespace EaxmBuilder.Export;

internal static partial class MathTextFormatter
{
    public enum SegmentKind
    {
        Text,
        Fraction,
        Radical
    }

    public sealed record MathSegment(
        SegmentKind Kind,
        string Text,
        string Numerator = "",
        string Denominator = "",
        string Radicand = "",
        string Degree = "");

    private static readonly Dictionary<char, char> Superscripts = new()
    {
        ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴',
        ['5'] = '⁵', ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸', ['9'] = '⁹',
        ['+'] = '⁺', ['-'] = '⁻', ['='] = '⁼', ['('] = '⁽', [')'] = '⁾',
        ['a'] = 'ᵃ', ['b'] = 'ᵇ', ['c'] = 'ᶜ', ['d'] = 'ᵈ', ['e'] = 'ᵉ',
        ['f'] = 'ᶠ', ['g'] = 'ᵍ', ['h'] = 'ʰ', ['i'] = 'ⁱ', ['j'] = 'ʲ',
        ['k'] = 'ᵏ', ['l'] = 'ˡ', ['m'] = 'ᵐ', ['n'] = 'ⁿ', ['o'] = 'ᵒ',
        ['p'] = 'ᵖ', ['r'] = 'ʳ', ['s'] = 'ˢ', ['t'] = 'ᵗ', ['u'] = 'ᵘ',
        ['v'] = 'ᵛ', ['w'] = 'ʷ', ['x'] = 'ˣ', ['y'] = 'ʸ', ['z'] = 'ᶻ'
    };

    private static readonly Dictionary<char, char> Subscripts = new()
    {
        ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃', ['4'] = '₄',
        ['5'] = '₅', ['6'] = '₆', ['7'] = '₇', ['8'] = '₈', ['9'] = '₉',
        ['+'] = '₊', ['-'] = '₋', ['='] = '₌', ['('] = '₍', [')'] = '₎',
        ['a'] = 'ₐ', ['e'] = 'ₑ', ['h'] = 'ₕ', ['i'] = 'ᵢ', ['j'] = 'ⱼ',
        ['k'] = 'ₖ', ['l'] = 'ₗ', ['m'] = 'ₘ', ['n'] = 'ₙ', ['o'] = 'ₒ',
        ['p'] = 'ₚ', ['r'] = 'ᵣ', ['s'] = 'ₛ', ['t'] = 'ₜ', ['u'] = 'ᵤ',
        ['v'] = 'ᵥ', ['x'] = 'ₓ'
    };

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["alpha"] = "α", ["Alpha"] = "Α", ["beta"] = "β", ["Beta"] = "Β",
        ["gamma"] = "γ", ["Gamma"] = "Γ", ["delta"] = "δ", ["Delta"] = "Δ",
        ["epsilon"] = "ε", ["varepsilon"] = "ε", ["Epsilon"] = "Ε",
        ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["vartheta"] = "ϑ", ["Theta"] = "Θ",
        ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ", ["Lambda"] = "Λ",
        ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ", ["Xi"] = "Ξ",
        ["pi"] = "π", ["varpi"] = "ϖ", ["Pi"] = "Π", ["rho"] = "ρ", ["varrho"] = "ϱ",
        ["sigma"] = "σ", ["varsigma"] = "ς", ["Sigma"] = "Σ", ["tau"] = "τ",
        ["upsilon"] = "υ", ["Upsilon"] = "Υ", ["phi"] = "φ", ["varphi"] = "φ", ["Phi"] = "Φ",
        ["chi"] = "χ", ["psi"] = "ψ", ["Psi"] = "Ψ", ["omega"] = "ω", ["Omega"] = "Ω",

        ["times"] = "×", ["div"] = "÷", ["cdot"] = "·", ["ast"] = "∗", ["star"] = "⋆",
        ["pm"] = "±", ["mp"] = "∓", ["circ"] = "∘", ["bullet"] = "•",
        ["sqrt"] = "√", ["sum"] = "∑", ["prod"] = "∏", ["coprod"] = "∐",
        ["int"] = "∫", ["iint"] = "∬", ["iiint"] = "∭", ["oint"] = "∮",
        ["partial"] = "∂", ["nabla"] = "∇", ["infty"] = "∞", ["prime"] = "′",
        ["degree"] = "°", ["deg"] = "°", ["%"] = "%", ["&"] = "&", ["_"] = "_",

        ["le"] = "≤", ["leq"] = "≤", ["leqslant"] = "≤", ["ge"] = "≥", ["geq"] = "≥", ["geqslant"] = "≥",
        ["neq"] = "≠", ["ne"] = "≠", ["equiv"] = "≡", ["approx"] = "≈", ["sim"] = "∼",
        ["simeq"] = "≃", ["cong"] = "≅", ["propto"] = "∝", ["parallel"] = "//",
        ["perp"] = "⊥", ["mid"] = "∣", ["nmid"] = "∤",

        ["in"] = "∈", ["notin"] = "∉", ["ni"] = "∋", ["subset"] = "⊂", ["supset"] = "⊃",
        ["subseteq"] = "⊆", ["supseteq"] = "⊇", ["cup"] = "∪", ["cap"] = "∩",
        ["setminus"] = "∖", ["emptyset"] = "∅", ["varnothing"] = "∅",
        ["mathbb"] = "", ["mathcal"] = "", ["mathscr"] = "", ["mathfrak"] = "",

        ["forall"] = "∀", ["exists"] = "∃", ["nexists"] = "∄", ["neg"] = "¬", ["land"] = "∧",
        ["lor"] = "∨", ["Rightarrow"] = "⇒", ["Leftarrow"] = "⇐", ["Leftrightarrow"] = "⇔",
        ["rightarrow"] = "→", ["to"] = "→", ["leftarrow"] = "←", ["leftrightarrow"] = "↔",
        ["mapsto"] = "↦", ["uparrow"] = "↑", ["downarrow"] = "↓",

        ["angle"] = "∠", ["measuredangle"] = "∡", ["triangle"] = "△", ["triangleq"] = "≜",
        ["square"] = "□", ["Box"] = "□", ["Diamond"] = "◇", ["odot"] = "⊙",
        ["overline"] = "¯", ["arc"] = "⌒",

        ["sin"] = "sin", ["cos"] = "cos", ["tan"] = "tan", ["cot"] = "cot",
        ["sec"] = "sec", ["csc"] = "csc", ["arcsin"] = "arcsin", ["arccos"] = "arccos",
        ["arctan"] = "arctan", ["sinh"] = "sinh", ["cosh"] = "cosh", ["tanh"] = "tanh",
        ["csch"] = "csch", ["sech"] = "sech", ["coth"] = "coth",
        ["exp"] = "exp", ["log"] = "log", ["ln"] = "ln", ["lg"] = "lg", ["lim"] = "lim",
        ["mean"] = "mean", ["median"] = "median", ["min"] = "min", ["max"] = "max",
        ["quartile"] = "quartile", ["quantile"] = "quantile", ["stdev"] = "stdev", ["stdevp"] = "stdevp",
        ["var"] = "var", ["varp"] = "varp", ["cov"] = "cov", ["covp"] = "covp", ["mad"] = "mad",
        ["corr"] = "corr", ["spearman"] = "spearman", ["stats"] = "stats", ["count"] = "count", ["total"] = "total",
        ["normaldist"] = "normaldist", ["tdist"] = "tdist", ["chisqdist"] = "chisqdist",
        ["uniformdist"] = "uniformdist", ["binomialdist"] = "binomialdist", ["poissondist"] = "poissondist",
        ["geodist"] = "geodist", ["discretedist"] = "discretedist", ["pdf"] = "pdf", ["cdf"] = "cdf",
        ["inversecdf"] = "inversecdf", ["random"] = "random",
        ["ztest"] = "ztest", ["ttest"] = "ttest", ["zproptest"] = "zproptest", ["chisqtest"] = "chisqtest",
        ["chisqgof"] = "chisqgof", ["pvalue"] = "p", ["pleft"] = "pleft", ["pright"] = "pright",
        ["score"] = "score", ["dof"] = "dof", ["stderr"] = "stderr", ["conf"] = "conf",
        ["lower"] = "lower", ["upper"] = "upper", ["estimate"] = "estimate",
        ["polygon"] = "polygon", ["distance"] = "distance", ["midpoint"] = "midpoint",
        ["lcm"] = "lcm", ["gcd"] = "gcd", ["mod"] = "mod", ["ceil"] = "ceil", ["floor"] = "floor",
        ["round"] = "round", ["sign"] = "sign", ["nPr"] = "nPr", ["nCr"] = "nCr"
    };

    private static readonly HashSet<string> TransparentCommands = new(StringComparer.Ordinal)
    {
        "mathrm", "mathbf", "mathit", "mathsf", "mathtt", "text", "textit", "textbf",
        "operatorname", "operatorname*", "displaystyle", "textstyle", "scriptstyle",
        "scriptscriptstyle", "left", "right", "big", "Big", "bigg", "Bigg", "begin", "end"
    };

    private static readonly HashSet<string> LooseCommands = new(StringComparer.Ordinal)
    {
        "sqrt", "frac", "dfrac", "tfrac", "angle", "measuredangle", "triangle", "perp", "parallel",
        "le", "leq", "leqslant", "ge", "geq", "geqslant", "neq", "ne", "equiv", "approx", "sim",
        "times", "div", "cdot", "pm", "mp", "degree", "infty", "alpha", "beta", "gamma", "delta",
        "epsilon", "theta", "lambda", "mu", "pi", "rho", "sigma", "phi", "omega",
        "sin", "cos", "tan", "csc", "sec", "cot", "arcsin", "arccos", "arctan",
        "sinh", "cosh", "tanh", "csch", "sech", "coth", "exp", "ln", "log", "lg",
        "int", "sum", "prod", "mean", "median", "quartile", "quantile", "stdev", "stdevp",
        "var", "varp", "cov", "covp", "mad", "corr", "spearman", "stats", "count", "total",
        "normaldist", "tdist", "chisqdist", "uniformdist", "binomialdist", "poissondist",
        "geodist", "discretedist", "pdf", "cdf", "inversecdf", "random", "ztest", "ttest",
        "zproptest", "chisqtest", "chisqgof", "pvalue", "pleft", "pright", "score", "dof",
        "stderr", "conf", "lower", "upper", "estimate", "polygon", "distance", "midpoint",
        "lcm", "gcd", "mod", "ceil", "floor", "round", "sign", "nPr", "nCr"
    };

    public static string ToDisplayText(
        string latex,
        IReadOnlyDictionary<string, string>? customSymbols = null)
    {
        var value = StripMathDelimiters(latex.Trim());
        var normalizedCustomSymbols = NormalizeCustomSymbols(customSymbols);
        value = AddMissingCommandBackslashes(value, normalizedCustomSymbols);
        return CollapseSpaces(Parse(value, normalizedCustomSymbols)).Trim();
    }

    public static string ToInlineDisplayText(
        string text,
        IReadOnlyDictionary<string, string>? customSymbols = null)
    {
        var normalizedCustomSymbols = NormalizeCustomSymbols(customSymbols);
        return Parse(AddMissingCommandBackslashes(text, normalizedCustomSymbols), normalizedCustomSymbols);
    }

    public static IReadOnlyList<MathSegment> ToMathSegments(
        string value,
        IReadOnlyDictionary<string, string>? customSymbols = null,
        bool stripMathDelimiters = false)
    {
        var normalizedCustomSymbols = NormalizeCustomSymbols(customSymbols);
        var text = stripMathDelimiters ? StripMathDelimiters(value.Trim()) : value;
        text = AddMissingCommandBackslashes(text, normalizedCustomSymbols);

        var segments = new List<MathSegment>();
        var cursor = 0;
        foreach (Match match in MathStructureRegex().Matches(text))
        {
            if (match.Index > cursor)
            {
                var plain = Parse(text[cursor..match.Index], normalizedCustomSymbols);
                if (plain.Length > 0) segments.Add(new MathSegment(SegmentKind.Text, plain));
            }

            if (match.Groups["frac"].Success)
            {
                segments.Add(new MathSegment(
                    SegmentKind.Fraction,
                    string.Empty,
                    Numerator: ToDisplayText(match.Groups["num"].Value, normalizedCustomSymbols),
                    Denominator: ToDisplayText(match.Groups["den"].Value, normalizedCustomSymbols)));
            }
            else if (match.Groups["slash"].Success)
            {
                segments.Add(new MathSegment(
                    SegmentKind.Fraction,
                    string.Empty,
                    Numerator: ToDisplayText(match.Groups["num2"].Value, normalizedCustomSymbols),
                    Denominator: ToDisplayText(match.Groups["den2"].Value, normalizedCustomSymbols)));
            }
            else if (match.Groups["sqrt"].Success)
            {
                var radicand = match.Groups["rad"].Success
                    ? match.Groups["rad"].Value
                    : match.Groups["radsingle"].Value;
                segments.Add(new MathSegment(
                    SegmentKind.Radical,
                    string.Empty,
                    Radicand: ToDisplayText(radicand, normalizedCustomSymbols),
                    Degree: match.Groups["degree"].Success
                        ? ToDisplayText(match.Groups["degree"].Value, normalizedCustomSymbols)
                        : string.Empty));
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
        {
            var plain = Parse(text[cursor..], normalizedCustomSymbols);
            if (plain.Length > 0) segments.Add(new MathSegment(SegmentKind.Text, plain));
        }

        if (segments.Count == 0)
        {
            var plain = Parse(text, normalizedCustomSymbols);
            if (plain.Length > 0) segments.Add(new MathSegment(SegmentKind.Text, plain));
        }
        return segments;
    }

    private static IReadOnlyDictionary<string, string> NormalizeCustomSymbols(
        IReadOnlyDictionary<string, string>? customSymbols)
    {
        if (customSymbols is null || customSymbols.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in customSymbols)
        {
            var command = key.Trim();
            if (command.StartsWith('\\')) command = command[1..];
            if (command.Length == 0 || string.IsNullOrWhiteSpace(value)) continue;
            normalized[command] = value.Trim();
        }
        return normalized;
    }

    private static string AddMissingCommandBackslashes(
        string value,
        IReadOnlyDictionary<string, string> customSymbols)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;

        var commands = LooseCommands
            .Concat(customSymbols.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(command => command.Length);
        var result = value;
        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command)) continue;
            result = Regex.Replace(
                result,
                $@"(?<![\\A-Za-z]){Regex.Escape(command)}(?![A-Za-z])",
                "\\" + command,
                RegexOptions.CultureInvariant);
        }
        return result;
    }

    private static string Parse(string value, IReadOnlyDictionary<string, string> customSymbols)
    {
        var output = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '\\':
                    output.Append(ParseCommand(value, ref index, customSymbols));
                    break;
                case '^':
                case '_':
                    output.Append(ParseScript(value, ref index, character == '^' ? Superscripts : Subscripts, customSymbols));
                    break;
                case '{':
                    output.Append(Parse(ReadGroup(value, ref index), customSymbols));
                    break;
                case '}':
                    break;
                case '~':
                    output.Append(' ');
                    break;
                default:
                    output.Append(character);
                    break;
            }
        }
        return output.ToString();
    }

    private static string ParseCommand(
        string value,
        ref int index,
        IReadOnlyDictionary<string, string> customSymbols)
    {
        var commandStart = index + 1;
        if (commandStart >= value.Length) return string.Empty;

        if (!char.IsLetter(value[commandStart]))
        {
            index = commandStart;
            return Symbols.TryGetValue(value[commandStart].ToString(), out var escaped)
                ? escaped
                : value[commandStart] switch
                {
                    ',' or ';' or ':' or ' ' => " ",
                    '!' => string.Empty,
                    '{' => "{",
                    '}' => "}",
                    _ => value[commandStart].ToString()
                };
        }

        var cursor = commandStart;
        while (cursor < value.Length && char.IsLetter(value[cursor])) cursor++;
        if (cursor < value.Length && value[cursor] == '*') cursor++;

        var command = value[commandStart..cursor];
        index = cursor - 1;

        if (customSymbols.TryGetValue(command, out var customSymbol))
        {
            return TryParseFollowingGroup(value, ref index, customSymbols, out var argument) &&
                   customSymbol.Contains("#1", StringComparison.Ordinal)
                ? customSymbol.Replace("#1", argument, StringComparison.Ordinal)
                : customSymbol;
        }

        if (command is "frac" or "dfrac" or "tfrac")
        {
            var numerator = ParseOptionalRequiredGroup(value, ref index, customSymbols);
            var denominator = ParseOptionalRequiredGroup(value, ref index, customSymbols);
            return $"({numerator})/({denominator})";
        }

        if (command is "binom" or "dbinom" or "tbinom")
        {
            var top = ParseOptionalRequiredGroup(value, ref index, customSymbols);
            var bottom = ParseOptionalRequiredGroup(value, ref index, customSymbols);
            return $"C({top},{bottom})";
        }

        if (command == "sqrt")
        {
            var degree = ParseOptionalBracket(value, ref index, customSymbols);
            var radicand = ParseOptionalRequiredGroup(value, ref index, customSymbols);
            var formatted = ShouldWrapRadicand(radicand) ? $"√({radicand})" : $"√{radicand}";
            return string.IsNullOrWhiteSpace(degree) ? formatted : $"{ToScript(degree, Superscripts)}{formatted}";
        }

        if (command is "overline" or "bar")
            return AddCombiningMark(ParseOptionalRequiredGroup(value, ref index, customSymbols), '\u0305');
        if (command is "hat" or "widehat")
            return AddCombiningMark(ParseOptionalRequiredGroup(value, ref index, customSymbols), '\u0302');
        if (command is "tilde" or "widetilde")
            return AddCombiningMark(ParseOptionalRequiredGroup(value, ref index, customSymbols), '\u0303');
        if (command == "vec")
            return AddCombiningMark(ParseOptionalRequiredGroup(value, ref index, customSymbols), '\u20d7');
        if (command is "overrightarrow" or "overleftarrow")
            return ParseOptionalRequiredGroup(value, ref index, customSymbols);

        if (TransparentCommands.Contains(command))
            return ParseOptionalRequiredGroup(value, ref index, customSymbols);

        if (Symbols.TryGetValue(command, out var symbol))
        {
            if (command is "mathbb" or "mathcal" or "mathscr" or "mathfrak")
                return ParseOptionalRequiredGroup(value, ref index, customSymbols);
            return symbol;
        }

        return command;
    }

    private static string ParseScript(
        string value,
        ref int index,
        IReadOnlyDictionary<char, char> map,
        IReadOnlyDictionary<string, string> customSymbols)
    {
        var content = ParseOptionalRequiredGroup(value, ref index, customSymbols);
        return ToScript(content, map, customSymbols);
    }

    private static string ToScript(
        string value,
        IReadOnlyDictionary<char, char> map,
        IReadOnlyDictionary<string, string>? customSymbols = null)
    {
        var parsed = Parse(value, NormalizeCustomSymbols(customSymbols));
        var converted = new StringBuilder();
        foreach (var character in parsed)
            converted.Append(map.TryGetValue(character, out var mapped) ? mapped : character);
        return converted.ToString();
    }

    private static string ParseOptionalRequiredGroup(
        string value,
        ref int index,
        IReadOnlyDictionary<string, string> customSymbols)
    {
        SkipWhiteSpace(value, ref index);
        if (index + 1 >= value.Length) return string.Empty;
        index++;
        if (value[index] == '{') return Parse(ReadGroup(value, ref index), customSymbols);
        return Parse(value[index].ToString(), customSymbols);
    }

    private static bool TryParseFollowingGroup(
        string value,
        ref int index,
        IReadOnlyDictionary<string, string> customSymbols,
        out string argument)
    {
        argument = string.Empty;
        var cursor = index;
        SkipWhiteSpace(value, ref cursor);
        if (cursor + 1 >= value.Length || value[cursor + 1] != '{') return false;
        index = cursor + 1;
        argument = Parse(ReadGroup(value, ref index), customSymbols);
        return true;
    }

    private static string ParseOptionalBracket(
        string value,
        ref int index,
        IReadOnlyDictionary<string, string> customSymbols)
    {
        SkipWhiteSpace(value, ref index);
        if (index + 1 >= value.Length || value[index + 1] != '[') return string.Empty;
        index += 2;
        var start = index;
        var depth = 1;
        while (index < value.Length && depth > 0)
        {
            if (value[index] == '[') depth++;
            else if (value[index] == ']') depth--;
            if (depth > 0) index++;
        }
        return Parse(value[start..index], customSymbols);
    }

    private static string ReadGroup(string value, ref int index)
    {
        var start = index + 1;
        var cursor = start;
        var depth = 1;
        while (cursor < value.Length && depth > 0)
        {
            if (value[cursor] == '{') depth++;
            else if (value[cursor] == '}') depth--;
            cursor++;
        }
        index = Math.Max(start, cursor - 1);
        return value[start..Math.Max(start, cursor - 1)];
    }

    private static void SkipWhiteSpace(string value, ref int index)
    {
        while (index + 1 < value.Length && char.IsWhiteSpace(value[index + 1]))
            index++;
    }

    private static string AddCombiningMark(string value, char mark)
    {
        var output = new StringBuilder();
        foreach (var character in value)
        {
            output.Append(character);
            if (!char.IsWhiteSpace(character)) output.Append(mark);
        }
        return output.ToString();
    }

    private static bool ShouldWrapRadicand(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        !Regex.IsMatch(value, @"^\d+([.,]\d+)?$", RegexOptions.CultureInvariant);

    private static string StripMathDelimiters(string value)
    {
        if (value.StartsWith("$$", StringComparison.Ordinal) &&
            value.EndsWith("$$", StringComparison.Ordinal) &&
            value.Length >= 4)
            return value[2..^2];
        if (value.StartsWith(@"\(", StringComparison.Ordinal) &&
            value.EndsWith(@"\)", StringComparison.Ordinal) &&
            value.Length >= 4)
            return value[2..^2];
        if (value.StartsWith(@"\[", StringComparison.Ordinal) &&
            value.EndsWith(@"\]", StringComparison.Ordinal) &&
            value.Length >= 4)
            return value[2..^2];
        if (value.StartsWith('$') && value.EndsWith('$') && value.Length >= 2)
            return value[1..^1];
        return value;
    }

    private static string CollapseSpaces(string value) =>
        MultiSpaceRegex().Replace(value, " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(
        @"(?<frac>\\(?:dfrac|tfrac|frac)\s*\{(?<num>[^{}]+)\}\s*\{(?<den>[^{}]+)\})|(?<sqrt>\\sqrt(?:\[(?<degree>[^\]]+)\])?\s*(?:\{(?<rad>[^{}]+)\}|(?<radsingle>[A-Za-z0-9.]+)))|(?<slash>\((?<num2>[^()\u4e00-\u9fff]{1,30})\)\s*/\s*\((?<den2>[^()\u4e00-\u9fff]{1,30})\))",
        RegexOptions.CultureInvariant)]
    private static partial Regex MathStructureRegex();
}
