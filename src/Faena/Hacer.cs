// `faena hacer` and `faena habilidades`: the agent as a local worker. It does the task, headless
// by default, writes a log and a report per run, stops before anything irreversible unless it
// was allowed, and learns procedures as skills it can replay next time. The words people read
// are Spanish.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Faena.Agente;
using Faena.Escena;

namespace Faena;

internal static class Hacer
{
    /// <summary>Exit code when the run stopped before an irreversible action nobody allowed.</summary>
    public const int Pendiente = 3;

    // --- seams for the tests -----------------------------------------------------------

    internal static Func<string?, string?, IChatBackend> PickBackend { get; set; } = (name, model) => Backends.Pick(name, model);

    /// <summary>Opens the web (url, show the window, profile) and returns the surface and what closes it.</summary>
    internal static Func<string, bool, string, Task<(ISuperficie Superficie, IAsyncDisposable Cierre)>> AbrirWeb { get; set; } = AbrirWebDefault;

    /// <summary>Opens a desktop app (exe, arguments, show it, window title): on a hidden desktop unless --ver (ESCRITORIO.md).</summary>
    internal static Func<string, string?, bool, string?, CancellationToken, Task<(ISuperficie Superficie, IAsyncDisposable Cierre)>>? AbrirEscritorio { get; set; } =
        async (app, argumentos, ver, titulo, ct) =>
        {
            var sup = await Faena.Escritorio.EscritorioSuperficie.AbrirAsync(app, argumentos, oculto: !ver, titulo, ct).ConfigureAwait(false);
            return (sup, sup);
        };

    internal static Func<DateTime> Ahora { get; set; } = () => DateTime.Now;

    // --- parser ------------------------------------------------------------------------

    public static void Registrar(ArgParser ap)
    {
        var p = ap.AddSubparser("command", "hacer", help: "la IA hace la tarea en segundo plano: registro, informe y " +
                                                          "parada ante lo irreversible", aliases: "do-task");
        p.Add("goal", help: "lo que tiene que hacer, en lenguaje natural");
        p.Add("--url", help: "la web donde empieza (si no, el inicio de la habilidad)");
        p.Add("--app", help: "una aplicación de escritorio (exe) en vez de una web");
        p.Add("--args", help: "argumentos para --app");
        p.Add("--ventana", help: "con --app: el título (o parte) de la ventana a manejar");
        p.AddSwitch("--ver", help: "con ventana, para mirarlo (por defecto, sin ventana y sin tocar tu escritorio)");
        p.Add("--max-pasos", kind: ArgKind.Int, @default: "30");
        p.Add("--max-minutos", kind: ArgKind.Float, @default: "10");
        p.AddAppend("--permitir", help: "categoría irreversible que puede hacer sin preguntar: enviar, borrar, pagar, aceptar, " +
                                        "irreversible o * (todas); repetible o separada por comas");
        p.AddSwitch("--fondo", help: "nunca preguntar: ante lo irreversible, parar y dejarlo en el informe");
        p.AddSwitch("--capturas", help: "guardar una captura tras cada paso");
        p.Add("--habilidad", help: "usar esta habilidad (si no, se elige sola entre las que encajan)");
        p.AddSwitch("--sin-habilidades", help: "razonar desde cero");
        p.AddSwitch("--sin-modelo", help: "con --habilidad: repetirla sin preguntar a ningún modelo (valores con --param)");
        p.AddAppend("--param", help: "con --sin-modelo: nombre=valor de un parámetro de la habilidad; repetible");
        p.Add("--aprender", help: "si sale bien, guardar lo hecho como habilidad con este nombre");
        p.Add("--carpeta", help: "dónde dejar el log y el informe (por defecto, una carpeta nueva en tareas)");
        p.Add("--backend", choices: new[] { "ollama", "claude" });
        p.Add("--model");
        p.AddSwitch("--no-reparar", help: "si un paso de la habilidad falla y el modelo termina la tarea, no reescribir la habilidad");
        p.AddSwitch("--dry-run", help: "muestra lo que haría sin abrir nada ni llamar al modelo");
        p.AddSwitch("--preguntar-en-carpeta", help: "ante lo irreversible, dejar pregunta.json en la carpeta y esperar respuesta.json " +
                                                    "(lo usa la ventana de faena)");
        p.Handler = CmdHacer;

        p = ap.AddSubparser("command", "habilidades", help: "los procedimientos aprendidos que usa `hacer`", aliases: "skills");
        var d = p.AddSubparser("habilidades_cmd", "lista", help: "las habilidades que hay");
        d.Handler = CmdLista;
        d = p.AddSubparser("habilidades_cmd", "ver", help: "el .toml de una habilidad");
        d.Add("nombre");
        d.Handler = CmdVer;
        d = p.AddSubparser("habilidades_cmd", "aprender", help: "crear una habilidad desde una tarea hecha (su carpeta o " +
                                                                 "informe.json) o un proceso grabado en tramoya (su id o carpeta)");
        d.Add("origen");
        d.Add("--nombre", required: true);
        d.Add("--descripcion", help: "qué consigue, en una frase (por defecto, el objetivo o el nombre del proyecto)");
        d.Add("--cuando", help: "expresiones que la delatan en un objetivo, separadas por «;»");
        d.AddSwitch("--force", help: "sustituir una que ya exista");
        d.Handler = CmdAprender;
        d = p.AddSubparser("habilidades_cmd", "importar", help: "traer las habilidades que aprendió tramoya (o las de otra carpeta)");
        d.Add("carpeta", optional: true, help: $"de dónde (por defecto, {Paths.HabilidadesTramoya})");
        d.AddSwitch("--force", help: "sustituir las que ya existan con el mismo nombre");
        d.Handler = CmdImportar;
    }

    // --- hacer -------------------------------------------------------------------------

    private static int CmdHacer(ParsedArgs args)
    {
        var goal = args.GetRequired("goal").Trim();
        var url = args.Get("url");
        var app = args.Get("app");
        var maxPasos = args.Int("max_pasos")!.Value;
        var maxMinutos = args.Double("max_minutos", 10);
        if (goal.Length == 0) { Console.Error.WriteLine("Dime qué tiene que hacer."); return 2; }
        if (maxPasos < 1 || maxMinutos <= 0) { Console.Error.WriteLine("--max-pasos y --max-minutos deben ser mayores que cero."); return 2; }
        if (url is not null && app is not null) { Console.Error.WriteLine("Usa --url o --app, no los dos."); return 2; }
        var permitidas = (args.List("permitir") ?? []).SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        var desconocidas = permitidas.Where(x => x != "*" && x != "irreversible" && !Guardian.Categorias.ContainsKey(x)).ToList();
        if (desconocidas.Count != 0)
        {
            Console.Error.WriteLine($"--permitir: no conozco {string.Join(", ", desconocidas)}; usa {string.Join(", ", Guardian.Categorias.Keys)}, irreversible o *.");
            return 2;
        }

        var avisos = new List<string>();
        AvisarDeTramoya();
        var habilidades = args.Flag("sin_habilidades") ? [] : Agente.Habilidades.Cargar(Paths.Habilidades, avisos);
        foreach (var a in avisos) Console.Error.WriteLine($"Aviso: habilidad ilegible: {a}");
        var forzada = args.Get("habilidad");
        Habilidad? elegida = null;
        if (forzada is not null)
        {
            elegida = habilidades.FirstOrDefault(h => string.Equals(h.Nombre, forzada, StringComparison.OrdinalIgnoreCase));
            if (elegida is null) { Console.Error.WriteLine($"No hay ninguna habilidad «{forzada}» en {Paths.Habilidades}."); return 2; }
        }
        var sinModelo = args.Flag("sin_modelo");
        var valores = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var par in args.List("param") ?? [])
        {
            var igual = par.IndexOf('=');
            if (igual <= 0) { Console.Error.WriteLine($"--param espera nombre=valor, no «{par}»."); return 2; }
            valores[par[..igual].Trim()] = par[(igual + 1)..];
        }
        if (sinModelo && elegida is null) { Console.Error.WriteLine("--sin-modelo necesita --habilidad: sin modelo no hay quien elija ni razone."); return 2; }
        if (valores.Count != 0 && !sinModelo) { Console.Error.WriteLine("--param solo vale con --sin-modelo (si no, los valores salen del objetivo)."); return 2; }
        if (elegida is not null && valores.Keys.FirstOrDefault(k => !elegida.Parametros.ContainsKey(k)) is { } raro)
        {
            Console.Error.WriteLine($"La habilidad «{elegida.Nombre}» no tiene el parámetro «{raro}» (tiene: {string.Join(", ", elegida.Parametros.Keys)}).");
            return 2;
        }
        // Sin sitio, el de la habilidad: «hazme esto» y ya sabe dónde.
        var escritorio = app is not null || (url is null && elegida?.Superficie == "escritorio");
        if (escritorio) app ??= elegida?.Inicio;
        else url ??= elegida?.Inicio;
        if (!escritorio && string.IsNullOrEmpty(url)) { Console.Error.WriteLine("Falta dónde: --url <web> o --app <programa> (o una --habilidad con inicio)."); return 2; }
        if (escritorio && string.IsNullOrEmpty(app)) { Console.Error.WriteLine("Falta la aplicación: --app <programa>."); return 2; }
        var superficie = escritorio ? "escritorio" : "web";
        var donde = (escritorio ? app : url)!;
        var ver = args.Flag("ver");
        var carpeta = args.Get("carpeta") is { } c ? Path.GetFullPath(c)
            : Path.Combine(Paths.Tareas, Ahora().ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) + "-" + Recortar(Agente.Habilidades.Slug(goal), 40));

        if (args.Flag("dry_run"))
        {
            var candidatas = elegida is not null ? [elegida] : Agente.Habilidades.Candidatas(goal, habilidades, superficie, donde);
            Console.WriteLine($"objetivo: {goal}");
            Console.WriteLine($"{(escritorio ? "aplicación" : "url")}: {donde}");
            Console.WriteLine($"ventana: {(ver ? "a la vista" : escritorio ? "escritorio oculto" : "sin ventana")}");
            Console.WriteLine($"límites: {maxPasos} pasos, {maxMinutos.ToString("0.#", CultureInfo.InvariantCulture)} min");
            Console.WriteLine($"irreversible: {(permitidas.Count == 0 ? "preguntar o parar" : "permitido " + string.Join(", ", permitidas))}");
            Console.WriteLine($"habilidades: {(candidatas.Count == 0 ? "ninguna encaja" : string.Join(", ", candidatas.Select(h => h.Nombre)))}");
            if (sinModelo) Console.WriteLine($"modelo: ninguno{(valores.Count != 0 ? "; " + string.Join(", ", valores.Select(v => $"{v.Key} = «{v.Value}»")) : "")}");
            Console.WriteLine($"carpeta: {carpeta}");
            return 0;
        }

        IChatBackend? backend = null;
        if (!sinModelo)
        {
            try { backend = PickBackend(args.Get("backend"), args.Get("model")); }
            catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
        }
        if (escritorio && AbrirEscritorio is null)
        {
            Console.Error.WriteLine("Esta versión aún no maneja aplicaciones de escritorio.");
            return 2;
        }
        if (!escritorio && Cli.EnsureBrowser() is { } missing) return missing;

        Directory.CreateDirectory(carpeta);
        using var registro = new Registro(Path.Combine(carpeta, "registro.log"));
        registro.Report($"Objetivo: {goal}");
        registro.Report($"{(escritorio ? "Aplicación" : "Web")}: {donde}");
        var fondo = args.Flag("fondo") || !Cli.StdinIsTty();
        var opciones = new AgentOptions(goal, "es", maxPasos)
        {
            MaxTiempo = TimeSpan.FromMinutes(maxMinutos),
            Permitidas = permitidas,
            CarpetaCapturas = args.Flag("capturas") ? Path.Combine(carpeta, "capturas") : null,
            Confirmar = args.Flag("preguntar_en_carpeta") ? PreguntarPorArchivo(carpeta, registro) : fondo ? null : Preguntar,
        };

        ResultadoEncargo r;
        using (var cancelar = new CancellationTokenSource())
        {
            ConsoleCancelEventHandler alCancelar = (_, e) => { e.Cancel = true; cancelar.Cancel(); };
            Console.CancelKeyPress += alCancelar;
            try
            {
                Func<ISuperficie, Task<ResultadoEncargo>> encargo = backend is null
                    ? sup => Encargo.SinModeloAsync(sup, opciones, elegida!, valores, registro, cancelar.Token)
                    : sup => Encargo.RunAsync(sup, opciones, habilidades, forzada, backend, registro, cancelar.Token);
                r = Correr(escritorio, donde, args.Get("args"), args.Get("ventana"), ver, encargo, cancelar.Token).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is PlaywrightException or InvalidOperationException or ArgumentException or IOException or TimeoutException)
            {
                registro.Report($"No se ha podido abrir {(escritorio ? "la aplicación" : "la web")}: {e.Message.Split('\n')[0]}");
                return 1;
            }
            finally { Console.CancelKeyPress -= alCancelar; }
        }

        // In a desktop app «where it started» is the program, so the report and a learned skill can open it again.
        if (escritorio) r.Agente.Inicio = donde;
        if (!args.Flag("no_reparar")) Reparar(r, habilidades, superficie, registro);
        var json = Encargo.InformeJson(goal, superficie, r);
        File.WriteAllText(Path.Combine(carpeta, "informe.json"), json.ToJsonString(Json.Pretty) + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(carpeta, "informe.md"), Encargo.InformeMarkdown(goal, superficie, r), new UTF8Encoding(false));
        var estado = Encargo.Estado(r.Agente);
        Console.WriteLine($"{estado}: {r.Agente.Resumen}");
        if (r.Agente.Pendiente is { } pendiente && r.Agente.Resumen.Length == 0)
            Console.WriteLine($"pendiente: {pendiente.Descripcion}");
        if (r.Agente.Pendiente is { } p)
            Console.WriteLine($"para hacerlo: repite con --permitir {p.Categoria}");
        Console.WriteLine($"informe: {Path.Combine(carpeta, "informe.md")}");

        if (args.Get("aprender") is { } nombre && r.Agente.Ok)
        {
            try
            {
                var h = Agente.Habilidades.Aprender(r.Agente.Pasos, nombre, goal, r.Agente.Inicio.Length != 0 ? r.Agente.Inicio : donde, superficie,
                    origen: $"tarea {Path.GetFileName(carpeta)}");
                Console.WriteLine($"habilidad: {Agente.Habilidades.Guardar(h, Paths.Habilidades)}");
            }
            catch (ArgumentException e) { Console.Error.WriteLine($"No se ha guardado la habilidad: {e.Message}"); }
        }
        else if (args.Get("aprender") is not null)
            Console.Error.WriteLine("No se ha guardado la habilidad: la tarea no ha terminado bien.");
        return estado switch { "hecho" => 0, "pendiente" => Pendiente, _ => 1 };
    }

    private static async Task<ResultadoEncargo> Correr(bool escritorio, string donde, string? argumentos, string? ventana, bool ver,
        Func<ISuperficie, Task<ResultadoEncargo>> encargo, CancellationToken ct)
    {
        var (sup, cierre) = escritorio
            ? await AbrirEscritorio!(donde, argumentos, ver, ventana, ct).ConfigureAwait(false)
            : await AbrirWeb(donde, ver, Paths.PerfilTareas).ConfigureAwait(false);
        await using (cierre.ConfigureAwait(false))
            return await encargo(sup).ConfigureAwait(false);
    }

    /// <summary>
    /// The web in its own persistent profile (sessions survive between tasks), without a window
    /// unless --ver. PWDEBUG=console gives window.playwright.selector, the durable selectors.
    /// </summary>
    private static async Task<(ISuperficie, IAsyncDisposable)> AbrirWebDefault(string url, bool ver, string perfil)
    {
        Environment.SetEnvironmentVariable("PWDEBUG", "console");
        var pw = await Playwright.CreateAsync().ConfigureAwait(false);
        IBrowserContext ctx;
        try
        {
            ctx = await pw.Chromium.LaunchPersistentContextAsync(perfil, new()
            {
                Headless = !ver,
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
                IgnoreHTTPSErrors = true,
                Locale = "es-ES",
                Args = ArgumentosNavegador(url),
            }).ConfigureAwait(false);
        }
        catch
        {
            pw.Dispose();
            throw;
        }
        ctx.SetDefaultNavigationTimeout(60000);
        var page = ctx.Pages.Count > 0 ? ctx.Pages[0] : await ctx.NewPageAsync().ConfigureAwait(false);
        try { await page.GotoAsync(url).ConfigureAwait(false); }
        catch (PlaywrightException) { /* a slow sign-in is not a failure: the agent waits */ }
        return (new WebSuperficie(page), new Cierre(async () =>
        {
            try { await ctx.CloseAsync().ConfigureAwait(false); } catch (PlaywrightException) { }
            pw.Dispose();
        }));
    }

    /// <summary>A person at the console decides: anything but «s»/«si»/«y» is a no.</summary>
    private static Task<bool> Preguntar(Irreversible accion, CancellationToken ct)
    {
        Console.Error.Write($"\n⚠ Acción irreversible ({accion.Categoria}): {accion.Descripcion}.\n  ¿La hago? [s/N] ");
        var linea = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
        return Task.FromResult(linea is "s" or "si" or "sí" or "y" or "yes");
    }

    /// <summary>
    /// Self-healing, as Stagehand's action cache does: when a skill broke halfway (the page
    /// changed), the model finished the task and the run went well, the skill is rewritten with
    /// the steps that worked this time. Its name, description and triggers stay.
    /// </summary>
    internal static void Reparar(ResultadoEncargo r, List<Habilidad> habilidades, string superficie, IProgress<string> log)
    {
        if (r.Habilidad is not { Fallo.Length: > 0 } uso || !r.Agente.Ok) return;
        var vieja = habilidades.FirstOrDefault(h => h.Nombre == uso.Nombre);
        if (vieja is null || vieja.Ruta.Length == 0) return;
        try
        {
            var nueva = Agente.Habilidades.Aprender(r.Agente.Pasos, vieja.Nombre, vieja.Descripcion,
                vieja.Inicio.Length != 0 ? vieja.Inicio : r.Agente.Inicio, superficie, $"reparada en la tarea del {DateTime.Now:yyyy-MM-dd HH:mm}");
            nueva.Cuando = vieja.Cuando;
            nueva.Descripcion = vieja.Descripcion;
            Agente.Habilidades.Guardar(nueva, Path.GetDirectoryName(vieja.Ruta)!);
            uso.Reparada = true;
            log.Report($"La habilidad «{vieja.Nombre}» se ha reescrito con los pasos que han funcionado.");
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            log.Report($"No he podido reparar la habilidad «{vieja.Nombre}»: {e.Message}");
        }
    }

    /// <summary>How long a question waits for the window before it counts as a no.</summary>
    internal static TimeSpan EsperaRespuesta { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The window asks instead of the console: the question goes to pregunta.json in the run's
    /// folder and the answer comes back in respuesta.json ({"si": true}). No answer in time,
    /// a cancelled run or anything unreadable is a no.
    /// </summary>
    internal static Func<Irreversible, CancellationToken, Task<bool>> PreguntarPorArchivo(string carpeta, IProgress<string> log) =>
        async (accion, ct) =>
        {
            var pregunta = Path.Combine(carpeta, "pregunta.json");
            var respuesta = Path.Combine(carpeta, "respuesta.json");
            File.Delete(respuesta);
            var datos = new JsonObject
            {
                ["categoria"] = accion.Categoria,
                ["accion"] = accion.Descripcion,
                ["motivo"] = accion.Motivo,
                ["hora"] = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            };
            File.WriteAllText(pregunta, datos.ToJsonString(Json.Compact), new UTF8Encoding(false));
            log.Report("Espero tu respuesta en la ventana de faena.");
            var limite = DateTime.UtcNow + EsperaRespuesta;
            try
            {
                while (DateTime.UtcNow < limite && !ct.IsCancellationRequested)
                {
                    if (File.Exists(respuesta))
                    {
                        try
                        {
                            var si = Json.Parse(File.ReadAllText(respuesta)) is JsonObject o && o["si"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                            log.Report(si ? "Me has dicho que sí." : "Me has dicho que no.");
                            return si;
                        }
                        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { /* half written: read it again */ }
                    }
                    await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
                }
                log.Report("Nadie ha contestado: no lo hago.");
                return false;
            }
            finally
            {
                File.Delete(pregunta);
                File.Delete(respuesta);
            }
        };

    // --- habilidades -------------------------------------------------------------------

    private static int CmdLista(ParsedArgs args)
    {
        var avisos = new List<string>();
        AvisarDeTramoya();
        var lista = Agente.Habilidades.Cargar(Paths.Habilidades, avisos);
        if (lista.Count == 0) Console.WriteLine($"No hay habilidades en {Paths.Habilidades}.");
        foreach (var h in lista)
        {
            var ps = h.Parametros.Count == 0 ? "" : $" ({string.Join(", ", h.Parametros.Keys)})";
            Console.WriteLine($"{h.Nombre} [{h.Superficie}, {h.Pasos.Count} {(h.Pasos.Count == 1 ? "paso" : "pasos")}]{ps}: {h.Descripcion}");
        }
        foreach (var a in avisos) Console.Error.WriteLine($"Aviso: habilidad ilegible: {a}");
        return 0;
    }

    private static int CmdVer(ParsedArgs args)
    {
        var nombre = args.GetRequired("nombre");
        var h = Agente.Habilidades.Cargar(Paths.Habilidades).FirstOrDefault(x => string.Equals(x.Nombre, nombre, StringComparison.OrdinalIgnoreCase));
        if (h is null) { Console.Error.WriteLine($"No hay ninguna habilidad «{nombre}» en {Paths.Habilidades}."); return 1; }
        Console.Write(File.ReadAllText(h.Ruta));
        return 0;
    }

    private static int CmdAprender(ParsedArgs args)
    {
        var origen = args.GetRequired("origen");
        JsonObject datos;
        string tipoOrigen;
        try { (datos, tipoOrigen) = LeerOrigen(origen); }
        catch (Exception e) when (e is IOException or ArgumentException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"No puedo leer {origen}: {e.Message}");
            return 2;
        }
        var pasos = datos["pasos"] is JsonArray arr ? arr.OfType<JsonObject>().ToList() : [];
        var descripcion = args.Get("descripcion") ?? Campos.Str(datos, tipoOrigen == "tarea" ? "objetivo" : "nombre");
        var inicio = Campos.Str(datos, "inicio");
        var superficie = Campos.Str(datos, "superficie") is { Length: > 0 } s ? s : "web";
        Habilidad h;
        try { h = Agente.Habilidades.Aprender(pasos, args.GetRequired("nombre"), descripcion, inicio, superficie, $"{tipoOrigen} {origen}"); }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
        if (args.Get("cuando") is { } cuando)
            h.Cuando = cuando.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var destino = Path.Combine(Paths.Habilidades, Agente.Habilidades.Slug(h.Nombre) + ".toml");
        if (File.Exists(destino) && !args.Flag("force"))
        {
            Console.Error.WriteLine($"Ya existe {destino}; usa --force para sustituirla.");
            return 2;
        }
        Console.WriteLine($"habilidad {h.Nombre}: {h.Pasos.Count} pasos, parámetros: {(h.Parametros.Count == 0 ? "ninguno" : string.Join(", ", h.Parametros.Select(p => $"{p.Key} = «{p.Value}»")))}");
        Console.WriteLine(Agente.Habilidades.Guardar(h, Paths.Habilidades));
        return 0;
    }

    /// <summary>The steps to learn from: a task's informe.json, a tramoya project's proyecto.json, or a tramoya project id.</summary>
    private static (JsonObject Datos, string Tipo) LeerOrigen(string origen)
    {
        string? Archivo(string ruta)
        {
            if (File.Exists(ruta)) return ruta;
            foreach (var nombre in new[] { "informe.json", "proyecto.json" })
                if (File.Exists(Path.Combine(ruta, nombre))) return Path.Combine(ruta, nombre);
            return null;
        }
        var archivo = Archivo(origen) ?? Archivo(Path.Combine(Paths.Tareas, origen)) ?? Archivo(Path.Combine(Paths.ProyectosTramoya, origen))
                      ?? throw new FileNotFoundException("no es una tarea (informe.json) ni un proyecto de tramoya (proyecto.json)");
        var datos = Json.Parse(File.ReadAllText(archivo)) as JsonObject ?? throw new ArgumentException("no es un objeto JSON");
        return (datos, Path.GetFileName(archivo) == "informe.json" ? "tarea" : "proyecto");
    }

    /// <summary>
    /// The skills tramoya learned before faena existed are not read from its folder (faena keeps
    /// its own), but nobody should lose them: while faena has none, it says where they are.
    /// </summary>
    private static void AvisarDeTramoya()
    {
        if (Directory.Exists(Paths.HabilidadesTramoya) && Directory.EnumerateFiles(Paths.HabilidadesTramoya, "*.toml").Any()
            && (!Directory.Exists(Paths.Habilidades) || !Directory.EnumerateFiles(Paths.Habilidades, "*.toml").Any()))
            Console.Error.WriteLine($"Aviso: hay habilidades de tramoya en {Paths.HabilidadesTramoya}; tráelas con `faena habilidades importar`.");
    }

    private static int CmdImportar(ParsedArgs args)
    {
        var origen = args.Get("carpeta") is { } c ? Path.GetFullPath(c) : Paths.HabilidadesTramoya;
        if (!Directory.Exists(origen)) { Console.Error.WriteLine($"No existe {origen}."); return 2; }
        var avisos = new List<string>();
        var lista = Agente.Habilidades.Cargar(origen, avisos);
        foreach (var a in avisos) Console.Error.WriteLine($"Aviso: habilidad ilegible: {a}");
        if (lista.Count == 0) { Console.WriteLine($"No hay habilidades en {origen}."); return 0; }
        Directory.CreateDirectory(Paths.Habilidades);
        var traidas = 0;
        foreach (var h in lista)
        {
            var destino = Path.Combine(Paths.Habilidades, Path.GetFileName(h.Ruta));
            if (File.Exists(destino) && !args.Flag("force"))
            {
                Console.WriteLine($"{h.Nombre}: ya está (usa --force para sustituirla)");
                continue;
            }
            File.Copy(h.Ruta, destino, overwrite: true);
            Console.WriteLine($"{h.Nombre}: {destino}");
            traidas++;
        }
        Console.WriteLine($"{traidas} {(traidas == 1 ? "habilidad traída" : "habilidades traídas")} a {Paths.Habilidades}");
        return 0;
    }

    // --- helpers -----------------------------------------------------------------------

    /// <summary>
    /// Integrated Windows sign-in (Kerberos/NTLM) for the web, its domain and Microsoft's, which
    /// many company webs go through; no translate bar; Spanish. As tramoya opens its webs.
    /// </summary>
    internal static List<string> ArgumentosNavegador(string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.HostNameType != UriHostNameType.Unknown ? uri.DnsSafeHost.ToLowerInvariant() : "";
        var dominios = new List<string>();
        if (host.Length != 0) dominios.Add(host);
        var trozos = host.Split('.');
        if (trozos.Length > 2 && !host.Replace(".", "").All(char.IsAsciiDigit)) dominios.Add("*." + string.Join(".", trozos[^2..]));
        dominios.AddRange(["*.microsoftonline.com", "*.windows.net"]);
        var lista = new List<string> { $"--auth-server-allowlist={string.Join(",", dominios)}", "--disable-features=Translate", "--lang=es-ES" };
        // FAENA_CDP: a debugging port, so a test can watch the browser of a real job.
        if (Environment.GetEnvironmentVariable("FAENA_CDP") is { Length: > 0 } cdp) lista.Add($"--remote-debugging-port={cdp}");
        return lista;
    }

    private static string Recortar(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd('-');

    private sealed class Cierre(Func<Task> cerrar) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await cerrar().ConfigureAwait(false);
    }

    /// <summary>Every line on stderr and, with the time, in the run's registro.log.</summary>
    private sealed class Registro(string ruta) : IProgress<string>, IDisposable
    {
        private readonly StreamWriter _archivo = new(ruta, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        private readonly object _cerrojo = new();

        public void Report(string value)
        {
            Console.Error.WriteLine(value);
            lock (_cerrojo) _archivo.WriteLine($"{DateTime.Now:HH:mm:ss} {value}");
        }

        public void Dispose() => _archivo.Dispose();
    }
}
