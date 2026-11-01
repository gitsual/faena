using System.Diagnostics;
using System.Text.Json.Nodes;
using Faena.Agente;

namespace Faena.Tests;

/// <summary>
/// Los encargos de la ventana: cómo se lanza <c>faena hacer</c> por debajo, cómo se lee lo
/// que deja en su carpeta (registro, pregunta, informe) y el repertorio. El proceso es un
/// <c>cmd /c exit</c> que no hace nada; lo que el agente escribiría se deja a mano.
/// </summary>
public sealed class EncargosTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "faena-encargos-" + Guid.NewGuid().ToString("N"));
    private readonly Opciones _o;
    private readonly Func<ProcessStartInfo, Process?> _lanzador = Encargos.Lanzador;
    private readonly Func<Process, bool> _vive = Encargos.Vive;
    private readonly Func<DateTime> _ahora = Encargos.Ahora;
    private ProcessStartInfo? _lanzado;
    private readonly Idioma _idioma = new("es");

    public EncargosTests()
    {
        Directory.CreateDirectory(_raiz);
        _o = new Opciones
        {
            Exe = @"C:\kit\faena.exe",
            CarpetaTareas = Path.Combine(_raiz, "tareas"),
            CarpetaHabilidades = Path.Combine(_raiz, "habilidades"),
            Entorno = new Dictionary<string, string> { ["PLAYWRIGHT_BROWSERS_PATH"] = @"C:\kit\navegador" },
            ProyectosTramoya = Path.Combine(_raiz, "tramoya"),
        };
        Encargos.Lanzador = psi =>
        {
            _lanzado = psi;
            return Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false });
        };
        Encargos.Vive = _ => true;
        Encargos.Ahora = () => new DateTime(2026, 10, 28, 19, 30, 5);
    }

    public void Dispose()
    {
        _idioma.Dispose();
        Encargos.Lanzador = _lanzador;
        Encargos.Vive = _vive;
        Encargos.Ahora = _ahora;
        Encargos.PararTodos(_o);
        try { Directory.Delete(_raiz, true); } catch (IOException) { }
    }

    [Fact]
    public void SinCarpetaDeTareasLaListaEstaVacia() => Assert.Empty(Encargos.Lista(_o));

    [Fact]
    public void EncargarLanzaHacerConSuCarpetaYLoApunta()
    {
        var id = Encargos.Encargar(_o, new JsonObject
        {
            ["objetivo"] = "Crea una nota «Pan»",
            ["url"] = "127.0.0.1:8770/index.html",
            ["permitir"] = new JsonArray("enviar", "borrar"),
            ["ver"] = true,
            ["capturas"] = true,
            ["aprender"] = "crear-nota",
            ["max_pasos"] = 12,
            ["max_minutos"] = "5",
        });
        Assert.Equal("2026-10-28-193005-crea-una-nota-pan", id);
        var psi = _lanzado!;
        Assert.Equal(@"C:\kit\faena.exe", psi.FileName);
        Assert.Equal(_o.CarpetaTareas, psi.Environment["FAENA_TAREAS"]);
        Assert.Equal(@"C:\kit\navegador", psi.Environment["PLAYWRIGHT_BROWSERS_PATH"]);
        var argv = psi.ArgumentList.ToList();
        Assert.Equal(["hacer", "Crea una nota «Pan»", "--url", "https://127.0.0.1:8770/index.html"], argv.Take(4));
        Assert.Contains("--ver", argv);
        Assert.Contains("--capturas", argv);
        Assert.Contains("--fondo", argv);
        Assert.Contains("--preguntar-en-carpeta", argv);
        Assert.Equal("enviar", argv[argv.IndexOf("--permitir") + 1]);
        Assert.Equal("borrar", argv[argv.LastIndexOf("--permitir") + 1]);
        Assert.Equal("crear-nota", argv[argv.IndexOf("--aprender") + 1]);
        Assert.Equal("12", argv[argv.IndexOf("--max-pasos") + 1]);
        Assert.Equal("5", argv[argv.IndexOf("--max-minutos") + 1]);
        Assert.Equal(Path.Combine(_o.CarpetaTareas!, id), argv[argv.IndexOf("--carpeta") + 1]);
        Assert.Equal(_o.CarpetaHabilidades, psi.Environment["FAENA_HABILIDADES"]);
        Assert.Equal(@"C:\kit\navegador", psi.Environment["PLAYWRIGHT_BROWSERS_PATH"]);

        var d = Encargos.Detalle(_o, id)!;
        Assert.Equal("en-marcha", d["estado"]!.ToString());
        Assert.Equal("Crea una nota «Pan»", d["objetivo"]!.ToString());
        Assert.Equal("https://127.0.0.1:8770/index.html", d["donde"]!.ToString());
        Assert.Equal(2, d["pedido"]!["permitir"]!.AsArray().Count);
        Assert.Equal("en-marcha", Encargos.Lista(_o).Single()!["estado"]!.ToString());
    }

    [Fact]
    public void ConUnaObraSinModeloPasaLosDatos()
    {
        Encargos.Encargar(_o, new JsonObject
        {
            ["habilidad"] = "crear-nota",
            ["sin_modelo"] = true,
            ["parametros"] = new JsonObject { ["title"] = "Leche", ["text"] = "" },
            ["app"] = @"C:\apps\Notas.exe",
        });
        var argv = _lanzado!.ArgumentList.ToList();
        Assert.Equal("Repetir «crear-nota»", argv[1]);
        Assert.Equal(@"C:\apps\Notas.exe", argv[argv.IndexOf("--app") + 1]);
        Assert.Contains("--sin-modelo", argv);
        Assert.Equal("title=Leche", argv[argv.IndexOf("--param") + 1]);
        Assert.Single(argv, a => a == "--param");
    }

    [Theory]
    [InlineData("{}", "Dígame qué hay que hacer")]
    [InlineData("{\"objetivo\":\"x\"}", "Dígame dónde")]
    [InlineData("{\"objetivo\":\"x\",\"url\":\"a.test\",\"app\":\"b.exe\"}", "no los dos")]
    [InlineData("{\"objetivo\":\"x\",\"url\":\"a.test\",\"sin_modelo\":true}", "elegir un oficio")]
    public void EncargarExplicaLoQueFalta(string pedido, string motivo) =>
        Assert.Contains(motivo, Assert.Throws<ArgumentException>(() => Encargos.Encargar(_o, (JsonObject)JsonNode.Parse(pedido)!)).Message);

    private string CarpetaFalsa(string id, string? informe = null)
    {
        var c = Path.Combine(_o.CarpetaTareas!, id);
        Directory.CreateDirectory(c);
        File.WriteAllText(Path.Combine(c, "encargo.json"), """{"objetivo":"Envía el aviso","superficie":"web","donde":"https://a.test/","creado":"28-10-2026 19:00","max_pasos":30}""");
        File.WriteAllText(Path.Combine(c, "registro.log"), string.Join("\n",
            "19:00:01 Objetivo: Envía el aviso",
            "19:00:03 Turno 1: escribir 1 «Marta» — el destinatario",
            "19:00:03 Paso 1: Escribe «Marta» en el campo «Para» — porque el destinatario",
            "19:00:05 Acción irreversible: Pulsar el botón «Enviar» (enviar).",
            "sin hora"));
        if (informe is not null) File.WriteAllText(Path.Combine(c, "informe.json"), informe);
        return c;
    }

    [Fact]
    public void ElDetalleConvierteElRegistroEnHojaDeCues()
    {
        var c = CarpetaFalsa("2026-10-28-190000-envia-el-aviso");
        Encargos.Vive = _ => false;
        var d = Encargos.Detalle(_o, "2026-10-28-190000-envia-el-aviso")!;
        Assert.Equal("interrumpido", d["estado"]!.ToString());
        Assert.Equal(1, d["n"]!.GetValue<int>());
        Assert.Equal("Escribe «Marta» en el campo «Para»", d["ultimo"]!.ToString());
        var cues = d["cues"]!.AsArray();
        Assert.Equal(["nota", "turno", "paso", "alto", "nota"], cues.Select(x => x!["tipo"]!.ToString()));
        Assert.Equal("19:00:03", cues[2]!["hora"]!.ToString());
        Assert.Equal("", cues[4]!["hora"]!.ToString());

        File.WriteAllText(Path.Combine(c, "informe.json"), """{"estado":"pendiente","resumen":"Me he parado","pendiente":{"categoria":"enviar","accion":"Pulsar el botón «Enviar»"},"pasos":[]}""");
        d = Encargos.Detalle(_o, "2026-10-28-190000-envia-el-aviso")!;
        Assert.Equal("pendiente", d["estado"]!.ToString());
        Assert.Equal("enviar", d["informe"]!["pendiente"]!["categoria"]!.ToString());
        Assert.Null(Encargos.Detalle(_o, "../fuera"));
        Assert.Null(Encargos.Detalle(_o, "2026-10-28-190000-no-existe"));
    }

    [Fact]
    public void LaPreguntaSeVeYLaRespuestaQuedaEscrita()
    {
        var id = Encargos.Encargar(_o, new JsonObject { ["objetivo"] = "Envía el aviso", ["url"] = "https://a.test/" });
        var c = Path.Combine(_o.CarpetaTareas!, id);
        Assert.False(Encargos.Responder(_o, id, true));
        File.WriteAllText(Path.Combine(c, "pregunta.json"), """{"categoria":"enviar","accion":"Pulsar el botón «Enviar»","hora":"19:30:09"}""");
        var d = Encargos.Detalle(_o, id)!;
        Assert.Equal("pregunta", d["estado"]!.ToString());
        Assert.Equal("Pulsar el botón «Enviar»", d["pregunta"]!["accion"]!.ToString());
        Assert.True(Encargos.Responder(_o, id, true));
        Assert.Contains("\"si\":true", File.ReadAllText(Path.Combine(c, "respuesta.json")));

        Encargos.Parar(_o, id);
        Assert.Equal("parado", Encargos.Detalle(_o, id)!["estado"]!.ToString());
    }

    [Fact]
    public async Task ElAgentePreguntaPorArchivoYEsperaLaRespuesta()
    {
        var c = Path.Combine(_raiz, "pregunta");
        Directory.CreateDirectory(c);
        var log = new List<string>();
        var preguntar = Hacer.PreguntarPorArchivo(c, new Progress(log));
        var tarea = preguntar(new Irreversible("enviar", "Pulsar el botón «Enviar»", "toca"), CancellationToken.None);
        var pregunta = Path.Combine(c, "pregunta.json");
        for (var i = 0; i < 50 && !File.Exists(pregunta); i++) await Task.Delay(50);
        Assert.Contains("\"categoria\":\"enviar\"", File.ReadAllText(pregunta));
        File.WriteAllText(Path.Combine(c, "respuesta.json"), """{"si":true}""");
        Assert.True(await tarea.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(pregunta));
        Assert.False(File.Exists(Path.Combine(c, "respuesta.json")));
        Assert.Contains("Me has dicho que sí.", log);

        var antes = Hacer.EsperaRespuesta;
        Hacer.EsperaRespuesta = TimeSpan.FromMilliseconds(400);
        try { Assert.False(await preguntar(new Irreversible("borrar", "Borrar", ""), CancellationToken.None)); }
        finally { Hacer.EsperaRespuesta = antes; }
        Assert.Contains("Nadie ha contestado: no lo hago.", log);
    }

    private sealed class Progress(List<string> lineas) : IProgress<string>
    {
        public void Report(string value) { lock (lineas) lineas.Add(value); }
    }

    [Fact]
    public void LasCapturasSoloSonLasSuyas()
    {
        var c = CarpetaFalsa("2026-10-28-190000-envia-el-aviso");
        Directory.CreateDirectory(Path.Combine(c, "capturas"));
        File.WriteAllBytes(Path.Combine(c, "capturas", "01.png"), [1]);
        Assert.NotNull(Encargos.Captura(_o, "2026-10-28-190000-envia-el-aviso", "01.png"));
        Assert.Null(Encargos.Captura(_o, "2026-10-28-190000-envia-el-aviso", "02.png"));
        Assert.Null(Encargos.Captura(_o, "2026-10-28-190000-envia-el-aviso", "..\\informe.json"));
    }

    [Fact]
    public void ElRepertorioAprendeDeUnEncargoYDeUnProcesoDeTramoya()
    {
        const string pasos = """[{"tipo":"clic","selector":"#nueva","desc":"Pulsa el botón «New note»"},{"tipo":"escribir","selector":"#t","valor":"Pan","desc":"Escribe «Pan» en el campo «Title»"}]""";
        CarpetaFalsa("2026-10-28-190000-envia-el-aviso", $$"""{"estado":"hecho","objetivo":"Crea la nota «Pan»","inicio":"http://n.test/","superficie":"web","pasos":{{pasos}}}""");
        Assert.Equal("crear-nota", Encargos.AprenderDeEncargo(_o, "2026-10-28-190000-envia-el-aviso", "Crear nota"));
        Assert.Contains("Ya sé un oficio", Assert.Throws<ArgumentException>(() => Encargos.AprenderDeEncargo(_o, "2026-10-28-190000-envia-el-aviso", "crear-nota")).Message);

        // Un proyecto de tramoya: su carpeta con proyecto.json, buscada por id o por ruta.
        var pid = "alta-de-cliente";
        var proyecto = Path.Combine(_o.ProyectosTramoya!, pid);
        Directory.CreateDirectory(proyecto);
        File.WriteAllText(Path.Combine(proyecto, "proyecto.json"), """{"nombre":"Alta de cliente","url":"https://crm.test/","pasos":[]}""");
        Assert.Contains("aún no tiene pasos", Assert.Throws<ArgumentException>(() => Encargos.AprenderDeProyecto(_o, pid, "alta")).Message);
        Assert.Contains("proyecto.json", Assert.Throws<ArgumentException>(() => Encargos.AprenderDeProyecto(_o, Path.Combine(_raiz, "nada"), "alta")).Message);
        File.WriteAllText(Path.Combine(proyecto, "proyecto.json"), $$"""{"nombre":"Alta de cliente","url":"https://crm.test/","pasos":{{pasos}}}""");
        Assert.Equal("alta", Encargos.AprenderDeProyecto(_o, pid, "alta"));
        Assert.Equal("alta-2", Encargos.AprenderDeProyecto(_o, "\"" + proyecto + "\"", "alta-2"));
        Assert.True(Encargos.Olvidar(_o, "alta-2"));

        var rep = Encargos.Repertorio(_o);
        Assert.Equal(["alta", "crear-nota"], rep.Select(x => x!["nombre"]!.ToString()));
        Assert.Equal("Pan", rep[1]!["parametros"]!["title"]!.ToString());
        Assert.Equal("Pulsa el botón «New note»", rep[1]!["pasos"]![0]!.ToString());
        Assert.True(Encargos.Olvidar(_o, "alta"));
        Assert.False(Encargos.Olvidar(_o, "alta"));
        Assert.Single(Encargos.Repertorio(_o));
    }

    [Theory]
    [InlineData("Paso 3: Pulsa «Guardar» — porque sí", "paso")]
    [InlineData("Repetido 2/4: Escribe «x»", "paso")]
    [InlineData("Turno 4: terminar — ya está", "turno")]
    [InlineData("Me he parado antes de pulsar «Enviar»", "alto")]
    [InlineData("Autorizado: sigo.", "permiso")]
    [InlineData("Terminado: hecho", "fin")]
    [InlineData("Falló: la acción…", "fallo")]
    [InlineData("Uso la habilidad «crear-nota»", "repertorio")]
    [InlineData("Objetivo: x", "nota")]
    public void CadaLineaDelRegistroTieneSuTipo(string linea, string tipo) => Assert.Equal(tipo, Encargos.Tipo(linea));
}
