using System.Diagnostics;
using System.Text.Json.Nodes;
using Faena.Agente;
using Faena.Escritorio;
using Xunit.Abstractions;

namespace Faena.Tests;

/// <summary>
/// Las aplicaciones de escritorio: el selector <c>uia=…</c> y los tipos siempre; con
/// <c>FAENA_DESKTOP_TESTS=1</c>, además, la app de notas de <c>examples/escritorio</c>
/// abierta en un escritorio oculto (no se ve ni roba el foco) y manejada de verdad.
/// </summary>
public sealed class DesktopTests(ITestOutputHelper salida)
{
    private readonly Idioma _idioma = new("es");

    // ------------------------------------------------------------------ selector

    [Theory]
    [InlineData("uia=Button[id=\"anadir\"]", "Button", "anadir", null, null, 1)]
    [InlineData("uia=Button[name=\"Borrar todo\"]", "Button", null, "Borrar todo", null, 1)]
    [InlineData("uia=Button[ventana=\"Notas\"][name=\"Sí\"]", "Button", null, "Sí", "Notas", 1)]
    [InlineData("uia=ListItem[name=\"Pan\"][n=\"2\"]", "ListItem", null, "Pan", null, 2)]
    [InlineData("uia=*[name=\"con \\\"comillas\\\"\"]", "*", null, "con \"comillas\"", null, 1)]
    public void SelectorSeLeeYSeEscribeIgual(string texto, string tipo, string? id, string? nombre, string? ventana, int n)
    {
        var s = SelectorUia.Parse(texto);
        Assert.Equal(new SelectorUia(tipo, id, nombre, ventana, n), s);
        Assert.Equal(s, SelectorUia.Parse(s.ToString()));
    }

    [Fact]
    public void SelectorConIdNoLlevaNombre()
    {
        Assert.Equal("uia=Edit[id=\"titulo\"]", new SelectorUia("Edit", Id: "titulo", Nombre: "Título").ToString());
        Assert.Equal("uia=Button[ventana=\"Guardar como\"][name=\"Guardar\"][n=\"3\"]", new SelectorUia("Button", Nombre: "Guardar", Ventana: "Guardar como", N: 3).ToString());
    }

    [Theory]
    [InlineData("#titulo")]
    [InlineData("uia=Button")]
    [InlineData("uia=Button[color=\"rojo\"]")]
    [InlineData("uia=Button[name=\"x\"][n=\"0\"]")]
    [InlineData("uia=Button[name=x]")]
    public void SelectorRechazaLoQueNoLoEs(string texto) => Assert.Throws<ArgumentException>(() => SelectorUia.Parse(texto));

    [Theory]
    [InlineData("anadir", true)]
    [InlineData("15", true)]
    [InlineData("", false)]
    [InlineData("1234567", false)]
    [InlineData("42.7.1.3", false)]
    public void SoloLosIdsFiablesSonSelector(string id, bool esperado) => Assert.Equal(esperado, SelectorUia.IdDuradero(id));

    [Fact]
    public void LosTiposSeDicenComoEnLaWeb()
    {
        Assert.Equal("el botón", TiposUia.Tipo(TiposUia.Corto("ControlType.Button")));
        Assert.Equal("el campo", TiposUia.Tipo("Edit"));
        Assert.Equal("la casilla", TiposUia.Tipo("CheckBox"));
        Assert.Equal("el desplegable", TiposUia.Tipo("ComboBox"));
        Assert.Null(TiposUia.Tipo("Text"));
        Assert.Null(TiposUia.Tipo("TitleBar"));
    }

    [Fact]
    public async Task LasAppsDeLaTiendaNoSeAbrenEnElEscritorioOculto()
    {
        // notepad y calc existen en System32 pero en Windows 11 abren la app de la Tienda: no se lanza nada.
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => EscritorioSuperficie.AbrirAsync("notepad", null, oculto: true, null, CancellationToken.None));
        Assert.Contains("Tienda", e.Message);
    }

    [Fact]
    public async Task UnaAppQueNoExisteSeDiceClaro() =>
        await Assert.ThrowsAsync<FileNotFoundException>(() => EscritorioSuperficie.AbrirAsync("no-existe-esta-app-de-prueba", null, oculto: true, null, CancellationToken.None));

    [Fact]
    public void ElPromptDeTareaEnEscritorioNoTieneUrls()
    {
        var p = Protocol.SystemPromptTarea("Añade una nota", "escritorio");
        Assert.Contains("Windows desktop application", p);
        Assert.DoesNotContain("\"goto\"", p);
    }

    // ------------------------------------------------------------------ de verdad, en un escritorio oculto

    internal static string AppNotas()
    {
        var raiz = new DirectoryInfo(AppContext.BaseDirectory);
        while (raiz is not null && !File.Exists(Path.Combine(raiz.FullName, "Faena.sln"))) raiz = raiz.Parent;
        Assert.NotNull(raiz);
        var proyecto = Path.Combine(raiz!.FullName, "examples", "escritorio", "NotasEscritorio");
        var exe = Path.Combine(proyecto, "bin", "Release", "net9.0-windows", "NotasEscritorio.exe");
        if (!File.Exists(exe))
        {
            using var p = Process.Start(new ProcessStartInfo("dotnet", "build -c Release") { WorkingDirectory = proyecto, UseShellExecute = false, RedirectStandardOutput = true })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
        }
        Assert.True(File.Exists(exe), $"no se ha podido compilar {exe}");
        return exe;
    }

    [DesktopFact]
    public async Task ObservaEscribePulsaYRepiteEnUnEscritorioOculto()
    {
        var reloj = Stopwatch.StartNew();
        void Marca(string que) => salida.WriteLine($"[{reloj.Elapsed.TotalSeconds:0.0} s] {que}");
        await using var sup = await EscritorioSuperficie.AbrirAsync(AppNotas(), null, oculto: true, "Notas", CancellationToken.None);
        Marca("abierta");
        Assert.True(sup.Oculto);
        var obs = await sup.ObservarAsync(CancellationToken.None);
        salida.WriteLine(Protocol.Observacion(obs));
        Assert.Equal("Notas", obs.Title);
        var titulo = obs.Elements.Single(e => e.Tipo == "el campo" && e.Nombre == "Título");
        var anadir = obs.Elements.Single(e => e.Nombre == "Añadir");
        Assert.DoesNotContain(obs.Elements, e => e.Nombre is "Minimizar" or "Cerrar");
        Assert.Contains("0 notas", obs.Text);

        var paso = await sup.ActuarAsync(obs, new Decision("", "escribir", titulo.Ref, "Pan", "", ""), CancellationToken.None);
        Assert.Equal("uia=Edit[id=\"titulo\"]", paso!["selector"]!.ToString());
        Assert.Equal("Escribe «Pan» en el campo «Título»", paso["desc"]!.ToString());
        Marca("escrito el título");
        paso = await sup.ActuarAsync(obs, new Decision("", "clic", anadir.Ref, "", "", ""), CancellationToken.None);
        Assert.Equal("Pulsa el botón «Añadir»", paso!["desc"]!.ToString());
        Marca("asentar tras añadir");
        await sup.AsentarAsync();
        obs = await sup.ObservarAsync(CancellationToken.None);
        Assert.Contains("1 nota", obs.Text);
        Assert.Contains(obs.Elements, e => e.Tipo == "el elemento de la lista" && e.Nombre == "Pan");

        Marca("añadida la primera nota");
        // Repetir por selector, como hace una habilidad.
        await sup.RepetirAsync(new JsonObject { ["tipo"] = "escribir", ["selector"] = "uia=Edit[id=\"titulo\"]", ["valor"] = "Leche" }, CancellationToken.None);
        await sup.RepetirAsync(new JsonObject { ["tipo"] = "clic", ["selector"] = "uia=Button[name=\"Añadir\"]" }, CancellationToken.None);
        await sup.AsentarAsync();
        obs = await sup.ObservarAsync(CancellationToken.None);
        Assert.Contains("2 notas", obs.Text);

        Marca("repetidos los pasos");
        // «Borrar todo» abre un diálogo modal: la pulsación no vuelve, se da por hecha y el diálogo pasa delante.
        var borrar = obs.Elements.Single(e => e.Nombre == "Borrar todo");
        await sup.ActuarAsync(obs, new Decision("", "clic", borrar.Ref, "", "", ""), CancellationToken.None);
        await sup.AsentarAsync();
        obs = await sup.ObservarAsync(CancellationToken.None);
        salida.WriteLine(Protocol.Observacion(obs));
        Assert.StartsWith("Notas (sobre", obs.Title);
        Marca("el diálogo está delante");
        var no = obs.Elements.First(e => e.Tipo == "el botón" && e.Nombre == "No");
        await sup.ActuarAsync(obs, new Decision("", "clic", no.Ref, "", "", ""), CancellationToken.None);
        await sup.AsentarAsync();
        obs = await sup.ObservarAsync(CancellationToken.None);
        Assert.Equal("Notas", obs.Title);
        Assert.Contains("2 notas", obs.Text);

        Marca("diálogo cerrado");
        var png = await sup.CapturaAsync();
        salida.WriteLine($"captura: {png?.Length ?? 0} bytes");
    }
}

/// <summary>Como <see cref="BrowserFactAttribute"/>: solo con <c>FAENA_DESKTOP_TESTS=1</c>, porque abre una aplicación (aunque sea en un escritorio oculto).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DesktopFactAttribute : FactAttribute
{
    public const string Variable = "FAENA_DESKTOP_TESTS";

    public DesktopFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1") Skip = $"set {Variable}=1 to run the desktop tests";
    }
}
