// The `faena` command line: `do` and `skills` (Hacer.cs), `skill` (AgentSkills.cs), `window`
// (the window's server, with or without the window) and the hidden `browser install`, the
// first-run download of Chromium by hand. English by default, Spanish with --lang es (or
// FAENA_LANG, or the window's Settings): the commands and options have a name in each language
// and both always work (`do` = `hacer`, `--background` = `--fondo`).
//
// What the tests swap (the browser check, whether a person is at the console) are static
// delegates, so no test opens a browser or waits for a keyboard.
using System.Reflection;
using System.Runtime.CompilerServices;
using Faena.Agente;

[assembly: InternalsVisibleTo("Faena.Tests")]

namespace Faena;

internal static class Cli
{
    private static string Doc() => Lengua.T(
        "faena: le encargas una tarea en una web o en un programa de Windows y la hace sola, en segundo plano. " +
        "Pregunta antes de enviar, borrar o pagar nada, y lo que sale bien lo aprende como habilidad. " +
        "Sin argumentos abre la ventana.",
        "faena: hand it a task in a website or a Windows program and it does it on its own, in the background. " +
        "It asks before sending, deleting or paying for anything, and what goes well it learns as a skill. " +
        "Without arguments it opens the window.");

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

    /// <summary>A command's name in the interface's language first, the other one (and older ones) as aliases.</summary>
    internal static (string Nombre, string[] Alias) Comando(string en, string es, params string[] otros) =>
        Lengua.Es ? (es, [en, .. otros]) : (en, [es, .. otros]);

    internal static ArgParser BuildParser()
    {
        var ap = new ArgParser("faena", description: Doc());
        ap.AddVersion($"faena {Version()}");
        ap.Add("--lang", choices: ["en", "es"], help: Lengua.T(
            "el idioma de la salida, los informes y lo que escribe el modelo (por defecto, FAENA_LANG, el de los ajustes o en)",
            "the language of the output, the reports and what the model writes (default: FAENA_LANG, the one in Settings, or en)"));
        Hacer.Registrar(ap);
        AgentSkills.Registrar(ap);

        var (nombre, alias) = Comando("window", "ventana");
        var p = ap.AddSubparser("command", nombre, help: Lengua.T("abre la ventana (lo mismo que sin argumentos)", "open the window (the same as no arguments)"), aliases: alias);
        p.AddSwitch("--no-window", help: Lengua.T("solo el servidor: imprime su dirección y sigue escuchando (para abrirla en un navegador)",
                                                  "only the server: print its address and keep listening (to open it in a browser)"), es: "--sin-ventana");
        p.Handler = CmdVentana;
        return ap;
    }

    private static int CmdVentana(ParsedArgs args)
    {
        if (!args.Flag("sin_ventana")) return Program.RunWindow();
        using var servidor = Servidor.Start(Servidor.Opciones());
        Console.Out.WriteLine(servidor.Url);
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    /// <summary>`faena browser install`: the first-run download, runnable by hand (publish.ps1 can use it too).</summary>
    private static int CmdNavegador(string[] rest)
    {
        if (rest.Length == 1 && rest[0] is "instalar" or "install")
        {
            if (Browsers.Installed())
            {
                Console.WriteLine(Lengua.T($"Chromium ya está en {Paths.Navegador}", $"Chromium is already in {Paths.Navegador}"));
                return 0;
            }
            if (EnsureBrowser() is { } rc)
                return rc;
            Console.WriteLine(Lengua.T($"Chromium instalado en {Paths.Navegador}", $"Chromium installed in {Paths.Navegador}"));
            return 0;
        }
        Console.Error.WriteLine(Lengua.T("uso: faena navegador instalar", "usage: faena browser install"));
        return 2;
    }

    /// <summary>
    /// The language of this run, before anything is parsed (the help itself must come out in it):
    /// <c>--lang xx</c> or <c>--lang=xx</c> anywhere, then FAENA_LANG, then the window's Settings.
    /// The flag is taken out of the arguments, so every subcommand accepts it.
    /// </summary>
    internal static string[] ElegirIdioma(string[] args)
    {
        string? flag = null;
        var resto = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--lang" && i + 1 < args.Length) { flag = args[++i]; continue; }
            if (args[i].StartsWith("--lang=", StringComparison.Ordinal)) { flag = args[i]["--lang=".Length..]; continue; }
            resto.Add(args[i]);
        }
        if (flag is not null && !Lengua.Valido(flag))
        {
            // Unknown: keep it, so the parser explains the valid choices.
            resto.Insert(0, flag);
            resto.Insert(0, "--lang");
        }
        Lengua.Actual = Ajustes.Idioma(flag, Ajustes.Leer());
        return resto.ToArray();
    }

    /// <summary>Parse, dispatch, return the exit code.</summary>
    public static int Run(string[] args)
    {
        args = ElegirIdioma(args);
        // Hidden: `faena browser install`, the first-run download by hand.
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
