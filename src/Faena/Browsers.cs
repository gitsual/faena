// Chromium provisioning (taken from tramoya, where it was written). A kit ships Playwright's
// Chromium inside `navegador\`; installed, the browser lives in %LOCALAPPDATA%\Faena\navegador
// (or tramoya's, see Paths.Navegador) and has to be downloaded the first time.
//
// What Playwright needs on Windows to launch and record Chromium is four
// folders under PLAYWRIGHT_BROWSERS_PATH, each closed with an
// INSTALLATION_COMPLETE marker and holding its executable: the browser, its
// headless shell, winldd (the dependency checker) and Playwright's own ffmpeg,
// the one the driver runs to encode the recorded video (the kit's separate
// `ffmpeg\ffmpeg.exe` is for tramoya's assembly, the driver never looks at it).
// Their names carry the build numbers of the exact Playwright version compiled
// in, so they are read from the `browsers.json` the package copies next to the
// exe instead of being hardcoded: a package bump must never leave this class
// looking for the wrong folder. The marker alone is not enough: a folder whose
// files were cleaned out by an antivirus or a half-copied kit still has it.
//
// The download is done by the driver itself, run the way the package's
// `Program.Main(["install", "chromium"])` runs it, but as a child process of
// our own: `.playwright\node\win32_x64\node.exe .playwright\package\cli.js
// install chromium`. Owning the Process is the point: the driver is known to
// hang after a finished download now and then, and the user can close the
// first-run dialog halfway, so there has to be something real to kill and to
// wait for, with a bound. The driver also goes into a Job Object that kills
// its whole tree when this process dies, so neither Ctrl+C nor a task manager
// kill leaves a node.exe downloading in the background. Its stdout/stderr
// lines are the progress the user sees; the files it writes (the zip in the
// temp folder, then the extracted tree) are the second sign of life.
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Faena.Escena;
using Faena.Agente;

namespace Faena;

internal static class Browsers
{
    /// <summary>Marker Playwright writes when a browser folder is complete.</summary>
    public const string Marker = "INSTALLATION_COMPLETE";

    /// <summary>
    /// The entries of browsers.json Chromium needs on Windows, in Playwright's naming, with
    /// the executable the driver looks for inside each folder. Any of the candidates will
    /// do: the layout inside the folder changed with the Chromium build (`chrome-win` up to
    /// build 1169, `chrome-win64` from 1181), and this must not break on a package bump.
    /// </summary>
    internal static readonly (string Name, string[] Executables)[] Needed =
    {
        ("chromium", new[] { @"chrome-win64\chrome.exe", @"chrome-win\chrome.exe" }),
        ("chromium-headless-shell", new[] { @"chrome-headless-shell-win64\chrome-headless-shell.exe", @"chrome-win\headless_shell.exe" }),
        ("winldd", new[] { "PrintDeps.exe" }),
        ("ffmpeg", new[] { "ffmpeg-win64.exe" }),
    };

    /// <summary>How long the driver may stay silent (no output, nothing written) before it is taken for hung.</summary>
    internal static TimeSpan Stall { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>How long a killed driver gets to go away before that is reported as a failure instead of waited on.</summary>
    internal static TimeSpan ReapBound { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The `.playwright` folder of the Microsoft.Playwright package compiled in: the node
    /// runtime, the driver (`package\cli.js`) and its `browsers.json`, next to the exe.
    /// </summary>
    public static string DriverDir
    {
        get
        {
            // PLAYWRIGHT_DRIVER_SEARCH_PATH is how playwright.ps1 points the package at its
            // driver; honouring it keeps both ways of running the driver in step.
            var roots = new[]
            {
                Environment.GetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH"),
                AppContext.BaseDirectory,
                Paths.ExeDir,
            };
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root))
                    continue;
                var candidate = Path.Combine(root, ".playwright");
                if (File.Exists(Path.Combine(candidate, "package", "browsers.json")))
                    return candidate;
            }
            return Path.Combine(AppContext.BaseDirectory, ".playwright");
        }
    }

    /// <summary>The `browsers.json` of the Microsoft.Playwright package compiled in.</summary>
    public static string BrowsersJson => Path.Combine(DriverDir, "package", "browsers.json");

    /// <summary>
    /// What this build's Playwright expects under <see cref="Paths.Navegador"/>: the folder
    /// of each needed component (<c>chromium-1248</c>, <c>chromium_headless_shell-1248</c>,
    /// <c>winldd-1007</c>, <c>ffmpeg-1013</c>: the driver swaps the dashes of the name for
    /// underscores and appends the revision) and the executables that may be inside it.
    /// </summary>
    internal static IReadOnlyList<(string Folder, string[] Executables)> Layout()
    {
        var json = Json.LoadNode(BrowsersJson) as JsonObject
                   ?? throw new InvalidOperationException(Lengua.T($"No puedo leer {BrowsersJson}", $"Cannot read {BrowsersJson}"));
        var revisions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in json["browsers"]?.AsArray() ?? new JsonArray())
        {
            if (entry is JsonObject o && o["name"]?.GetValue<string>() is { } name
                                      && o["revision"]?.GetValue<string>() is { } revision)
                revisions[name] = revision;
        }
        var layout = new List<(string, string[])>();
        foreach (var (name, executables) in Needed)
        {
            if (!revisions.TryGetValue(name, out var revision))
                throw new InvalidOperationException(Lengua.T($"{BrowsersJson} no trae la revisión de {name}", $"{BrowsersJson} has no revision for {name}"));
            layout.Add((name.Replace('-', '_') + "-" + revision, executables));
        }
        return layout;
    }

    /// <summary>The folder names of <see cref="Layout"/>.</summary>
    public static IReadOnlyList<string> Folders() => Layout().Select(l => l.Folder).ToList();

    /// <summary>True when every folder Chromium needs is complete under <see cref="Paths.Navegador"/>: marker and executable.</summary>
    public static bool Installed()
    {
        try
        {
            var root = Paths.Navegador;
            return Layout().All(l =>
                File.Exists(Path.Combine(root, l.Folder, Marker))
                && l.Executables.Any(exe => File.Exists(Path.Combine(root, l.Folder, exe))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or System.Text.Json.JsonException)
        {
            // No browsers.json, unreadable folder: Playwright could not launch either.
            return false;
        }
    }

    /// <summary>
    /// Make sure Chromium is installed, downloading it when it is not. Returns null when it is
    /// there, else the message to show the user (Spanish). Progress lines go to
    /// <paramref name="progress"/>; cancelling kills the download, waits for it to be gone and
    /// throws <see cref="OperationCanceledException"/> (or <see cref="InvalidOperationException"/>
    /// when the driver could not be killed, so the user hears about the process left behind).
    /// </summary>
    public static async Task<string?> EnsureAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        if (Installed())
            return null;
        try
        {
            Directory.CreateDirectory(Paths.Navegador);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Lengua.T($"No puedo crear la carpeta del navegador {Paths.Navegador}: {e.Message}", $"Cannot create the browser folder {Paths.Navegador}: {e.Message}");
        }

        string? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            progress?.Report(attempt == 1
                ? Lengua.T($"Descargando Chromium en {Paths.Navegador}…", $"Downloading Chromium into {Paths.Navegador}…")
                : Lengua.T("La descarga se quedó parada; lo intento otra vez…", "The download stalled; trying again…"));
            var (code, hung, error) = await InstallOnceAsync(progress, cancel).ConfigureAwait(false);
            if (Installed())
                return null;
            last = hung
                ? Lengua.T("La descarga se quedó parada dos veces", "The download stalled twice")
                : error ?? (code == 0
                    ? Lengua.T($"Playwright terminó sin dejar en {Paths.Navegador} las carpetas {string.Join(", ", Folders())}", $"Playwright ended without leaving the folders {string.Join(", ", Folders())} in {Paths.Navegador}")
                    : Lengua.T($"Playwright terminó con el código {code}", $"Playwright ended with code {code}"));
            if (!hung)
                break;
        }
        return Lengua.T("No he podido descargar el navegador Chromium: ", "Could not download the Chromium browser: ") + last + ".";
    }

    /// <summary>
    /// One run of the driver under the watchdog: exit code, whether it was killed for hanging
    /// (the caller retries once), and a message when it could not start or could not be
    /// killed (no retry then: a second driver would fight the one still there).
    /// </summary>
    private static async Task<(int Code, bool Hung, string? Error)> InstallOnceAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        Process proc;
        try
        {
            proc = StartDriver();
        }
        catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return (-1, false, $"no he podido arrancar el instalador de Playwright ({e.Message})");
        }

        using (proc)
        using (var job = Job.Adopt(proc))
        {
            // The driver's lines are read on the pool and handed to the loop below, which is
            // the only thing that reports: progress stays in order and on one thread.
            var lines = new ConcurrentQueue<string>();
            void OnLine(object _, DataReceivedEventArgs e)
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    lines.Enqueue(e.Data.Trim());
            }
            proc.OutputDataReceived += OnLine;
            proc.ErrorDataReceived += OnLine;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            // Completes once the driver has exited and its pipes have closed, so by then every
            // line has been queued. Never awaited bare after a kill: see Terminate.
            var exited = proc.WaitForExitAsync(CancellationToken.None);

            var lastChange = DateTime.UtcNow;
            var lastSeen = Snapshot();
            while (true)
            {
                var tick = await Task.WhenAny(exited, Task.Delay(2000, cancel)).ConfigureAwait(false);
                var said = false;
                while (lines.TryDequeue(out var line))
                {
                    said = true;
                    lastChange = DateTime.UtcNow;
                    progress?.Report(Translate(line));
                }
                if (tick == exited)
                    break;
                if (cancel.IsCancellationRequested)
                {
                    // Closed by the user: the driver goes first, then the caller hears about
                    // the cancellation. A driver that will not die is reported, not waited on.
                    if (!await TerminateAsync(proc, job, exited).ConfigureAwait(false))
                        throw new InvalidOperationException(Immortal(proc));
                    cancel.ThrowIfCancellationRequested();
                }
                var seen = Snapshot();
                if (seen != lastSeen)
                {
                    lastSeen = seen;
                    lastChange = DateTime.UtcNow;
                    if (!said)
                        progress?.Report(Describe(seen));
                }
                else if (DateTime.UtcNow - lastChange > Stall)
                {
                    // Nothing said and nothing written for too long: the driver is stuck (it
                    // happens after the download finishes). Kill it; the caller retries once.
                    if (!await TerminateAsync(proc, job, exited).ConfigureAwait(false))
                        return (-1, false, Immortal(proc));
                    return (-1, true, null);
                }
            }
            return (proc.ExitCode, false, null);
        }
    }

    /// <summary>
    /// The driver as a child process: what <c>Microsoft.Playwright.Program.Main</c> runs,
    /// with PLAYWRIGHT_BROWSERS_PATH pointing at <see cref="Paths.Navegador"/>.
    /// </summary>
    private static Process StartDriver()
    {
        var driver = DriverDir;
        var node = Path.Combine(driver, "node", "win32_x64", "node.exe");
        var cli = Path.Combine(driver, "package", "cli.js");
        if (!File.Exists(node) || !File.Exists(cli))
            throw new FileNotFoundException($"falta el driver de Playwright en {driver}");
        var psi = new ProcessStartInfo(node)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = driver,
        };
        psi.ArgumentList.Add(cli);
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add("chromium");
        psi.Environment[Paths.BrowsersVariable] = Paths.Navegador;
        // What the package sets before running the driver: the hints it prints are worded
        // for .NET instead of for `npx playwright`.
        psi.Environment["PW_LANG_NAME"] = "csharp";
        return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start no ha devuelto ningún proceso");
    }

    /// <summary>
    /// Kill the driver and everything it started and wait, bounded by <see cref="ReapBound"/>,
    /// for it to be gone. True when it is. <paramref name="exited"/> also waits for the pipes
    /// to close, which a leftover could hold open, so the process itself is the verdict.
    /// </summary>
    private static async Task<bool> TerminateAsync(Process proc, Job? job, Task exited)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // Already gone, or Kill could not reach it: the job below is the second try.
        }
        var deadline = DateTime.UtcNow + ReapBound;
        var jobTried = false;
        while (true)
        {
            if (exited.IsCompleted || HasExited(proc))
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            if (!jobTried && job is not null)
            {
                // Still there after Kill: terminating the job takes the whole tree with it.
                jobTried = true;
                job.Terminate();
            }
            await Task.WhenAny(exited, Task.Delay(200)).ConfigureAwait(false);
        }
    }

    private static bool HasExited(Process proc)
    {
        try { return proc.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static string Immortal(Process proc)
    {
        int id;
        try { id = proc.Id; }
        catch (InvalidOperationException) { id = 0; }
        return Lengua.T($"no he podido parar el instalador de Playwright (node.exe, pid {id}); ciérralo desde el Administrador de tareas",
                        $"could not stop Playwright's installer (node.exe, pid {id}); close it from the Task Manager");
    }

    /// <summary>The driver's lines as the user sees them: the known shapes in the interface's language, the rest as they come.</summary>
    private static string Translate(string line)
    {
        // The driver dims the URL with ANSI colour codes even into a pipe; a dialog label
        // would show them as garbage.
        line = Regex.Replace(line, @"\x1b\[[0-9;]*[A-Za-z]", "").Trim();
        var m = Regex.Match(line, @"^Downloading (.+?) from \S+$");
        if (m.Success)
            return Lengua.T($"Descargando {m.Groups[1].Value}…", $"Downloading {m.Groups[1].Value}…");
        m = Regex.Match(line, @"^\|.*\|\s*(\d+)% of (.+)$");
        if (m.Success)
            return Lengua.T($"Descargando… {m.Groups[1].Value} % de {m.Groups[2].Value}", $"Downloading… {m.Groups[1].Value}% of {m.Groups[2].Value}");
        m = Regex.Match(line, @"^(.+?) downloaded to .+$");
        if (m.Success)
            return Lengua.T($"{m.Groups[1].Value} descargado; instalando…", $"{m.Groups[1].Value} downloaded; installing…");
        return line;
    }

    /// <summary>What the driver has written so far: the zip growing in the temp folder and the files extracted under Navegador.</summary>
    private readonly record struct Activity(long DownloadBytes, int Files, long Bytes);

    private static Activity Snapshot()
    {
        long download = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "playwright-download-*"))
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                    download += SafeLength(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var files = 0;
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Paths.Navegador, "*", SearchOption.AllDirectories))
            {
                files++;
                bytes += SafeLength(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new Activity(download, files, bytes);
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static string Describe(Activity a)
    {
        if (a.DownloadBytes > 0)
            return Lengua.T($"Descargando Chromium… {a.DownloadBytes / (1024 * 1024)} MB", $"Downloading Chromium… {a.DownloadBytes / (1024 * 1024)} MB");
        return Lengua.T($"Instalando… {a.Files} ficheros ({a.Bytes / (1024 * 1024)} MB)", $"Installing… {a.Files} files ({a.Bytes / (1024 * 1024)} MB)");
    }

    // --- Job Object: the driver's tree dies with this process -------------------------

    /// <summary>
    /// A kill-on-close Job Object holding the driver and whatever it spawns. Windows kills
    /// every process in the job when its last handle closes, and this process holds the
    /// only handle: if faena.exe goes down with the driver running (Ctrl+C in the
    /// terminal, the task manager, a crash), the download does not keep running on its
    /// own. Disposing it after the driver is gone is a no-op; after a failed kill it is the
    /// last resort.
    /// </summary>
    private sealed class Job : IDisposable
    {
        private readonly SafeFileHandle _handle;

        private Job(SafeFileHandle handle) => _handle = handle;

        /// <summary>Put the process in a new job; null when Windows would not let us (the driver then runs unowned by the job, still owned by Kill).</summary>
        public static Job? Adopt(Process proc)
        {
            var handle = CreateJobObjectW(IntPtr.Zero, null);
            if (handle.IsInvalid)
                return null;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
            };
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size)
                    || !AssignProcessToJobObject(handle, proc.Handle))
                {
                    handle.Dispose();
                    return null;
                }
            }
            catch (InvalidOperationException)
            {
                // The process was gone before it could be adopted.
                handle.Dispose();
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return new Job(handle);
        }

        /// <summary>Kill every process in the job now.</summary>
        public void Terminate() => TerminateJobObject(_handle, 1);

        public void Dispose() => _handle.Dispose();
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
