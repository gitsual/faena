// A small argparse look-alike: just the slice `cli.py` uses (positionals, `--flag value`,
// switches, `action=append`, `choices`, `type=float/int`, `required`, `nargs="?"`,
// nested subparsers with aliases, `--version`), with usage, help and error texts laid
// out the way argparse lays them out (taken from tramoya, whose CLI mirrors a Python one).
//
// Parsing is strict in the argparse sense: `--help` wins wherever it appears, an unknown
// flag or a missing required one prints the usage and exits with 2, and a subcommand
// swallows every argument after it.
using System.Globalization;
using System.Text;

namespace Faena;

/// <summary>What argparse does with <c>sys.exit</c>: the text already printed, the code to return.</summary>
internal sealed class ArgParseExit : Exception
{
    public int Code { get; }

    public ArgParseExit(int code, string message) : base(message) => Code = code;
}

internal enum ArgKind { String, Float, Int }

internal sealed class ArgSpec
{
    public required string Dest { get; init; }
    public string? Flag { get; init; }
    public bool Positional => Flag is null;
    public bool Switch { get; init; }
    public bool Append { get; init; }
    public bool Required { get; init; }
    public bool Optional { get; init; }
    public string? Default { get; init; }
    public string[]? Choices { get; init; }
    public string? Help { get; init; }
    public ArgKind Kind { get; init; } = ArgKind.String;

    public string Metavar => Choices is { } c ? "{" + string.Join(",", c) + "}"
        : Positional ? Dest : Dest.ToUpperInvariant();

    /// <summary>How the argument shows in the usage line.</summary>
    public string Usage()
    {
        if (Positional)
            return Optional ? $"[{Metavar}]" : Metavar;
        var body = Switch ? Flag! : $"{Flag} {Metavar}";
        return Required ? body : $"[{body}]";
    }

    /// <summary>How the argument shows in the help list.</summary>
    public string Invocation() => Positional ? Metavar : Switch ? Flag! : $"{Flag} {Metavar}";
}

internal sealed class ParsedArgs
{
    private readonly Dictionary<string, object?> _values = new();

    public Func<ParsedArgs, int>? Handler { get; set; }

    public void Set(string dest, object? value) => _values[dest] = value;

    public bool Has(string dest) => _values.ContainsKey(dest);

    public string? Get(string dest) => _values.TryGetValue(dest, out var v) ? v as string : null;

    public string GetRequired(string dest) =>
        Get(dest) ?? throw new KeyNotFoundException($"argument {dest} was not parsed");

    public bool Flag(string dest) => _values.TryGetValue(dest, out var v) && v is true;

    public List<string>? List(string dest) => _values.TryGetValue(dest, out var v) ? v as List<string> : null;

    public double? Double(string dest) => Get(dest) is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : null;

    public double Double(string dest, double fallback) => Double(dest) ?? fallback;

    public int? Int(string dest) => Get(dest) is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : null;

    /// <summary>Every dest parsed so far (to merge a subparser's namespace into its parent's).</summary>
    public IEnumerable<string> Dests() => _values.Keys.ToList();

    public object? Raw(string dest) => _values[dest];
}

internal sealed class ArgParser
{
    private const int Width = 78;
    private const int MaxHelpPosition = 24;

    private readonly List<ArgSpec> _specs = new();
    private readonly List<ArgParser> _subparsers = new();
    private readonly Dictionary<string, ArgParser> _subByName = new(StringComparer.Ordinal);
    private string? _subDest;
    private string? _version;

    public string Prog { get; }
    public string? Description { get; }
    public string? Help { get; }
    public string Name { get; }
    public IReadOnlyList<string> Aliases { get; }
    public Func<ParsedArgs, int>? Handler { get; set; }

    public ArgParser(string prog, string? description = null, string? help = null, string? name = null,
                     IReadOnlyList<string>? aliases = null)
    {
        Prog = prog;
        Description = description;
        Help = help;
        Name = name ?? prog;
        Aliases = aliases ?? Array.Empty<string>();
    }

    // --- building -----------------------------------------------------------------

    public ArgParser Add(string name, string? help = null, string? @default = null, bool required = false,
                         string[]? choices = null, ArgKind kind = ArgKind.String, bool optional = false)
    {
        var positional = !name.StartsWith("--", StringComparison.Ordinal);
        _specs.Add(new ArgSpec
        {
            Dest = positional ? name : DestOf(name), Flag = positional ? null : name, Help = help,
            Default = @default, Required = required, Choices = choices, Kind = kind, Optional = optional,
        });
        return this;
    }

    public ArgParser AddSwitch(string flag, string? help = null)
    {
        _specs.Add(new ArgSpec { Dest = DestOf(flag), Flag = flag, Switch = true, Help = help });
        return this;
    }

    public ArgParser AddAppend(string flag, string? help = null)
    {
        _specs.Add(new ArgSpec { Dest = DestOf(flag), Flag = flag, Append = true, Help = help });
        return this;
    }

    public ArgParser AddVersion(string version)
    {
        _version = version;
        return this;
    }

    public ArgParser AddSubparser(string dest, string name, string? help = null, string? description = null,
                                  params string[] aliases)
    {
        _subDest = dest;
        var sub = new ArgParser($"{Prog} {name}", description, help, name, aliases);
        _subparsers.Add(sub);
        _subByName[name] = sub;
        foreach (var alias in aliases)
            _subByName[alias] = sub;
        return sub;
    }

    private static string DestOf(string flag) => flag.TrimStart('-').Replace('-', '_');

    // --- parsing ------------------------------------------------------------------

    public ParsedArgs Parse(IReadOnlyList<string> argv)
    {
        var result = new ParsedArgs();
        foreach (var spec in _specs.Where(s => !s.Positional))
            result.Set(spec.Dest, spec.Switch ? false : spec.Append ? null : spec.Default);
        foreach (var spec in _specs.Where(s => s.Positional && s.Optional))
            result.Set(spec.Dest, spec.Default);
        var positionals = new Queue<ArgSpec>(_specs.Where(s => s.Positional));
        var seen = new HashSet<string>();
        var unknown = new List<string>();

        for (var i = 0; i < argv.Count; i++)
        {
            var token = argv[i];
            if (token is "-h" or "--help")
                throw new ArgParseExit(0, FormatHelp());
            if (token == "--version" && _version is not null)
                throw new ArgParseExit(0, _version + "\n");
            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                string? inline = null;
                var eq = token.IndexOf('=');
                if (eq > 0)
                {
                    inline = token[(eq + 1)..];
                    token = token[..eq];
                }
                var spec = _specs.FirstOrDefault(s => s.Flag == token);
                if (spec is null)
                {
                    unknown.Add(argv[i]);
                    continue;
                }
                seen.Add(spec.Dest);
                if (spec.Switch)
                {
                    if (inline is not null)
                        Error($"argument {spec.Flag}: ignored explicit argument '{inline}'");
                    result.Set(spec.Dest, true);
                    continue;
                }
                string value;
                if (inline is not null)
                    value = inline;
                else if (i + 1 < argv.Count && !LooksLikeFlag(argv[i + 1]))
                    value = argv[++i];
                else
                {
                    Error($"argument {spec.Flag}: expected one argument");
                    return result;
                }
                Validate(spec, value);
                if (spec.Append)
                {
                    var list = result.List(spec.Dest) ?? new List<string>();
                    list.Add(value);
                    result.Set(spec.Dest, list);
                }
                else
                    result.Set(spec.Dest, value);
                continue;
            }
            if (positionals.Count > 0)
            {
                var spec = positionals.Dequeue();
                Validate(spec, token);
                result.Set(spec.Dest, token);
                continue;
            }
            if (_subDest is not null)
            {
                if (!_subByName.TryGetValue(token, out var sub))
                    Error($"argument {_subDest}: invalid choice: '{token}' (choose from {SubChoices()})");
                CheckMissing(result, positionals, seen, subcommandSeen: true);
                if (unknown.Count > 0)
                    Error($"unrecognized arguments: {string.Join(" ", unknown)}");
                result.Set(_subDest, sub!.Name);
                var nested = sub.Parse(argv.Skip(i + 1).ToList());
                foreach (var dest in nested.Dests())
                    result.Set(dest, nested.Raw(dest));
                result.Handler = nested.Handler ?? sub.Handler;
                return result;
            }
            unknown.Add(token);
        }

        CheckMissing(result, positionals, seen, subcommandSeen: false);
        if (unknown.Count > 0)
            Error($"unrecognized arguments: {string.Join(" ", unknown)}");
        result.Handler = Handler;
        return result;
    }

    private static bool LooksLikeFlag(string token) =>
        token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2;

    private void Validate(ArgSpec spec, string value)
    {
        var label = spec.Positional ? spec.Dest : spec.Flag!;
        if (spec.Choices is { } choices && !choices.Contains(value))
            Error($"argument {label}: invalid choice: '{value}' (choose from {string.Join(", ", choices)})");
        if (spec.Kind == ArgKind.Float && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            Error($"argument {label}: invalid float value: '{value}'");
        if (spec.Kind == ArgKind.Int && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            Error($"argument {label}: invalid int value: '{value}'");
    }

    private void CheckMissing(ParsedArgs result, Queue<ArgSpec> positionals, HashSet<string> seen, bool subcommandSeen)
    {
        var missing = new List<string>();
        foreach (var spec in _specs)
        {
            if (spec.Positional)
            {
                if (positionals.Contains(spec) && !spec.Optional)
                    missing.Add(spec.Dest);
            }
            else if (spec.Required && !seen.Contains(spec.Dest))
                missing.Add(spec.Flag!);
        }
        if (_subDest is not null && !subcommandSeen)
            missing.Add(_subDest);
        if (missing.Count > 0)
            Error($"the following arguments are required: {string.Join(", ", missing)}");
    }

    private string SubChoices() => string.Join(", ", _subByName.Keys.Select(k => $"'{k}'"));

    /// <summary>argparse's metavar for the subcommands: names and aliases, in order of definition.</summary>
    private string SubChoicesBraced() => "{" + string.Join(",", _subByName.Keys) + "}";

    private void Error(string message) =>
        throw new ArgParseExit(2, FormatUsage() + $"{Prog}: error: {message}\n");

    // --- help text ------------------------------------------------------------------

    public string FormatUsage()
    {
        var parts = new List<string> { "[-h]" };
        if (_version is not null)
            parts.Add("[--version]");
        parts.AddRange(_specs.Where(s => !s.Positional).Select(s => s.Usage()));
        parts.AddRange(_specs.Where(s => s.Positional).Select(s => s.Usage()));
        if (_subparsers.Count > 0)
        {
            parts.Add(SubChoicesBraced());
            parts.Add("...");
        }
        var head = $"usage: {Prog} ";
        var oneLine = head + string.Join(" ", parts);
        if (oneLine.Length <= Width)
            return oneLine + "\n";

        // argparse wraps the optionals and the positionals as two groups, each on
        // lines of its own, indented under the program name.
        var indent = new string(' ', head.Length);
        var optionals = parts.TakeWhile(p => p.StartsWith('[') || p.StartsWith("--", StringComparison.Ordinal)).ToList();
        var rest = parts.Skip(optionals.Count).ToList();
        var sb = new StringBuilder();
        var lines = WrapParts(optionals, Width - head.Length);
        if (rest.Count > 0)
            lines.AddRange(WrapParts(rest, Width - head.Length));
        for (var i = 0; i < lines.Count; i++)
            sb.Append(i == 0 ? head : indent).Append(lines[i]).Append('\n');
        return sb.ToString();
    }

    private static List<string> WrapParts(IEnumerable<string> parts, int width)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var part in parts)
        {
            if (current.Length > 0 && current.Length + 1 + part.Length > width)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0)
                current.Append(' ');
            current.Append(part);
        }
        if (current.Length > 0)
            lines.Add(current.ToString());
        return lines;
    }

    public string FormatHelp()
    {
        var sb = new StringBuilder(FormatUsage());
        if (Description is { } description)
            sb.Append('\n').Append(Wrap(Description, Width, "")).Append('\n');

        var rows = new List<(string Invocation, string? Help, int Indent)>();
        var positionalRows = new List<(string Invocation, string? Help, int Indent)>();
        foreach (var spec in _specs.Where(s => s.Positional))
            positionalRows.Add((spec.Invocation(), spec.Help, 2));
        if (_subparsers.Count > 0)
        {
            positionalRows.Add((SubChoicesBraced(), null, 2));
            foreach (var sub in _subparsers)
            {
                var invocation = sub.Aliases.Count > 0 ? $"{sub.Name} ({string.Join(", ", sub.Aliases)})" : sub.Name;
                positionalRows.Add((invocation, sub.Help, 4));
            }
        }
        var optionRows = new List<(string Invocation, string? Help, int Indent)> { ("-h, --help", "show this help message and exit", 2) };
        if (_version is not null)
            optionRows.Add(("--version", "show program's version number and exit", 2));
        foreach (var spec in _specs.Where(s => !s.Positional))
            optionRows.Add((spec.Invocation(), spec.Help, 2));
        rows.AddRange(positionalRows);
        rows.AddRange(optionRows);

        var helpPosition = Math.Min(rows.Max(r => r.Invocation.Length + r.Indent) + 2, MaxHelpPosition);
        if (positionalRows.Count > 0)
        {
            sb.Append("\npositional arguments:\n");
            foreach (var row in positionalRows)
                sb.Append(FormatRow(row, helpPosition));
        }
        sb.Append("\noptions:\n");
        foreach (var row in optionRows)
            sb.Append(FormatRow(row, helpPosition));
        return sb.ToString();
    }

    private static string FormatRow((string Invocation, string? Help, int Indent) row, int helpPosition)
    {
        var lead = new string(' ', row.Indent) + row.Invocation;
        if (row.Help is null)
            return lead + "\n";
        var helpIndent = new string(' ', helpPosition);
        var wrapped = Wrap(row.Help, Width - helpPosition, helpIndent);
        if (lead.Length + 2 > helpPosition)
            return lead + "\n" + wrapped + "\n";
        return lead.PadRight(helpPosition) + wrapped[helpPosition..] + "\n";
    }

    /// <summary>textwrap.fill: greedy word wrap, every line prefixed with <paramref name="indent"/>.</summary>
    private static string Wrap(string text, int width, string indent)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in words)
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > width)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0)
                current.Append(' ');
            current.Append(word);
        }
        if (current.Length > 0)
            lines.Add(current.ToString());
        return string.Join("\n", lines.Select(l => indent + l));
    }
}
