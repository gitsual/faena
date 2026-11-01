// The window of faena: a WebView2 over the local server (1180x820, Spanish, no translate bar,
// its own user-data dir). Closing it ends the program and Program stops the server and the
// jobs; nothing else lives in this class.
using System.Diagnostics;
using Faena.Agente;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Faena;

internal sealed class MainWindow : Form
{
    private const string WebView2Download = "https://developer.microsoft.com/microsoft-edge/webview2/";

    private readonly string _url;
    private readonly WebView2 _web;

    public MainWindow(string url)
    {
        _url = url;
        Text = "Faena";
        Size = new Size(1180, 820);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterScreen;
        if (Paths.Icono() is { } icono)
            Icon = icono;

        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            await IniciarAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // Windows 11 ships the runtime; this is for older or trimmed installs.
            var respuesta = MessageBox.Show(this,
                Lengua.T("Faena necesita el componente «WebView2» de Microsoft Edge y no está instalado "
                         + "en este equipo.\n\n¿Quieres abrir la página de descarga de Microsoft?",
                         "Faena needs Microsoft Edge's “WebView2” component and it is not installed "
                         + "on this computer.\n\nOpen Microsoft's download page?"),
                "Faena", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (respuesta == DialogResult.Yes)
                AbrirFuera(WebView2Download);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Lengua.T("No he podido abrir la ventana de faena.\n\n", "Could not open faena's window.\n\n") + ex.Message,
                "Faena", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async Task IniciarAsync()
    {
        var opciones = new CoreWebView2EnvironmentOptions
        {
            Language = "es",
            // La ventana solo habla con 127.0.0.1. En los portátiles de empresa la política de
            // Chromium impone un proxy que deja esa dirección en blanco, así que aquí nunca hay proxy.
            AdditionalBrowserArguments = "--lang=es --disable-features=Translate --no-proxy-server",
        };
        Directory.CreateDirectory(Paths.Ventana);
        var entorno = await CoreWebView2Environment.CreateAsync(null, Paths.Ventana, opciones);
        await _web.EnsureCoreWebView2Async(entorno);

        var core = _web.CoreWebView2;
        var depurar = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FAENA_DEBUG"));
        var ajustes = core.Settings;
        ajustes.AreDefaultContextMenusEnabled = depurar;
        ajustes.AreDevToolsEnabled = depurar;
        ajustes.IsStatusBarEnabled = false;
        ajustes.IsZoomControlEnabled = false;
        ajustes.IsGeneralAutofillEnabled = false;
        ajustes.IsPasswordAutosaveEnabled = false;

        // Links that want a new tab (target=_blank, window.open) belong in the
        // user's browser, not in this app window.
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            AbrirFuera(args.Uri);
        };

        core.Navigate(_url);
    }

    private static void AbrirFuera(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No default browser: nothing sensible to do from here.
        }
    }
}
