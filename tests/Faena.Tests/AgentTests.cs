using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NSubstitute;
using Faena.Agente;
using Faena.Escena;
using Xunit.Abstractions;
using AgentRunner = Faena.Agente.Agent;

namespace Faena.Tests;

/// <summary>
/// El agente: cómo lee las decisiones del modelo, cómo le enseña la página y cómo recorre
/// un objetivo apuntando los pasos. El modelo es un guion y la página un doble de
/// NSubstitute; solo los <see cref="BrowserFactAttribute"/> abren Chromium y solo
/// <see cref="OllamaRecorreWikipedia"/> (con <c>FAENA_OLLAMA_TESTS=1</c>) habla con un modelo de verdad.
/// </summary>
public sealed class AgentTests(ITestOutputHelper salida)
{
    // These check the Spanish texts of the log and the steps (I18nTests checks the English ones).
    private readonly Idioma _idioma = new("es");

    // ------------------------------------------------------------------ ParseDecision

    [Fact]
    public void ParseDecisionLeeElJsonLimpio()
    {
        var d = Protocol.ParseDecision("""{"pensamiento":"El 12 es el buscador","accion":"escribir","ref":12,"valor":"Madrid","frase":"Escribimos Madrid","resumen":""}""");
        Assert.Equal("escribir", d.Accion);
        Assert.Equal(12, d.Ref);
        Assert.Equal("Madrid", d.Valor);
        Assert.Equal("Escribimos Madrid", d.Frase);
        Assert.Equal("El 12 es el buscador", d.Pensamiento);
        Assert.Equal("", d.Resumen);
    }

    [Fact]
    public void ParseDecisionSeQuedaConLaUltimaClaveRepetida()
    {
        var d = Protocol.ParseDecision("""{"pensamiento":"primero","pensamiento":"El 7 es Done","accion":"clic","ref":7,"frase":"Abrimos Done"}""");
        Assert.Equal("clic", d.Accion);
        Assert.Equal(7, d.Ref);
        Assert.Equal("El 7 es Done", d.Pensamiento);
    }

    [Fact]
    public void ParseDecisionToleraVallasYTextoAlrededor()
    {
        var d = Protocol.ParseDecision("Claro, aquí va:\n```json\n{\"accion\":\"clic\",\"ref\":\"3\",\"frase\":\"Abrimos el artículo\"}\n```\nListo.");
        Assert.Equal("clic", d.Accion);
        Assert.Equal(3, d.Ref);
        Assert.Equal("Abrimos el artículo", d.Frase);
    }

    [Fact]
    public void ParseDecisionQuitaElRazonamientoThink()
    {
        var d = Protocol.ParseDecision("<think>\nhmm {no es json} ...\n</think>\n{\"accion\":\"terminar\",\"resumen\":\"Hecho\",\"ref\":null}");
        Assert.Equal("terminar", d.Accion);
        Assert.Null(d.Ref);
        Assert.Equal("Hecho", d.Resumen);
    }

    [Fact]
    public void ParseDecisionAceptaClickComoClic()
    {
        Assert.Equal("clic", Protocol.ParseDecision("""{"accion":"Click","ref":1}""").Accion);
    }

    [Theory]
    [InlineData("no hay json aquí")]
    [InlineData("{\"accion\":\"clic\",\"ref\":}")]
    [InlineData("{\"accion\":\"volar\",\"ref\":1}")]
    [InlineData("{\"accion\":\"clic\"}")]
    [InlineData("{\"accion\":\"escribir\",\"ref\":2}")]
    public void ParseDecisionRechazaLoQueNoEsUnaDecision(string texto)
    {
        Assert.Throws<ArgumentException>(() => Protocol.ParseDecision(texto));
    }

    // ------------------------------------------------------------------ Observación

    [Fact]
    public void ObservacionEsCompactaYOmiteLasColumnasVacias()
    {
        var obs = new Observation
        {
            Url = "https://ejemplo.test/",
            Title = "Ejemplo",
            Text = "Bienvenido\nal ejemplo",
            Total = 4,
            Elements =
            [
                new(1, "", "el botón", "Guardar", "", ""),
                new(2, "", "el campo", "Nombre", "hola", ""),
                new(3, "", "la casilla", "Acepto | condiciones", "", "marcado"),
                new(4, "iframe[name=\"m\"] >> internal:control=enter-frame >> ", "el enlace", "Ayuda", "", "deshabilitado"),
            ],
        };
        var texto = Protocol.Observacion(obs);
        // What the model reads is English, whatever the interface says.
        Assert.Contains("Title: Ejemplo\n", texto);
        Assert.Contains("Url: https://ejemplo.test/\n", texto);
        Assert.Contains("Visible text: Bienvenido\nal ejemplo\n", texto);
        Assert.Contains("\n1 | button | Guardar\n", texto);
        Assert.Contains("\n2 | field | Nombre | hola\n", texto);
        Assert.Contains("\n3 | checkbox | Acepto / condiciones | | checked\n", texto);
        Assert.Contains("[frame: iframe[name=\"m\"] >> internal:control=enter-frame >> ]\n4 | link | Ayuda | | disabled\n", texto);
        Assert.DoesNotContain("trimmed", texto);

        obs.Total = 400;
        Assert.Contains("list trimmed: there are 400 elements and only 4 are shown", Protocol.Observacion(obs));
    }

    [Fact]
    public void ElSystemPromptLlevaElObjetivoLasAccionesYElEjemplo()
    {
        var prompt = Protocol.SystemPromptTarea("Busca \"Madrid\"");
        Assert.Contains("GOAL: Busca \"Madrid\"", prompt);
        foreach (var accion in Protocol.Acciones.Where(a => a is not "habilidad" and not "señalar")) Assert.Contains($"\"{Protocol.AccionEn[accion]}\"", prompt);
        Assert.Contains("\"irreversible\"", prompt);
        Assert.DoesNotContain("\"frase\"", prompt);
        Assert.Contains("Example: {\"thought\"", prompt);
        Assert.Contains("Write \"summary\" in Spanish", prompt);
        Assert.DoesNotContain("\r", prompt);
    }

    [Fact]
    public void ParseDecisionAceptaSenalarYSusSinonimos()
    {
        foreach (var a in new[] { "señalar", "senalar", "point", "hover" })
            Assert.Equal("señalar", Protocol.ParseDecision($$$"""{"accion":"{{{a}}}","ref":4,"frase":"Aquí está"}""").Accion);
        Assert.Throws<ArgumentException>(() => Protocol.ParseDecision("""{"accion":"señalar","frase":"sin ref"}"""));
    }

    // ------------------------------------------------------------------ Backends

    [Fact]
    public async Task OllamaMandaElChatEnJsonYLeeElContenido()
    {
        JsonNode? enviado = null;
        string? url = null;
        var backend = new OllamaChatBackend("qwen3-coder:30b", "http://localhost:11434/", (u, payload, _, _) =>
        {
            url = u;
            enviado = payload;
            return Task.FromResult<JsonNode>(JsonNode.Parse("""{"message":{"role":"assistant","content":"{\"accion\":\"esperar\"}"}}""")!);
        });
        var texto = await backend.CompleteAsync("sistema", [new("user", "hola"), new("assistant", "{}"), new("user", "otra", [1, 2, 3])], CancellationToken.None);
        Assert.Equal("{\"accion\":\"esperar\"}", texto);
        Assert.Equal("http://localhost:11434/api/chat", url);
        var o = Assert.IsType<JsonObject>(enviado);
        Assert.Equal("qwen3-coder:30b", o["model"]!.GetValue<string>());
        Assert.False(o["stream"]!.GetValue<bool>());
        Assert.Equal("json", o["format"]!.GetValue<string>());
        Assert.False(o["think"]!.GetValue<bool>());
        Assert.Equal(0.1, o["options"]!["temperature"]!.GetValue<double>());
        Assert.Equal(16384, o["options"]!["num_ctx"]!.GetValue<int>());
        var mensajes = o["messages"]!.AsArray();
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, mensajes.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("sistema", mensajes[0]!["content"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), mensajes[3]!["images"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task ClaudeMandaLaCapturaComoImagenBase64()
    {
        JsonNode? enviado = null;
        IDictionary<string, string>? cabeceras = null;
        var backend = new ClaudeChatBackend("clave", "claude-x", (_, payload, h, _) =>
        {
            enviado = payload;
            cabeceras = h;
            return Task.FromResult<JsonNode>(JsonNode.Parse("""{"content":[{"type":"text","text":"{\"accion\":"},{"type":"text","text":"\"esperar\"}"}]}""")!);
        });
        var texto = await backend.CompleteAsync("sistema", [new("user", "mira", [9, 9])], CancellationToken.None);
        Assert.Equal("{\"accion\":\"esperar\"}", texto);
        Assert.Equal("clave", cabeceras!["x-api-key"]);
        var o = Assert.IsType<JsonObject>(enviado);
        Assert.Equal(1024, o["max_tokens"]!.GetValue<int>());
        Assert.Equal("sistema", o["system"]!.GetValue<string>());
        var contenido = o["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("image", contenido[0]!["type"]!.GetValue<string>());
        Assert.Equal("image/png", contenido[0]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String([9, 9]), contenido[0]!["source"]!["data"]!.GetValue<string>());
        Assert.Equal("mira", contenido[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void PickEligeComoElAsistente()
    {
        var ollama = Assert.IsType<OllamaChatBackend>(Backends.Pick(null, null, new Dictionary<string, string>()));
        Assert.Equal("qwen3-coder:30b", ollama.Model);
        Assert.Equal("http://localhost:11434", ollama.BaseUrl);

        var claude = Assert.IsType<ClaudeChatBackend>(Backends.Pick(null, null, new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "k" }));
        Assert.Equal("k", claude.ApiKey);

        var porEntorno = Assert.IsType<OllamaChatBackend>(Backends.Pick(null, null,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "k", ["TRAMOYA_LLM"] = "ollama", ["TRAMOYA_LLM_MODEL"] = "otro", ["OLLAMA_HOST"] = "127.0.0.1:11434" }));
        Assert.Equal("otro", porEntorno.Model);
        Assert.Equal("http://127.0.0.1:11434", porEntorno.BaseUrl);
        // La variable del servidor es su dirección de escucha, que no sirve para conectarse.
        Assert.Equal("http://127.0.0.1:11434", Backends.UrlOllama("0.0.0.0:11434"));
        Assert.Equal("http://localhost:11434", Backends.UrlOllama("[::]:11434"));
        Assert.Equal("http://otro:11434", Backends.UrlOllama("http://otro:11434/"));

        Assert.Equal("mio", Assert.IsType<OllamaChatBackend>(Backends.Pick("ollama", "mio", new Dictionary<string, string> { ["TRAMOYA_LLM_MODEL"] = "otro" })).Model);
        Assert.Throws<ArgumentException>(() => Backends.Pick("claude", null, new Dictionary<string, string>()));
        Assert.Throws<ArgumentException>(() => Backends.Pick("gpt", null, new Dictionary<string, string>()));
    }

    // ------------------------------------------------------------------ el recorrido con dobles

    /// <summary>
    /// Un modelo de guion: contesta las respuestas en orden. <c>{ref:Nombre}</c> en una respuesta se
    /// sustituye por el número que ese nombre tiene en la observación del turno, así el guion no
    /// depende de la numeración.
    /// </summary>
    private sealed class ScriptedBackend(params string[] respuestas) : IChatBackend
    {
        private readonly Queue<string> _respuestas = new(respuestas);
        public List<(string System, IReadOnlyList<ChatMessage> Messages)> Llamadas { get; } = [];

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
        {
            Llamadas.Add((system, messages));
            if (_respuestas.Count == 0) throw new InvalidOperationException("el guion se ha acabado");
            var respuesta = _respuestas.Dequeue();
            var usuario = messages[^1].Text;
            respuesta = Regex.Replace(respuesta, @"\{ref:([^}]+)\}", m => RefDe(usuario, m.Groups[1].Value));
            return Task.FromResult(respuesta);
        }

        public static string RefDe(string observacion, string nombre)
        {
            var m = Regex.Match(observacion, @"^(\d+) \| [^|\n]+ \| " + Regex.Escape(nombre) + @"(?: \||\n|$)", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : "0";
        }
    }

    /// <summary>Una página de NSubstitute que contesta siempre la misma observación y apunta lo que se le hace.</summary>
    private sealed class PaginaFalsa
    {
        public IPage Page { get; } = Substitute.For<IPage>();
        public IKeyboard Keyboard { get; } = Substitute.For<IKeyboard>();
        public List<string> Clics { get; } = [];
        public List<string> Senalados { get; } = [];
        public List<(string Selector, string Texto)> Escritos { get; } = [];
        public List<string> Teclas { get; } = [];
        public List<string> Idas { get; } = [];
        public int Observaciones { get; private set; }

        public PaginaFalsa(string observacionJson, Func<int, string> selectorDe)
        {
            Page.Url.Returns("https://ejemplo.test/inicio");
            Page.TitleAsync().Returns(Task.FromResult("Ejemplo de prueba"));
            Page.Frames.Returns(new List<IFrame>());
            Page.Keyboard.Returns(Keyboard);
            Page.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(ci =>
            {
                var script = ci.ArgAt<string>(0);
                if (script == Observer.ObserveJs) { Observaciones++; return Task.FromResult(observacionJson); }
                if (script == Observer.SelectorJs) return Task.FromResult(selectorDe(Convert.ToInt32(ci.ArgAt<object?>(1))));
                return Task.FromResult("");
            });
            Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(ci => Locator(ci.ArgAt<string>(0)));
            Keyboard.PressAsync(Arg.Any<string>(), Arg.Any<KeyboardPressOptions>()).Returns(ci => { Teclas.Add(ci.ArgAt<string>(0)); return Task.CompletedTask; });
            Page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>()).Returns(ci => { Idas.Add(ci.ArgAt<string>(0)); return Task.FromResult<IResponse?>(null); });
        }

        private readonly Dictionary<string, ILocator> _locators = [];

        /// <summary>El mismo doble para el mismo selector, así un test puede configurarlo antes de correr.</summary>
        public ILocator Locator(string selector)
        {
            if (_locators.TryGetValue(selector, out var hecho)) return hecho;
            var loc = _locators[selector] = Substitute.For<ILocator>();
            loc.ClickAsync(Arg.Any<LocatorClickOptions>()).Returns(_ => { Clics.Add(selector); return Task.CompletedTask; });
            loc.FillAsync(Arg.Any<string>(), Arg.Any<LocatorFillOptions>()).Returns(ci => { Escritos.Add((selector, ci.ArgAt<string>(0))); return Task.CompletedTask; });
            loc.HoverAsync(Arg.Any<LocatorHoverOptions>()).Returns(_ => { Senalados.Add(selector); return Task.CompletedTask; });
            loc.ScrollIntoViewIfNeededAsync(Arg.Any<LocatorScrollIntoViewIfNeededOptions>()).Returns(Task.CompletedTask);
            return loc;
        }
    }

    private const string ObservacionJson = """
        {"url":"https://ejemplo.test/inicio","title":"Ejemplo de prueba","text":"Un formulario de ejemplo","total":3,
         "elementos":[{"ref":1,"tipo":"el botón","nombre":"Siguiente","valor":"","estado":""},
                      {"ref":2,"tipo":"el campo","nombre":"Nombre","valor":"","estado":""},
                      {"ref":3,"tipo":"el botón","nombre":"Guardar","valor":"","estado":""}]}
        """;

    private static string SelectorFalso(int n) => n switch
    {
        1 => "internal:role=button[name=\"Siguiente\"i]",
        2 => "internal:role=textbox[name=\"Nombre\"i]",
        3 => "internal:role=button[name=\"Guardar\"i]",
        _ => "",
    };

    [Fact]
    public async Task RunAsyncApuntaLosPasosConSuSelector()
    {
        var pagina = new PaginaFalsa(ObservacionJson, SelectorFalso);
        var modelo = new ScriptedBackend(
            """{"pensamiento":"empezar por Siguiente","accion":"clic","ref":{ref:Siguiente},"frase":"Vamos a rellenar el formulario: pulsamos Siguiente"}""",
            """{"pensamiento":"rellenar el nombre","accion":"escribir","ref":{ref:Nombre},"valor":"hola","frase":"Escribimos el nombre"}""",
            """{"pensamiento":"confirmar","accion":"tecla","valor":"Intro","frase":""}""",
            """{"pensamiento":"me invento uno","accion":"clic","ref":9,"frase":"no debería salir"}""",
            """{"pensamiento":"guardar","accion":"clic","ref":{ref:Guardar},"frase":"Guardamos"}""",
            """{"pensamiento":"ya está","accion":"terminar","resumen":"Formulario guardado"}""");
        var registro = new List<string>();

        var r = await AgentRunner.RunAsync(pagina.Page, new AgentOptions("Rellena el formulario"), modelo, new Registro(registro), CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Equal("Formulario guardado", r.Resumen);
        Assert.Equal("https://ejemplo.test/inicio", r.Inicio);
        Assert.Equal("Ejemplo de prueba", r.Titulo);
        Assert.Empty(r.Avisos);
        Assert.Equal(6, modelo.Llamadas.Count);
        Assert.Equal(6, pagina.Observaciones);

        Assert.Equal(4, r.Pasos.Count);
        Assert.Equal(new[] { "clic", "escribir", "tecla", "clic" }, r.Pasos.Select(p => p["tipo"]!.GetValue<string>()));
        Assert.Equal("internal:role=button[name=\"Siguiente\"i]", r.Pasos[0]["selector"]!.GetValue<string>());
        Assert.Equal("Pulsa el botón «Siguiente»", r.Pasos[0]["desc"]!.GetValue<string>());
        Assert.Null(r.Pasos[0]["texto"]);
        Assert.False(r.Pasos[0]["guarda"]!.GetValue<bool>());
        Assert.Equal("internal:role=textbox[name=\"Nombre\"i]", r.Pasos[1]["selector"]!.GetValue<string>());
        Assert.Equal("hola", r.Pasos[1]["valor"]!.GetValue<string>());
        Assert.Equal("Escribe «hola» en el campo «Nombre»", r.Pasos[1]["desc"]!.GetValue<string>());
        Assert.Equal("Enter", r.Pasos[2]["valor"]!.GetValue<string>());
        Assert.Equal("Pulsa la tecla Intro", r.Pasos[2]["desc"]!.GetValue<string>());
        Assert.Equal("Pulsa el botón «Guardar»", r.Pasos[3]["desc"]!.GetValue<string>());
        Assert.True(r.Pasos[3]["guarda"]!.GetValue<bool>());

        // La página ha recibido exactamente lo que el modelo pidió, por el número del turno.
        Assert.Equal(new[] { "[data-faena-ref=\"1\"]", "[data-faena-ref=\"3\"]" }, pagina.Clics);
        Assert.Equal(new[] { ("[data-faena-ref=\"2\"]", "hola") }, pagina.Escritos);
        Assert.Equal(new[] { "Enter" }, pagina.Teclas);

        // El registro, en español y con el porqué.
        Assert.Contains("Paso 1: Pulsa el botón «Siguiente» — porque empezar por Siguiente", registro);
        Assert.DoesNotContain(registro, l => l.Contains("Vamos a rellenar", StringComparison.Ordinal));
        Assert.Contains("Paso 3: Pulsa la tecla Intro — porque confirmar", registro);
        Assert.Contains("Terminado: Formulario guardado", registro);
        Assert.Contains(registro, l => l.Contains("no existe (9)"));

        // Lo que ve el modelo: el objetivo en el sistema, la observación y el resultado anterior en el usuario.
        var (system, mensajes) = modelo.Llamadas[4];
        Assert.Contains("GOAL: Rellena el formulario", system);
        var usuario = mensajes[^1].Text;
        Assert.Contains("there is no element 9", usuario);
        Assert.Contains("1. Pulsa el botón «Siguiente»", usuario);
        Assert.Contains("3. Pulsa la tecla Intro", usuario);
        Assert.Contains("1 | button | Siguiente", usuario);
        Assert.Contains("Steps left at most: 22.", usuario);
        // El historial: los turnos anteriores van por parejas user/assistant antes del actual.
        Assert.Equal(new[] { "user", "assistant", "user", "assistant", "user", "assistant", "user", "assistant", "user" }, mensajes.Select(m => m.Role));
        Assert.Contains("Page: Ejemplo de prueba", mensajes[0].Text);   // los antiguos, resumidos
        Assert.Contains("CURRENT PAGE", mensajes[^3].Text);            // los recientes, enteros
    }

    /// <summary>Un <see cref="IProgress{T}"/> que apunta en el acto (el de .NET despacha por el contexto y llega tarde a las aserciones).</summary>
    private sealed class Registro(List<string> lineas) : IProgress<string>
    {
        public void Report(string value) => lineas.Add(value);
    }

    [Fact]
    public async Task RunAsyncReintentaUnJsonTorcidoYSeParaEnElTope()
    {
        var pagina = new PaginaFalsa(ObservacionJson, SelectorFalso);
        var modelo = new ScriptedBackend(
            "esto no es json",
            """{"accion":"clic","ref":1,"pensamiento":"ahora sí"}""",
            """{"accion":"esperar","pensamiento":"a ver"}""",
            """{"accion":"clic","ref":1,"pensamiento":"otra vez"}""",
            """{"accion":"clic","ref":1,"pensamiento":"no debería llegar"}""");
        var registro = new List<string>();

        var r = await AgentRunner.RunAsync(pagina.Page, new AgentOptions("Pulsa dos veces", MaxPasos: 2), modelo, new Registro(registro), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Equal(2, r.Pasos.Count);
        Assert.Contains("Se ha llegado al tope de 2 pasos sin terminar.", r.Avisos);
        Assert.Equal(4, modelo.Llamadas.Count);
        // El reintento lleva la respuesta mala y el error, para que el modelo se corrija.
        var reintento = modelo.Llamadas[1].Messages;
        Assert.Equal("assistant", reintento[^2].Role);
        Assert.Equal("esto no es json", reintento[^2].Text);
        Assert.StartsWith("Your answer is not valid:", reintento[^1].Text);
        Assert.Contains(registro, l => l.StartsWith("La respuesta del modelo no vale"));
    }

    [Fact]
    public async Task RunAsyncCuentaLasAccionesFallidasAlModeloSinApuntarlas()
    {
        var pagina = new PaginaFalsa(ObservacionJson, SelectorFalso);
        pagina.Locator("[data-faena-ref=\"1\"]").ClickAsync(Arg.Any<LocatorClickOptions>())
            .Returns(_ => Task.FromException(new TimeoutException("Timeout 10000ms exceeded.")));
        var modelo = new ScriptedBackend(
            """{"accion":"clic","ref":1,"pensamiento":"pulso"}""",
            """{"accion":"fallo","resumen":"No se puede pulsar","pensamiento":"me rindo"}""");

        var r = await AgentRunner.RunAsync(pagina.Page, new AgentOptions("Pulsa"), modelo, null, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Empty(r.Pasos);
        Assert.Equal("No se puede pulsar", r.Resumen);
        Assert.Contains("failed: the element could not be clicked", modelo.Llamadas[1].Messages[^1].Text);
    }

    // ------------------------------------------------------------------ con Chromium

    private static string DataUrl(string html) => "data:text/html;charset=utf-8," + Uri.EscapeDataString(html);

    /// <summary>Chromium con <c>PWDEBUG=console</c>: así existe <c>window.playwright.selector</c>, el que da los selectores duraderos.</summary>
    private static async Task<(IPlaywright Pw, IBrowser Browser, IPage Page)> AbrirAsync(string url, bool headless = true)
    {
        Environment.SetEnvironmentVariable("PWDEBUG", "console");
        var pw = await Playwright.CreateAsync();
        var browser = await pw.Chromium.LaunchAsync(new() { Headless = headless });
        var context = await browser.NewContextAsync(new() { ViewportSize = new ViewportSize { Width = 1280, Height = 800 }, Locale = "es-ES" });
        var page = await context.NewPageAsync();
        await page.GotoAsync(url);
        return (pw, browser, page);
    }

    [BrowserFact]
    public async Task ElAgenteRecorreUnaPaginaDeVerdadConSelectoresDuraderos()
    {
        var url = DataUrl("""
            <html><body>
            <h1>Formulario</h1>
            <button onclick="document.getElementById('o').textContent='siguiente pulsado'">Siguiente</button>
            <button onclick="document.getElementById('o').textContent='otro pulsado'">Otro</button>
            <label>Nombre <input></label>
            <p id=o></p>
            </body></html>
            """);
        var (pw, browser, page) = await AbrirAsync(url);
        try
        {
            var obs = await Observer.ObserveAsync(page);
            Assert.Equal(3, obs.Total);
            Assert.Equal(new[] { "el botón", "el botón", "el campo" }, obs.Elements.Select(e => e.Tipo));
            Assert.Equal(new[] { "Siguiente", "Otro", "Nombre" }, obs.Elements.Select(e => e.Nombre));
            Assert.Equal(new[] { 1, 2, 3 }, obs.Elements.Select(e => e.Ref));
            Assert.Contains("Formulario", obs.Text);

            var modelo = new ScriptedBackend(
                """{"pensamiento":"empiezo","accion":"clic","ref":{ref:Siguiente},"frase":"Vamos a rellenar el formulario: pulsamos Siguiente"}""",
                """{"pensamiento":"el nombre","accion":"escribir","ref":{ref:Nombre},"valor":"hola","frase":"Escribimos el nombre"}""",
                """{"pensamiento":"listo","accion":"terminar","resumen":"Formulario relleno"}""");
            var registro = new List<string>();
            var r = await AgentRunner.RunAsync(page, new AgentOptions("Pulsa Siguiente y escribe el nombre"), modelo, new Registro(registro), CancellationToken.None);

            Assert.True(r.Ok, string.Join("\n", registro));
            Assert.Equal(2, r.Pasos.Count);
            Assert.Equal("siguiente pulsado", await page.Locator("#o").TextContentAsync());
            Assert.Equal("hola", await page.GetByLabel("Nombre").InputValueAsync());
            var selectores = r.Pasos.Select(p => p["selector"]!.GetValue<string>()).ToList();
            Assert.StartsWith("internal:role=button[name=\"Siguiente\"", selectores[0]);
            Assert.StartsWith("internal:", selectores[1]);
            Assert.Contains("Nombre", selectores[1]);
            Assert.Equal("Pulsa el botón «Siguiente»", r.Pasos[0]["desc"]!.GetValue<string>());
            Assert.Equal("Escribe «hola» en el campo «Nombre»", r.Pasos[1]["desc"]!.GetValue<string>());
            // La numeración vuelve a empezar en cada turno: el campo sigue siendo el 3 y no queda ningún número viejo.
            Assert.Equal(3, await page.Locator("[data-faena-ref]").CountAsync());
        }
        finally
        {
            await browser.CloseAsync();
            pw.Dispose();
        }
    }

    [BrowserFact]
    public async Task ElObservadorNumeraLosMarcosConSuPrefijo()
    {
        var dentro = "<html><body><button>Dentro</button></body></html>";
        var url = DataUrl($"<html><body><button>Fuera</button><iframe name=\"m\" srcdoc=\"{dentro.Replace("\"", "&quot;")}\"></iframe></body></html>");
        var (pw, browser, page) = await AbrirAsync(url);
        try
        {
            await page.FrameLocator("iframe[name=m]").Locator("button").WaitForAsync();
            var obs = await Observer.ObserveAsync(page);
            Assert.Equal(new[] { "Fuera", "Dentro" }, obs.Elements.Select(e => e.Nombre));
            Assert.Equal("", obs.Elements[0].Frame);
            Assert.Contains("internal:control=enter-frame >> ", obs.Elements[1].Frame);
            Assert.True(obs.Marcos.ContainsKey(2));
            var selector = await Observer.SelectorCompletoAsync(page, obs, obs.Elements[1]);
            Assert.Contains(">> internal:control=enter-frame >> internal:role=button[name=\"Dentro\"", selector);
            Assert.Contains("[marco: ", Protocol.Observacion(obs));
        }
        finally
        {
            await browser.CloseAsync();
            pw.Dispose();
        }
    }

    // ------------------------------------------------------------------ con un modelo de verdad

    /// <summary>Como <see cref="BrowserFactAttribute"/>, pero además hace falta Ollama con el modelo: <c>FAENA_OLLAMA_TESTS=1</c>.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    private sealed class OllamaFactAttribute : FactAttribute
    {
        public OllamaFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(BrowserFactAttribute.Variable) != "1" || Environment.GetEnvironmentVariable("FAENA_OLLAMA_TESTS") != "1")
                Skip = $"set {BrowserFactAttribute.Variable}=1 and FAENA_OLLAMA_TESTS=1 to run the agent against Ollama";
        }
    }

    /// <summary>
    /// La prueba de fuego del prompt: Ollama en local con qwen3-coder:30b busca Madrid en
    /// Wikipedia y abre el artículo. Solo con <c>FAENA_OLLAMA_TESTS=1</c> (y el navegador).
    /// </summary>
    [OllamaFact]
    public async Task OllamaRecorreWikipedia()
    {
        var (pw, browser, page) = await AbrirAsync("https://www.wikipedia.org", headless: true);
        try
        {
            var registro = new List<string>();
            var r = await AgentRunner.RunAsync(page, new AgentOptions("Busca \"Madrid\" y abre el artículo", MaxPasos: 6),
                Backends.Pick("ollama", "qwen3-coder:30b"), new Registro(registro), CancellationToken.None);
            foreach (var linea in registro) salida.WriteLine(linea);
            salida.WriteLine("Pasos: " + Json.Dump(new JsonArray(r.Pasos.Select(p => (JsonNode)p.DeepClone()).ToArray())));
            salida.WriteLine("Url final: " + page.Url);
            Assert.Contains("Madrid", page.Url);
            Assert.True(r.Ok, "el agente no ha dicho «terminar»: " + r.Resumen);
            Assert.NotEmpty(r.Pasos);
        }
        finally
        {
            await browser.CloseAsync();
            pw.Dispose();
        }
    }
}
