// Entry point of faena.exe. Without arguments it is the app: start the local server of the
// window (Servidor) and open the window over it. With arguments it is the CLI. A WinExe has no
// console, so the CLI first attaches to the one that launched it; that way the same exe serves
// both the double-click and `faena hacer ...` from a terminal.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Faena.Agente;

namespace Faena;

internal static class Program
{
    private static Servidor? _servidor;

    [STAThread]
    private static int Main(string[] args)
    {
        // Numbers and dates are formatted the invariant way everywhere (the files are read by
        // other programs); the user-facing text follows Lengua (--lang, FAENA_LANG, Settings), not the culture.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        Paths.ConfigureEnvironment();
        if (args.Length == 0) Lengua.Actual = Ajustes.Idioma(null, Ajustes.Leer());
        return args.Length > 0 ? RunCli(args) : RunWindow();
    }

    // --- app window ----------------------------------------------------------------

    internal static int RunWindow()
    {
        // One window per session: a second double-click just raises the first one.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Faena", out var primera);
        if (!primera)
        {
            ActivarVentanaExistente();
            return 0;
        }

        ApplicationConfiguration.Initialize();

        // The jobs on the web need Playwright's Chromium. The kit brings it in `navegador\`;
        // an installed copy has to download it the first time (or borrows tramoya's), and the
        // user sees why the window is taking its time.
        if (!Browsers.Installed())
        {
            var fallo = InstallDialog.Run();
            if (fallo is null)
                return 1; // closed by the user: no window, no message
            if (fallo.Length > 0)
            {
                MessageBox.Show(fallo + "\n\n" + Lengua.T(
                                "Faena maneja las webs con su propio Chromium y no puede seguir sin él.\n\n" +
                                "Si estás en una red de empresa con proxy, puede que la descarga esté cortada: " +
                                "copia una carpeta «navegador» junto a faena.exe (la del kit de tramoya vale) " +
                                "o pide que dejen pasar la descarga.\n\n" +
                                "Carpeta del navegador: ",
                                "Faena works websites with its own Chromium and cannot go on without it.\n\n" +
                                "On a company network with a proxy the download may be blocked: " +
                                "copy a “navegador” folder next to faena.exe (the one in tramoya's kit works) " +
                                "or ask for the download to be let through.\n\n" +
                                "Browser folder: ") + Paths.Navegador,
                                "Faena", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        string url;
        try
        {
            _servidor = Servidor.Start(Servidor.Opciones());
            url = _servidor.Url;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Lengua.T("No he podido arrancar faena.\n\n", "Could not start faena.\n\n") + ex.Message, "Faena",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        try
        {
            Application.Run(new MainWindow(url));
        }
        finally
        {
            // Closing the window stops the server and every job still running.
            _servidor?.Stop();
        }
        return 0;
    }

    private static void ActivarVentanaExistente()
    {
        var ventana = FindWindow(null, "Faena");
        if (ventana == IntPtr.Zero)
            return;
        ShowWindow(ventana, SW_RESTORE);
        SetForegroundWindow(ventana);
    }

    // --- CLI -----------------------------------------------------------------------

    private static int RunCli(string[] args)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        // Handles inherited from the parent (pipes, files) are captured before attaching
        // and put back afterwards, so `faena ... > out.txt` and `type x | faena ...`
        // keep working whatever AttachConsole does to the std handles.
        var entrada = StreamHeredado(STD_INPUT_HANDLE, FileAccess.Read);
        var salida = StreamHeredado(STD_OUTPUT_HANDLE, FileAccess.Write);
        var errores = StreamHeredado(STD_ERROR_HANDLE, FileAccess.Write);

        if (!AttachConsole(ATTACH_PARENT_PROCESS) && (salida is null || errores is null))
        {
            // Launched without a console (Run dialog, a shortcut): give it one so
            // the user sees the output.
            AllocConsole();
        }

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch (Exception)
        {
            // No console at all: the writers below are UTF-8 anyway.
        }
        if (entrada is not null)
            Console.SetIn(new StreamReader(entrada, utf8));
        Console.SetOut(new StreamWriter(salida ?? Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        Console.SetError(new StreamWriter(errores ?? Console.OpenStandardError(), utf8) { AutoFlush = true });

        return Cli.Run(args);
    }

    /// <summary>A stream over the inherited std handle when it is a pipe or a file; null when it is a console or nothing.</summary>
    private static Stream? StreamHeredado(int which, FileAccess access)
    {
        var handle = GetStdHandle(which);
        if (handle == IntPtr.Zero || handle == INVALID_HANDLE_VALUE)
            return null;
        var tipo = GetFileType(handle);
        if (tipo != FILE_TYPE_PIPE && tipo != FILE_TYPE_DISK)
            return null;
        try
        {
            return new FileStream(new SafeFileHandle(handle, ownsHandle: false), access, 1);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // --- first-run dialog ----------------------------------------------------------

    /// <summary>
    /// «Preparing the browser (first time only)…»: a small window with a marquee bar
    /// and the last progress line while <see cref="Browsers.EnsureAsync"/> downloads Chromium.
    /// <see cref="Run"/> returns "" when the browser is ready, the error text when it is not,
    /// and null when the user closed the dialog.
    /// </summary>
    private sealed class InstallDialog : Form
    {
        /// <summary>How long the dialog waits for a cancelled download to be killed and reaped before giving up on it.</summary>
        private static readonly TimeSpan EsperaAlCancelar = TimeSpan.FromSeconds(15);

        private readonly Label _estado;
        private readonly CancellationTokenSource _cancelar = new();
        private string? _resultado;
        private volatile bool _terminado;
        private bool _cerrando;

        private InstallDialog()
        {
            Text = "Faena";
            Icon = Paths.Icono();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(460, 118);

            var titulo = new Label
            {
                Text = Lengua.T("Preparando el navegador (solo la primera vez)…", "Preparing the browser (first time only)…"),
                AutoSize = false,
                Location = new Point(16, 14),
                Size = new Size(428, 22),
                Font = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold),
            };
            var barra = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Location = new Point(16, 44),
                Size = new Size(428, 20),
            };
            _estado = new Label
            {
                Text = Lengua.T("Comprobando…", "Checking…"),
                AutoSize = false,
                AutoEllipsis = true,
                Location = new Point(16, 76),
                Size = new Size(428, 30),
            };
            Controls.AddRange(new Control[] { titulo, barra, _estado });
            Shown += (_, _) => Empezar();
            FormClosing += (_, e) =>
            {
                if (_terminado || _cerrando)
                    return;
                // Closing while it downloads: the window stays until the driver has been
                // killed and reaped, so RunWindow never returns with a node.exe still
                // downloading behind it. The download task ends (cancelled, or with the
                // message of a driver that would not die) and closes the dialog itself;
                // the timer is the bound, for a task that does not come back in time.
                _cerrando = true;
                e.Cancel = true;
                _estado.Text = Lengua.T("Parando la descarga…", "Stopping the download…");
                UseWaitCursor = true;
                _cancelar.Cancel();
                var limite = new System.Windows.Forms.Timer { Interval = (int)EsperaAlCancelar.TotalMilliseconds };
                limite.Tick += (_, _) =>
                {
                    limite.Stop();
                    if (_terminado)
                        return;
                    _resultado = Lengua.T("No he podido parar la descarga del navegador a tiempo; " +
                                          "si sigue un proceso node.exe, ciérralo desde el Administrador de tareas.",
                                          "Could not stop the browser download in time; " +
                                          "if a node.exe process is still running, close it from the Task Manager.");
                    _terminado = true;
                    Close();
                };
                limite.Start();
            };
        }

        public static string? Run()
        {
            using var dialogo = new InstallDialog();
            Application.Run(dialogo);
            return dialogo._resultado;
        }

        private void Empezar()
        {
            // Progress<T> posts to the message loop that is running now, so the label
            // is updated on the UI thread.
            var progreso = new Progress<string>(linea => _estado.Text = linea);
            Task.Run(async () =>
            {
                try
                {
                    return await Browsers.EnsureAsync(progreso, _cancelar.Token).ConfigureAwait(false) ?? "";
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception e)
                {
                    return Lengua.T("No he podido preparar el navegador: ", "Could not prepare the browser: ") + e.Message;
                }
            }).ContinueWith(t =>
            {
                // Not the thread of the window: the fields are set here and Close is posted
                // to the message loop, where FormClosing sees _terminado and lets it go.
                _resultado = t.Status == TaskStatus.RanToCompletion ? t.Result : Lengua.T("No he podido preparar el navegador.", "Could not prepare the browser.");
                _terminado = true;
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(Close);
            }, TaskScheduler.Default);
        }
    }

    // --- Win32 ---------------------------------------------------------------------

    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;
    private const uint FILE_TYPE_DISK = 0x0001;
    private const uint FILE_TYPE_PIPE = 0x0003;
    private const int SW_RESTORE = 9;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
