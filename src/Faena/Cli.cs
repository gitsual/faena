// The `faena` command line: `hacer` and `habilidades` (Hacer.cs), `ventana` (the window's
// server, with or without the window) and the hidden `navegador instalar`, the first-run
// download of Chromium by hand. The words people read are Spanish.
//
// What the tests swap (the browser check, whether a person is at the console) are static
// delegates, so no test opens a browser or waits for a keyboard.
using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Faena.Tests")]

namespace Faena;

internal static class Cli
{
    private const string Doc =
        "faena: le encargas una tarea en una web o en un programa de Windows y la hace sola, en segundo plano. " +
        "Pregunta antes de enviar, borrar o pagar nada, y lo que sale bien lo aprende como habilidad. " +
        "Sin argumentos abre la ventana.";

    // --- seams for the tests -----------------------------------------------------------

    internal static Func<bool> StdinIsTty { get; set; } = () => !Console.IsInputRedirected;

    /// <summary>Chromium check before a browser command: null when it is there, else the exit code after printing why.</summary>
    internal static Func<int?> EnsureBrowser { get; set; } = EnsureBrowserDefault;

    public static string Version() =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private sealed class StderrProgress : IProgress<string>
    {
        public void Report(string value) => Console.Error.WriteLine(value);
    }

    private static int? EnsureBrowserDefault()
    {
        var error = Browsers.EnsureAsync(new StderrProgress(), CancellationToken.None).GetAwaiter().GetResult();
        if (error is null)
            return null;
        Console.Error.WriteLine(error);
        return 2;
    }

    internal static ArgParser BuildParser()
    {
        var ap = new ArgParser("faena", description: Doc);
        ap.AddVersion($"faena {Version()}");
        Hacer.Registrar(ap);

        var p = ap.AddSubparser("command", "ventana", help: "abre la ventana (lo mismo que sin argumentos)", aliases: "window");
        p.AddSwitch("--no-window", help: "solo el servidor: imprime su dirección y sigue escuchando (para abrirla en un navegador)");
        p.Handler = CmdVentana;
        return ap;
    }

    private static int CmdVentana(ParsedArgs args)
    {
        if (!args.Flag("no_window")) return Program.RunWindow();
        using var servidor = Servidor.Start(Servidor.Opciones());
        Console.Out.WriteLine(servidor.Url);
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    /// <summary>`faena navegador instalar`: the first-run download, runnable by hand (publish.ps1 can use it too).</summary>
    private static int CmdNavegador(string[] rest)
    {
        if (rest.Length == 1 && rest[0] is "instalar" or "install")
        {
            if (Browsers.Installed())
            {
                Console.WriteLine($"Chromium ya está en {Paths.Navegador}");
                return 0;
            }
            if (EnsureBrowser() is { } rc)
                return rc;
            Console.WriteLine($"Chromium instalado en {Paths.Navegador}");
            return 0;
        }
        Console.Error.WriteLine("uso: faena navegador instalar");
        return 2;
    }

    /// <summary>Parse, dispatch, return the exit code.</summary>
    public static int Run(string[] args)
    {
        // Hidden: `faena navegador instalar`, the first-run download by hand.
        if (args.Length > 0 && args[0] is "navegador" or "browser")
            return CmdNavegador(args.Skip(1).ToArray());
        ParsedArgs parsed;
        try
        {
            parsed = BuildParser().Parse(args);
        }
        catch (ArgParseExit exit)
        {
            (exit.Code == 0 ? Console.Out : Console.Error).Write(exit.Message);
            return exit.Code;
        }
        if (parsed.Handler is null)
        {
            Console.Error.Write(BuildParser().FormatHelp());
            return 2;
        }
        return parsed.Handler(parsed);
    }
}
