namespace Faena.Agente;

/// <summary>
/// The language of everything a person reads: the CLI and its help, the window, the log, the
/// reports, the steps the agent writes down («Click the “Save” button») and the question before
/// anything irreversible. English by default, Spanish to choose (<c>--lang</c>, <c>FAENA_LANG</c> or
/// the window's Settings). The same pattern as tramoya's <c>Lengua</c>.
/// </summary>
/// <remarks>
/// What the model reads is always English (<see cref="Protocol"/>); it is only asked to write its
/// summary in this language. The observer and the desktop still name elements with the Spanish
/// role phrases they always used («el botón», «la casilla»): those are internal names, turned into
/// the words of the chosen language at the edge. The guard's words (<see cref="Guardian"/>) do not
/// depend on this: it recognises Spanish and English controls whatever the interface says.
/// </remarks>
public static class Lengua
{
    public const string Variable = "FAENA_LANG";
    public static readonly IReadOnlyList<string> Idiomas = ["en", "es"];

    private static string _actual = Normalizar(Environment.GetEnvironmentVariable(Variable));

    /// <summary>The current language; the jobs the window launches inherit it through <c>FAENA_LANG</c> and <c>--lang</c>.</summary>
    public static string Actual
    {
        get => _actual;
        set => _actual = Normalizar(value);
    }

    public static bool Es => _actual == "es";

    /// <summary>«es», «ES», «es-ES» → «es»; anything unknown or empty → «en».</summary>
    public static string Normalizar(string? lang)
    {
        var l = (lang ?? "").Trim().ToLowerInvariant();
        if (l.Length > 2) l = l[..2];
        return Idiomas.Contains(l) ? l : "en";
    }

    /// <summary>Whether a value names a language faena speaks (for validating flags and settings).</summary>
    public static bool Valido(string? lang)
    {
        var l = (lang ?? "").Trim().ToLowerInvariant();
        return l.Length >= 2 && Idiomas.Contains(l[..2]);
    }

    /// <summary>The Spanish or the English text, by language.</summary>
    public static string T(string es, string en, string? lang = null) => Normalizar(lang ?? _actual) == "es" ? es : en;

    /// <summary>The language as the model should read it in «write the summary in …».</summary>
    public static string NombreParaModelo(string? lang = null) => T("Spanish", "English", lang);

    // ------------------------------------------------------------------ the words of the steps

    private static readonly Dictionary<string, string> RolesEn = new(StringComparer.Ordinal)
    {
        ["el botón"] = "button", ["el enlace"] = "link", ["la casilla"] = "checkbox", ["el campo"] = "field",
        ["el campo numérico"] = "number field", ["la celda"] = "cell", ["la columna"] = "column", ["la imagen"] = "image",
        ["la pestaña"] = "tab", ["la opción del menú"] = "menu item", ["la opción"] = "option", ["el elemento"] = "item",
        ["el interruptor"] = "switch", ["el desplegable"] = "drop-down", ["la fila"] = "row", ["el título"] = "heading",
        ["el buscador"] = "search box", ["la lista"] = "list", ["el texto"] = "text", ["la etiqueta"] = "label",
        ["el menú"] = "menu", ["la ventana"] = "window", ["el diálogo"] = "dialog", ["el grupo"] = "group",
        ["la barra"] = "bar", ["el árbol"] = "tree", ["la tabla"] = "table", ["el control deslizante"] = "slider",
    };

    private static readonly Dictionary<string, string> EstadosEn = new(StringComparer.Ordinal)
    {
        ["marcado"] = "checked", ["abierto"] = "expanded", ["seleccionado"] = "selected", ["deshabilitado"] = "disabled",
        ["normal"] = "normal", ["solo lectura"] = "read-only", ["cerrado"] = "collapsed",
    };

    private static readonly Dictionary<string, string> TeclasEs = new(StringComparer.Ordinal)
    {
        ["Enter"] = "Intro", ["Tab"] = "Tabulador", ["Escape"] = "Esc", ["Backspace"] = "Borrar",
        ["ArrowDown"] = "flecha abajo", ["ArrowUp"] = "flecha arriba",
    };

    private static readonly Dictionary<string, string> TeclasEn = new(StringComparer.Ordinal)
    {
        ["Enter"] = "Enter", ["Tab"] = "Tab", ["Escape"] = "Esc", ["Backspace"] = "Backspace",
        ["ArrowDown"] = "down arrow", ["ArrowUp"] = "up arrow",
    };

    /// <summary>An internal role phrase («el botón») in the language: as it is in Spanish, without article in English.</summary>
    public static string Rol(string rol, string? lang = null) =>
        T(rol, RolesEn.GetValueOrDefault(rol, rol.Length == 0 ? "" : rol.StartsWith("el ", StringComparison.Ordinal) || rol.StartsWith("la ", StringComparison.Ordinal) ? "item" : rol), lang);

    /// <summary>An element's state («marcado, abierto») in the language.</summary>
    public static string Estado(string estado, string? lang = null) =>
        T(estado, string.Join(", ", estado.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(e => EstadosEn.GetValueOrDefault(e, e))), lang);

    /// <summary>The thing a step acts on: «el botón «Guardar»» / «the “Save” button».</summary>
    public static string Cosa(string rol, string nombre, string? lang = null)
    {
        if (T("es", "en", lang) == "es")
            return nombre.Length != 0 ? (rol.Length != 0 ? $"{rol} «{nombre}»" : $"«{nombre}»") : (rol.Length != 0 ? rol : "este elemento");
        var r = Rol(rol, "en");
        return nombre.Length != 0 ? (r.Length != 0 ? $"the “{nombre}” {r}" : $"“{nombre}”") : (r.Length != 0 ? $"the {r}" : "this item");
    }

    /// <summary>«Pulsa …», «Marca …», «Desmarca …» (marca: "on", "off" or "").</summary>
    public static string Clic(string cosa, string marca = "", string? lang = null) => marca switch
    {
        "on" => T($"Marca {cosa}", $"Check {cosa}", lang),
        "off" => T($"Desmarca {cosa}", $"Uncheck {cosa}", lang),
        _ => T($"Pulsa {cosa}", $"Click {cosa}", lang),
    };

    public static string Cookies(string desc, string? lang = null) =>
        desc + T(" (aviso de cookies: si no sale, se salta)", " (cookie notice: skipped if it does not appear)", lang);

    public static string Escribe(string valor, string campo, string? lang = null) => campo.Length != 0
        ? T($"Escribe «{valor}» en el campo «{campo}»", $"Type “{valor}” in the “{campo}” field", lang)
        : T($"Escribe «{valor}»", $"Type “{valor}”", lang);

    public static string Elige(string valor, string donde, string? lang = null) => donde.Length != 0
        ? T($"Elige «{valor}» en «{donde}»", $"Choose “{valor}” in “{donde}”", lang)
        : T($"Elige «{valor}»", $"Choose “{valor}”", lang);

    /// <summary>A key by its name in the language («Intro» / «Enter»).</summary>
    public static string NombreTecla(string tecla, string? lang = null) =>
        T(TeclasEs.GetValueOrDefault(tecla, tecla), TeclasEn.GetValueOrDefault(tecla, tecla), lang);

    public static string Tecla(string tecla, string? lang = null) => T(
        $"Pulsa la tecla {NombreTecla(tecla, "es")}", $"Press {NombreTecla(tecla, "en")}", lang);

    public static string Ve(string url, string? lang = null) => T($"Ve a «{url}»", $"Go to “{url}”", lang);

    public static string Senala(string cosa, string? lang = null) => T($"Señala {cosa}", $"Point at {cosa}", lang);

    /// <summary>A value quoted the way the language quotes: «x» or “x”.</summary>
    public static string Comillas(string s, string? lang = null) => T($"«{s}»", $"“{s}”", lang);

    /// <summary>
    /// The quoted pieces of a text, in either language's quotes, in order: «Escribe «x» en el campo
    /// «Title»» and «Type “x” in the “Title” field» both give [x, Title]. The steps of a learned
    /// skill name their values and fields this way, so a skill learned in one language is read in the other.
    /// </summary>
    public static List<string> Citas(string texto)
    {
        var lista = new List<string>();
        var m = System.Text.RegularExpressions.Regex.Matches(texto ?? "", "«([^»]*)»|“([^”]*)”");
        foreach (System.Text.RegularExpressions.Match x in m)
            lista.Add(x.Groups[1].Success ? x.Groups[1].Value : x.Groups[2].Value);
        return lista;
    }
}
