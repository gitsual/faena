using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Faena.Agente;
using Faena.Escena;

namespace Faena;

/// <summary>
/// Los encargos de la ventana: lo mismo que <c>faena hacer</c>, lanzado como proceso hijo
/// con su carpeta en <see cref="Opciones.CarpetaTareas"/>. La ventana no habla con el
/// agente por una tubería: lee lo que deja en la carpeta (<c>encargo.json</c> al lanzarlo,
/// <c>registro.log</c> mientras trabaja, <c>informe.json</c> al acabar) y, ante algo
/// irreversible, el agente deja <c>pregunta.json</c> y espera a que la ventana escriba
/// <c>respuesta.json</c>. Así un encargo sobrevive a que se cierre y se abra la ventana, y lo
/// que se ve es exactamente lo que queda en disco.
/// </summary>
public static class Encargos
{
    private static readonly Dictionary<string, Process> Vivos = new();
    private static readonly Regex Id = new("^[0-9]{4}-[0-9]{2}-[0-9]{2}-[0-9]{6}-[a-z0-9-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex LineaRegistro = new(@"^(\d{2}:\d{2}:\d{2}) (.*)$", RegexOptions.CultureInvariant);

    /// <summary>Lanza el proceso; se cambia en los tests para no abrir nada.</summary>
    internal static Func<ProcessStartInfo, Process?> Lanzador { get; set; } = Process.Start;

    /// <summary>Si un proceso sigue trabajando; se cambia en los tests.</summary>
    internal static Func<Process, bool> Vive { get; set; } = p => { try { return !p.HasExited; } catch (InvalidOperationException) { return false; } };

    internal static Func<DateTime> Ahora { get; set; } = () => DateTime.Now;

    public static bool EsId(string id) => Id.IsMatch(id);

    private static string Carpeta(Opciones o, string id) => Path.Combine(o.CarpetaTareas, id);

    // --- encargar ----------------------------------------------------------------------

    /// <summary>
    /// Lanza un encargo con lo que manda la ventana y devuelve su id (el nombre de su carpeta).
    /// Lo que no tiene sentido se explica con <see cref="ArgumentException"/>, para la ventana.
    /// </summary>
    public static string Encargar(Opciones o, JsonObject pedido)
    {
        var objetivo = Campos.Str(pedido, "objetivo").Trim();
        var habilidad = Campos.Str(pedido, "habilidad").Trim();
        var sinModelo = Campos.Bool(pedido, "sin_modelo");
        if (sinModelo && habilidad.Length == 0) throw new ArgumentException(Lengua.T("Para hacerlo sin pensar hay que elegir un oficio.", "To do it without thinking, choose a trade."));
        if (objetivo.Length == 0)
        {
            if (habilidad.Length == 0) throw new ArgumentException(Lengua.T("Dígame qué hay que hacer.", "Tell me what has to be done."));
            objetivo = Lengua.T($"Repetir «{habilidad}»", $"Replay “{habilidad}”");
        }
        var url = Campos.Str(pedido, "url").Trim();
        var app = Campos.Str(pedido, "app").Trim();
        if (url.Length != 0 && app.Length != 0) throw new ArgumentException(Lengua.T("Elija una web o un programa, no los dos.", "Choose a website or a program, not both."));
        if (url.Length == 0 && app.Length == 0 && habilidad.Length == 0) throw new ArgumentException(Lengua.T("Dígame dónde: una web o un programa del ordenador.", "Tell me where: a website or a program on this computer."));
        if (url.Length != 0 && !Regex.IsMatch(url, "^https?://", RegexOptions.IgnoreCase)) url = "https://" + url;
        var maxPasos = Entero(pedido["max_pasos"], 30, 1, 200);
        var maxMinutos = Entero(pedido["max_minutos"], 10, 1, 240);

        var id = Ahora().ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) + "-" + Recortar(Habilidades.Slug(objetivo), 40);
        while (Directory.Exists(Carpeta(o, id))) id += "-b";
        var carpeta = Carpeta(o, id);
        Directory.CreateDirectory(carpeta);
        var encargo = new JsonObject
        {
            ["objetivo"] = objetivo,
            ["superficie"] = app.Length != 0 ? "escritorio" : "web",
            ["donde"] = app.Length != 0 ? app : url,
            ["habilidad"] = habilidad,
            ["sin_modelo"] = sinModelo,
            ["max_pasos"] = maxPasos,
            ["max_minutos"] = maxMinutos,
            ["creado"] = Ahora().ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture),
            ["permitir"] = pedido["permitir"] is JsonArray pa ? pa.DeepClone() : new JsonArray(),
            ["ver"] = Campos.Bool(pedido, "ver"),
            ["capturas"] = Campos.Bool(pedido, "capturas"),
            ["parametros"] = pedido["parametros"] is JsonObject po ? po.DeepClone() : new JsonObject(),
            ["idioma"] = Lengua.Actual,
        };
        File.WriteAllText(Path.Combine(carpeta, "encargo.json"), Json.Dump(encargo, indent: true), new UTF8Encoding(false));

        var argv = new List<string> { "hacer", objetivo };
        if (url.Length != 0) argv.AddRange(["--url", url]);
        if (app.Length != 0) argv.AddRange(["--app", app]);
        if (habilidad.Length != 0) argv.AddRange(["--habilidad", habilidad]);
        if (sinModelo)
        {
            argv.Add("--sin-modelo");
            if (pedido["parametros"] is JsonObject ps)
                foreach (var (k, v) in ps)
                    if (v is not null && v.ToString().Length != 0) argv.AddRange(["--param", $"{k}={(v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToString())}"]);
        }
        if (pedido["permitir"] is JsonArray permitir)
            foreach (var p in permitir.Select(x => x?.ToString() ?? "").Where(x => x.Length != 0))
                argv.AddRange(["--permitir", p]);
        if (Campos.Bool(pedido, "ver")) argv.Add("--ver");
        if (Campos.Bool(pedido, "capturas")) argv.Add("--capturas");
        if (Campos.Str(pedido, "aprender").Trim() is { Length: > 0 } aprender)
        {
            argv.AddRange(["--aprender", aprender]);
            if (Campos.Bool(pedido, "exportar_skill")) argv.Add("--skill");
        }
        argv.AddRange(["--max-pasos", maxPasos.ToString(CultureInfo.InvariantCulture),
                       "--max-minutos", maxMinutos.ToString(CultureInfo.InvariantCulture),
                       "--carpeta", carpeta, "--fondo", "--preguntar-en-carpeta", "--lang", Lengua.Actual]);

        var psi = new ProcessStartInfo(o.Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = carpeta,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);
        foreach (var (k, v) in o.Entorno) psi.Environment[k] = v;
        psi.Environment["FAENA_TAREAS"] = o.CarpetaTareas;
        psi.Environment["FAENA_HABILIDADES"] = o.CarpetaHabilidades;
        var proc = Lanzador(psi) ?? throw new InvalidOperationException(Lengua.T($"No se ha podido lanzar {psi.FileName}", $"Could not launch {psi.FileName}"));
        // La salida ya está en registro.log; se vacían las tuberías para que el hijo no se atasque,
        // y lo que no llegue al registro (un error al arrancar) se guarda aparte.
        if (psi.RedirectStandardOutput && proc.StartInfo.RedirectStandardOutput)
        {
            var salida = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is { } l) lock (salida) salida.AppendLine(l); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is { } l) lock (salida) salida.AppendLine(l); };
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                try { lock (salida) File.WriteAllText(Path.Combine(carpeta, "salida.txt"), salida.ToString(), new UTF8Encoding(false)); }
                catch (IOException) { }
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        lock (Vivos) Vivos[id] = proc;
        return id;
    }

    private static int Entero(JsonNode? n, int defecto, int min, int max)
    {
        // Un número de la ventana puede llegar como entero, decimal o texto.
        double d = defecto;
        if (n is JsonValue j)
        {
            if (j.TryGetValue<int>(out var i)) d = i;
            else if (j.TryGetValue<double>(out var x)) d = x;
            else if (j.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) d = t;
        }
        return Math.Clamp((int)Math.Round(d), min, max);
    }

    // --- mirar ---------------------------------------------------------------------------

    private static bool EnMarcha(string id)
    {
        lock (Vivos)
        {
            if (!Vivos.TryGetValue(id, out var p)) return false;
            if (Vive(p)) return true;
            Vivos.Remove(id);
            return false;
        }
    }

    private static JsonObject? LeerJson(string ruta)
    {
        try { return File.Exists(ruta) ? Json.Parse(File.ReadAllText(ruta, Encoding.UTF8)) as JsonObject : null; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>en-marcha, pregunta, hecho, pendiente, sin terminar, parado o interrumpido (la ventana se cerró a medias).</summary>
    private static string Estado(string carpeta, string id, JsonObject? informe)
    {
        if (EnMarcha(id)) return File.Exists(Path.Combine(carpeta, "pregunta.json")) ? "pregunta" : "en-marcha";
        if (informe is not null) return Campos.Str(informe, "estado") is { Length: > 0 } e ? e : "sin terminar";
        return File.Exists(Path.Combine(carpeta, "parado.json")) ? "parado" : "interrumpido";
    }

    /// <summary>Los encargos, del más reciente al más antiguo (como mucho 40).</summary>
    public static JsonArray Lista(Opciones o)
    {
        var lista = new JsonArray();
        if (!Directory.Exists(o.CarpetaTareas)) return lista;
        foreach (var dir in Directory.GetDirectories(o.CarpetaTareas).Select(Path.GetFileName).OfType<string>()
                     .Where(EsId).OrderDescending(StringComparer.Ordinal).Take(40))
        {
            var carpeta = Carpeta(o, dir);
            var encargo = LeerJson(Path.Combine(carpeta, "encargo.json"));
            var informe = LeerJson(Path.Combine(carpeta, "informe.json"));
            if (encargo is null && informe is null) continue;
            lista.Add(new JsonObject
            {
                ["id"] = dir,
                ["objetivo"] = Campos.Str(encargo ?? informe!, "objetivo"),
                ["estado"] = Estado(carpeta, dir, informe),
                ["creado"] = encargo is null ? "" : Campos.Str(encargo, "creado"),
                ["superficie"] = Campos.Str(encargo ?? informe!, "superficie"),
                ["donde"] = encargo is null ? Campos.Str(informe!, "inicio") : Campos.Str(encargo, "donde"),
            });
        }
        return lista;
    }

    /// <summary>Todo lo de un encargo: lo pedido, el registro (como hoja de cues), la pregunta pendiente y el informe.</summary>
    public static JsonObject? Detalle(Opciones o, string id)
    {
        if (!EsId(id)) return null;
        var carpeta = Carpeta(o, id);
        if (!Directory.Exists(carpeta)) return null;
        var encargo = LeerJson(Path.Combine(carpeta, "encargo.json")) ?? new JsonObject();
        var informe = LeerJson(Path.Combine(carpeta, "informe.json"));
        var cues = new JsonArray();
        var pasos = 0;
        var ultimo = "";
        foreach (var linea in LeerLineas(Path.Combine(carpeta, "registro.log")))
        {
            var m = LineaRegistro.Match(linea);
            var (hora, texto) = m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : ("", linea);
            var tipo = Tipo(texto);
            if (tipo == "paso") { pasos++; ultimo = Regex.Replace(texto, @"^(Paso \d+|Repetido \d+/\d+|Step \d+|Replayed \d+/\d+): ", "").Split(" — porque ")[0].Split(" — because ")[0]; }
            cues.Add(new JsonObject { ["hora"] = hora, ["texto"] = texto, ["tipo"] = tipo });
        }
        if (cues.Count > 300) cues = new JsonArray(cues.Skip(cues.Count - 300).Select(c => c!.DeepClone()).ToArray());
        var estado = Estado(carpeta, id, informe);
        var detalle = new JsonObject
        {
            ["id"] = id,
            ["objetivo"] = Campos.Str(encargo, "objetivo"),
            ["superficie"] = Campos.Str(encargo, "superficie"),
            ["donde"] = Campos.Str(encargo, "donde"),
            ["habilidad"] = Campos.Str(encargo, "habilidad"),
            ["sin_modelo"] = Campos.Bool(encargo, "sin_modelo"),
            ["max_pasos"] = encargo["max_pasos"]?.DeepClone(),
            ["max_minutos"] = encargo["max_minutos"]?.DeepClone(),
            ["creado"] = Campos.Str(encargo, "creado"),
            ["estado"] = estado,
            ["n"] = pasos,
            ["ultimo"] = ultimo,
            ["cues"] = cues,
            ["pedido"] = encargo.DeepClone(),
        };
        if (estado == "pregunta" && LeerJson(Path.Combine(carpeta, "pregunta.json")) is { } pregunta) detalle["pregunta"] = pregunta;
        if (informe is not null) detalle["informe"] = informe;
        if (estado == "interrumpido" && LeerLineas(Path.Combine(carpeta, "salida.txt")).LastOrDefault(l => l.Trim().Length != 0) is { } error)
            detalle["error"] = error.Trim();
        return detalle;
    }

    /// <summary>
    /// What each line of the log is, to paint it on the cue sheet. The log is written in the
    /// language the job ran in, so both languages' openings are known.
    /// </summary>
    internal static string Tipo(string texto)
    {
        bool Empieza(params string[] xs) => xs.Any(x => texto.StartsWith(x, StringComparison.Ordinal));
        return texto switch
        {
            _ when Empieza("Paso ", "Repetido ", "Step ", "Replayed ") => "paso",
            _ when Empieza("Turno ", "Turn ") => "turno",
            _ when Empieza("Acción irreversible", "Me he parado", "Irreversible action", "Stopped before") => "alto",
            _ when Empieza("Autorizado", "Allowed") => "permiso",
            _ when Empieza("Terminado", "Done:") => "fin",
            _ when Empieza("Falló", "No he podido", "La habilidad se queda", "Failed", "Could not", "The skill stops") => "fallo",
            _ when Empieza("Uso la habilidad", "Habilidades que", "Using the skill", "Skills that") => "repertorio",
            _ => "nota",
        };
    }

    private static List<string> LeerLineas(string ruta)
    {
        try
        {
            if (!File.Exists(ruta)) return [];
            // El agente lo tiene abierto para escribir: se lee compartiendo.
            using var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return sr.ReadToEnd().Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch (IOException) { return []; }
    }

    /// <summary>La ruta de una captura del encargo, solo si es una de las suyas.</summary>
    public static string? Captura(Opciones o, string id, string nombre)
    {
        if (!EsId(id) || !Regex.IsMatch(nombre, "^(\\d{2}|final)\\.png$")) return null;
        var ruta = Path.Combine(Carpeta(o, id), "capturas", nombre);
        return File.Exists(ruta) ? ruta : null;
    }

    public static string? CarpetaDe(Opciones o, string id) =>
        EsId(id) && Directory.Exists(Carpeta(o, id)) ? Carpeta(o, id) : null;

    // --- actuar ----------------------------------------------------------------------------

    /// <summary>La respuesta de la persona a la pregunta del agente: la lee en cuanto aparece.</summary>
    public static bool Responder(Opciones o, string id, bool si)
    {
        if (CarpetaDe(o, id) is not { } carpeta || !File.Exists(Path.Combine(carpeta, "pregunta.json"))) return false;
        File.WriteAllText(Path.Combine(carpeta, "respuesta.json"), Json.Dump(new JsonObject { ["si"] = si }, indent: false), new UTF8Encoding(false));
        return true;
    }

    /// <summary>Para el encargo (mata su árbol de procesos: el navegador o la app van con él).</summary>
    public static void Parar(Opciones o, string id)
    {
        Process? p;
        lock (Vivos) Vivos.Remove(id, out p);
        if (p is not null)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        if (CarpetaDe(o, id) is { } carpeta && !File.Exists(Path.Combine(carpeta, "informe.json")))
            File.WriteAllText(Path.Combine(carpeta, "parado.json"), "{\"parado\":true}", new UTF8Encoding(false));
    }

    /// <summary>Al cerrar la ventana no queda ningún agente trabajando a ciegas.</summary>
    public static void PararTodos(Opciones o)
    {
        List<string> ids;
        lock (Vivos) ids = Vivos.Keys.ToList();
        foreach (var id in ids) Parar(o, id);
    }

    public static void Borrar(Opciones o, string id)
    {
        Parar(o, id);
        if (CarpetaDe(o, id) is { } carpeta)
        {
            try { Directory.Delete(carpeta, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // --- repertorio ------------------------------------------------------------------------

    /// <summary>Las habilidades, para la ventana: lo que hace falta para elegirlas y usarlas.</summary>
    public static JsonArray Repertorio(Opciones o)
    {
        var lista = new JsonArray();
        foreach (var h in Habilidades.Cargar(o.CarpetaHabilidades))
            lista.Add(new JsonObject
            {
                ["nombre"] = h.Nombre,
                ["descripcion"] = h.Descripcion,
                ["superficie"] = h.Superficie,
                ["inicio"] = h.Inicio,
                ["aprendida"] = h.Aprendida,
                ["parametros"] = new JsonObject(h.Parametros.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value)))),
                ["pasos"] = new JsonArray(h.Pasos.Select(p => (JsonNode?)JsonValue.Create(Campos.Str(p, "desc") is { Length: > 0 } d ? d : Campos.Str(p, "tipo"))).ToArray()),
            });
        return lista;
    }

    private static string GuardarHabilidad(Opciones o, Habilidad h)
    {
        if (h.Nombre.Length == 0) throw new ArgumentException(Lengua.T("Póngale un nombre.", "Give it a name."));
        if (File.Exists(Path.Combine(o.CarpetaHabilidades, h.Nombre + ".toml")))
            throw new ArgumentException(Lengua.T($"Ya sé un oficio «{h.Nombre}»; elija otro nombre o descosa el otro.", $"I already know a trade called “{h.Nombre}”; choose another name or unstitch the other one."));
        Habilidades.Guardar(h, o.CarpetaHabilidades);
        return h.Nombre;
    }

    /// <summary>Un encargo que salió bien pasa al repertorio.</summary>
    public static string AprenderDeEncargo(Opciones o, string id, string nombre)
    {
        var carpeta = CarpetaDe(o, id) ?? throw new ArgumentException(Lengua.T("Ese recado ya no existe.", "That errand no longer exists."));
        var informe = LeerJson(Path.Combine(carpeta, "informe.json")) ?? throw new ArgumentException(Lengua.T("El recado aún no ha terminado.", "The errand has not finished yet."));
        if (Campos.Str(informe, "estado") != "hecho") throw new ArgumentException(Lengua.T("Solo aprendo de un recado que salió bien.", "I only learn from an errand that went well."));
        var pasos = informe["pasos"] is JsonArray a ? a.OfType<JsonObject>().ToList() : [];
        var h = Habilidades.Aprender(pasos, nombre, Campos.Str(informe, "objetivo"), Campos.Str(informe, "inicio"),
            Campos.Str(informe, "superficie") is { Length: > 0 } s ? s : "web", Lengua.T($"tarea {id}", $"task {id}"));
        return GuardarHabilidad(o, h);
    }

    /// <summary>
    /// Un proceso grabado en tramoya pasa al repertorio: su carpeta (la que tiene
    /// <c>proyecto.json</c>) o su id, que se busca entre los proyectos de tramoya.
    /// </summary>
    public static string AprenderDeProyecto(Opciones o, string origen, string nombre)
    {
        origen = origen.Trim().Trim('"');
        if (origen.Length == 0) throw new ArgumentException(Lengua.T("Dígame qué proceso: la carpeta del proyecto de tramoya o su id.", "Tell me which process: the tramoya project's folder or its id."));
        string? archivo = null;
        foreach (var carpeta in new[] { origen, o.ProyectosTramoya is { } raiz && Regex.IsMatch(origen, "^[a-z0-9-]+$") ? Path.Combine(raiz, origen) : null })
        {
            if (carpeta is null) continue;
            if (File.Exists(carpeta) && Path.GetFileName(carpeta) == "proyecto.json") archivo = carpeta;
            else if (File.Exists(Path.Combine(carpeta, "proyecto.json"))) archivo = Path.Combine(carpeta, "proyecto.json");
            if (archivo is not null) break;
        }
        if (archivo is null) throw new ArgumentException(Lengua.T("Ahí no hay ningún proyecto de tramoya (falta proyecto.json).", "There is no tramoya project there (proyecto.json is missing)."));
        var p = LeerJson(archivo) ?? throw new ArgumentException(Lengua.T("El proyecto de tramoya no se puede leer.", "The tramoya project cannot be read."));
        var pasos = p["pasos"] is JsonArray a ? a.OfType<JsonObject>().ToList() : [];
        if (pasos.Count == 0) throw new ArgumentException(Lengua.T("Ese proceso aún no tiene pasos grabados.", "That process has no recorded steps yet."));
        var inicio = Campos.Str(p, "inicio") is { Length: > 0 } i ? i : Campos.Str(p, "url");
        var h = Habilidades.Aprender(pasos, nombre, Campos.Str(p, "nombre"), inicio, "web",
            Lengua.T($"proyecto de tramoya {Path.GetFileName(Path.GetDirectoryName(archivo))}", $"tramoya project {Path.GetFileName(Path.GetDirectoryName(archivo))}"));
        return GuardarHabilidad(o, h);
    }

    /// <summary>
    /// A trade as an Agent Skill, faena-&lt;name&gt;/SKILL.md, in the user's skills folder (or the
    /// options' one, for the tests): the window's «Export as skill». It replaces an earlier export,
    /// keeping a .bak, since the trade is the source.
    /// </summary>
    public static string ExportarSkill(Opciones o, string nombre)
    {
        var h = Habilidades.Cargar(o.CarpetaHabilidades).FirstOrDefault(x => string.Equals(x.Nombre, nombre, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(Lengua.T($"No sé ningún oficio «{nombre}».", $"I know no trade called “{nombre}”."));
        return AgentSkills.Exportar(h, o.CarpetaSkills ?? AgentSkills.Destino("user", null), Lengua.Actual, force: true);
    }

    public static bool Olvidar(Opciones o, string nombre)
    {
        var h = Habilidades.Cargar(o.CarpetaHabilidades).FirstOrDefault(x => string.Equals(x.Nombre, nombre, StringComparison.OrdinalIgnoreCase));
        if (h is null || h.Ruta.Length == 0) return false;
        File.Delete(h.Ruta);
        return true;
    }

    private static string Recortar(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd('-');
}
