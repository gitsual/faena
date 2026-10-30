using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Faena.Tests;

/// <summary>
/// El servidor de la ventana, de verdad en 127.0.0.1: la página, el inicio, los encargos y el
/// repertorio. Los encargos se lanzan con un proceso que no hace nada (<c>cmd /c exit</c>).
/// </summary>
public sealed class ServidorTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "faena-servidor-" + Guid.NewGuid().ToString("N"));
    private readonly Servidor _servidor;
    private readonly HttpClient _http = new();
    private readonly Func<ProcessStartInfo, Process?> _lanzador = Encargos.Lanzador;
    private ProcessStartInfo? _lanzado;

    public ServidorTests()
    {
        Encargos.Lanzador = psi =>
        {
            _lanzado = psi;
            return Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false });
        };
        _servidor = Servidor.Start(new Opciones
        {
            Exe = @"C:\kit\faena.exe",
            CarpetaTareas = Path.Combine(_raiz, "tareas"),
            CarpetaHabilidades = Path.Combine(_raiz, "habilidades"),
            ProyectosTramoya = Path.Combine(_raiz, "tramoya"),
            Windows = false,
        });
    }

    public void Dispose()
    {
        _servidor.Stop();
        _http.Dispose();
        Encargos.Lanzador = _lanzador;
        try { Directory.Delete(_raiz, true); } catch (IOException) { }
    }

    private async Task<(int Code, JsonNode? Datos)> Post(string ruta, JsonObject cuerpo)
    {
        using var r = await _http.PostAsync(_servidor.Url + ruta.TrimStart('/'), new StringContent(cuerpo.ToJsonString(), Encoding.UTF8, "application/json"));
        return ((int)r.StatusCode, JsonNode.Parse(await r.Content.ReadAsStringAsync()));
    }

    private async Task<JsonNode?> Get(string ruta) => JsonNode.Parse(await _http.GetStringAsync(_servidor.Url + ruta.TrimStart('/')));

    [Fact]
    public async Task SirveLaVentanaYSuInicio()
    {
        Assert.StartsWith("http://127.0.0.1:", _servidor.Url);
        var html = await _http.GetStringAsync(_servidor.Url);
        Assert.Contains("<title>Faena</title>", html);
        Assert.Contains("Encargar a la IA", html);
        Assert.DoesNotContain("Nuevo vídeo", html);
        var inicio = await Get("/api/inicio");
        Assert.Equal(Path.Combine(_raiz, "tareas"), inicio!["tareas"]!.ToString());
        Assert.False(inicio["windows"]!.GetValue<bool>());
        Assert.Empty((await Get("/api/encargos"))!.AsArray());
        Assert.Empty((await Get("/api/repertorio"))!.AsArray());
        using var r = await _http.GetAsync(_servidor.Url + "api/nada");
        Assert.Equal(404, (int)r.StatusCode);
    }

    [Fact]
    public async Task EncargarDejaElEncargoEnLaListaYSeVeSuDetalle()
    {
        var (code, mal) = await Post("/api/encargar", new JsonObject { ["objetivo"] = "x" });
        Assert.Equal(400, code);
        Assert.Contains("Dile dónde", mal!["error"]!.ToString());

        var (ok, datos) = await Post("/api/encargar", new JsonObject { ["objetivo"] = "Crea la nota «Pan»", ["url"] = "http://n.test/" });
        Assert.Equal(200, ok);
        var id = datos!["id"]!.ToString();
        Assert.Equal("hacer", _lanzado!.ArgumentList[0]);
        var lista = (await Get("/api/encargos"))!.AsArray();
        Assert.Equal(id, lista.Single()!["id"]!.ToString());
        Assert.Equal("web", lista[0]!["superficie"]!.ToString());
        Assert.Equal("http://n.test/", lista[0]!["donde"]!.ToString());
        var detalle = await Get($"/api/encargos/{id}");
        Assert.Equal("Crea la nota «Pan»", detalle!["objetivo"]!.ToString());

        var (parado, _) = await Post($"/api/encargos/{id}/parar", []);
        Assert.Equal(200, parado);
        Assert.Equal("parado", (await Get($"/api/encargos/{id}"))!["estado"]!.ToString());
        var (borrado, _) = await Post($"/api/encargos/{id}/borrar", []);
        Assert.Equal(200, borrado);
        Assert.Empty((await Get("/api/encargos"))!.AsArray());
    }

    [Fact]
    public async Task UnProcesoDeTramoyaPasaAlRepertorio()
    {
        var (code, mal) = await Post("/api/aprender", new JsonObject { ["origen"] = "no-existe", ["nombre"] = "x" });
        Assert.Equal(400, code);
        Assert.Contains("proyecto.json", mal!["error"]!.ToString());

        var carpeta = Path.Combine(_raiz, "tramoya", "alta");
        Directory.CreateDirectory(carpeta);
        File.WriteAllText(Path.Combine(carpeta, "proyecto.json"),
            """{"nombre":"Alta","inicio":"https://crm.test/","pasos":[{"tipo":"escribir","selector":"#n","valor":"Ana","desc":"Escribe «Ana» en el campo «Nombre»"}]}""");
        var (ok, datos) = await Post("/api/aprender", new JsonObject { ["origen"] = "alta", ["nombre"] = "Alta de cliente" });
        Assert.Equal(200, ok);
        Assert.Equal("alta-de-cliente", datos!["nombre"]!.ToString());
        var obra = (await Get("/api/repertorio"))!.AsArray().Single()!;
        Assert.Equal("https://crm.test/", obra["inicio"]!.ToString());
        Assert.Equal("Ana", obra["parametros"]!["nombre"]!.ToString());
        var (olvidada, _) = await Post("/api/repertorio/alta-de-cliente/olvidar", []);
        Assert.Equal(200, olvidada);
        Assert.Empty((await Get("/api/repertorio"))!.AsArray());
    }
}
