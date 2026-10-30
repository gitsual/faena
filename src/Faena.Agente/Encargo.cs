using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Faena.Escena;

namespace Faena.Agente;

/// <summary>Lo que se hizo con las habilidades antes de que el modelo tomara el mando.</summary>
public sealed class UsoHabilidad
{
    public string Nombre { get; set; } = "";
    public Dictionary<string, string> Valores { get; set; } = [];
    /// <summary>Cuántos de sus pasos se han repetido bien.</summary>
    public int Repetidos { get; set; }
    public int Total { get; set; }
    /// <summary>Por qué se dejó de repetir (un paso que ya no está, por ejemplo); vacío si se repitió entera.</summary>
    public string Fallo { get; set; } = "";
    /// <summary>La habilidad falló a medias, el modelo terminó la tarea y la habilidad se ha reescrito con lo que funcionó.</summary>
    public bool Reparada { get; set; }
}

/// <summary>El resultado de un encargo: el del agente y, si se usó, la habilidad.</summary>
public sealed record ResultadoEncargo(AgentResult Agente, UsoHabilidad? Habilidad);

/// <summary>
/// Un encargo («hazme esto»): si hay una habilidad aprendida que encaja, se repiten sus pasos
/// tal cual (rápido y sin gastar modelo); si alguno falla, o al acabar, el modelo sigue desde ahí
/// con la receta delante, comprueba que el objetivo está cumplido y termina. Sin habilidad, el
/// modelo razona desde cero. Lo irreversible se para también al repetir.
/// </summary>
public static class Encargo
{
    public static async Task<ResultadoEncargo> RunAsync(ISuperficie sup, AgentOptions opciones, IReadOnlyList<Habilidad> habilidades,
        string? forzada, IChatBackend backend, IProgress<string>? log, CancellationToken ct)
    {
        opciones = opciones with { Repertorio = habilidades.Where(h => h.Superficie == sup.Tipo).ToList() };
        var eleccion = await ElegirAsync(sup, opciones.Goal, habilidades, forzada, backend, log, ct).ConfigureAwait(false);
        if (eleccion is null)
            return new(await Agent.RunAsync(sup, opciones, backend, log, ct).ConfigureAwait(false), null);

        var inicio = sup.Donde;
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var (hechos, uso, parado) = await RepetirHabilidadAsync(sup, eleccion.Habilidad, eleccion.Valores, opciones, log, ct).ConfigureAwait(false);
        if (parado is not null)
        {
            parado.Duracion = reloj.Elapsed;
            return new(parado, uso);
        }
        var conReceta = opciones with
        {
            Receta = Habilidades.Receta(eleccion.Habilidad, Habilidades.Rellenar(eleccion.Habilidad, eleccion.Valores)),
            Previos = hechos,
            MaxPasos = opciones.MaxPasos + hechos.Count,
        };
        var r = await Agent.RunAsync(sup, conReceta, backend, log, ct).ConfigureAwait(false);
        r.Inicio = inicio;
        r.Duracion = reloj.Elapsed;
        return new(r, uso);
    }

    /// <summary>
    /// Sin modelo: repite la habilidad con los valores dados y ya. Sale bien si se repiten todos
    /// los pasos; nadie comprueba el resultado, así que vale para procedimientos mecánicos ya
    /// probados. Lo irreversible se para igual.
    /// </summary>
    public static async Task<ResultadoEncargo> SinModeloAsync(ISuperficie sup, AgentOptions opciones, Habilidad h,
        IReadOnlyDictionary<string, string> valores, IProgress<string>? log, CancellationToken ct)
    {
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var todos = new Dictionary<string, string>(h.Parametros, StringComparer.Ordinal);
        foreach (var (k, v) in valores)
        {
            if (!todos.ContainsKey(k)) throw new ArgumentException($"la habilidad «{h.Nombre}» no tiene el parámetro «{k}» (tiene: {string.Join(", ", h.Parametros.Keys)})");
            todos[k] = v;
        }
        var (hechos, uso, parado) = await RepetirHabilidadAsync(sup, h, todos, opciones, log, ct).ConfigureAwait(false);
        var r = parado ?? new AgentResult { Inicio = sup.Donde, Pasos = hechos };
        if (parado is null)
        {
            r.Ok = uso.Fallo.Length == 0 && uso.Repetidos == uso.Total;
            r.Resumen = r.Ok
                ? $"Habilidad «{h.Nombre}» repetida entera ({uso.Total} pasos), sin modelo."
                : ct.IsCancellationRequested ? "Se ha cancelado antes de terminar." : $"La habilidad «{h.Nombre}» se ha quedado a medias: {uso.Fallo}.";
            if (!r.Ok) r.Avisos.Add(r.Resumen);
            try { r.Final = sup.Donde; } catch (Exception) { }
        }
        r.Duracion = reloj.Elapsed;
        if (opciones.CarpetaCapturas is { } carpeta && await sup.CapturaAsync().ConfigureAwait(false) is { } png)
        {
            Directory.CreateDirectory(carpeta);
            var ruta = Path.Combine(carpeta, "final.png");
            await File.WriteAllBytesAsync(ruta, png, CancellationToken.None).ConfigureAwait(false);
            r.Capturas.Add(ruta);
        }
        return new(r, uso);
    }

    /// <summary>Repite los pasos de una habilidad hasta el final, hasta uno que falle o hasta uno irreversible no autorizado (entonces, <c>parado</c>).</summary>
    internal static async Task<(List<JsonObject> Hechos, UsoHabilidad Uso, AgentResult? Parado)> RepetirHabilidadAsync(ISuperficie sup, Habilidad h,
        IReadOnlyDictionary<string, string> valores, AgentOptions opciones, IProgress<string>? log, CancellationToken ct)
    {
        var pasos = Habilidades.Rellenar(h, valores);
        var uso = new UsoHabilidad { Nombre = h.Nombre, Valores = new(valores), Total = pasos.Count };
        log?.Report($"Uso la habilidad «{h.Nombre}» ({pasos.Count} pasos){(valores.Count != 0 ? ": " + string.Join(", ", valores.Select(v => $"{v.Key} = «{v.Value}»")) : "")}.");
        var hechos = new List<JsonObject>();
        foreach (var paso in pasos)
        {
            if (ct.IsCancellationRequested) break;
            var desc = Observer.Str(paso, "desc");
            if (Observer.Str(paso, "tipo") == "clic" && Guardian.Categoria(Nombrado(desc)) is { } categoria)
            {
                var peligro = new Irreversible(categoria, desc.Length != 0 ? desc : $"Pulsar {Observer.Str(paso, "selector")}", $"paso de la habilidad «{h.Nombre}»");
                if (!Guardian.Permitida(peligro, opciones.Permitidas) && !await PreguntarAsync(opciones, peligro, ct).ConfigureAwait(false))
                {
                    var parado = new AgentResult
                    {
                        Inicio = sup.Donde,
                        Pasos = hechos,
                        Pendiente = peligro,
                        Resumen = $"Me he parado antes de: {desc} ({Agent.Porque(peligro)}, no autorizado). Lo anterior de la habilidad está hecho.",
                    };
                    parado.Avisos.Add(parado.Resumen);
                    try { parado.Final = sup.Donde; } catch (Exception) { }
                    log?.Report(parado.Resumen);
                    return (hechos, uso, parado);
                }
            }
            try
            {
                await sup.RepetirAsync(paso, ct).ConfigureAwait(false);
                await sup.AsentarAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                uso.Fallo = $"el paso {hechos.Count + 1} («{desc}») no se ha podido repetir: {Observer.Recortar((e.Message ?? "").TrimEnd().Split('\n')[0], 160)}";
                log?.Report($"La habilidad se queda aquí: {uso.Fallo.TrimEnd('.')}.");
                break;
            }
            hechos.Add(paso);
            uso.Repetidos++;
            log?.Report($"Repetido {uso.Repetidos}/{pasos.Count}: {desc}");
        }
        return (hechos, uso, null);
    }

    private static async Task<Eleccion?> ElegirAsync(ISuperficie sup, string objetivo, IReadOnlyList<Habilidad> habilidades, string? forzada,
        IChatBackend backend, IProgress<string>? log, CancellationToken ct)
    {
        if (forzada is { Length: > 0 })
        {
            var h = habilidades.FirstOrDefault(x => string.Equals(x.Nombre, forzada, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"no hay ninguna habilidad «{forzada}»");
            // Aunque venga elegida, los valores salen del objetivo.
            return await Habilidades.ElegirAsync(objetivo, [h], backend, ct).ConfigureAwait(false) ?? new Eleccion(h, h.Parametros);
        }
        var candidatas = Habilidades.Candidatas(objetivo, habilidades, sup.Tipo, sup.Donde);
        if (candidatas.Count == 0) return null;
        log?.Report($"Habilidades que podrían servir: {string.Join(", ", candidatas.Select(c => c.Nombre))}.");
        var eleccion = await Habilidades.ElegirAsync(objetivo, candidatas, backend, ct).ConfigureAwait(false);
        if (eleccion is null) log?.Report("Ninguna encaja del todo: razono desde cero.");
        return eleccion;
    }

    private static async Task<bool> PreguntarAsync(AgentOptions opciones, Irreversible peligro, CancellationToken ct)
    {
        if (opciones.Confirmar is not { } confirmar) return false;
        try { return await confirmar(peligro, ct).ConfigureAwait(false); }
        catch (Exception) { return false; }
    }

    /// <summary>El nombre del control que sale en la descripción de un paso («Pulsa el botón «Enviar»» → «Enviar»).</summary>
    internal static string Nombrado(string desc)
    {
        var fin = desc.LastIndexOf('»');
        var ini = fin > 0 ? desc.LastIndexOf('«', fin) : -1;
        return ini >= 0 ? desc[(ini + 1)..fin] : desc;
    }

    // ------------------------------------------------------------------ informe

    /// <summary>El informe en JSON (para máquinas y para aprender de él con <c>habilidades aprender</c>).</summary>
    public static JsonObject InformeJson(string objetivo, string superficie, ResultadoEncargo r)
    {
        var a = r.Agente;
        var o = new JsonObject
        {
            ["objetivo"] = objetivo,
            ["superficie"] = superficie,
            ["estado"] = Estado(a),
            ["ok"] = a.Ok,
            ["resumen"] = a.Resumen,
            ["inicio"] = a.Inicio,
            ["final"] = a.Final,
            ["titulo"] = a.Titulo,
            ["segundos"] = Math.Round(a.Duracion.TotalSeconds, 1),
            ["turnos"] = a.Turnos,
            ["pasos"] = new JsonArray(a.Pasos.Select(p => (JsonNode)p.DeepClone()).ToArray()),
            ["avisos"] = new JsonArray(a.Avisos.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["capturas"] = new JsonArray(a.Capturas.Select(x => (JsonNode?)JsonValue.Create(Path.GetFileName(x))).ToArray()),
        };
        if (a.Pendiente is { } p)
            o["pendiente"] = new JsonObject { ["categoria"] = p.Categoria, ["accion"] = p.Descripcion, ["motivo"] = p.Motivo };
        if (r.Habilidad is { } h)
            o["habilidad"] = new JsonObject
            {
                ["nombre"] = h.Nombre,
                ["valores"] = new JsonObject(h.Valores.Select(v => KeyValuePair.Create(v.Key, (JsonNode?)JsonValue.Create(v.Value)))),
                ["repetidos"] = h.Repetidos,
                ["total"] = h.Total,
                ["fallo"] = h.Fallo,
                ["reparada"] = h.Reparada,
            };
        return o;
    }

    /// <summary>«hecho», «pendiente» (parado ante algo irreversible) o «sin terminar».</summary>
    public static string Estado(AgentResult a) => a.Ok ? "hecho" : a.Pendiente is not null ? "pendiente" : "sin terminar";

    /// <summary>El informe para una persona: qué se pidió, cómo acabó, qué falta por autorizar y cada paso.</summary>
    public static string InformeMarkdown(string objetivo, string superficie, ResultadoEncargo r)
    {
        var a = r.Agente;
        var sb = new StringBuilder();
        sb.Append("# Tarea: ").Append(objetivo).Append("\n\n");
        var estado = Estado(a) switch
        {
            "hecho" => "✅ Hecha",
            "pendiente" => "⏸️ Parada: falta tu autorización",
            _ => "⚠️ Sin terminar",
        };
        sb.Append("**Estado:** ").Append(estado).Append("  \n");
        sb.Append("**Dónde:** ").Append(superficie == "escritorio" ? "aplicación " : "").Append(a.Inicio).Append(a.Final.Length != 0 && a.Final != a.Inicio ? $" → {a.Final}" : "").Append("  \n");
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"**Tiempo:** {a.Duracion.TotalSeconds:0} s, {a.Turnos} turnos del modelo, {a.Pasos.Count} pasos\n\n"));
        if (a.Resumen.Length != 0) sb.Append("> ").Append(a.Resumen.Replace("\n", "\n> ")).Append("\n\n");
        if (a.Pendiente is { } p)
        {
            sb.Append("## Pendiente de autorizar\n\n");
            sb.Append("- **Acción:** ").Append(p.Descripcion).Append('\n');
            sb.Append("- **Tipo:** ").Append(p.Categoria).Append('\n');
            if (p.Motivo.Length != 0) sb.Append("- **Por qué:** ").Append(p.Motivo).Append('\n');
            sb.Append("\nPara hacerla, repite la tarea con `--permitir ").Append(p.Categoria).Append("` (o hazla tú a mano).\n\n");
        }
        if (r.Habilidad is { } h)
        {
            sb.Append("## Habilidad usada\n\n");
            sb.Append($"«{h.Nombre}»: {h.Repetidos} de {h.Total} pasos repetidos tal cual");
            if (h.Valores.Count != 0) sb.Append(" con ").Append(string.Join(", ", h.Valores.Select(v => $"{v.Key} = «{v.Value}»")));
            sb.Append(".\n");
            if (h.Fallo.Length != 0) sb.Append("Se dejó de repetir porque ").Append(h.Fallo).Append("; siguió el modelo.\n");
            if (h.Reparada) sb.Append("La habilidad se ha reescrito con los pasos que han funcionado esta vez.\n");
            sb.Append('\n');
        }
        sb.Append("## Pasos\n\n");
        if (a.Pasos.Count == 0) sb.Append("(ninguno)\n");
        for (var i = 0; i < a.Pasos.Count; i++)
            sb.Append(i + 1).Append(". ").Append(Observer.Str(a.Pasos[i], "desc")).Append('\n');
        if (a.Avisos.Count != 0)
        {
            sb.Append("\n## Avisos\n\n");
            foreach (var x in a.Avisos) sb.Append("- ").Append(x).Append('\n');
        }
        if (a.Capturas.Count != 0)
        {
            sb.Append("\n## Capturas\n\n");
            foreach (var c in a.Capturas) sb.Append("- [").Append(Path.GetFileName(c)).Append("](capturas/").Append(Path.GetFileName(c)).Append(")\n");
        }
        return sb.ToString();
    }
}
