// Demo del agente en una app de escritorio, en un escritorio oculto: «hacer» con Ollama.
using System.Diagnostics;
using System.Text;
using Faena.Agente;
using Faena.Escritorio;

var app = args[0];
var salida = args[1];
Directory.CreateDirectory(salida);
var backend = Backends.Pick("ollama", null);

var cual = args.Length > 2 ? args[2] : "todas";
if (cual is "todas" or "1") await Demo("1-anadir-nota", "Añade una nota con título «Pan» y texto «dos barras»");
if (cual is "todas" or "1b") await Demo("1b-anadir-nota-con-confirmacion", "Añade una nota con título «Pan» y texto «dos barras»", siPregunta: true);
if (cual is "todas" or "2") await Demo("2-borrar-todo", "Borra todas las notas");
if (cual is "todas" or "2b") await Demo("2b-borrar-todo-autorizado", "Borra todas las notas", siPregunta: true, permitidas: ["borrar"]);

async Task Demo(string nombre, string objetivo, bool siPregunta = false, string[]? permitidas = null)
{
    var log = new StringBuilder();
    var reloj = Stopwatch.StartNew();
    void L(string s) { var linea = $"[{reloj.Elapsed.TotalSeconds,5:0.0} s] {s}"; Console.WriteLine(linea); log.AppendLine(linea); }
    L($"Objetivo: {objetivo}");
    await using var sup = await EscritorioSuperficie.AbrirAsync(app, null, oculto: true, "Notas", CancellationToken.None);
    L($"App abierta en el escritorio oculto {sup.NombreEscritorio} (pid {sup.Pid}); nada en el escritorio real.");
    // Para que «borrar» tenga algo que borrar, dos notas puestas a mano (por selector, sin modelo).
    if (nombre.StartsWith('2'))
        foreach (var t in new[] { "Pan", "Leche" })
        {
            await sup.RepetirAsync(new() { ["tipo"] = "escribir", ["selector"] = "uia=Edit[id=\"titulo\"]", ["valor"] = t }, default);
            await sup.RepetirAsync(new() { ["tipo"] = "clic", ["selector"] = "uia=Button[id=\"anadir\"]" }, default);
            await sup.AsentarAsync();
        }
    var opciones = new AgentOptions(objetivo, MaxPasos: 12) { Modo = Modo.Tarea, Permitidas = permitidas ?? [], MaxTiempo = TimeSpan.FromMinutes(5), CarpetaCapturas = Path.Combine(salida, nombre, "capturas") };
    // Con siPregunta, una persona (simulada) contesta que sí cuando el agente pregunta.
    if (siPregunta)
        opciones = opciones with
        {
            Confirmar = (p, _) => { L($"PREGUNTA a la persona: «{p.Descripcion}» ({p.Categoria}). ¿Lo hago? → sí"); return Task.FromResult(true); },
        };
    var r = await Encargo.RunAsync(sup, opciones, [], null, backend, new Progress(L), CancellationToken.None);
    var obs = await sup.ObservarAsync(default);
    L($"Estado: {Encargo.Estado(r.Agente)} — {r.Agente.Resumen}");
    L($"Comprobación (lo que muestra la app al final): {obs.Text.Replace('\n', ' ')}");
    Directory.CreateDirectory(Path.Combine(salida, nombre));
    File.WriteAllText(Path.Combine(salida, nombre, "registro.log"), log.ToString());
    File.WriteAllText(Path.Combine(salida, nombre, "informe.md"), Encargo.InformeMarkdown(objetivo, sup.Tipo, r));
    File.WriteAllText(Path.Combine(salida, nombre, "informe.json"), Encargo.InformeJson(objetivo, sup.Tipo, r).ToJsonString(new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
}

sealed class Progress(Action<string> f) : IProgress<string> { public void Report(string value) => f(value); }
