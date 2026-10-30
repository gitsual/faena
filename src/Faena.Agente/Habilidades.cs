using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace Faena.Agente;

/// <summary>
/// Un procedimiento aprendido: los pasos de una ejecución que salió bien (o de un proyecto de
/// <c>easy</c>), con los valores que cambian de una vez a otra convertidos en parámetros
/// (<c>{titulo}</c>). Vive en un <c>.toml</c> de la carpeta de habilidades; el formato está en
/// <c>windows/HABILIDADES.md</c>.
/// </summary>
public sealed class Habilidad
{
    /// <summary>Identificador corto (el nombre del archivo sin <c>.toml</c>).</summary>
    public string Nombre { get; set; } = "";
    /// <summary>Qué consigue, en una frase: es lo que lee el modelo para decidir si encaja.</summary>
    public string Descripcion { get; set; } = "";
    /// <summary>Palabras o expresiones que la delatan en un objetivo («nota nueva», «apuntar»).</summary>
    public List<string> Cuando { get; set; } = [];
    /// <summary>«web» o «escritorio».</summary>
    public string Superficie { get; set; } = "web";
    /// <summary>Dónde empieza: la url, o la aplicación de escritorio. Vacío: donde esté.</summary>
    public string Inicio { get; set; } = "";
    /// <summary>Cada parámetro con el valor de ejemplo (el que se usó al aprenderla).</summary>
    public Dictionary<string, string> Parametros { get; set; } = [];
    /// <summary>Pasos en el formato de <c>easy</c>: <c>tipo</c>, <c>selector</c>, <c>valor</c>, <c>desc</c>, <c>opcional</c>.</summary>
    public List<JsonObject> Pasos { get; set; } = [];
    public string Origen { get; set; } = "";
    public string Aprendida { get; set; } = "";
    /// <summary>El archivo del que se ha leído (vacío si aún no se ha guardado).</summary>
    public string Ruta { get; set; } = "";
}

/// <summary>La habilidad elegida para un objetivo y los valores de sus parámetros.</summary>
public sealed record Eleccion(Habilidad Habilidad, IReadOnlyDictionary<string, string> Valores);

public static class Habilidades
{
    public static readonly IReadOnlySet<string> Tipos = new HashSet<string>(StringComparer.Ordinal) { "clic", "señalar", "escribir", "elegir", "tecla", "ir", "esperar" };

    private static readonly Regex Hueco = new(@"\{([\p{L}\p{N}_]+)\}", RegexOptions.CultureInvariant);

    // ------------------------------------------------------------------ leer y escribir

    /// <summary>Las habilidades de una carpeta (las que no se pueden leer se cuentan en <paramref name="avisos"/>).</summary>
    public static List<Habilidad> Cargar(string carpeta, List<string>? avisos = null)
    {
        var lista = new List<Habilidad>();
        if (!Directory.Exists(carpeta)) return lista;
        foreach (var ruta in Directory.EnumerateFiles(carpeta, "*.toml").Order(StringComparer.OrdinalIgnoreCase))
        {
            try { lista.Add(Leer(File.ReadAllText(ruta), ruta)); }
            catch (Exception e) when (e is ArgumentException or TomlException or IOException)
            {
                avisos?.Add($"{Path.GetFileName(ruta)}: {e.Message}");
            }
        }
        return lista;
    }

    public static Habilidad Leer(string toml, string ruta = "")
    {
        var tabla = Toml.ToModel(toml);
        var h = new Habilidad
        {
            Nombre = Texto(tabla, "nombre"),
            Descripcion = Texto(tabla, "descripcion"),
            Superficie = Texto(tabla, "superficie") is { Length: > 0 } s ? s : "web",
            Inicio = Texto(tabla, "inicio"),
            Origen = Texto(tabla, "origen"),
            Aprendida = Texto(tabla, "aprendida"),
            Ruta = ruta,
        };
        if (h.Nombre.Length == 0) h.Nombre = ruta.Length != 0 ? Path.GetFileNameWithoutExtension(ruta) : "";
        if (h.Nombre.Length == 0) throw new ArgumentException("la habilidad no tiene nombre");
        if (tabla.TryGetValue("cuando", out var cuando) && cuando is TomlArray arr)
            h.Cuando = arr.Select(x => x?.ToString() ?? "").Where(x => x.Trim().Length != 0).ToList();
        if (tabla.TryGetValue("parametros", out var ps) && ps is TomlTable pt)
            foreach (var (k, v) in pt) h.Parametros[k] = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        if (!tabla.TryGetValue("pasos", out var pasos) || pasos is not TomlTableArray pasosArr || pasosArr.Count == 0)
            throw new ArgumentException("la habilidad no tiene [[pasos]]");
        var n = 0;
        foreach (var p in pasosArr)
        {
            n++;
            var paso = new JsonObject();
            foreach (var (k, v) in p)
                paso[k] = v switch
                {
                    bool b => JsonValue.Create(b),
                    long l => JsonValue.Create(l),
                    double d => JsonValue.Create(d),
                    _ => JsonValue.Create(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""),
                };
            var tipo = Observer.Str(paso, "tipo");
            if (!Tipos.Contains(tipo)) throw new ArgumentException($"paso {n}: tipo desconocido «{tipo}»");
            if (tipo is "clic" or "señalar" or "escribir" or "elegir" && Observer.Str(paso, "selector").Length == 0)
                throw new ArgumentException($"paso {n}: «{tipo}» necesita selector");
            h.Pasos.Add(paso);
        }
        return h;
    }

    /// <summary>El <c>.toml</c> de una habilidad, comentado para que se pueda retocar a mano.</summary>
    public static string Escribir(Habilidad h)
    {
        var sb = new StringBuilder();
        sb.Append("# Habilidad de faena: un procedimiento aprendido. Formato en docs/HABILIDADES.md de faena.\n");
        sb.Append("# Los {huecos} de los pasos se rellenan con los parámetros que el modelo saca del objetivo.\n");
        sb.Append("nombre = ").Append(Cadena(h.Nombre)).Append('\n');
        sb.Append("descripcion = ").Append(Cadena(h.Descripcion)).Append('\n');
        sb.Append("cuando = [").Append(string.Join(", ", h.Cuando.Select(Cadena))).Append("]\n");
        sb.Append("superficie = ").Append(Cadena(h.Superficie)).Append('\n');
        if (h.Inicio.Length != 0) sb.Append("inicio = ").Append(Cadena(h.Inicio)).Append('\n');
        if (h.Origen.Length != 0) sb.Append("origen = ").Append(Cadena(h.Origen)).Append('\n');
        if (h.Aprendida.Length != 0) sb.Append("aprendida = ").Append(Cadena(h.Aprendida)).Append('\n');
        sb.Append("\n[parametros]  # nombre = valor de ejemplo\n");
        foreach (var (k, v) in h.Parametros) sb.Append(Clave(k)).Append(" = ").Append(Cadena(v)).Append('\n');
        foreach (var p in h.Pasos)
        {
            sb.Append("\n[[pasos]]\n");
            foreach (var clave in new[] { "tipo", "selector", "valor", "desc" })
                if (Observer.Str(p, clave) is { Length: > 0 } v) sb.Append(clave).Append(" = ").Append(Cadena(v)).Append('\n');
            if (Opcional(p)) sb.Append("opcional = true\n");
        }
        return sb.ToString();
    }

    public static string Guardar(Habilidad h, string carpeta)
    {
        Directory.CreateDirectory(carpeta);
        var ruta = Path.Combine(carpeta, Slug(h.Nombre) + ".toml");
        File.WriteAllText(ruta, Escribir(h), new UTF8Encoding(false));
        h.Ruta = ruta;
        return ruta;
    }

    // ------------------------------------------------------------------ aprender

    /// <summary>
    /// Una habilidad a partir de los pasos de una ejecución o de un proyecto de <c>easy</c>. Lo que
    /// se escribió en cada campo pasa a ser un parámetro con el nombre del campo, y se deja como
    /// valor de ejemplo; los pasos quitados o que no son acciones se descartan.
    /// </summary>
    public static Habilidad Aprender(IEnumerable<JsonObject> pasos, string nombre, string descripcion, string inicio, string superficie = "web", string origen = "")
    {
        var h = new Habilidad
        {
            Nombre = Slug(nombre),
            Descripcion = descripcion.Trim(),
            Superficie = superficie,
            Inicio = inicio,
            Origen = origen,
            Aprendida = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        };
        foreach (var original in pasos)
        {
            if (original["quitado"] is JsonValue q && q.TryGetValue<bool>(out var quitado) && quitado) continue;
            var tipo = Observer.Str(original, "tipo");
            if (!Tipos.Contains(tipo)) continue;
            // Como en ASI: solo entra lo que cambió algo en la pantalla (el agente lo apunta en «cambio»).
            if (tipo is "clic" or "tecla" or "elegir" && original["cambio"] is JsonValue c && c.TryGetValue<bool>(out var cambio) && !cambio) continue;
            var paso = new JsonObject { ["tipo"] = tipo };
            foreach (var clave in new[] { "selector", "valor", "desc" })
                if (Observer.Str(original, clave) is { Length: > 0 } v) paso[clave] = v;
            if (Opcional(original)) paso["opcional"] = true;
            if (tipo is "escribir" or "elegir" && Observer.Str(paso, "valor") is { Length: > 0 } valor)
            {
                var param = NombreParametro(Observer.Str(paso, "desc"), tipo, h.Parametros.Keys);
                h.Parametros[param] = valor;
                paso["valor"] = "{" + param + "}";
                paso["desc"] = Observer.Str(paso, "desc").Replace($"«{valor}»", "«{" + param + "}»", StringComparison.Ordinal);
            }
            h.Pasos.Add(paso);
        }
        if (h.Pasos.Count == 0) throw new ArgumentException("no hay pasos que aprender");
        // La descripción suele ser el objetivo de aquella vez: sus valores también pasan a ser
        // huecos, para que el modelo la reconozca con otros valores y no la confunda con esos.
        foreach (var (param, valor) in h.Parametros.OrderByDescending(p => p.Value.Length))
            if (valor.Trim().Length >= 2)
                h.Descripcion = Regex.Replace(h.Descripcion, Regex.Escape(valor), "{" + param + "}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        h.Cuando = PalabrasClave(nombre.Replace('-', ' ') + " " + Hueco.Replace(h.Descripcion, " "));
        return h;
    }

    /// <summary>«Escribe «x» en el campo «Title»» → <c>title</c>; si no hay nombre, <c>texto1</c>.</summary>
    private static string NombreParametro(string desc, string tipo, IEnumerable<string> usados)
    {
        var comillas = Regex.Matches(desc, "«([^»]*)»").Select(m => m.Groups[1].Value).ToList();
        var campo = comillas.Count >= 2 ? comillas[^1] : "";
        var baseNombre = Slug(campo).Replace('-', '_');
        if (baseNombre.Length == 0 || baseNombre.Length > 30) baseNombre = tipo == "elegir" ? "opcion" : "texto";
        var set = usados.ToHashSet(StringComparer.Ordinal);
        if (!set.Contains(baseNombre)) return baseNombre;
        for (var i = 2; ; i++)
            if (!set.Contains($"{baseNombre}{i}")) return $"{baseNombre}{i}";
    }

    private static readonly HashSet<string> Vacias = new(StringComparer.Ordinal)
    {
        "el", "la", "los", "las", "un", "una", "unos", "unas", "de", "del", "al", "a", "en", "y", "o", "con", "por", "para",
        "que", "se", "su", "sus", "lo", "le", "es", "the", "and", "of", "to", "in", "on", "for", "with", "an",
    };

    /// <summary>Las palabras con contenido de un texto, sin tildes y en minúscula.</summary>
    public static List<string> PalabrasClave(string texto) =>
        Regex.Matches(Normalizar(texto), @"[\p{L}\p{N}]{3,}").Select(m => m.Value).Where(w => !Vacias.Contains(w)).Distinct().ToList();

    // ------------------------------------------------------------------ elegir

    /// <summary>
    /// Las habilidades que podrían encajar con el objetivo, de más a menos: cuenta las palabras de
    /// <c>cuando</c>, nombre y descripción que aparecen en el objetivo, y suma si empieza en el mismo
    /// sitio. Las de otra superficie no cuentan.
    /// </summary>
    public static List<Habilidad> Candidatas(string objetivo, IEnumerable<Habilidad> todas, string superficie, string donde, int max = 3)
    {
        var obj = " " + Normalizar(objetivo) + " ";
        var palabrasObjetivo = PalabrasClave(objetivo).ToHashSet(StringComparer.Ordinal);
        var puntuadas = new List<(Habilidad H, double P)>();
        foreach (var h in todas)
        {
            if (!string.Equals(h.Superficie, superficie, StringComparison.OrdinalIgnoreCase)) continue;
            double p = 0;
            foreach (var c in h.Cuando)
            {
                var n = Normalizar(c).Trim();
                if (n.Length != 0 && obj.Contains(" " + n, StringComparison.Ordinal)) p += 2;
            }
            p += PalabrasClave(h.Nombre.Replace('-', ' ') + " " + Hueco.Replace(h.Descripcion, " ")).Count(palabrasObjetivo.Contains);
            if (p == 0) continue;
            if (h.Inicio.Length != 0 && donde.Length != 0 && MismoSitio(h.Inicio, donde)) p += 1;
            puntuadas.Add((h, p));
        }
        return puntuadas.OrderByDescending(x => x.P).ThenBy(x => x.H.Nombre, StringComparer.Ordinal).Take(max).Select(x => x.H).ToList();
    }

    private static bool MismoSitio(string a, string b)
    {
        if (Uri.TryCreate(a, UriKind.Absolute, out var ua) && Uri.TryCreate(b, UriKind.Absolute, out var ub))
            return string.Equals(ua.Authority, ub.Authority, StringComparison.OrdinalIgnoreCase);
        return string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Las obras como se le enseñan al modelo en el prompt: una línea con nombre, parámetros (con ejemplo) y qué hace.</summary>
    public static string Menu(IEnumerable<Habilidad> obras) => string.Join("\n", obras.Select(h =>
        $"- {h.Nombre}({string.Join(", ", h.Parametros.Select(p => $"{p.Key}: p. ej. «{p.Value}»"))}): {h.Descripcion}"));

    /// <summary>Lo que se le pregunta al modelo para elegir entre las candidatas.</summary>
    public static string PromptEleccion(string objetivo, IReadOnlyList<Habilidad> candidatas)
    {
        var sb = new StringBuilder();
        sb.Append("Una persona encarga esta tarea: «").Append(objetivo).Append("».\n");
        sb.Append("Estas son habilidades aprendidas (procedimientos que ya se sabe hacer). Elige la que hace EXACTAMENTE esa tarea, o ninguna si no encaja del todo.\n\n");
        foreach (var h in candidatas)
        {
            sb.Append("- ").Append(h.Nombre).Append(": ").Append(h.Descripcion).Append('\n');
            if (h.Parametros.Count != 0)
                sb.Append("  parámetros (con un valor de ejemplo): ")
                  .Append(string.Join(", ", h.Parametros.Select(p => $"{p.Key} = «{p.Value}»"))).Append('\n');
        }
        sb.Append("\nSi eliges una, saca del encargo el valor de cada parámetro; si el encargo no lo dice, deja el de ejemplo.\n");
        sb.Append("Responde SOLO con JSON: {\"habilidad\":\"nombre o vacío\",\"parametros\":{\"nombre\":\"valor\"},\"porque\":\"…\"}");
        return sb.ToString();
    }

    /// <summary>Lee la elección del modelo; null si no ha elegido ninguna de las candidatas.</summary>
    public static Eleccion? LeerEleccion(string respuesta, IReadOnlyList<Habilidad> candidatas)
    {
        var limpio = Regex.Replace(respuesta ?? "", "<think>.*?</think>", "", RegexOptions.Singleline);
        int i = limpio.IndexOf('{'), j = limpio.LastIndexOf('}');
        if (i < 0 || j <= i) return null;
        JsonObject? o;
        try { o = JsonNode.Parse(limpio[i..(j + 1)]) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
        if (o is null) return null;
        var nombre = Observer.Str(o, "habilidad").Trim();
        var h = candidatas.FirstOrDefault(c => string.Equals(c.Nombre, nombre, StringComparison.OrdinalIgnoreCase));
        if (h is null) return null;
        var valores = new Dictionary<string, string>(h.Parametros, StringComparer.Ordinal);
        if (o["parametros"] is JsonObject ps)
            foreach (var (k, v) in ps)
                if (valores.ContainsKey(k) && v is not null && v.ToString().Trim().Length != 0)
                    valores[k] = v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToString();
        return new Eleccion(h, valores);
    }

    /// <summary>Pregunta al modelo cuál de las candidatas encaja; null si ninguna (o si el modelo no contesta).</summary>
    public static async Task<Eleccion?> ElegirAsync(string objetivo, IReadOnlyList<Habilidad> candidatas, IChatBackend backend, CancellationToken ct)
    {
        if (candidatas.Count == 0) return null;
        string respuesta;
        try
        {
            respuesta = await backend.CompleteAsync("Eliges procedimientos aprendidos para una tarea. Respondes solo con JSON.",
                [new ChatMessage("user", PromptEleccion(objetivo, candidatas))], ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
        return LeerEleccion(respuesta, candidatas);
    }

    // ------------------------------------------------------------------ usar

    /// <summary>Los pasos con los parámetros puestos.</summary>
    public static List<JsonObject> Rellenar(Habilidad h, IReadOnlyDictionary<string, string> valores)
    {
        string Poner(string s) => Hueco.Replace(s, m => valores.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        var lista = new List<JsonObject>();
        foreach (var p in h.Pasos)
        {
            var copia = (JsonObject)p.DeepClone();
            foreach (var clave in new[] { "valor", "desc", "selector" })
                if (copia[clave] is JsonValue v && v.TryGetValue<string>(out var s)) copia[clave] = Poner(s);
            lista.Add(copia);
        }
        return lista;
    }

    /// <summary>La receta que se le enseña al modelo: los pasos numerados, ya con los valores.</summary>
    public static string Receta(Habilidad h, IReadOnlyList<JsonObject> pasos)
    {
        var sb = new StringBuilder();
        sb.Append(h.Nombre).Append(": ").Append(h.Descripcion).Append('\n');
        for (var i = 0; i < pasos.Count; i++)
            sb.Append(i + 1).Append(". ").Append(Observer.Str(pasos[i], "desc") is { Length: > 0 } d ? d : Observer.Str(pasos[i], "tipo")).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>Las líneas del lenguaje de pasos de tramoya con las que se repite un paso (sin pausas de vídeo).</summary>
    public static List<string> Lineas(JsonObject p)
    {
        var s = Observer.Str(p, "selector");
        var v = Observer.Str(p, "valor");
        return Observer.Str(p, "tipo") switch
        {
            "clic" => [$"click {s}"],
            "señalar" => [$"point {s}"],
            "escribir" => [$"clear {s}", $"type {s} | {v}"],
            "tecla" => [$"press {Palabras.Tecla(v)}"],
            "elegir" => [$"select {s} | {v}"],
            "ir" => [$"goto {v}"],
            "esperar" => ["beat 1"],
            _ => [],
        };
    }

    public static bool Opcional(JsonObject p) => p["opcional"] is JsonValue o && o.TryGetValue<bool>(out var b) && b;

    // ------------------------------------------------------------------ utilidades

    public static string Normalizar(string s)
    {
        var d = (s ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string Slug(string s)
    {
        var t = Regex.Replace(Normalizar(s), @"[^a-z0-9]+", "-").Trim('-');
        return t.Length > 60 ? t[..60].TrimEnd('-') : t;
    }

    private static string Texto(TomlTable t, string clave) =>
        t.TryGetValue(clave, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? "" : "";

    private static string Clave(string k) => Regex.IsMatch(k, "^[A-Za-z0-9_-]+$") ? k : Cadena(k);

    /// <summary>Una cadena TOML básica, con sus escapes.</summary>
    internal static string Cadena(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
