// Where faena keeps its things on Windows.
//
// What a person reads and edits lives in %USERPROFILE%\Faena: `tareas\` (one folder per job,
// with its log, report and captures) and `habilidades\` (the learned skills, one .toml each).
// What only the program needs goes to %LOCALAPPDATA%\Faena: the browser profile of the tasks
// and the WebView2 profile of the window. Inside a portable kit (a `navegador\` folder beside
// faena.exe, or FAENA_KIT pointing at one) everything stays in the kit folder.
//
// Chromium: faena drives the same Playwright build as tramoya, so when tramoya has already
// downloaded it, faena uses that copy instead of downloading another 150 MB.
//
// Everything is computed on each access instead of cached at startup: it is cheap, and it lets
// the tests switch layouts just by changing the environment.
using System.Drawing;

namespace Faena;

public static class Paths
{
    public const string KitVariable = "FAENA_KIT";
    public const string BrowsersVariable = "PLAYWRIGHT_BROWSERS_PATH";

    /// <summary>The folder that holds faena.exe (the test host's folder under tests).</summary>
    public static string ExeDir =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>The portable kit, or null when installed normally.</summary>
    public static string? Kit
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(KitVariable);
            if (!string.IsNullOrWhiteSpace(env))
                return Path.GetFullPath(env);
            return Directory.Exists(Path.Combine(ExeDir, "navegador")) ? ExeDir : null;
        }
    }

    /// <summary>Home of faena for the user: the kit, or %USERPROFILE%\Faena.</summary>
    public static string Casa => Kit ?? Path.Combine(UserProfile, "Faena");

    /// <summary>What only the program reads: profiles of the browser and the window.</summary>
    public static string Datos => Kit is { } kit
        ? Path.Combine(kit, "datos")
        : Path.Combine(LocalAppData, "Faena");

    /// <summary>One folder per `hacer` run (log, report, captures); $FAENA_TAREAS overrides it.</summary>
    public static string Tareas => Env("FAENA_TAREAS") ?? Path.Combine(Casa, "tareas");

    /// <summary>The learned skills (one .toml each), meant to be read and edited by hand; $FAENA_HABILIDADES overrides it.</summary>
    public static string Habilidades => Env("FAENA_HABILIDADES") ?? Path.Combine(Casa, "habilidades");

    /// <summary>Where tramoya keeps the skills it learned before faena existed: `habilidades importar` brings them over.</summary>
    public static string HabilidadesTramoya => Path.Combine(UserProfile, "Tramoya", "habilidades");

    /// <summary>tramoya's projects (one folder each, with proyecto.json): a recorded process can be taught by its id.</summary>
    public static string ProyectosTramoya => Path.Combine(LocalAppData, "Tramoya", "datos");

    /// <summary>The persistent browser profile of the tasks: sessions survive from one job to the next.</summary>
    public static string PerfilTareas => Path.Combine(Datos, "navegador-tareas");

    /// <summary>User data folder of the WebView2 window.</summary>
    public static string Ventana => Path.Combine(Datos, "ventana");

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? Path.GetFullPath(v) : null;

    /// <summary>
    /// Where Playwright keeps Chromium (PLAYWRIGHT_BROWSERS_PATH): the kit's, faena's own, or
    /// tramoya's when faena has none yet and tramoya has one.
    /// </summary>
    public static string Navegador
    {
        get
        {
            if (Kit is { } kit) return Path.Combine(kit, "navegador");
            var propio = Path.Combine(LocalAppData, "Faena", "navegador");
            if (Directory.Exists(propio)) return propio;
            var tramoya = Path.Combine(LocalAppData, "Tramoya", "navegador");
            return Directory.Exists(tramoya) ? tramoya : propio;
        }
    }

    /// <summary>The icon lives inside the exe itself, so its path is the exe's.</summary>
    public static string? IconPath => Environment.ProcessPath;

    /// <summary>The exe's own icon, or null if it cannot be read (test hosts, odd launchers).</summary>
    public static Icon? Icono()
    {
        try
        {
            return IconPath is { } path ? Icon.ExtractAssociatedIcon(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Point Playwright at our Chromium, for this process and the jobs it launches.</summary>
    public static void ConfigureEnvironment() => Environment.SetEnvironmentVariable(BrowsersVariable, Navegador);

    private static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}
