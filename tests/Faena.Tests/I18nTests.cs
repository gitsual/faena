using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Faena.Agente;

namespace Faena.Tests;

/// <summary>A faena command line in a temporary home: console captured, no browser, a fake web and model.</summary>
internal sealed class Consola : IDisposable
{
    public string Carpeta { get; } = Path.Combine(Path.GetTempPath(), "faena-i18n-" + Guid.NewGuid().ToString("N"));
    private readonly TextWriter _out = Console.Out, _err = Console.Error;
    private readonly TextReader _in = Console.In;
    public StringWriter Out { get; } = new();
    public StringWriter Err { get; } = new();
    private readonly Func<int?> _browser = Cli.EnsureBrowser;
    private readonly Func<bool> _tty = Cli.StdinIsTty;
    private readonly Func<string?, string?, IChatBackend> _backend = Hacer.PickBackend;
    private readonly Func<string, bool, string, Task<(ISuperficie, IAsyncDisposable)>> _web = Hacer.AbrirWeb;
    private readonly Func<string> _exe = AgentSkills.Exe, _home = AgentSkills.Home;

    public string Habilidades => Path.Combine(Carpeta, "habilidades");
    public string Home => Path.Combine(Carpeta, "home");
    public string Ajustes => Path.Combine(Carpeta, "ajustes.json");

    public Consola()
    {
        Directory.CreateDirectory(Carpeta);
        Environment.SetEnvironmentVariable("FAENA_TAREAS", Path.Combine(Carpeta, "tareas"));
        Environment.SetEnvironmentVariable("FAENA_HABILIDADES", Habilidades);
        Environment.SetEnvironmentVariable("FAENA_AJUSTES", Ajustes);
        Cli.EnsureBrowser = () => null;
        Cli.StdinIsTty = () => false;
        AgentSkills.Exe = () => @"C:\Tools\faena\faena.exe";
        AgentSkills.Home = () => Home;
        Console.SetOut(Out);
        Console.SetError(Err);
    }

    /// <summary>The web is <paramref name="sup"/> and the model answers <paramref name="respuestas"/>, in order.</summary>
    public Guion Web(ISuperficie sup, params string[] respuestas)
    {
        var g = new Guion(respuestas);
        Hacer.AbrirWeb = (_, _, _) => Task.FromResult<(ISuperficie, IAsyncDisposable)>((sup, new Nada()));
        Hacer.PickBackend = (_, _) => g;
        return g;
    }

    public void Dispose()
    {
        Console.SetOut(_out);
        Console.SetError(_err);
        Console.SetIn(_in);
        Cli.EnsureBrowser = _browser;
        Cli.StdinIsTty = _tty;
        Hacer.PickBackend = _backend;
        Hacer.AbrirWeb = _web;
        AgentSkills.Exe = _exe;
        AgentSkills.Home = _home;
        Environment.SetEnvironmentVariable("FAENA_TAREAS", null);
        Environment.SetEnvironmentVariable("FAENA_HABILIDADES", null);
        try { Directory.Delete(Carpeta, true); } catch (IOException) { }
    }

    internal sealed class Guion(params string[] respuestas) : IChatBackend
    {
        private readonly Queue<string> _respuestas = new(respuestas);
        public List<(string System, IReadOnlyList<ChatMessage> Mensajes)> Llamadas { get; } = [];

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
        {
            Llamadas.Add((system, messages));
            return Task.FromResult(_respuestas.Count == 0 ? """{"action":"fail","summary":"out of script"}""" : _respuestas.Dequeue());
        }
    }

    private sealed class Nada : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A mail form: «Para», «Send» and «Guardar borrador»; it writes its steps as the real surfaces do, in the current language.</summary>
    internal sealed class Correo : ISuperficie
    {
        public string Tipo => "web";
        public string Donde => "https://correo.test/";
        public List<string> Hecho { get; } = [];
        private readonly Observation _obs = new()
        {
            Url = "https://correo.test/", Title = "Mail", Total = 3,
            Elements = [new(1, "", "el campo", "Para", "", ""), new(2, "", "el botón", "Send", "", ""), new(3, "", "el botón", "Guardar borrador", "", "")],
        };

        public Task<string> TituloAsync() => Task.FromResult("Mail");
        public Task<Observation> ObservarAsync(CancellationToken ct) => Task.FromResult(_obs);
        public Task AsentarAsync() => Task.CompletedTask;
        public Task<byte[]?> CapturaAsync() => Task.FromResult<byte[]?>(null);
        public Task RepetirAsync(JsonObject paso, CancellationToken ct) { Hecho.Add(paso["desc"]!.ToString()); return Task.CompletedTask; }

        public Task<JsonObject?> ActuarAsync(Observation obs, Decision d, CancellationToken ct)
        {
            var el = obs.Elemento(d.Ref ?? 0)!;
            var desc = d.Accion == "escribir" ? Lengua.Escribe(d.Valor, el.Nombre) : Lengua.Clic(Lengua.Cosa(el.Tipo, el.Nombre));
            Hecho.Add(desc);
            var paso = new JsonObject { ["tipo"] = d.Accion, ["selector"] = $"#e{d.Ref}", ["desc"] = desc };
            if (d.Valor.Length != 0) paso["valor"] = d.Valor;
            return Task.FromResult<JsonObject?>(paso);
        }
    }

    public const string Escribir = """{"thought":"the recipient","action":"type","ref":1,"value":"Marta","irreversible":false,"summary":""}""";
    public const string Guardar = """{"thought":"save it","action":"click","ref":3}""";
    public const string Enviar = """{"thought":"send it","action":"click","ref":2}""";
    public const string Terminar = """{"thought":"done","action":"done","summary":"Draft saved for Marta"}""";
}

/// <summary>English by default, Spanish to choose: the CLI, the reports, the prompts, the guard and the Settings.</summary>
public sealed class I18nTests : IDisposable
{
    private readonly Idioma _idioma = new("en");

    public void Dispose() => _idioma.Dispose();

    // ------------------------------------------------------------------ Lengua

    [Fact]
    public void LenguaDiceCadaCosaEnSuIdioma()
    {
        Assert.Equal("en", Lengua.Normalizar(null));
        Assert.Equal("es", Lengua.Normalizar("es-ES"));
        Assert.Equal("en", Lengua.Normalizar("fr"));
        Assert.Equal("the “Save” button", Lengua.Cosa("el botón", "Save", "en"));
        Assert.Equal("el botón «Save»", Lengua.Cosa("el botón", "Save", "es"));
        Assert.Equal("Check the “I agree” checkbox", Lengua.Clic(Lengua.Cosa("la casilla", "I agree", "en"), "on", "en"));
        Assert.Equal("Type “Milk” in the “Title” field", Lengua.Escribe("Milk", "Title", "en"));
        Assert.Equal("Pulsa la tecla Intro", Lengua.Tecla("Enter", "es"));
        Assert.Equal(["x", "Title"], Lengua.Citas("Escribe «x» en el campo «Title»"));
        Assert.Equal(["x", "Title"], Lengua.Citas("Type “x” in the “Title” field"));
    }

    [Fact]
    public void UnaHabilidadAprendidaEnInglesSacaSusParametros()
    {
        var pasos = new List<JsonObject>
        {
            new() { ["tipo"] = "escribir", ["selector"] = "#t", ["valor"] = "Milk", ["desc"] = "Type “Milk” in the “Title” field" },
            new() { ["tipo"] = "clic", ["selector"] = "#s", ["desc"] = "Click the “Save” button" },
        };
        var h = Habilidades.Aprender(pasos, "nota", "Note “Milk”", "https://n.test/");
        Assert.Equal("Milk", h.Parametros["title"]);
        Assert.Equal("Type “{title}” in the “Title” field", h.Pasos[0]["desc"]!.ToString());
        Assert.Equal("Send", Encargo.Nombrado("Click the “Send” button"));
    }

    // ------------------------------------------------------------------ the CLI

    [Fact]
    public void LaAyudaSaleEnInglesYEnEspanol()
    {
        using var c = new Consola();
        Assert.Equal(0, Cli.Run(["--help"]));
        var en = c.Out.ToString();
        Assert.Contains("{do,skills,skill,window}", en);
        Assert.Contains("do (hacer, do-task)", en);
        Assert.Contains("show this help message and exit", en);
        Assert.Contains("--lang {en,es}", en);

        c.Out.GetStringBuilder().Clear();
        Assert.Equal(0, Cli.Run(["--lang", "es", "--help"]));
        var es = c.Out.ToString();
        Assert.Contains("{hacer,habilidades,skill,ventana}", es);
        Assert.Contains("hacer (do, do-task)", es);
        Assert.Contains("muestra esta ayuda y sale", es);

        c.Out.GetStringBuilder().Clear();
        Assert.Equal(0, Cli.Run(["do", "--help"]));
        Assert.Contains("--background", c.Out.ToString());
        Assert.Contains("--use-skill USE_SKILL", c.Out.ToString());
        Assert.DoesNotContain("--fondo", c.Out.ToString());
        c.Out.GetStringBuilder().Clear();
        Assert.Equal(0, Cli.Run(["hacer", "--help", "--lang=es"]));
        Assert.Contains("--fondo", c.Out.ToString());
        Assert.Contains("objetivo", c.Out.ToString());
    }

    [Fact]
    public void ElIdiomaSaleDeLaOpcionLaVariableOLosAjustes()
    {
        using var c = new Consola();
        File.WriteAllText(c.Ajustes, """{"idioma":"es"}""");
        Environment.SetEnvironmentVariable(Lengua.Variable, null);
        Cli.Run(["--help"]);
        Assert.Contains("muestra esta ayuda", c.Out.ToString());   // the saved setting

        c.Out.GetStringBuilder().Clear();
        Environment.SetEnvironmentVariable(Lengua.Variable, "en");
        Cli.Run(["--help"]);
        Assert.Contains("show this help", c.Out.ToString());       // the environment wins over the setting

        c.Out.GetStringBuilder().Clear();
        Cli.Run(["--help", "--lang", "es"]);
        Assert.Contains("muestra esta ayuda", c.Out.ToString());   // and the flag over both

        Assert.Equal(2, Cli.Run(["--lang", "fr", "--help"]));
        Assert.Contains("invalid choice: 'fr'", c.Err.ToString());
    }

    [Fact]
    public void LosNombresDeLosDosIdiomasValenSiempre()
    {
        using var c = new Consola();
        var rc = Cli.Run(["hacer", "Write to Marta", "--url", "https://correo.test/", "--permitir", "send,borrar", "--fondo", "--dry-run"]);
        Assert.Equal(0, rc);
        var salida = c.Out.ToString();
        Assert.Contains("goal: Write to Marta", salida);
        Assert.Contains("window: no window", salida);
        Assert.Contains("irreversible: allowed send, delete", salida);
        Assert.Contains("limits: 30 steps, 10 min", salida);
        Assert.Contains("language: en", salida);

        c.Out.GetStringBuilder().Clear();
        rc = Cli.Run(["--lang", "es", "do", "Escribe a Marta", "--url", "https://correo.test/", "--allow", "send", "--background", "--max-steps", "5", "--dry-run"]);
        Assert.Equal(0, rc);
        Assert.Contains("irreversible: permitido enviar", c.Out.ToString());
        Assert.Contains("límites: 5 pasos", c.Out.ToString());
    }

    [Theory]
    [InlineData("en", new[] { "do", "x" }, "Where is missing")]
    [InlineData("en", new[] { "do", "x", "--url", "http://a.test", "--allow", "fly" }, "--allow: unknown fly; use send, delete, pay, accept")]
    [InlineData("en", new[] { "do", "x", "--url", "http://a.test", "--no-model" }, "--no-model needs --use-skill")]
    [InlineData("en", new[] { "do", "x", "--url", "http://a.test", "--skill" }, "--skill goes with --learn")]
    [InlineData("es", new[] { "do", "x" }, "Falta dónde")]
    [InlineData("es", new[] { "hacer", "x", "--url", "http://a.test", "--skill" }, "--skill va con --aprender")]
    public void LosErroresSalenEnElIdioma(string lang, string[] argv, string motivo)
    {
        using var c = new Consola();
        Assert.Equal(2, Cli.Run([.. argv, "--lang", lang]));
        Assert.Contains(motivo, c.Err.ToString());
    }

    [Fact]
    public void ElInformeElRegistroYLaSalidaSalenEnIngles()
    {
        using var c = new Consola();
        var sup = new Consola.Correo();
        var modelo = c.Web(sup, Consola.Escribir, Consola.Guardar, Consola.Terminar);
        var carpeta = Path.Combine(c.Carpeta, "run");
        var rc = Cli.Run(["do", "Save a draft for Marta", "--url", "https://correo.test/", "--folder", carpeta]);
        Assert.Equal(0, rc);
        Assert.Equal(["Type “Marta” in the “Para” field", "Click the “Guardar borrador” button"], sup.Hecho);
        var md = File.ReadAllText(Path.Combine(carpeta, "informe.md"));
        Assert.Contains("# Task: Save a draft for Marta", md);
        Assert.Contains("**Status:** ✅ Done", md);
        Assert.Contains("## Steps\n\n1. Type “Marta” in the “Para” field\n", md);
        var log = File.ReadAllText(Path.Combine(carpeta, "registro.log"));
        Assert.Contains("Goal: Save a draft for Marta", log);
        Assert.Contains("Turn 1: type 1 “Marta” — the recipient", log);
        Assert.Contains("Step 1: Type “Marta” in the “Para” field — because the recipient", log);
        Assert.Contains("Done: Draft saved for Marta", log);
        // The data stay the same in every language.
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(carpeta, "informe.json")))!;
        Assert.Equal("hecho", json["estado"]!.ToString());
        Assert.Equal("en", json["idioma"]!.ToString());
        Assert.Contains("done: Draft saved for Marta", c.Out.ToString());
        Assert.Contains("report: ", c.Out.ToString());
        // The model reads English and is asked for the summary in English.
        Assert.Contains("GOAL: Save a draft for Marta", modelo.Llamadas[0].System);
        Assert.Contains("Write \"summary\" in English", modelo.Llamadas[0].System);
        // And the log's lines are recognised by the window in English too.
        Assert.Equal("paso", Encargos.Tipo("Step 1: Type “Marta” in the “Para” field"));
        Assert.Equal("turno", Encargos.Tipo("Turn 1: type 1"));
        Assert.Equal("fin", Encargos.Tipo("Done: Draft saved"));
        Assert.Equal("alto", Encargos.Tipo("Stopped before clicking the “Send” button"));
    }

    [Fact]
    public void AnteLoIrreversiblePreguntaEnSuIdioma()
    {
        using var c = new Consola();
        Cli.StdinIsTty = () => true;
        Console.SetIn(new StringReader("y\n"));
        var sup = new Consola.Correo();
        c.Web(sup, Consola.Escribir, Consola.Enviar, Consola.Terminar);
        Assert.Equal(0, Cli.Run(["do", "Send it to Marta", "--url", "https://correo.test/", "--folder", Path.Combine(c.Carpeta, "a")]));
        Assert.Contains("⚠ Irreversible action (send): Click the “Send” button.", c.Err.ToString());
        Assert.Contains("Do it? [y/N]", c.Err.ToString());
        Assert.Contains("Click the “Send” button", sup.Hecho);

        Console.SetIn(new StringReader("s\n"));
        sup = new Consola.Correo();
        c.Web(sup, Consola.Escribir, Consola.Enviar, Consola.Terminar);
        Assert.Equal(0, Cli.Run(["--lang", "es", "hacer", "Envíaselo a Marta", "--url", "https://correo.test/", "--carpeta", Path.Combine(c.Carpeta, "b")]));
        Assert.Contains("¿La hago? [s/N]", c.Err.ToString());
        Assert.Contains("Pulsa el botón «Send»", sup.Hecho);
    }

    [Fact]
    public void EnSegundoPlanoSeParaYLoCuentaEnIngles()
    {
        using var c = new Consola();
        c.Web(new Consola.Correo(), Consola.Escribir, Consola.Enviar);
        var carpeta = Path.Combine(c.Carpeta, "run");
        Assert.Equal(Hacer.Pendiente, Cli.Run(["do", "Send it to Marta", "--url", "https://correo.test/", "--background", "--folder", carpeta]));
        Assert.Contains("to do it: run again with --allow send", c.Out.ToString());
        var md = File.ReadAllText(Path.Combine(carpeta, "informe.md"));
        Assert.Contains("⏸️ Stopped: waiting for your permission", md);
        Assert.Contains("- **Kind:** send", md);
        Assert.Contains("`--allow send`", md);
        Assert.Contains("Stopped before: Click the “Send” button (it is irreversible: send; not allowed).", md);
    }

    // ------------------------------------------------------------------ the guard does not depend on the language

    [Theory]
    [InlineData("en", "Enviar", "enviar")]
    [InlineData("en", "Send", "enviar")]
    [InlineData("es", "Send", "enviar")]
    [InlineData("es", "Submit form", "enviar")]
    [InlineData("en", "Borrar", "borrar")]
    [InlineData("es", "Delete all", "borrar")]
    [InlineData("en", "Pagar", "pagar")]
    [InlineData("es", "Place order", "pagar")]
    [InlineData("en", "Aceptar", "aceptar")]
    [InlineData("es", "Approve", "aceptar")]
    [InlineData("en", "Accept all cookies", null)]
    [InlineData("es", "Guardar", null)]
    [InlineData("en", "Save", null)]
    public void ElGuardianDetectaEspanolEInglesConCualquierIdioma(string lang, string nombre, string? categoria)
    {
        Lengua.Actual = lang;
        Assert.Equal(categoria, Guardian.Categoria(nombre));
    }

    [Fact]
    public void PermitirAceptaLasCategoriasEnLosDosIdiomas()
    {
        var envio = new Irreversible("enviar", "Click the “Send” button", "");
        Assert.True(Guardian.Permitida(envio, ["send"]));
        Assert.True(Guardian.Permitida(envio, ["Enviar"]));
        Assert.True(Guardian.Permitida(envio, ["*"]));
        Assert.False(Guardian.Permitida(envio, ["delete"]));
        Assert.Equal("borrar", Guardian.CategoriaDe("delete"));
        Assert.Null(Guardian.CategoriaDe("fly"));
        Assert.Equal("send", Guardian.NombreCategoria("enviar", "en"));
        Assert.Equal("enviar", Guardian.NombreCategoria("enviar", "es"));
    }

    // ------------------------------------------------------------------ the protocol

    [Fact]
    public void ElModeloContestaConLasClavesInglesas()
    {
        var d = Protocol.ParseDecision("""{"thought":"field 4","action":"type","ref":4,"value":"Bread","irreversible":false,"summary":""}""");
        Assert.Equal(("escribir", 4, "Bread", "field 4"), (d.Accion, d.Ref, d.Valor, d.Pensamiento));
        Assert.Equal("terminar", Protocol.ParseDecision("""{"action":"done","summary":"All set"}""").Accion);
        Assert.Equal("All set", Protocol.ParseDecision("""{"action":"done","summary":"All set"}""").Resumen);
        Assert.Equal("ir", Protocol.ParseDecision("""{"action":"goto","value":"https://x.test"}""").Accion);
        var s = Protocol.ParseDecision("""{"action":"skill","value":"crear-nota","params":{"title":"Milk"}}""");
        Assert.Equal(("habilidad", "Milk"), (s.Accion, s.Parametros!["title"]));
        var e = Assert.Throws<ArgumentException>(() => Protocol.ParseDecision("""{"action":"click"}"""));
        Assert.Contains("needs a numeric \"ref\"", e.Message);
        Assert.Contains("Write \"summary\" in Spanish", Protocol.SystemPromptTarea("x", lang: "es"));
        Assert.Contains("Write \"summary\" in English", Protocol.SystemPromptTarea("x", lang: "en"));
    }

    // ------------------------------------------------------------------ Settings

    [Fact]
    public void LosAjustesSeGuardanSinEnsenarLaClave()
    {
        using var c = new Consola();
        var a = Ajustes.Guardar(new JsonObject { ["idioma"] = "ES", ["ia_backend"] = "openai", ["ia_url"] = "http://llm.test/v1", ["ia_clave"] = "sk-1234567890" }, c.Ajustes);
        Assert.Equal("es", a["idioma"]!.ToString());
        a = Ajustes.Guardar(new JsonObject { ["ia_modelo"] = "small", ["ia_clave"] = "" }, c.Ajustes);
        Assert.Equal("sk-1234567890", a["ia_clave"]!.ToString());   // an empty key keeps the saved one
        var publicos = Ajustes.Publicos(a);
        Assert.Equal("", publicos["ia_clave"]!.ToString());
        Assert.True(publicos["ia_clave_guardada"]!.GetValue<bool>());
        Assert.Equal("7890", publicos["ia_clave_fin"]!.ToString());
        a = Ajustes.Guardar(new JsonObject { ["ia_backend"] = "ollama" }, c.Ajustes);
        Assert.Null(a["ia_clave"]);                                 // another backend forgets the old one's key and server
        Assert.Null(a["ia_url"]);
        Assert.Throws<ArgumentException>(() => Ajustes.Guardar(new JsonObject { ["idioma"] = "fr" }, c.Ajustes));
        Assert.Throws<ArgumentException>(() => Ajustes.Guardar(new JsonObject { ["ia_backend"] = "gpt" }, c.Ajustes));
    }

    [Fact]
    public void LoQueSeEscribeParaUnaVezMandaSobreLosAjustes()
    {
        var ajustes = new JsonObject { ["ia_backend"] = "openai", ["ia_url"] = "http://llm.test/v1", ["ia_modelo"] = "small", ["ia_clave"] = "k" };
        var vacio = new Dictionary<string, string>();
        var b = Assert.IsType<OpenAiChatBackend>(Ajustes.Backend(null, null, ajustes, vacio));
        Assert.Equal(("http://llm.test/v1", "small", "k"), (b.BaseUrl, b.Model, b.ApiKey));
        // A flag for another backend does not inherit the saved model.
        var o = Assert.IsType<OllamaChatBackend>(Ajustes.Backend("ollama", null, ajustes, vacio));
        Assert.Equal(Backends.DefaultOllamaModel, o.Model);
        // The environment wins over the settings, and the flags over both.
        Assert.IsType<OllamaChatBackend>(Ajustes.Backend(null, null, ajustes, new Dictionary<string, string> { ["FAENA_LLM"] = "ollama" }));
        Assert.Equal("big", Assert.IsType<OpenAiChatBackend>(Ajustes.Backend(null, null, ajustes, new Dictionary<string, string> { ["FAENA_LLM_MODEL"] = "big" })).Model);
        Assert.Equal("flag", Assert.IsType<OpenAiChatBackend>(Ajustes.Backend(null, "flag", ajustes, vacio)).Model);
        // Without settings, as before: Claude when there is a key, else the local Ollama.
        Assert.IsType<ClaudeChatBackend>(Ajustes.Backend(null, null, new JsonObject(), new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "x" }));
        Assert.IsType<OllamaChatBackend>(Ajustes.Backend(null, null, new JsonObject(), vacio));
        // A saved ollama is not overridden by a stray ANTHROPIC_API_KEY.
        Assert.IsType<OllamaChatBackend>(Ajustes.Backend(null, null, new JsonObject { ["ia_backend"] = "ollama" }, new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "x" }));
        // The language: flag, environment, settings, English.
        Assert.Equal("es", Ajustes.Idioma("es", new JsonObject(), vacio));
        Assert.Equal("es", Ajustes.Idioma(null, new JsonObject(), new Dictionary<string, string> { ["FAENA_LANG"] = "es" }));
        Assert.Equal("es", Ajustes.Idioma(null, new JsonObject { ["idioma"] = "es" }, vacio));
        Assert.Equal("en", Ajustes.Idioma(null, new JsonObject { ["idioma"] = "es" }, new Dictionary<string, string> { ["FAENA_LANG"] = "en" }));
        Assert.Equal("en", Ajustes.Idioma(null, new JsonObject(), vacio));
    }

    [Fact]
    public async Task LaVentanaGuardaLosAjustesYCambiaDeIdioma()
    {
        using var c = new Consola();
        Environment.SetEnvironmentVariable(Lengua.Variable, null);
        var lanzador = Encargos.Lanzador;
        ProcessStartInfo? lanzado = null;
        Encargos.Lanzador = psi => { lanzado = psi; return Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false }); };
        using var servidor = Servidor.Start(new Opciones
        {
            Exe = @"C:\kit\faena.exe", CarpetaTareas = Path.Combine(c.Carpeta, "tareas"), CarpetaHabilidades = c.Habilidades,
            Windows = false, ArchivoAjustes = c.Ajustes, CarpetaSkills = Path.Combine(c.Carpeta, "skills"),
        });
        using var http = new HttpClient();
        try
        {
            var inicio = (await http.GetFromJsonAsync<JsonObject>(servidor.Url + "api/inicio"))!;
            Assert.Equal("en", inicio["idioma"]!.ToString());
            var r = await http.PostAsJsonAsync(servidor.Url + "api/ajustes", new { idioma = "es", ia_backend = "claude", ia_clave = "sk-secret-0001" });
            var guardados = (await r.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal("", guardados["ia_clave"]!.ToString());
            Assert.Equal("es", Lengua.Actual);
            inicio = (await http.GetFromJsonAsync<JsonObject>(servidor.Url + "api/inicio"))!;
            Assert.Equal("es", inicio["idioma"]!.ToString());
            Assert.StartsWith("claude", inicio["ia"]!.ToString());
            Assert.DoesNotContain("sk-secret", inicio.ToJsonString());
            Assert.Contains("sk-secret-0001", File.ReadAllText(c.Ajustes));
            // The jobs it launches get the window's language.
            r = await http.PostAsJsonAsync(servidor.Url + "api/encargar", new { objetivo = "Escribe a Marta", url = "https://correo.test/" });
            Assert.True(r.IsSuccessStatusCode);
            Assert.Equal("es", lanzado!.ArgumentList[lanzado.ArgumentList.IndexOf("--lang") + 1]);
            var mal = await http.PostAsJsonAsync(servidor.Url + "api/ajustes", new { idioma = "fr" });
            Assert.Equal(400, (int)mal.StatusCode);
        }
        finally
        {
            servidor.Stop();
            Encargos.Lanzador = lanzador;
        }
    }
}
