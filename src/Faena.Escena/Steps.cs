using System.Globalization;
using System.Text;

namespace Faena.Escena;

/// <summary>
/// The step language that tramoya writes in its projects and profiles: one verb per line,
/// <c>click #save</c>, <c>type #title | Hello</c>, <c>say 6</c>. Faena replays a learned skill
/// with these same lines, so a process recorded in tramoya can be taught to faena as it is;
/// keep the verbs in step with tramoya's <c>Steps.cs</c>.
/// </summary>
public sealed record Step(string Verb, List<string> Args);

public class StepError : ArgumentException
{
    public StepError(string message) : base(message) { }
    public StepError(string message, Exception inner) : base(message, inner) { }
}

public static class Steps
{
    /// <summary>verb -> (min args, max args, help)</summary>
    public static readonly IReadOnlyDictionary<string, (int Min, int Max, string Usage)> Verbs =
        new Dictionary<string, (int, int, string)>
        {
            ["click"] = (1, 1, "click <selector>"),
            ["click_text"] = (1, 1, "click_text <visible text>"),
            ["type"] = (2, 2, "type <selector> | <text>"),
            ["select"] = (2, 2, "select <selector> | <value>"),
            ["clear"] = (1, 1, "clear <selector>"),
            ["point"] = (1, 1, "point <selector>"),
            ["scroll"] = (1, 1, "scroll <pixels>"),
            ["press"] = (1, 1, "press <key>"),
            ["goto"] = (1, 1, "goto <path or url>"),
            ["beat"] = (1, 1, "beat <seconds>"),
            ["say"] = (0, 1, "say [fallback seconds]  (plays this scene's narration)"),
            ["wait"] = (2, 2, "wait <selector> | <max seconds>  (a wait window the montage speeds up)"),
        };

    public static Step Parse(string line)
    {
        var text = line.Trim();
        var space = text.IndexOf(' ');
        var verb = space < 0 ? text : text[..space];
        var rest = space < 0 ? "" : text[(space + 1)..];
        if (!Verbs.TryGetValue(verb, out var spec))
        {
            var known = string.Join(", ", Verbs.Keys.Order(StringComparer.Ordinal));
            throw new StepError($"unknown step {PyRepr.Of(verb)} in {PyRepr.Of(line)}; known: {known}");
        }
        var (lo, hi, usage) = spec;
        rest = rest.Trim();
        List<string> args;
        if (hi == 2)
            args = rest.Length > 0 ? rest.Split('|', 2).Select(part => part.Trim()).ToList() : new List<string>();
        else
            args = rest.Length > 0 ? new List<string> { rest } : new List<string>();
        if (args.Count < lo || args.Count > hi)
            throw new StepError($"{verb} takes {usage}, got {PyRepr.Of(line)}");
        return new Step(verb, args);
    }

    /// <summary>
    /// Drive <paramref name="stage"/> through <paramref name="steps"/>. <c>say(fallback)</c>
    /// plays the scene's narration; the caller binds the key. A bad line is reported with
    /// its number so the person can fix the profile.
    /// </summary>
    public static async Task RunAsync(IStage stage, IList<string> steps, Func<double, Task> say, string baseUrl = "")
    {
        var number = 0;
        foreach (var line in steps)
        {
            number++;
            Step step;
            try
            {
                step = Parse(line);
            }
            catch (StepError exc)
            {
                throw new StepError($"line {number}: {exc.Message}", exc);
            }
            var a = step.Args;
            switch (step.Verb)
            {
                case "click": await stage.ClickAsync(a[0]); break;
                case "click_text": await stage.ClickTextAsync(a[0]); break;
                case "type": await stage.TypeIntoAsync(a[0], a[1]); break;
                case "select": await stage.SelectAsync(a[0], a[1]); break;
                case "clear": await stage.ClearAsync(a[0]); break;
                case "point": await stage.PointAtAsync(a[0]); break;
                case "scroll": await stage.ScrollAsync((int)Math.Truncate(Float(a[0]))); break;
                case "press": await stage.PressAsync(a[0]); break;
                case "goto":
                {
                    var target = a[0];
                    if (baseUrl.Length > 0
                        && !target.StartsWith("http://", StringComparison.Ordinal)
                        && !target.StartsWith("https://", StringComparison.Ordinal))
                        target = baseUrl.TrimEnd('/') + "/" + target.TrimStart('/');
                    await stage.GotoAsync(target);
                    break;
                }
                case "beat": await stage.BeatAsync(Float(a[0])); break;
                case "say": await say(a.Count > 0 ? Float(a[0]) : 0.0); break;
                case "wait": await stage.WaitForAsync(a[0], Float(a[1])); break;
            }
        }
    }

    /// <summary>Python's <c>float(text)</c>, with its error as an <c>ArgumentException</c>.</summary>
    private static double Float(string text)
    {
        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new ArgumentException($"could not convert string to float: {PyRepr.Of(text)}");
    }
}

/// <summary>
/// Python's <c>repr()</c> for the strings and lists that appear in error messages, so the
/// two implementations complain in the same words.
/// </summary>
internal static class PyRepr
{
    public static string Of(string s)
    {
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder().Append(quote);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c == quote) sb.Append('\\').Append(c);
                    else if (c < 0x20 || c == 0x7f) sb.Append("\\x").Append(((int)c).ToString("x2"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append(quote).ToString();
    }

    public static string Of(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Of)) + "]";
}
