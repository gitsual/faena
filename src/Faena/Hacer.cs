// `faena do` and `faena skills` (hacer, habilidades): the agent as a local worker. It does the task, headless
// by default, writes a log and a report per run, stops before anything irreversible unless it
// was allowed, and learns procedures as skills it can replay next time. The words people read
// follow Lengua (English by default, Spanish to choose).
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

    internal static Func<string?, string?, IChatBackend> PickBackend { get; set; } = (name, model) => Ajustes.Backend(name, model, Ajustes.Leer());

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
        static string T(string es, string en) => Lengua.T(es, en);
        var (nombre, alias) = Cli.Comando("do", "hacer", "do-task");
        var p = ap.AddSubparser("command", nombre, help: T("la IA hace la tarea en segundo plano: registro, informe y parada ante lo irreversible",
                                                           "the AI does the task in the background: log, report and a stop before anything irreversible"), aliases: alias);
        p.Add("goal", es: "objetivo", help: T("lo que tiene que hacer, en lenguaje natural", "what it has to do, in plain words"));
        p.Add("--url", help: T("la web donde empieza (si no, el inicio de la habilidad)", "the website where it starts (otherwise, the skill's start)"));
        p.Add("--app", help: T("una aplicación de escritorio (exe) en vez de una web", "a desktop application (exe) instead of a website"));
        p.Add("--args", help: T("argumentos para --app", "arguments for --app"));
        p.Add("--window-title", help: T("con --app: el título (o parte) de la ventana a manejar", "with --app: the title (or part of it) of the window to work on"), es: "--ventana");
        p.AddSwitch("--show", help: T("con ventana, para mirarlo (por defecto, sin ventana y sin tocar tu escritorio)",
                                      "with a visible window, to watch it (by default, no window and your desktop untouched)"), es: "--ver");
        p.Add("--max-steps", kind: ArgKind.Int, @default: "30", help: T("tope de pasos", "step limit"), es: "--max-pasos");
        p.Add("--max-minutes", kind: ArgKind.Float, @default: "10", help: T("tope de tiempo", "time limit"), es: "--max-minutos");
        p.AddAppend("--allow", help: T("categoría irreversible que puede hacer sin preguntar: enviar, borrar, pagar, aceptar, irreversible o * (todas); repetible o separada por comas",
                                       "irreversible category it may do without asking: send, delete, pay, accept (or enviar, borrar, pagar, aceptar), irreversible or * (all); repeatable or comma-separated"), es: "--permitir");
        p.AddSwitch("--background", help: T("nunca preguntar: ante lo irreversible, parar y dejarlo en el informe",
                                            "never ask: before anything irreversible, stop and leave it in the report"), es: "--fondo");
        p.AddSwitch("--screenshots", help: T("guardar una captura tras cada paso", "save a screenshot after each step"), es: "--capturas");
        p.Add("--use-skill", help: T("usar esta habilidad (si no, se elige sola entre las que encajan)", "use this skill (otherwise it picks one that fits, if any)"), es: "--habilidad");
        p.AddSwitch("--no-skills", help: T("razonar desde cero", "reason from scratch"), es: "--sin-habilidades");
        p.AddSwitch("--no-model", help: T("con --habilidad: repetirla sin preguntar a ningún modelo (valores con --param)",
                                          "with --use-skill: replay it without asking any model (values with --param)"), es: "--sin-modelo");
        p.AddAppend("--param", help: T("con --sin-modelo: nombre=valor de un parámetro de la habilidad; repetible",
                                       "with --no-model: name=value of a parameter of the skill; repeatable"));
        p.Add("--learn", help: T("si sale bien, guardar lo hecho como habilidad con este nombre", "if it goes well, save what was done as a skill with this name"), es: "--aprender");
        p.AddSwitch("--skill", help: T("con --aprender: exportar también la habilidad como skill para agentes (faena-<nombre>/SKILL.md, como `faena skill export`)",
                                       "with --learn: also export the skill as an Agent Skill (faena-<name>/SKILL.md, like `faena skill export`)"));
        p.Add("--folder", help: T("dónde dejar el log y el informe (por defecto, una carpeta nueva en tareas)", "where to leave the log and the report (default: a new folder in tareas)"), es: "--carpeta");
        p.Add("--backend", choices: new[] { "ollama", "claude", "openai" }, help: T("el modelo que piensa (por defecto, el de los ajustes o Ollama en este equipo)",
                                                                                    "the model that thinks (default: the one in Settings, or Ollama on this computer)"));
        p.Add("--model", help: T("el nombre del modelo", "the model's name"));
        p.AddSwitch("--no-repair", help: T("si un paso de la habilidad falla y el modelo termina la tarea, no reescribir la habilidad",
                                           "if a step of the skill fails and the model finishes the task, do not rewrite the skill"), es: "--no-reparar");
        p.AddSwitch("--dry-run", help: T("muestra lo que haría sin abrir nada ni llamar al modelo", "show what it would do without opening anything or calling the model"));
        p.AddSwitch("--ask-in-folder", help: T("ante lo irreversible, dejar pregunta.json en la carpeta y esperar respuesta.json (lo usa la ventana de faena)",
                                               "before anything irreversible, leave pregunta.json in the folder and wait for respuesta.json (the faena window uses it)"), es: "--preguntar-en-carpeta");
        p.Handler = CmdHacer;

        (nombre, alias) = Cli.Comando("skills", "habilidades");
        p = ap.AddSubparser("command", nombre, help: T("los procedimientos aprendidos que usa `hacer`", "the learned procedures `do` uses"), aliases: alias);
        (nombre, alias) = Cli.Comando("list", "lista");
        var d = p.AddSubparser("habilidades_cmd", nombre, help: T("las habilidades que hay", "the skills there are"), aliases: alias);
        d.Handler = CmdLista;
        (nombre, alias) = Cli.Comando("show", "ver");
        d = p.AddSubparser("habilidades_cmd", nombre, help: T("el .toml de una habilidad", "a skill's .toml"), aliases: alias);
        d.Add("name", es: "nombre");
        d.Handler = CmdVer;
        (nombre, alias) = Cli.Comando("learn", "aprender");
        d = p.AddSubparser("habilidades_cmd", nombre, help: T("crear una habilidad desde una tarea hecha (su carpeta o informe.json) o un proceso grabado en tramoya (su id o carpeta)",
                                                              "create a skill from a finished task (its folder or informe.json) or a process recorded in tramoya (its id or folder)"), aliases: alias);
        d.Add("source", es: "origen");
        d.Add("--name", required: true, es: "--nombre");
        d.Add("--description", help: T("qué consigue, en una frase (por defecto, el objetivo o el nombre del proyecto)", "what it achieves, in one sentence (default: the goal or the project's name)"), es: "--descripcion");
        d.Add("--when", help: T("expresiones que la delatan en un objetivo, separadas por «;»", "phrases that give it away in a goal, separated by “;”"), es: "--cuando");
        d.AddSwitch("--force", help: T("sustituir una que ya exista", "replace one that already exists"));
        d.Handler = CmdAprender;
        (nombre, alias) = Cli.Comando("import", "importar");
        d = p.AddSubparser("habilidades_cmd", nombre, help: T("traer las habilidades que aprendió tramoya (o las de otra carpeta)", "bring over the skills tramoya learned (or those of another folder)"), aliases: alias);
        d.Add("folder", es: "carpeta", optional: true, help: T($"de dónde (por defecto, {Paths.HabilidadesTramoya})", $"from where (default: {Paths.HabilidadesTramoya})"));
        d.AddSwitch("--force", help: T("sustituir las que ya existan con el mismo nombre", "replace those that already exist with the same name"));
        d.Handler = CmdImportar;
    }

    // --- hacer -------------------------------------------------------------------------

    private static int CmdHacer(ParsedArgs args)
    {
        static string T(string es, string en) => Lengua.T(es, en);
        var goal = args.GetRequired("objetivo").Trim();
        var url = args.Get("url");
        var app = args.Get("app");
        var maxPasos = args.Int("max_pasos")!.Value;
        var maxMinutos = args.Double("max_minutos", 10);
        if (goal.Length == 0) { Console.Error.WriteLine(T("Dime qué tiene que hacer.", "Tell me what it has to do.")); return 2; }
        if (maxPasos < 1 || maxMinutos <= 0) { Console.Error.WriteLine(T("--max-pasos y --max-minutos deben ser mayores que cero.", "--max-steps and --max-minutes must be greater than zero.")); return 2; }
        if (url is not null && app is not null) { Console.Error.WriteLine(T("Usa --url o --app, no los dos.", "Use --url or --app, not both.")); return 2; }
        var pedidas = (args.List("permitir") ?? []).SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        var desconocidas = pedidas.Where(x => Guardian.CategoriaDe(x) is null).ToList();
        if (desconocidas.Count != 0)
        {
            Console.Error.WriteLine(T($"--permitir: no conozco {string.Join(", ", desconocidas)}; usa {string.Join(", ", Guardian.Categorias.Keys)}, irreversible o *.",
                $"--allow: unknown {string.Join(", ", desconocidas)}; use {string.Join(", ", Guardian.CategoriasEn.Values)}, irreversible or *."));
            return 2;
        }
        // Kept as faena names them («send» → «enviar»): that is what the guard and the report use.
        var permitidas = pedidas.Select(x => Guardian.CategoriaDe(x)!).Distinct().ToList();

        var avisos = new List<string>();
        AvisarDeTramoya();
        var habilidades = args.Flag("sin_habilidades") ? [] : Agente.Habilidades.Cargar(Paths.Habilidades, avisos);
        foreach (var a in avisos) Console.Error.WriteLine(T($"Aviso: habilidad ilegible: {a}", $"Warning: unreadable skill: {a}"));
        var forzada = args.Get("habilidad");
        Habilidad? elegida = null;
        if (forzada is not null)
        {
            elegida = habilidades.FirstOrDefault(h => string.Equals(h.Nombre, forzada, StringComparison.OrdinalIgnoreCase));
            if (elegida is null) { Console.Error.WriteLine(T($"No hay ninguna habilidad «{forzada}» en {Paths.Habilidades}.", $"There is no skill “{forzada}” in {Paths.Habilidades}.")); return 2; }
        }
        var sinModelo = args.Flag("sin_modelo");
        var valores = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var par in args.List("param") ?? [])
        {
            var igual = par.IndexOf('=');
            if (igual <= 0) { Console.Error.WriteLine(T($"--param espera nombre=valor, no «{par}».", $"--param expects name=value, not “{par}”.")); return 2; }
            valores[par[..igual].Trim()] = par[(igual + 1)..];
        }
        if (sinModelo && elegida is null) { Console.Error.WriteLine(T("--sin-modelo necesita --habilidad: sin modelo no hay quien elija ni razone.", "--no-model needs --use-skill: without a model nobody chooses or reasons.")); return 2; }
        if (valores.Count != 0 && !sinModelo) { Console.Error.WriteLine(T("--param solo vale con --sin-modelo (si no, los valores salen del objetivo).", "--param only works with --no-model (otherwise the values come from the goal).")); return 2; }
        if (args.Flag("skill") && args.Get("aprender") is null) { Console.Error.WriteLine(T("--skill va con --aprender <nombre>: exporta la habilidad aprendida.", "--skill goes with --learn <name>: it exports the learned skill.")); return 2; }
        if (elegida is not null && valores.Keys.FirstOrDefault(k => !elegida.Parametros.ContainsKey(k)) is { } raro)
        {
            Console.Error.WriteLine(T($"La habilidad «{elegida.Nombre}» no tiene el parámetro «{raro}» (tiene: {string.Join(", ", elegida.Parametros.Keys)}).",
                $"The skill “{elegida.Nombre}” has no parameter “{raro}” (it has: {string.Join(", ", elegida.Parametros.Keys)})."));
            return 2;
        }
        // Sin sitio, el de la habilidad: «hazme esto» y ya sabe dónde.
        var escritorio = app is not null || (url is null && elegida?.Superficie == "escritorio");
        if (escritorio) app ??= elegida?.Inicio;
        else url ??= elegida?.Inicio;
        if (!escritorio && string.IsNullOrEmpty(url)) { Console.Error.WriteLine(T("Falta dónde: --url <web> o --app <programa> (o una --habilidad con inicio).", "Where is missing: --url <website> or --app <program> (or a --use-skill with a start).")); return 2; }
        if (escritorio && string.IsNullOrEmpty(app)) { Console.Error.WriteLine(T("Falta la aplicación: --app <programa>.", "The application is missing: --app <program>.")); return 2; }
        var superficie = escritorio ? "escritorio" : "web";
        var donde = (escritorio ? app : url)!;
        var ver = args.Flag("ver");
        var carpeta = args.Get("carpeta") is { } c ? Path.GetFullPath(c)
            : Path.Combine(Paths.Tareas, Ahora().ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) + "-" + Recortar(Agente.Habilidades.Slug(goal), 40));

        if (args.Flag("dry_run"))
        {
            var candidatas = elegida is not null ? [elegida] : Agente.Habilidades.Candidatas(goal, habilidades, superficie, donde);
            Console.WriteLine($"{T("objetivo", "goal")}: {goal}");
            Console.WriteLine($"{(escritorio ? T("aplicación", "application") : "url")}: {donde}");
            Console.WriteLine($"{T("ventana", "window")}: {(ver ? T("a la vista", "visible") : escritorio ? T("escritorio oculto", "hidden desktop") : T("sin ventana", "no window"))}");
            Console.WriteLine($"{T("límites", "limits")}: {maxPasos} {T("pasos", "steps")}, {maxMinutos.ToString("0.#", CultureInfo.InvariantCulture)} min");
            Console.WriteLine($"irreversible: {(permitidas.Count == 0 ? T("preguntar o parar", "ask or stop") : T("permitido ", "allowed ") + string.Join(", ", permitidas.Select(x => Guardian.NombreCategoria(x))))}");
            Console.WriteLine($"{T("habilidades", "skills")}: {(candidatas.Count == 0 ? T("ninguna encaja", "none fits") : string.Join(", ", candidatas.Select(h => h.Nombre)))}");
            if (sinModelo) Console.WriteLine($"{T("modelo", "model")}: {T("ninguno", "none")}{(valores.Count != 0 ? "; " + string.Join(", ", valores.Select(v => $"{v.Key} = {Lengua.Comillas(v.Value)}")) : "")}");
            Console.WriteLine($"{T("idioma", "language")}: {Lengua.Actual}");
            Console.WriteLine($"{T("carpeta", "folder")}: {carpeta}");
            return 0;
        }

        IChatBackend? backend = null;
        if (!sinModelo)
        {
            try { backend = PickBackend(args.Get("backend"), args.Get("model")); }
            // (PickBackend weighs the flags, the environment and the window's Settings: Ajustes.Backend.)
            catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
        }
        if (escritorio && AbrirEscritorio is null)
        {
            Console.Error.WriteLine(T("Esta versión aún no maneja aplicaciones de escritorio.", "This version cannot work on desktop applications yet."));
            return 2;
        }
        if (!escritorio && Cli.EnsureBrowser() is { } missing) return missing;

        Directory.CreateDirectory(carpeta);
        using var registro = new Registro(Path.Combine(carpeta, "registro.log"));
        registro.Report($"{T("Objetivo", "Goal")}: {goal}");
        registro.Report($"{(escritorio ? T("Aplicación", "Application") : "Web")}: {donde}");
        var fondo = args.Flag("fondo") || !Cli.StdinIsTty();
        var opciones = new AgentOptions(goal, Lengua.Actual, maxPasos)
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
                registro.Report(T($"No se ha podido abrir {(escritorio ? "la aplicación" : "la web")}: {e.Message.Split('\n')[0]}",
                                  $"Could not open the {(escritorio ? "application" : "website")}: {e.Message.Split('\n')[0]}"));
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
        Console.WriteLine($"{Encargo.EstadoTexto(estado)}: {r.Agente.Resumen}");
        if (r.Agente.Pendiente is { } pendiente && r.Agente.Resumen.Length == 0)
            Console.WriteLine($"{T("pendiente", "pending")}: {pendiente.Descripcion}");
        if (r.Agente.Pendiente is { } p)
            Console.WriteLine(T($"para hacerlo: repite con --permitir {p.Categoria}", $"to do it: run again with --allow {Guardian.NombreCategoria(p.Categoria)}"));
        Console.WriteLine($"{T("informe", "report")}: {Path.Combine(carpeta, "informe.md")}");

        if (args.Get("aprender") is { } nombre && r.Agente.Ok)
        {
            try
            {
                var h = Agente.Habilidades.Aprender(r.Agente.Pasos, nombre, goal, r.Agente.Inicio.Length != 0 ? r.Agente.Inicio : donde, superficie,
                    origen: $"tarea {Path.GetFileName(carpeta)}");
                Console.WriteLine($"{T("habilidad", "skill")}: {Agente.Habilidades.Guardar(h, Paths.Habilidades)}");
                if (args.Flag("skill"))
                {
                    try { Console.WriteLine($"{T("skill para agentes", "agent skill")}: {AgentSkills.Exportar(h, AgentSkills.Destino("user", null), Lengua.Actual, force: true)}"); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        Console.Error.WriteLine(T($"No se ha exportado la skill: {e.Message}", $"The agent skill was not exported: {e.Message}"));
                    }
                }
            }
            catch (ArgumentException e) { Console.Error.WriteLine(T($"No se ha guardado la habilidad: {e.Message}", $"The skill was not saved: {e.Message}")); }
        }
        else if (args.Get("aprender") is not null)
            Console.Error.WriteLine(T("No se ha guardado la habilidad: la tarea no ha terminado bien.", "The skill was not saved: the task did not end well."));
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
                Locale = LocaleNavegador(),
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

    /// <summary>A person at the console decides: anything but «y»/«yes»/«s»/«sí» is a no, in either language.</summary>
    private static Task<bool> Preguntar(Irreversible accion, CancellationToken ct)
    {
        Console.Error.Write(Lengua.T($"\n⚠ Acción irreversible ({accion.Categoria}): {accion.Descripcion}.\n  ¿La hago? [s/N] ",
                                     $"\n⚠ Irreversible action ({Guardian.NombreCategoria(accion.Categoria)}): {accion.Descripcion}.\n  Do it? [y/N] "));
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
                vieja.Inicio.Length != 0 ? vieja.Inicio : r.Agente.Inicio, superficie,
                Lengua.T($"reparada en la tarea del {DateTime.Now:yyyy-MM-dd HH:mm}", $"repaired in the task of {DateTime.Now:yyyy-MM-dd HH:mm}"));
            nueva.Cuando = vieja.Cuando;
            nueva.Descripcion = vieja.Descripcion;
            Agente.Habilidades.Guardar(nueva, Path.GetDirectoryName(vieja.Ruta)!);
            uso.Reparada = true;
            log.Report(Lengua.T($"La habilidad «{vieja.Nombre}» se ha reescrito con los pasos que han funcionado.", $"The skill “{vieja.Nombre}” has been rewritten with the steps that worked."));
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            log.Report(Lengua.T($"No he podido reparar la habilidad «{vieja.Nombre}»: {e.Message}", $"Could not repair the skill “{vieja.Nombre}”: {e.Message}"));
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
            log.Report(Lengua.T("Espero tu respuesta en la ventana de faena.", "Waiting for your answer in the faena window."));
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
                            log.Report(si ? Lengua.T("Me has dicho que sí.", "You said yes.") : Lengua.T("Me has dicho que no.", "You said no."));
                            return si;
                        }
                        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { /* half written: read it again */ }
                    }
                    await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
                }
                log.Report(Lengua.T("Nadie ha contestado: no lo hago.", "Nobody answered: not doing it."));
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
        if (lista.Count == 0) Console.WriteLine(Lengua.T($"No hay habilidades en {Paths.Habilidades}.", $"There are no skills in {Paths.Habilidades}."));
        foreach (var h in lista)
        {
            var ps = h.Parametros.Count == 0 ? "" : $" ({string.Join(", ", h.Parametros.Keys)})";
            var sup = h.Superficie == "escritorio" ? Lengua.T("escritorio", "desktop") : h.Superficie;
            Console.WriteLine($"{h.Nombre} [{sup}, {h.Pasos.Count} {(h.Pasos.Count == 1 ? Lengua.T("paso", "step") : Lengua.T("pasos", "steps"))}]{ps}: {h.Descripcion}");
        }
        foreach (var a in avisos) Console.Error.WriteLine(Lengua.T($"Aviso: habilidad ilegible: {a}", $"Warning: unreadable skill: {a}"));
        return 0;
    }

    private static int CmdVer(ParsedArgs args)
    {
        var nombre = args.GetRequired("nombre");
        var h = Agente.Habilidades.Cargar(Paths.Habilidades).FirstOrDefault(x => string.Equals(x.Nombre, nombre, StringComparison.OrdinalIgnoreCase));
        if (h is null) { Console.Error.WriteLine(Lengua.T($"No hay ninguna habilidad «{nombre}» en {Paths.Habilidades}.", $"There is no skill “{nombre}” in {Paths.Habilidades}.")); return 1; }
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
            Console.Error.WriteLine(Lengua.T($"No puedo leer {origen}: {e.Message}", $"Cannot read {origen}: {e.Message}"));
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
            Console.Error.WriteLine(Lengua.T($"Ya existe {destino}; usa --force para sustituirla.", $"{destino} already exists; use --force to replace it."));
            return 2;
        }
        Console.WriteLine(Lengua.T($"habilidad {h.Nombre}: {h.Pasos.Count} pasos, parámetros: ", $"skill {h.Nombre}: {h.Pasos.Count} steps, parameters: ")
                          + (h.Parametros.Count == 0 ? Lengua.T("ninguno", "none") : string.Join(", ", h.Parametros.Select(p => $"{p.Key} = {Lengua.Comillas(p.Value)}"))));
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
                      ?? throw new FileNotFoundException(Lengua.T("no es una tarea (informe.json) ni un proyecto de tramoya (proyecto.json)", "it is neither a task (informe.json) nor a tramoya project (proyecto.json)"));
        var datos = Json.Parse(File.ReadAllText(archivo)) as JsonObject ?? throw new ArgumentException(Lengua.T("no es un objeto JSON", "it is not a JSON object"));
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
            Console.Error.WriteLine(Lengua.T($"Aviso: hay habilidades de tramoya en {Paths.HabilidadesTramoya}; tráelas con `faena habilidades importar`.",
                                             $"Warning: there are tramoya skills in {Paths.HabilidadesTramoya}; bring them over with `faena skills import`."));
    }

    private static int CmdImportar(ParsedArgs args)
    {
        var origen = args.Get("carpeta") is { } c ? Path.GetFullPath(c) : Paths.HabilidadesTramoya;
        if (!Directory.Exists(origen)) { Console.Error.WriteLine(Lengua.T($"No existe {origen}.", $"{origen} does not exist.")); return 2; }
        var avisos = new List<string>();
        var lista = Agente.Habilidades.Cargar(origen, avisos);
        foreach (var a in avisos) Console.Error.WriteLine(Lengua.T($"Aviso: habilidad ilegible: {a}", $"Warning: unreadable skill: {a}"));
        if (lista.Count == 0) { Console.WriteLine(Lengua.T($"No hay habilidades en {origen}.", $"There are no skills in {origen}.")); return 0; }
        Directory.CreateDirectory(Paths.Habilidades);
        var traidas = 0;
        foreach (var h in lista)
        {
            var destino = Path.Combine(Paths.Habilidades, Path.GetFileName(h.Ruta));
            if (File.Exists(destino) && !args.Flag("force"))
            {
                Console.WriteLine(Lengua.T($"{h.Nombre}: ya está (usa --force para sustituirla)", $"{h.Nombre}: already there (use --force to replace it)"));
                continue;
            }
            File.Copy(h.Ruta, destino, overwrite: true);
            Console.WriteLine($"{h.Nombre}: {destino}");
            traidas++;
        }
        Console.WriteLine(Lengua.T($"{traidas} {(traidas == 1 ? "habilidad traída" : "habilidades traídas")} a {Paths.Habilidades}",
                                   $"{traidas} {(traidas == 1 ? "skill" : "skills")} brought to {Paths.Habilidades}"));
        return 0;
    }

    // --- helpers -----------------------------------------------------------------------

    /// <summary>
    /// Integrated Windows sign-in (Kerberos/NTLM) for the web, its domain and Microsoft's, which
    /// many company webs go through; no translate bar; the interface's language (FAENA_BROWSER_LANG
    /// overrides it, for webs that must be seen in another one). As tramoya opens its webs.
    /// </summary>
    internal static List<string> ArgumentosNavegador(string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.HostNameType != UriHostNameType.Unknown ? uri.DnsSafeHost.ToLowerInvariant() : "";
        var dominios = new List<string>();
        if (host.Length != 0) dominios.Add(host);
        var trozos = host.Split('.');
        if (trozos.Length > 2 && !host.Replace(".", "").All(char.IsAsciiDigit)) dominios.Add("*." + string.Join(".", trozos[^2..]));
        dominios.AddRange(["*.microsoftonline.com", "*.windows.net"]);
        var lista = new List<string> { $"--auth-server-allowlist={string.Join(",", dominios)}", "--disable-features=Translate", $"--lang={LocaleNavegador()}" };
        // FAENA_CDP: a debugging port, so a test can watch the browser of a real job.
        if (Environment.GetEnvironmentVariable("FAENA_CDP") is { Length: > 0 } cdp) lista.Add($"--remote-debugging-port={cdp}");
        return lista;
    }

    /// <summary>The browser's locale: FAENA_BROWSER_LANG (es-ES, en-US…), else that of the interface's language.</summary>
    internal static string LocaleNavegador() =>
        Environment.GetEnvironmentVariable("FAENA_BROWSER_LANG") is { Length: > 0 } l ? l.Trim() : Lengua.T("es-ES", "en-US");

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
