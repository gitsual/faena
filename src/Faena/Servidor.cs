using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Faena.Agente;
using Faena.Escena;

namespace Faena;

/// <summary>Dónde viven los encargos y cómo se lanza cada uno; los decide quien arranca el servidor (la ventana, el CLI o un test).</summary>
public sealed class Opciones
{
    /// <summary>Una carpeta por encargo (<c>faena hacer --carpeta</c>).</summary>
    public required string CarpetaTareas { get; init; }

    /// <summary>El repertorio: las habilidades aprendidas (un .toml cada una).</summary>
    public required string CarpetaHabilidades { get; init; }

    /// <summary>El ejecutable que hace cada encargo: este mismo faena.exe, con <c>hacer</c>.</summary>
    public required string Exe { get; init; }

    /// <summary>Variables que se añaden al entorno de los encargos (<c>PLAYWRIGHT_BROWSERS_PATH</c>…).</summary>
    public IDictionary<string, string> Entorno { get; init; } = new Dictionary<string, string>();

    /// <summary>Los proyectos de tramoya, para enseñarle a faena un proceso grabado allí por su id; null si no se buscan.</summary>
    public string? ProyectosTramoya { get; init; }

    /// <summary>Si la interfaz puede ofrecer lo que solo tiene sentido en Windows (abrir una carpeta en el explorador).</summary>
    public bool Windows { get; init; } = true;

    /// <summary>The Settings file (language and AI); <see cref="Paths.Ajustes"/> unless a test says otherwise.</summary>
    public string ArchivoAjustes { get; init; } = Paths.Ajustes;
}

/// <summary>
/// El servidor HTTP local de la ventana: sirve <c>ui/index.html</c> (incrustado) y la API
/// <c>/api/…</c> de encargos y repertorio, solo en 127.0.0.1 y en un puerto libre. La API es la
/// que tenía la ventana de tramoya cuando el agente vivía allí, para que la interfaz se porte tal cual.
/// </summary>
public sealed class Servidor : IDisposable
{
    private static readonly Regex RutaEncargo = new("^/api/encargos/([a-z0-9-]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex RutaCaptura = new(@"^/api/encargos/([a-z0-9-]+)/captura/([a-z0-9]+\.png)$", RegexOptions.CultureInvariant);
    private static readonly Regex RutaAccionEncargo = new("^/api/encargos/([a-z0-9-]+)/([a-z]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex RutaObra = new("^/api/repertorio/([a-z0-9-]+)/olvidar$", RegexOptions.CultureInvariant);

    private readonly Opciones _o;
    private readonly HttpListener _listener;
    private readonly Thread _bucle;
    private int _parado;

    public string Url { get; }

    private Servidor(Opciones o, HttpListener listener, string url)
    {
        _o = o;
        _listener = listener;
        Url = url;
        _bucle = new Thread(Aceptar) { IsBackground = true, Name = "faena-http" };
    }

    /// <summary>Las opciones de este exe: las carpetas de <see cref="Paths"/> y él mismo como trabajador.</summary>
    public static Opciones Opciones() => new()
    {
        CarpetaTareas = Paths.Tareas,
        CarpetaHabilidades = Paths.Habilidades,
        Exe = Environment.ProcessPath ?? "faena.exe",
        Entorno = new Dictionary<string, string> { [Paths.BrowsersVariable] = Paths.Navegador },
        ProyectosTramoya = Paths.ProyectosTramoya,
        Windows = OperatingSystem.IsWindows(),
    };

    /// <summary>Arranca el servidor en un puerto libre de 127.0.0.1 y vuelve enseguida: las peticiones se atienden en el pool de hilos.</summary>
    public static Servidor Start(Opciones opciones)
    {
        Directory.CreateDirectory(opciones.CarpetaTareas);
        Directory.CreateDirectory(opciones.CarpetaHabilidades);
        var (listener, url) = Escuchar();
        var servidor = new Servidor(opciones, listener, url);
        servidor._bucle.Start();
        return servidor;
    }

    /// <summary>Para el servidor y los encargos que sigan en marcha: al cerrar la ventana no queda ningún agente trabajando a ciegas.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _parado, 1) != 0) return;
        Encargos.PararTodos(_o);
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        try { _listener.Close(); } catch (ObjectDisposedException) { }
    }

    public void Dispose() => Stop();

    // Un puerto libre: se pide el 0 y se mira cuál ha tocado. Entre soltarlo y que HttpListener
    // lo coja podría colárselo otro proceso; se reintenta.
    private static (HttpListener, string) Escuchar()
    {
        HttpListenerException? ultimo = null;
        for (var intento = 0; intento < 5; intento++)
        {
            var url = $"http://127.0.0.1:{PuertoLibre()}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            try
            {
                listener.Start();
                return (listener, url);
            }
            catch (HttpListenerException e)
            {
                ultimo = e;
                listener.Close();
            }
        }
        throw ultimo!;
    }

    private static int PuertoLibre()
    {
        using var s = new TcpListener(IPAddress.Loopback, 0);
        s.Start();
        return ((IPEndPoint)s.LocalEndpoint).Port;
    }

    private void Aceptar()
    {
        while (_parado == 0)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                break; // Stop()
            }
            ThreadPool.QueueUserWorkItem(_ => Atender(ctx));
        }
    }

    private void Atender(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod == "GET") Get(ctx);
            else if (ctx.Request.HttpMethod == "POST") Post(ctx);
            else Error(ctx, 501);
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
        {
            // El cliente cerró antes de tiempo (la ventana se fue); no hay a quién contestar.
        }
        catch (Exception e)
        {
            // Mejor un 500 con el motivo, que la interfaz enseña como error, que cortar la conexión.
            try { Responder(ctx, new JsonObject { ["error"] = e.Message }, 500); }
            catch (Exception) { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch (Exception) { }
        }
    }

    // --- respuestas --------------------------------------------------------------------

    private static void Responder(HttpListenerContext ctx, JsonNode? datos, int code = 200)
    {
        var cuerpo = Encoding.UTF8.GetBytes(Json.Dump(datos, indent: false));
        var r = ctx.Response;
        r.StatusCode = code;
        r.ContentType = "application/json; charset=utf-8";
        r.Headers["Cache-Control"] = "no-store";
        r.ContentLength64 = cuerpo.Length;
        r.OutputStream.Write(cuerpo);
    }

    private static void Error(HttpListenerContext ctx, int code)
    {
        var r = ctx.Response;
        r.StatusCode = code;
        r.ContentType = "text/html; charset=utf-8";
        var cuerpo = Encoding.UTF8.GetBytes($"<!doctype html><title>{code}</title><h1>Error {code}</h1>");
        r.ContentLength64 = cuerpo.Length;
        r.OutputStream.Write(cuerpo);
    }

    private static byte[] Recurso(string nombre)
    {
        using var s = typeof(Servidor).Assembly.GetManifestResourceStream(nombre)
            ?? throw new FileNotFoundException($"Falta el recurso incrustado {nombre}");
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    private static readonly Lazy<byte[]> Ui = new(() => Recurso("ui/index.html"));

    private static void AbrirArchivo(string ruta) =>
        Process.Start(new ProcessStartInfo(ruta) { UseShellExecute = true });

    /// <summary>
    /// Whether there is a model at hand with the current settings: Claude or an OpenAI-compatible
    /// server that is configured, or an Ollama that answers (asked at /api/version, which is instant).
    /// </summary>
    private bool HayModelo()
    {
        try
        {
            var b = Ajustes.Backend(null, null, Ajustes.Leer(_o.ArchivoAjustes));
            if (b is not OllamaChatBackend o) return true;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
            return http.GetAsync(o.BaseUrl + "/api/version").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The draft of the Settings screen, not saved yet; an empty key means the saved one (when the backend is the same).</summary>
    private JsonObject Borrador(JsonObject cuerpo)
    {
        var guardados = Ajustes.Leer(_o.ArchivoAjustes);
        var b = new JsonObject();
        foreach (var k in new[] { Ajustes.KBackend, Ajustes.KModelo, Ajustes.KUrl, Ajustes.KClave })
            b[k] = Ajustes.Str(cuerpo, k);
        if (Ajustes.Str(b, Ajustes.KClave).Length == 0 && Ajustes.Str(b, Ajustes.KBackend) == Ajustes.Str(guardados, Ajustes.KBackend))
            b[Ajustes.KClave] = Ajustes.Str(guardados, Ajustes.KClave);
        return b;
    }

    /// <summary>A GET that returns JSON (the model lists), with an optional bearer or Anthropic key.</summary>
    internal static Func<string, IDictionary<string, string>, JsonNode?> GetJson { get; set; } = (url, headers) =>
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        using var r = http.Send(req);
        var texto = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"{url}: {(int)r.StatusCode} {Observer.Recortar(texto, 200)}");
        return JsonNode.Parse(texto);
    };

    /// <summary>The models the chosen server offers (Ollama's /api/tags, the OpenAI /models, Anthropic's /v1/models).</summary>
    private static List<string> Modelos(IChatBackend b)
    {
        var lista = new List<string>();
        JsonNode? r = b switch
        {
            OllamaChatBackend o => GetJson(o.BaseUrl + "/api/tags", new Dictionary<string, string>()),
            OpenAiChatBackend a => GetJson(a.BaseUrl + "/models", a.ApiKey is { } k ? new Dictionary<string, string> { ["Authorization"] = "Bearer " + k } : new Dictionary<string, string>()),
            ClaudeChatBackend c => GetJson("https://api.anthropic.com/v1/models", new Dictionary<string, string> { ["x-api-key"] = c.ApiKey, ["anthropic-version"] = "2023-06-01" }),
            _ => null,
        };
        if (r?["models"] is JsonArray ms) lista.AddRange(ms.Select(m => m?["name"]?.ToString() ?? m?["model"]?.ToString() ?? "").Where(s => s.Length != 0));
        if (r?["data"] is JsonArray ds) lista.AddRange(ds.Select(m => m?["id"]?.ToString() ?? "").Where(s => s.Length != 0));
        return lista.Distinct().Order(StringComparer.Ordinal).ToList();
    }

    // --- GET ---------------------------------------------------------------------------

    private void Get(HttpListenerContext ctx)
    {
        var ruta = ctx.Request.Url?.AbsolutePath ?? "/";
        if (ruta is "/" or "/index.html")
        {
            var r = ctx.Response;
            r.StatusCode = 200;
            r.ContentType = "text/html; charset=utf-8";
            r.Headers["Cache-Control"] = "no-store";
            r.ContentLength64 = Ui.Value.Length;
            r.OutputStream.Write(Ui.Value);
            return;
        }
        if (ruta == "/api/inicio")
        {
            var ajustes = Ajustes.Leer(_o.ArchivoAjustes);
            string ia;
            try { var (b, m, u) = Ajustes.Describir(Ajustes.Backend(null, null, ajustes)); ia = $"{b} · {m}{(u.Length != 0 ? " · " + u : "")}"; }
            catch (ArgumentException ex) { ia = ex.Message; }
            Responder(ctx, new JsonObject
            {
                ["version"] = Cli.Version(),
                ["windows"] = _o.Windows,
                ["modelo"] = HayModelo(),
                ["ia"] = ia,
                ["idioma"] = Lengua.Actual,
                ["ajustes"] = Ajustes.Publicos(ajustes),
                ["tareas"] = _o.CarpetaTareas,
                ["habilidades"] = _o.CarpetaHabilidades,
            });
            return;
        }
        if (ruta == "/api/encargos")
        {
            Responder(ctx, Encargos.Lista(_o));
            return;
        }
        if (ruta == "/api/repertorio")
        {
            Responder(ctx, Encargos.Repertorio(_o));
            return;
        }
        if (RutaEncargo.Match(ruta) is { Success: true } e)
        {
            if (Encargos.Detalle(_o, e.Groups[1].Value) is { } detalle) Responder(ctx, detalle);
            else Responder(ctx, new JsonObject { ["error"] = Lengua.T("no existe", "not found") }, 404);
            return;
        }
        if (RutaCaptura.Match(ruta) is { Success: true } c)
        {
            if (Encargos.Captura(_o, c.Groups[1].Value, c.Groups[2].Value) is not { } png)
            {
                Error(ctx, 404);
                return;
            }
            var bytes = File.ReadAllBytes(png);
            var r = ctx.Response;
            r.StatusCode = 200;
            r.ContentType = "image/png";
            r.ContentLength64 = bytes.Length;
            r.OutputStream.Write(bytes);
            return;
        }
        Error(ctx, 404);
    }

    // --- POST --------------------------------------------------------------------------

    private static JsonObject Cuerpo(HttpListenerContext ctx)
    {
        using var lector = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var texto = lector.ReadToEnd();
        if (texto.Trim().Length == 0) return new JsonObject();
        return Json.Parse(texto) as JsonObject ?? new JsonObject();
    }

    private void Post(HttpListenerContext ctx)
    {
        var cuerpo = Cuerpo(ctx);
        var ruta = ctx.Request.Url?.AbsolutePath ?? "/";
        if (ruta == "/api/ajustes")
        {
            // Saved for every faena that starts from now on; the window switches language at once.
            try
            {
                var guardados = Ajustes.Guardar(cuerpo, _o.ArchivoAjustes);
                if (Ajustes.Str(guardados, Ajustes.KIdioma) is { Length: > 0 } idioma && Environment.GetEnvironmentVariable(Lengua.Variable) is not { Length: > 0 })
                    Lengua.Actual = idioma;
                Responder(ctx, Ajustes.Publicos(guardados));
            }
            catch (ArgumentException e) { Responder(ctx, new JsonObject { ["error"] = e.Message }, 400); }
            return;
        }
        if (ruta is "/api/ia/modelos" or "/api/ia/probar")
        {
            try
            {
                var borrador = Borrador(cuerpo);
                // Only the draft counts here, not the environment of the window: it is what will be saved.
                var b = Backends.Pick(Ajustes.Str(borrador, Ajustes.KBackend) is { Length: > 0 } x ? x : null, null,
                    Ajustes.EntornoIa(null, borrador, new Dictionary<string, string>()));
                var (nombre, modelo, url) = Ajustes.Describir(b);
                if (ruta == "/api/ia/modelos")
                {
                    Responder(ctx, new JsonObject
                    {
                        ["backend"] = nombre, ["modelo"] = modelo, ["url"] = url,
                        ["modelos"] = new JsonArray(Modelos(b).Select(m => (JsonNode?)m).ToArray()),
                    });
                    return;
                }
                var reloj = Stopwatch.StartNew();
                using var tope = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var respuesta = b.CompleteAsync("You answer only with JSON.", [new ChatMessage("user", "Answer with {\"ok\": true}.")], tope.Token).GetAwaiter().GetResult();
                Responder(ctx, new JsonObject
                {
                    ["ok"] = true, ["backend"] = nombre, ["modelo"] = modelo, ["url"] = url,
                    ["ms"] = reloj.ElapsedMilliseconds, ["respuesta"] = Observer.Recortar(Protocol.QuitarThink(respuesta), 80),
                });
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or HttpRequestException or TaskCanceledException or OperationCanceledException or System.Text.Json.JsonException)
            {
                Responder(ctx, new JsonObject { ["ok"] = false, ["error"] = e.Message.Split('\n')[0] });
            }
            return;
        }
        if (ruta == "/api/encargar")
        {
            try { Responder(ctx, new JsonObject { ["id"] = Encargos.Encargar(_o, cuerpo) }); }
            catch (ArgumentException e) { Responder(ctx, new JsonObject { ["error"] = e.Message }, 400); }
            return;
        }
        if (ruta == "/api/aprender")
        {
            // Un proceso grabado en tramoya pasa al repertorio, por su carpeta o su id.
            try { Responder(ctx, new JsonObject { ["ok"] = true, ["nombre"] = Encargos.AprenderDeProyecto(_o, Campos.Str(cuerpo, "origen"), Campos.Str(cuerpo, "nombre")) }); }
            catch (ArgumentException e) { Responder(ctx, new JsonObject { ["error"] = e.Message }, 400); }
            return;
        }
        if (ruta == "/api/abrir-repertorio")
        {
            if (_o.Windows) AbrirArchivo(_o.CarpetaHabilidades);
            Responder(ctx, new JsonObject { ["ok"] = true });
            return;
        }
        if (RutaObra.Match(ruta) is { Success: true } o)
        {
            Responder(ctx, new JsonObject { ["ok"] = Encargos.Olvidar(_o, o.Groups[1].Value) });
            return;
        }
        if (RutaAccionEncargo.Match(ruta) is not { Success: true } m)
        {
            Error(ctx, 404);
            return;
        }
        var (id, accion) = (m.Groups[1].Value, m.Groups[2].Value);
        if (Encargos.CarpetaDe(_o, id) is not { } carpeta)
        {
            Responder(ctx, new JsonObject { ["error"] = Lengua.T("no existe", "not found") }, 404);
            return;
        }
        switch (accion)
        {
            case "responder":
                Responder(ctx, new JsonObject { ["ok"] = Encargos.Responder(_o, id, Campos.Bool(cuerpo, "si")) });
                return;
            case "parar":
                Encargos.Parar(_o, id);
                break;
            case "borrar":
                Encargos.Borrar(_o, id);
                break;
            case "abrir":
                if (_o.Windows) AbrirArchivo(carpeta);
                break;
            case "aprender":
                try { Responder(ctx, new JsonObject { ["ok"] = true, ["nombre"] = Encargos.AprenderDeEncargo(_o, id, Campos.Str(cuerpo, "nombre")) }); }
                catch (ArgumentException e) { Responder(ctx, new JsonObject { ["error"] = e.Message }, 400); }
                return;
            default:
                Responder(ctx, new JsonObject { ["error"] = Lengua.T("acción desconocida", "unknown action") }, 400);
                return;
        }
        Responder(ctx, new JsonObject { ["ok"] = true });
    }
}
