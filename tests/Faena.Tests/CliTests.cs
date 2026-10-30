namespace Faena.Tests;

/// <summary>Lo que el CLI hace sin abrir nada: la ayuda, la versión y traer las habilidades de tramoya.</summary>
public sealed class CliTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "faena-cli-" + Guid.NewGuid().ToString("N"));
    private readonly TextWriter _out = Console.Out;
    private readonly TextWriter _err = Console.Error;
    private readonly StringWriter _salida = new();
    private readonly StringWriter _errores = new();

    public CliTests()
    {
        Directory.CreateDirectory(_raiz);
        Environment.SetEnvironmentVariable("FAENA_HABILIDADES", Path.Combine(_raiz, "habilidades"));
        Console.SetOut(_salida);
        Console.SetError(_errores);
    }

    public void Dispose()
    {
        Console.SetOut(_out);
        Console.SetError(_err);
        Environment.SetEnvironmentVariable("FAENA_HABILIDADES", null);
        try { Directory.Delete(_raiz, true); } catch (IOException) { }
    }

    [Fact]
    public void LaAyudaListaLosComandos()
    {
        Assert.Equal(0, Cli.Run(["--help"]));
        var ayuda = _salida.ToString();
        Assert.Contains("usage: faena", ayuda);
        Assert.Contains("hacer", ayuda);
        Assert.Contains("habilidades", ayuda);
        Assert.Contains("ventana", ayuda);
        Assert.DoesNotContain("tramoya", ayuda.Replace("aprendió tramoya", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaVersionEsLaDelProyecto()
    {
        Assert.Equal(0, Cli.Run(["--version"]));
        Assert.Matches(@"^faena \d+\.\d+\.\d+", _salida.ToString());
    }

    [Fact]
    public void SinComandoExplicaComoSeUsa()
    {
        Assert.Equal(2, Cli.Run(["habilidades"]));
        Assert.Contains("usage: faena", _errores.ToString());
    }

    [Fact]
    public void ImportarTraeLasHabilidadesSinPisarLasQueHay()
    {
        var origen = Path.Combine(_raiz, "de-tramoya");
        Directory.CreateDirectory(origen);
        File.Copy(Path.Combine(Ejemplos(), "crear-nota.toml"), Path.Combine(origen, "crear-nota.toml"));

        Assert.Equal(0, Cli.Run(["habilidades", "importar", origen]));
        Assert.True(File.Exists(Path.Combine(_raiz, "habilidades", "crear-nota.toml")));
        Assert.Contains("1 habilidad traída", _salida.ToString());

        Assert.Equal(0, Cli.Run(["habilidades", "importar", origen]));
        Assert.Contains("ya está", _salida.ToString());
        Assert.Contains("0 habilidades traídas", _salida.ToString());

        Assert.Equal(0, Cli.Run(["habilidades", "lista"]));
        Assert.Contains("crear-nota [web", _salida.ToString());
        Assert.Equal(2, Cli.Run(["habilidades", "importar", Path.Combine(_raiz, "no-existe")]));
    }

    /// <summary>examples/habilidades, subiendo desde la carpeta de los tests.</summary>
    private static string Ejemplos()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "examples")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "examples", "habilidades");
    }
}
