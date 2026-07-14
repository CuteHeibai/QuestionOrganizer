using System.Text;
using System.Text.RegularExpressions;

namespace EaxmBuilder.Export;

internal static partial class MathTextFormatter
{
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
        ["simeq"] = "≃", ["cong"] = "≅", ["propto"] = "∝", ["parallel"] = "∥",
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
        ["log"] = "log", ["ln"] = "ln", ["lg"] = "lg", ["lim"] = "lim", ["max"] = "max", ["min"] = "min"
    };

    private static readonly HashSet<string> TransparentCommands = new(StringComparer.Ordinal)
    {
        "mathrm", "mathbf", "mathit", "mathsf", "mathtt", "text", "textit", "textbf",
        "operatorname", "operatorname*", "displaystyle", "textstyle", "scriptstyle",
        "scriptscriptstyle", "left", "right", "big", "Big", "bigg", "Bigg", "begin", "end"
    };

    public static string ToDisplayText(
        string latex,
        IReadOnlyDictionary<string, string>? customSymbols = null)
    {
        var value = StripMathDelimiters(latex.Trim());
        return CollapseSpaces(Parse(value, NormalizeCustomSymbols(customSymbols))).Trim();
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
            return string.IsNullOrWhiteSpace(degree) ? $"√({radicand})" : $"{ToScript(degree, Superscripts)}√({radicand})";
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
}
