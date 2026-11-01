using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Faena.Agente;

/// <summary>Una acción que no tiene vuelta atrás (enviar, borrar, pagar, aceptar…), antes de hacerla.</summary>
/// <param name="Categoria">La palabra que la delata («enviar», «borrar»…), o «irreversible» si lo dijo el modelo; es lo que se autoriza con <c>--permitir</c>.</param>
/// <param name="Descripcion">Lo que se iba a hacer, dicho para una persona («Pulsar el botón «Enviar»»).</param>
/// <param name="Motivo">Por qué quería hacerlo el modelo.</param>
public sealed record Irreversible(string Categoria, string Descripcion, string Motivo);

/// <summary>Lo que se le pide al agente.</summary>
/// <param name="Goal">El objetivo en lenguaje natural («Busca "Madrid" y abre el artículo»).</param>
/// <param name="Lang">The language of the log, the report and the model's summary («en» or «es»); null: <see cref="Lengua.Actual"/>.</param>
/// <param name="MaxPasos">Tope de pasos apuntados; las esperas y las acciones fallidas no cuentan.</param>
/// <param name="Capturas">Mandar también una captura de pantalla en cada turno (solo la aprovechan los modelos con visión).</param>
public sealed record AgentOptions(string Goal, string? Lang = null, int MaxPasos = 25, bool Capturas = false)
{
    /// <summary>Tope de tiempo de la ejecución entera; sin valor, solo el de pasos.</summary>
    public TimeSpan? MaxTiempo { get; init; }
    /// <summary>Las categorías de acciones irreversibles que se pueden hacer sin preguntar; «*» = todas.</summary>
    public IReadOnlyCollection<string> Permitidas { get; init; } = [];
    /// <summary>
    /// A quién se pregunta ante una acción irreversible no permitida (la consola, si hay una persona
    /// delante). Sin él, o si dice que no, el agente se para y deja la acción como <see cref="AgentResult.Pendiente"/>.
    /// </summary>
    public Func<Irreversible, CancellationToken, Task<bool>>? Confirmar { get; init; }
    /// <summary>Dónde guardar una captura PNG tras cada paso (<c>NN.png</c>) y otra al final; sin valor, ninguna.</summary>
    public string? CarpetaCapturas { get; init; }
    /// <summary>Una habilidad aprendida que encaja con el objetivo: sus pasos van al prompt como receta.</summary>
    public string? Receta { get; init; }
    /// <summary>Los pasos que ya se han hecho antes de empezar (al repetir una habilidad): el modelo sigue desde ahí.</summary>
    public IReadOnlyList<JsonObject> Previos { get; init; } = [];
    /// <summary>En modo tarea, las habilidades que el modelo puede invocar como una acción más («habilidad»).</summary>
    public IReadOnlyList<Habilidad> Repertorio { get; init; } = [];
}

/// <summary>El resultado: los pasos (en el formato de los proyectos de tramoya) y cómo acabó.</summary>
public sealed class AgentResult
{
    /// <summary>Solo cuando el modelo ha dicho «terminar».</summary>
    public bool Ok { get; set; }
    public string Resumen { get; set; } = "";
    /// <summary><c>tipo</c>, <c>selector</c>, <c>valor</c>, <c>desc</c>, <c>guarda</c>, <c>opcional</c> y <c>cambio</c>.</summary>
    public List<JsonObject> Pasos { get; set; } = [];
    public List<string> Avisos { get; set; } = [];
    /// <summary>La url en la que empezó: el <c>inicio</c> de una habilidad aprendida de aquí.</summary>
    public string Inicio { get; set; } = "";
    /// <summary>El título de la web al empezar, recortado como hace el grabador.</summary>
    public string Titulo { get; set; } = "";
    /// <summary>La acción irreversible que se ha dejado sin hacer, esperando a que alguien la autorice.</summary>
    public Irreversible? Pendiente { get; set; }
    /// <summary>Dónde acabó (url o ventana).</summary>
    public string Final { get; set; } = "";
    /// <summary>Cuántas decisiones ha tomado el modelo (las respuestas que no valían no cuentan).</summary>
    public int Turnos { get; set; }
    public TimeSpan Duracion { get; set; }
    /// <summary>Las capturas guardadas en <see cref="AgentOptions.CarpetaCapturas"/>, en orden.</summary>
    public List<string> Capturas { get; set; } = [];
}

/// <summary>
/// El agente: mira la pantalla, pregunta al modelo, hace lo que dice y apunta el paso con el
/// selector duradero capturado ANTES de actuar (después el elemento puede haber desaparecido).
/// Lo irreversible se para y se pregunta salvo que esté permitido.
/// </summary>
public static class Agent
{
    /// <summary>Cuántos turnos completos (observación + respuesta) se le enseñan al modelo.</summary>
    public const int Historial = 6;
    /// <summary>De esos, cuántos llevan la observación entera; los demás, solo un resumen (el contexto del modelo es finito).</summary>
    public const int HistorialCompleto = 2;

    public static Task<AgentResult> RunAsync(IPage page, AgentOptions options, IChatBackend backend, IProgress<string>? log, CancellationToken ct) =>
        RunAsync(new WebSuperficie(page), options, backend, log, ct);

    public static async Task<AgentResult> RunAsync(ISuperficie sup, AgentOptions options, IChatBackend backend, IProgress<string>? log, CancellationToken ct)
    {
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        using var tope = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.MaxTiempo is { } maxTiempo) tope.CancelAfter(maxTiempo);
        var resultado = await BucleAsync(sup, options, backend, log, tope.Token, ct).ConfigureAwait(false);
        resultado.Duracion = reloj.Elapsed;
        try { resultado.Final = sup.Donde; } catch (Exception) { }
        return resultado;
    }

    private static async Task<AgentResult> BucleAsync(ISuperficie sup, AgentOptions options, IChatBackend backend, IProgress<string>? log, CancellationToken ct, CancellationToken externo)
    {
        var resultado = new AgentResult { Inicio = sup.Donde };
        try { resultado.Titulo = Observer.Recortar(await sup.TituloAsync().ConfigureAwait(false), 60); }
        catch (Exception) { resultado.Titulo = ""; }
        // Las obras que encajan con el objetivo se le ofrecen como acciones (como las habilidades de ASI).
        var obras = Habilidades.Candidatas(options.Goal, options.Repertorio, sup.Tipo, sup.Donde, max: 5);
        var system = Protocol.SystemPromptTarea(options.Goal, sup.Tipo, options.Receta, Habilidades.Menu(obras), options.Lang);
        // Lo que se veía antes de la última acción y el paso que la apuntó: con la observación
        // siguiente se le cuenta al modelo qué cambió, y el paso sabe si hizo algo.
        Observation? previa = null;
        JsonObject? pasoPrevio = null;
        var historial = new List<(string Usuario, string Resumen, string Respuesta)>();
        var hechos = options.Previos.Select(p => Observer.Str(p, "desc")).ToList();
        resultado.Pasos.AddRange(options.Previos.Select(p => (JsonObject)p.DeepClone()));
        var ultimo = hechos.Count != 0
            ? "the steps of the learned skill have been replayed with the values of the goal; check on the screen whether the goal is already achieved (do not replay it with the same values)."
            : "the page has just been opened.";
        var turnos = 0;
        var fallosSeguidos = 0;
        var miradasFallidas = 0;
        // Un modelo que no acierta nunca podría dar vueltas sin apuntar pasos: hay un tope de turnos también.
        var maxTurnos = options.MaxPasos * 2 + 4;
        string Cancelado() => externo.IsCancellationRequested || options.MaxTiempo is null
            ? Lengua.T("Se ha cancelado antes de terminar.", "Cancelled before finishing.")
            : Lengua.T($"Se ha llegado al tope de tiempo ({options.MaxTiempo.Value.TotalMinutes:0.#} min) sin terminar.",
                       $"Reached the time limit ({options.MaxTiempo.Value.TotalMinutes:0.#} min) without finishing.");

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                resultado.Avisos.Add(Cancelado());
                break;
            }
            if (resultado.Pasos.Count >= options.MaxPasos)
            {
                resultado.Avisos.Add(Lengua.T($"Se ha llegado al tope de {options.MaxPasos} pasos sin terminar.", $"Reached the limit of {options.MaxPasos} steps without finishing."));
                break;
            }
            if (++turnos > maxTurnos)
            {
                resultado.Avisos.Add(Lengua.T("El modelo no ha conseguido avanzar; se deja aquí.", "The model could not make progress; leaving it here."));
                break;
            }

            Observation obs;
            try { obs = await sup.ObservarAsync(ct).ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                resultado.Avisos.Add(Cancelado());
                break;
            }
            catch (Exception e)
            {
                // La página está navegando o se ha cerrado: se espera un poco y se vuelve a mirar (sin gastar turno).
                log?.Report(Lengua.T($"No he podido mirar la página ({Breve(e)}); espero.", $"Could not look at the page ({Breve(e)}); waiting."));
                turnos--;
                if (++miradasFallidas > 5)
                {
                    resultado.Avisos.Add(Lengua.T($"No se puede mirar la página: {Breve(e)}", $"Cannot look at the page: {Breve(e)}"));
                    resultado.Resumen = resultado.Avisos[^1];
                    break;
                }
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
                continue;
            }
            miradasFallidas = 0;
            if (previa is not null)
            {
                var (cambios, cambio) = Protocol.Cambios(previa, obs);
                ultimo = $"{ultimo} {cambios}";
                if (pasoPrevio is not null) pasoPrevio["cambio"] = cambio;
                previa = null;
                pasoPrevio = null;
            }
            var usuario = Protocol.Turno(options.Goal, Protocol.Observacion(obs), ultimo, hechos, options.MaxPasos - resultado.Pasos.Count);
            byte[]? captura = options.Capturas ? await sup.CapturaAsync().ConfigureAwait(false) : null;
            var mensajes = Mensajes(historial, usuario, captura);

            string respuesta;
            Decision decision;
            try
            {
                respuesta = await backend.CompleteAsync(system, mensajes, ct).ConfigureAwait(false);
                try
                {
                    decision = Protocol.ParseDecision(respuesta);
                }
                catch (ArgumentException e)
                {
                    // Un JSON torcido no es un fallo: se le enseña el error y se le pide otra vez, una sola.
                    log?.Report(Lengua.T($"La respuesta del modelo no vale ({e.Message}); se lo pido otra vez.", $"The model's answer is not valid ({e.Message}); asking again."));
                    var reintento = new List<ChatMessage>(mensajes)
                    {
                        new("assistant", respuesta),
                        new("user", $"Your answer is not valid: {e.Message}. Answer ONLY with the JSON object of the next action."),
                    };
                    respuesta = await backend.CompleteAsync(system, reintento, ct).ConfigureAwait(false);
                    decision = Protocol.ParseDecision(respuesta);
                }
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                resultado.Avisos.Add(Cancelado());
                break;
            }
            catch (Exception e)
            {
                resultado.Avisos.Add(Lengua.T($"El modelo no ha contestado bien: {Breve(e)}", $"The model did not answer properly: {Breve(e)}"));
                resultado.Resumen = resultado.Avisos[^1];
                log?.Report(resultado.Resumen);
                break;
            }
            resultado.Turnos++;
            var resumenTurno = $"{Protocol.AccionEn[decision.Accion]}{(decision.Ref is int r && Protocol.ConRef.Contains(decision.Accion) ? $" {r}" : "")}{(decision.Valor.Length != 0 ? $" “{decision.Valor}”" : "")}";
            // In the log, in the person's words (the Spanish action names are faena's own: clic, escribir…).
            var resumenLog = Lengua.Es
                ? $"{decision.Accion}{(decision.Ref is int r2 && Protocol.ConRef.Contains(decision.Accion) ? $" {r2}" : "")}{(decision.Valor.Length != 0 ? $" «{decision.Valor}»" : "")}"
                : resumenTurno;
            historial.Add((usuario, $"Page: {obs.Title} ({obs.Url})\nResult of the last action: {ultimo}", respuesta));
            if (historial.Count > Historial) historial.RemoveAt(0);
            log?.Report(Lengua.T($"Turno {turnos}: {resumenLog} — {decision.Pensamiento}", $"Turn {turnos}: {resumenLog} — {decision.Pensamiento}"));

            if (decision.Accion == "terminar")
            {
                resultado.Ok = true;
                resultado.Resumen = decision.Resumen.Length != 0 ? decision.Resumen : decision.Pensamiento;
                log?.Report(Lengua.T($"Terminado: {resultado.Resumen}", $"Done: {resultado.Resumen}"));
                break;
            }
            if (decision.Accion == "fallo")
            {
                resultado.Resumen = decision.Resumen.Length != 0 ? decision.Resumen : decision.Pensamiento;
                resultado.Avisos.Add(Lengua.T($"El agente se ha rendido: {resultado.Resumen}", $"The agent gave up: {resultado.Resumen}"));
                log?.Report(Lengua.T($"No he podido: {resultado.Resumen}", $"Could not do it: {resultado.Resumen}"));
                break;
            }
            if (decision.Accion == "esperar")
            {
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { continue; }
                await sup.AsentarAsync().ConfigureAwait(false);
                ultimo = "waited one second.";
                previa = obs;
                continue;
            }
            if (decision.Accion == "habilidad")
            {
                var obra = options.Repertorio.FirstOrDefault(h => string.Equals(h.Nombre, decision.Valor.Trim(), StringComparison.OrdinalIgnoreCase));
                if (obra is null)
                {
                    ultimo = $"there is no procedure “{decision.Valor}” in the repertoire; use those in the list or do it step by step.";
                    continue;
                }
                var valores = new Dictionary<string, string>(obra.Parametros, StringComparer.Ordinal);
                foreach (var (k, v) in decision.Parametros ?? new Dictionary<string, string>())
                    if (valores.ContainsKey(k)) valores[k] = v;
                var (hechosObra, uso, parado) = await Encargo.RepetirHabilidadAsync(sup, obra, valores, options, log, ct).ConfigureAwait(false);
                resultado.Pasos.AddRange(hechosObra);
                hechos.AddRange(hechosObra.Select(p => Observer.Str(p, "desc")));
                if (parado is not null)
                {
                    resultado.Pendiente = parado.Pendiente;
                    resultado.Resumen = parado.Resumen;
                    resultado.Avisos.Add(parado.Resumen);
                    break;
                }
                ultimo = uso.Fallo.Length == 0
                    ? $"replayed the whole procedure “{obra.Nombre}” ({uso.Repetidos} steps)."
                    : $"the procedure “{obra.Nombre}” stopped halfway: {uso.Fallo}. Go on from here yourself.";
                previa = obs;
                fallosSeguidos = 0;
                continue;
            }

            if (Guardian.Detectar(obs, decision) is { } peligro && !Guardian.Permitida(peligro, options.Permitidas))
            {
                log?.Report(Lengua.T($"Acción irreversible: {peligro.Descripcion} ({peligro.Categoria}).", $"Irreversible action: {peligro.Descripcion} ({Guardian.NombreCategoria(peligro.Categoria)})."));
                var si = false;
                if (options.Confirmar is { } confirmar)
                {
                    try { si = await confirmar(peligro, ct).ConfigureAwait(false); }
                    catch (Exception) { si = false; }
                }
                if (!si)
                {
                    resultado.Pendiente = peligro;
                    resultado.Resumen = Lengua.T($"Me he parado antes de {Minuscula(peligro.Descripcion)}: {Porque(peligro)} y no está autorizado.",
                        $"Stopped before: {peligro.Descripcion} ({Porque(peligro)}; not allowed).");
                    resultado.Avisos.Add(resultado.Resumen);
                    log?.Report(resultado.Resumen);
                    break;
                }
                log?.Report(Lengua.T("Autorizado: sigo.", "Allowed: going on."));
            }

            JsonObject? paso;
            try
            {
                paso = await sup.ActuarAsync(obs, decision, ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                resultado.Avisos.Add(Cancelado());
                break;
            }
            catch (Exception e)
            {
                ultimo = $"the action “{resumenTurno}” failed: {Explicar(e)}";
                log?.Report(Lengua.T($"Falló: la acción «{resumenLog}» ha fallado: {Explicar(e, "es")}", $"Failed: {ultimo}"));
                if (++fallosSeguidos >= 4)
                {
                    resultado.Avisos.Add(Lengua.T("Cuatro acciones seguidas han fallado; se deja aquí.", "Four actions in a row failed; leaving it here."));
                    resultado.Resumen = resultado.Avisos[^1];
                    break;
                }
                continue;
            }
            fallosSeguidos = 0;
            if (paso is null)
            {
                ultimo = $"there is no element {decision.Ref}; use only the numbers of the current list.";
                log?.Report(Lengua.T($"El modelo ha pedido un elemento que no existe ({decision.Ref}).", $"The model asked for an element that does not exist ({decision.Ref})."));
                continue;
            }
            await sup.AsentarAsync().ConfigureAwait(false);
            resultado.Pasos.Add(paso);
            previa = obs;
            pasoPrevio = paso;
            var desc = paso["desc"]?.ToString() ?? "";
            hechos.Add(desc);
            ultimo = $"done: {desc}.";
            log?.Report(Lengua.T($"Paso {resultado.Pasos.Count}: {desc} — porque {decision.Pensamiento}", $"Step {resultado.Pasos.Count}: {desc} — because {decision.Pensamiento}"));
            await CapturarAsync(sup, options, resultado, $"{resultado.Pasos.Count:00}").ConfigureAwait(false);
        }
        await CapturarAsync(sup, options, resultado, "final").ConfigureAwait(false);
        return resultado;
    }

    private static async Task CapturarAsync(ISuperficie sup, AgentOptions options, AgentResult resultado, string nombre)
    {
        if (options.CarpetaCapturas is not { } carpeta) return;
        var png = await sup.CapturaAsync().ConfigureAwait(false);
        if (png is null) return;
        try
        {
            Directory.CreateDirectory(carpeta);
            var ruta = Path.Combine(carpeta, nombre + ".png");
            await File.WriteAllBytesAsync(ruta, png).ConfigureAwait(false);
            resultado.Capturas.Add(ruta);
        }
        catch (IOException) { /* sin captura no se para la tarea */ }
    }

    /// <summary>Why it counts as irreversible, said for a person.</summary>
    internal static string Porque(Irreversible p) => p.Categoria == "irreversible"
        ? Lengua.T("el modelo dice que no tiene vuelta atrás", "the model says it cannot be undone")
        : Lengua.T($"es irreversible ({p.Categoria})", $"it is irreversible: {Guardian.NombreCategoria(p.Categoria)}");

    private static string Minuscula(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>La conversación para el modelo: los últimos turnos (los antiguos, resumidos) y el de ahora.</summary>
    private static List<ChatMessage> Mensajes(List<(string Usuario, string Resumen, string Respuesta)> historial, string usuario, byte[]? captura)
    {
        var lista = new List<ChatMessage>();
        for (var i = 0; i < historial.Count; i++)
        {
            var completo = i >= historial.Count - HistorialCompleto;
            lista.Add(new ChatMessage("user", completo ? historial[i].Usuario : historial[i].Resumen));
            lista.Add(new ChatMessage("assistant", historial[i].Respuesta));
        }
        lista.Add(new ChatMessage("user", usuario, captura));
        return lista;
    }

    /// <summary>What failed, said for the model (English) or the log, without Playwright's stack.</summary>
    private static string Explicar(Exception e, string lang = "en")
    {
        var t = e.Message ?? "";
        if (t.Contains("Timeout", StringComparison.OrdinalIgnoreCase) || t.Contains("not visible", StringComparison.Ordinal))
            return Lengua.T("el elemento no se ha podido pulsar (no visible, tapado o ha tardado demasiado)", "the element could not be clicked (not visible, covered or too slow)", lang);
        if (t.Contains("strict mode", StringComparison.Ordinal))
            return Lengua.T("hay varios elementos iguales", "there are several identical elements", lang);
        if (t.Contains("intercepts pointer events", StringComparison.Ordinal))
            return Lengua.T("otro elemento lo tapa (un aviso o un menú abierto)", "another element covers it (a notice or an open menu)", lang);
        return Breve(e);
    }

    private static string Breve(Exception e) => Observer.Recortar((e.Message ?? e.GetType().Name).Split('\n', 2)[0].TrimEnd('\r'), 200);
}

/// <summary>
/// Las palabras del grabador de tramoya que el agente también usa al apuntar pasos (los botones
/// que guardan, el aviso de cookies, el nombre de las teclas), para que una habilidad aprendida
/// aquí se lea igual que un proyecto grabado allí.
/// </summary>
internal static class Palabras
{
    public static readonly Regex Guardan = new(@"^\s*(imputar|guardar|grabar|enviar|aceptar|confirmar|borrar|eliminar|firmar|validar|aprobar|copiar filas|save|submit|delete|ok)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static readonly Regex Cookies = new(@"cookie|aceptar tod|accept all|rechazar tod|reject all|permitir tod|consent|allow all", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static readonly Dictionary<string, string> Teclas = new()
    {
        ["Enter"] = "Intro", ["Tab"] = "Tabulador", ["Escape"] = "Esc", ["Backspace"] = "Borrar",
        ["ArrowDown"] = "flecha abajo", ["ArrowUp"] = "flecha arriba",
    };

    /// <summary>La tecla como la entiende Playwright, venga como la diga el modelo («Intro», «enter», «Esc»).</summary>
    public static string Tecla(string valor)
    {
        var v = valor.Trim();
        return v.ToLowerInvariant() switch
        {
            "enter" or "intro" or "return" or "\n" => "Enter",
            "tab" or "tabulador" => "Tab",
            "escape" or "esc" => "Escape",
            "backspace" or "borrar" => "Backspace",
            "arrowdown" or "down" or "abajo" or "flecha abajo" => "ArrowDown",
            "arrowup" or "up" or "arriba" or "flecha arriba" => "ArrowUp",
            "pagedown" or "avpág" or "avpag" => "PageDown",
            "pageup" or "repág" or "repag" => "PageUp",
            "space" or "espacio" => "Space",
            _ => v,
        };
    }
}
