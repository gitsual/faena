using System.Text;
using System.Text.RegularExpressions;

namespace Faena.Escritorio;

/// <summary>
/// El selector duradero de un control de escritorio, el equivalente a los de Playwright en la
/// web: <c>uia=Tipo[clave="valor"]…</c>. Claves: <c>id</c> (AutomationId), <c>name</c> (el nombre
/// visible), <c>ventana</c> (el título de la ventana, cuando no es la principal: un diálogo) y
/// <c>n</c> (cuál de los que coinciden, desde 1, si hay varios iguales). El tipo es el
/// <c>ControlType</c> de UI Automation sin prefijo (<c>Button</c>, <c>Edit</c>, <c>MenuItem</c>…)
/// o <c>*</c>. Ejemplos: <c>uia=Button[id="anadir"]</c>, <c>uia=Button[ventana="Notas"][name="Sí"]</c>.
/// </summary>
public sealed record SelectorUia(string Tipo, string? Id = null, string? Nombre = null, string? Ventana = null, int N = 1)
{
    public const string Prefijo = "uia=";

    private static readonly Regex Forma = new(@"^uia=(?<tipo>[A-Za-z*]+)(?<attrs>(\[[a-z]+=""(?:[^""\\]|\\.)*""\])*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Attr = new(@"\[(?<k>[a-z]+)=""(?<v>(?:[^""\\]|\\.)*)""\]", RegexOptions.CultureInvariant);

    public static bool Es(string selector) => selector.StartsWith(Prefijo, StringComparison.Ordinal);

    public static SelectorUia Parse(string selector)
    {
        var m = Forma.Match((selector ?? "").Trim());
        if (!m.Success) throw new ArgumentException($"selector de escritorio inválido «{selector}»: se espera uia=Tipo[id=\"…\"] o uia=Tipo[name=\"…\"]");
        var s = new SelectorUia(m.Groups["tipo"].Value);
        foreach (Match a in Attr.Matches(m.Groups["attrs"].Value))
        {
            var v = Regex.Unescape(a.Groups["v"].Value);
            s = a.Groups["k"].Value switch
            {
                "id" => s with { Id = v },
                "name" => s with { Nombre = v },
                "ventana" => s with { Ventana = v },
                "n" => s with { N = int.TryParse(v, out var n) && n > 0 ? n : throw new ArgumentException($"«n» debe ser un número positivo en «{selector}»") },
                var k => throw new ArgumentException($"clave desconocida «{k}» en «{selector}»: usa id, name, ventana o n"),
            };
        }
        if (s.Id is null && s.Nombre is null) throw new ArgumentException($"el selector «{selector}» necesita id o name");
        return s;
    }

    public override string ToString()
    {
        var sb = new StringBuilder(Prefijo).Append(Tipo);
        if (Ventana is { } w) sb.Append("[ventana=").Append(Cita(w)).Append(']');
        if (Id is { } id) sb.Append("[id=").Append(Cita(id)).Append(']');
        else if (Nombre is { } n) sb.Append("[name=").Append(Cita(n)).Append(']');
        if (N > 1) sb.Append("[n=\"").Append(N).Append("\"]");
        return sb.ToString();
    }

    private static string Cita(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// Si un AutomationId sirve para volver a encontrar el control: los que pone el programador sí;
    /// los vacíos, los numéricos largos (handles) y los que genera el sistema para la barra de
    /// título no.
    /// </summary>
    public static bool IdDuradero(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 80 && !Regex.IsMatch(id, @"^\d{5,}$") && !Regex.IsMatch(id, @"^\d+(\.\d+)+$");
}

/// <summary>Las palabras con que el agente nombra cada tipo de control, las mismas que en la web.</summary>
public static class TiposUia
{
    /// <summary>ProgrammaticName sin «ControlType.» → cómo se dice; solo los tipos con los que se actúa.</summary>
    public static readonly IReadOnlyDictionary<string, string> Interactivos = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Button"] = "el botón",
        ["SplitButton"] = "el botón",
        ["Edit"] = "el campo",
        ["Document"] = "el documento",
        ["MenuItem"] = "la opción del menú",
        ["ComboBox"] = "el desplegable",
        ["CheckBox"] = "la casilla",
        ["RadioButton"] = "la opción",
        ["ListItem"] = "el elemento de la lista",
        ["TabItem"] = "la pestaña",
        ["TreeItem"] = "el elemento",
        ["Hyperlink"] = "el enlace",
        ["DataItem"] = "la fila",
        ["Slider"] = "el control deslizante",
        ["Spinner"] = "el campo numérico",
    };

    /// <summary>«ControlType.Button» → «Button».</summary>
    public static string Corto(string programmaticName) =>
        programmaticName.StartsWith("ControlType.", StringComparison.Ordinal) ? programmaticName["ControlType.".Length..] : programmaticName;

    public static string? Tipo(string corto) => Interactivos.GetValueOrDefault(corto);
}
