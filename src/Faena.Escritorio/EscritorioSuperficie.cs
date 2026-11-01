using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Faena.Agente;

namespace Faena.Escritorio;

/// <summary>
/// Una aplicación de escritorio de Windows como superficie del agente, manejada SOLO por UI
/// Automation: se invocan los patrones de cada control (pulsar, escribir el valor, elegir), nunca
/// el ratón ni el teclado de verdad. Por eso puede ir en un escritorio Win32 oculto
/// (<c>CreateDesktop</c>): la aplicación se abre allí, no se ve, no roba el foco y el gestor de
/// ventanas no se entera. Diseño y límites en <c>windows/ESCRITORIO.md</c>.
/// </summary>
public sealed class EscritorioSuperficie : ISuperficie, IAsyncDisposable
{
    /// <summary>Tope de controles que se le enseñan al modelo, como en la web.</summary>
    public const int MaxElementos = 150;
    public const int MaxTexto = 2000;
    /// <summary>Cuánto se espera a que una pulsación vuelva; un botón que abre un diálogo modal no vuelve hasta que se cierra.</summary>
    public static readonly TimeSpan TopeInvocar = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Las que en Windows 11 son un lanzador de una aplicación de la Tienda: se activan por el
    /// shell y saldrían en el escritorio de verdad, no en el oculto.
    /// </summary>
    public static readonly IReadOnlySet<string> Empaquetadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "notepad", "calc", "mspaint", "snippingtool", "wordpad", "write", "explorer", "ms-settings", "control",
    };

    private readonly IntPtr _escritorio;
    private readonly Process _proceso;
    private readonly string _nombreProceso;
    private Dictionary<int, AutomationElement> _refs = [];
    private AutomationElement? _ultimo;
    private string _titulo = "";
    /// <summary>La ventana principal, por su handle: un diálogo puede llamarse igual que ella.</summary>
    private int _hwndPrincipal;

    public string Tipo => "escritorio";
    /// <summary>Si va en el escritorio oculto.</summary>
    public bool Oculto => _escritorio != IntPtr.Zero;
    public string NombreEscritorio { get; }
    public int Pid => _proceso.Id;
    public string Donde => $"{_nombreProceso} — {_titulo}";

    private EscritorioSuperficie(IntPtr escritorio, string nombreEscritorio, Process proceso, string nombreProceso)
    {
        _escritorio = escritorio;
        NombreEscritorio = nombreEscritorio;
        _proceso = proceso;
        _nombreProceso = nombreProceso;
    }

    /// <summary>
    /// Abre <paramref name="app"/> (ruta o nombre del PATH) y espera a su ventana principal (la que
    /// contenga <paramref name="titulo"/> en el título, si se da). Con <paramref name="oculto"/>, en
    /// un escritorio propio que nadie ve; si no, en el escritorio normal.
    /// </summary>
    public static async Task<EscritorioSuperficie> AbrirAsync(string app, string? argumentos, bool oculto, string? titulo, CancellationToken ct)
    {
        var ruta = Resolver(app);
        var corto = Path.GetFileNameWithoutExtension(ruta);
        if (oculto && (Empaquetadas.Contains(corto) || ruta.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(Lengua.T($"«{corto}» es (o abre) una aplicación de la Tienda: se activaría en el escritorio de verdad, no en el oculto. Usa una aplicación Win32 clásica, o --ver si no te importa que se vea.",
                $"“{corto}” is (or opens) a Store app: it would start on the real desktop, not on the hidden one. Use a classic Win32 application, or --show if you do not mind it being seen."));
        var nombreEscritorio = "";
        var escritorio = IntPtr.Zero;
        Process proceso;
        if (oculto)
        {
            nombreEscritorio = $"faena-{Environment.ProcessId}-{Guid.NewGuid():N}"[..32];
            escritorio = Nativo.CreateDesktop(nombreEscritorio, IntPtr.Zero, IntPtr.Zero, 0, Nativo.DesktopTodo, IntPtr.Zero);
            if (escritorio == IntPtr.Zero)
                throw new InvalidOperationException($"no se ha podido crear el escritorio oculto (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            var si = new Nativo.StartupInfo { cb = System.Runtime.InteropServices.Marshal.SizeOf<Nativo.StartupInfo>(), lpDesktop = @"WinSta0\" + nombreEscritorio };
            var linea = new StringBuilder($"\"{ruta}\"" + (string.IsNullOrWhiteSpace(argumentos) ? "" : " " + argumentos));
            if (!Nativo.CreateProcess(null, linea, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, Path.GetDirectoryName(ruta), ref si, out var pi))
            {
                var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                Nativo.CloseDesktop(escritorio);
                throw new InvalidOperationException(Lengua.T($"no se ha podido abrir «{ruta}» (error {error})", $"could not open “{ruta}” (error {error})"));
            }
            Nativo.CloseHandle(pi.hThread);
            Nativo.CloseHandle(pi.hProcess);
            proceso = Process.GetProcessById((int)pi.dwProcessId);
        }
        else
        {
            proceso = Process.Start(new ProcessStartInfo(ruta, argumentos ?? "") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ruta) ?? "" })
                      ?? throw new InvalidOperationException(Lengua.T($"no se ha podido abrir «{ruta}»", $"could not open “{ruta}”"));
        }
        var sup = new EscritorioSuperficie(escritorio, nombreEscritorio, proceso, corto);
        try
        {
            var reloj = Stopwatch.StartNew();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var ventanas = await sup.EnEscritorioAsync(() => sup.Ventanas().Select(v => (v.Current.Name, v.Current.NativeWindowHandle)).ToList()).ConfigureAwait(false);
                var principal = ventanas.FirstOrDefault(t => titulo is null || t.Name.Contains(titulo, StringComparison.OrdinalIgnoreCase));
                if (principal.Name is not null) { (sup._titulo, sup._hwndPrincipal) = principal; break; }
                if (sup._proceso.HasExited && !sup.Oculto)
                    throw new InvalidOperationException(Lengua.T($"«{corto}» se ha cerrado sin abrir ventana (¿lanza otra aplicación?)", $"“{corto}” closed without opening a window (does it launch another application?)"));
                if (reloj.Elapsed > TimeSpan.FromSeconds(20))
                    throw new TimeoutException(sup._proceso.HasExited
                        ? Lengua.T($"«{corto}» se ha cerrado sin abrir ventana en el escritorio oculto: probablemente delega en una aplicación de la Tienda.",
                                   $"“{corto}” closed without opening a window on the hidden desktop: it probably hands over to a Store app.")
                        : Lengua.T($"«{corto}» no ha abierto ninguna ventana{(titulo is null ? "" : $" con «{titulo}» en el título")} en 20 s",
                                   $"“{corto}” has not opened any window{(titulo is null ? "" : $" with “{titulo}” in its title")} in 20 s"));
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            try { sup._proceso.WaitForInputIdle(3000); } catch (InvalidOperationException) { }
            return sup;
        }
        catch
        {
            await sup.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>La ruta del ejecutable: tal cual si existe, si no buscando en el PATH (con o sin .exe).</summary>
    internal static string Resolver(string app)
    {
        if (File.Exists(app)) return Path.GetFullPath(app);
        var nombres = Path.HasExtension(app) ? [app] : new[] { app + ".exe", app };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in nombres)
            {
                string c;
                try { c = Path.Combine(dir.Trim('"'), n); } catch (ArgumentException) { continue; }
                if (File.Exists(c)) return c;
            }
        throw new FileNotFoundException(Lengua.T($"no encuentro la aplicación «{app}»", $"cannot find the application “{app}”"));
    }

    // ------------------------------------------------------------------ hilos en el escritorio

    /// <summary>
    /// Ejecuta <paramref name="f"/> en un hilo nuevo (MTA) que antes de nada se pasa al escritorio
    /// oculto: UI Automation solo ve las ventanas del escritorio de su hilo, y
    /// <c>SetThreadDesktop</c> falla si el hilo ya tiene ventanas (un hilo STA de COM las tiene).
    /// Con <paramref name="tope"/>, si no ha vuelto a tiempo se da por hecho y se sigue (el hilo
    /// acaba solo cuando el control vuelve).
    /// </summary>
    private Task<T> EnEscritorioAsync<T>(Func<T> f, TimeSpan? tope = null, T? siTarda = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hilo = new Thread(() =>
        {
            try
            {
                if (_escritorio != IntPtr.Zero && !Nativo.SetThreadDesktop(_escritorio))
                    throw new InvalidOperationException($"no se puede entrar en el escritorio oculto (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
                tcs.TrySetResult(f());
            }
            catch (Exception e) { tcs.TrySetException(e); }
        }) { IsBackground = true, Name = "faena-uia" };
        hilo.SetApartmentState(ApartmentState.MTA);
        hilo.Start();
        if (tope is not { } t) return tcs.Task;
        return Esperar();
        async Task<T> Esperar()
        {
            var gana = await Task.WhenAny(tcs.Task, Task.Delay(t)).ConfigureAwait(false);
            return gana == tcs.Task ? await tcs.Task.ConfigureAwait(false) : siTarda!;
        }
    }

    private Task EnEscritorioAsync(Action f, TimeSpan? tope = null) => EnEscritorioAsync(() => { f(); return true; }, tope, true);

    /// <summary>
    /// Las ventanas de nivel superior de la aplicación, los diálogos primero y la principal al
    /// final. En el escritorio oculto son todas las que haya (allí solo está esta aplicación, y así
    /// cuentan también las de un proceso hijo); en el normal, las de su proceso.
    /// </summary>
    private List<AutomationElement> Ventanas()
    {
        var hijos = Oculto
            ? AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition)
            : AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, _proceso.Id));
        var lista = new List<AutomationElement>();
        foreach (AutomationElement v in hijos)
        {
            try
            {
                // Las ventanas sin nombre son las de servicio de cada framework (la «parking window» de WinForms, por ejemplo).
                if (v.Current.Name.Length == 0 || v.Current.IsOffscreen && v.Current.BoundingRectangle.IsEmpty) continue;
                lista.Add(v);
            }
            catch (ElementNotAvailableException) { }
        }
        // Los diálogos de una ventana (un «¿Seguro?», un «Guardar como») cuelgan de ella en el árbol
        // de UI Automation, no del escritorio: se sacan aparte.
        var anidadas = new List<AutomationElement>();
        foreach (var v in lista)
        {
            try { anidadas.AddRange(v.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)).Cast<AutomationElement>()); }
            catch (ElementNotAvailableException) { }
        }
        // Los diálogos primero y la principal (la que se abrió) al final: un diálogo abierto es lo que manda.
        return anidadas.Concat(lista.OrderBy(v => EsPrincipal(v) ? 1 : 0)).ToList();
    }

    private bool EsPrincipal(AutomationElement v)
    {
        try { return _hwndPrincipal != 0 ? v.Current.NativeWindowHandle == _hwndPrincipal : Nombre(v) == _titulo; }
        catch (ElementNotAvailableException) { return false; }
    }

    // ------------------------------------------------------------------ ISuperficie

    public Task<string> TituloAsync() => Task.FromResult(_titulo);

    public async Task<Observation> ObservarAsync(CancellationToken ct)
    {
        var (obs, refs) = await EnEscritorioAsync(Observar).ConfigureAwait(false);
        _refs = refs;
        return obs;
    }

    private (Observation, Dictionary<int, AutomationElement>) Observar()
    {
        var ventanas = Ventanas();
        if (ventanas.Count == 0) throw new InvalidOperationException(Lengua.T("la aplicación no tiene ninguna ventana abierta", "the application has no open window"));
        var obs = new Observation();
        var refs = new Dictionary<int, AutomationElement>();
        var texto = new StringBuilder();
        var principal = ventanas.FirstOrDefault(EsPrincipal) ?? ventanas[^1];
        var tituloPrincipal = Nombre(principal);
        if (tituloPrincipal.Length != 0) _titulo = tituloPrincipal;
        obs.Title = EsPrincipal(ventanas[0]) ? _titulo : $"{Nombre(ventanas[0])} (over {_titulo})";
        obs.Url = $"{_nombreProceso} — {obs.Title}";
        foreach (var ventana in ventanas)
        {
            var etiqueta = ventanas.Count == 1 ? "" : EsPrincipal(ventana) ? $"main window “{Nombre(ventana)}”" : $"dialog “{Nombre(ventana)}” (in front)";
            if (texto.Length < MaxTexto) texto.Append(Nombre(ventana)).Append(": ");
            Recorrer(ventana, 0, (el, corto) =>
            {
                obs.Total++;
                if (obs.Elements.Count >= MaxElementos) return;
                var n = obs.Elements.Count + 1;
                refs[n] = el;
                obs.Elements.Add(new ObservedElement(n, etiqueta, TiposUia.Tipo(corto)!, Recortar(Nombre(el), 60), Recortar(Valor(el), 60), Estado(el)));
            }, t => { if (texto.Length < MaxTexto) texto.Append(t).Append(' '); });
            texto.Append('\n');
        }
        obs.Text = Recortar(texto.ToString().Trim(), MaxTexto);
        return (obs, refs);
    }

    /// <summary>
    /// Recorre la vista de controles: los interactivos van a <paramref name="control"/>, los textos
    /// (y el contenido de los documentos) a <paramref name="textoVisto"/>. La barra de título se
    /// salta: minimizar, maximizar y el menú del sistema no son del programa.
    /// </summary>
    private static void Recorrer(AutomationElement padre, int nivel, Action<AutomationElement, string> control, Action<string> textoVisto)
    {
        if (nivel > 25) return;
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? hijo;
        try { hijo = walker.GetFirstChild(padre); }
        catch (ElementNotAvailableException) { return; }
        while (hijo is not null)
        {
            try
            {
                var corto = TiposUia.Corto(hijo.Current.ControlType.ProgrammaticName);
                if (corto is not "TitleBar" and not "Window")
                {
                    if (TiposUia.Tipo(corto) is not null) control(hijo, corto);
                    if (corto is "Text" or "Header" or "StatusBar" && hijo.Current.Name.Length != 0) textoVisto(hijo.Current.Name);
                    if (corto is "Document" && TextoDe(hijo) is { Length: > 0 } doc) textoVisto(doc);
                    // Los controles con hijos propios (listas, árboles, menús, paneles) se recorren; un campo de texto no.
                    if (corto is not "Edit" and not "Document") Recorrer(hijo, nivel + 1, control, textoVisto);
                }
                hijo = walker.GetNextSibling(hijo);
            }
            catch (ElementNotAvailableException) { break; }
        }
    }

    public async Task<JsonObject?> ActuarAsync(Observation obs, Decision d, CancellationToken ct)
    {
        if (d.Accion == "ir") throw new InvalidOperationException("a desktop application has no urls: use the controls of the window");
        if (d.Accion == "tecla")
        {
            var tecla = TeclaDe(d.Valor);
            await EnEscritorioAsync(() => MandarTecla(tecla)).ConfigureAwait(false);
            return new JsonObject { ["tipo"] = "tecla", ["valor"] = tecla, ["desc"] = Lengua.Tecla(tecla) };
        }
        if (d.Ref is not int n || !_refs.TryGetValue(n, out var el) || obs.Elemento(n) is not { } visto) return null;
        var selector = await EnEscritorioAsync(() => SelectorDe(el)).ConfigureAwait(false);
        var cosa = Lengua.Cosa(visto.Tipo, visto.Nombre);
        await Hacer(el, d.Accion, d.Valor).ConfigureAwait(false);
        _ultimo = el;
        return d.Accion switch
        {
            "clic" => new JsonObject
            {
                ["tipo"] = "clic", ["selector"] = selector,
                ["desc"] = Lengua.Clic(cosa, visto.Tipo == "la casilla" ? (visto.Estado.Contains("marcado") ? "off" : "on") : ""),
                ["guarda"] = Guardian.Categoria(visto.Nombre) is not null,
            },
            "señalar" => new JsonObject { ["tipo"] = "señalar", ["selector"] = selector, ["desc"] = Lengua.Senala(cosa) },
            "escribir" => new JsonObject { ["tipo"] = "escribir", ["selector"] = selector, ["valor"] = d.Valor, ["desc"] = Lengua.Escribe(d.Valor, visto.Nombre) },
            "elegir" => new JsonObject { ["tipo"] = "elegir", ["selector"] = selector, ["valor"] = d.Valor, ["desc"] = Lengua.Elige(d.Valor, visto.Nombre) },
            _ => throw new ArgumentException($"unknown action “{d.Accion}”"),
        };
    }

    public async Task RepetirAsync(JsonObject paso, CancellationToken ct)
    {
        var tipo = Str(paso, "tipo");
        var valor = Str(paso, "valor");
        var opcional = paso["opcional"] is JsonValue o && o.TryGetValue<bool>(out var b) && b;
        if (tipo == "esperar") { await Task.Delay(1000, ct).ConfigureAwait(false); return; }
        if (tipo == "tecla") { await EnEscritorioAsync(() => MandarTecla(TeclaDe(valor))).ConfigureAwait(false); return; }
        if (tipo == "ir") throw new InvalidOperationException(Lengua.T("un paso «ir» no vale en una aplicación de escritorio", "a “goto” step does not work in a desktop application"));
        var selector = SelectorUia.Parse(Str(paso, "selector"));
        AutomationElement? el = null;
        var reloj = Stopwatch.StartNew();
        var tope = TimeSpan.FromSeconds(opcional ? 3 : 10);
        while (el is null && reloj.Elapsed < tope)
        {
            el = await EnEscritorioAsync(() => Buscar(selector)).ConfigureAwait(false);
            if (el is null) await Task.Delay(250, ct).ConfigureAwait(false);
        }
        if (el is null)
        {
            if (opcional) return;
            throw new InvalidOperationException($"no encuentro el control {selector}");
        }
        await Hacer(el, tipo, valor).ConfigureAwait(false);
        _ultimo = el;
    }

    public async Task AsentarAsync()
    {
        try { if (!_proceso.HasExited) _proceso.WaitForInputIdle(2000); }
        catch (InvalidOperationException) { }
        await Task.Delay(300).ConfigureAwait(false);
    }

    /// <summary>
    /// La ventana de delante pintada con <c>PrintWindow</c>. En el escritorio oculto no hay
    /// composición: con las aplicaciones Win32 clásicas sale bien, con las que pintan por DirectX
    /// puede salir en negro.
    /// </summary>
    public async Task<byte[]?> CapturaAsync()
    {
        try
        {
            return await EnEscritorioAsync(() =>
            {
                var ventana = Ventanas().FirstOrDefault();
                if (ventana is null) return null;
                var hwnd = new IntPtr(ventana.Current.NativeWindowHandle);
                if (hwnd == IntPtr.Zero || !Nativo.GetWindowRect(hwnd, out var r)) return null;
                int ancho = r.Right - r.Left, alto = r.Bottom - r.Top;
                if (ancho <= 0 || alto <= 0 || ancho > 8000 || alto > 8000) return null;
                using var bmp = new Bitmap(ancho, alto, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    var hdc = g.GetHdc();
                    try { if (!Nativo.PrintWindow(hwnd, hdc, Nativo.PwRenderFullContent)) Nativo.PrintWindow(hwnd, hdc, 0); }
                    finally { g.ReleaseHdc(hdc); }
                }
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }).ConfigureAwait(false);
        }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ actuar sobre un control

    /// <summary>La acción sobre el control, por sus patrones. Pulsar puede no volver (abre un modal): se da por hecha a los 3 s.</summary>
    private Task Hacer(AutomationElement el, string accion, string valor) => accion switch
    {
        "clic" => EnEscritorioAsync(() => Pulsar(el), TopeInvocar),
        "escribir" => EnEscritorioAsync(() => Escribir(el, valor)),
        "elegir" => EnEscritorioAsync(() => Elegir(el, valor)),
        "señalar" => EnEscritorioAsync(() => { _ = el.Current.Name; }),
        _ => throw new ArgumentException($"unknown action “{accion}”"),
    };

    private static void Pulsar(AutomationElement el)
    {
        if (!el.Current.IsEnabled) throw new InvalidOperationException(Lengua.T("el control está deshabilitado", "the control is disabled"));
        // Un botón Win32 de verdad (también los de WinForms y los de los cuadros de diálogo): se le
        // dice a su ventana padre que lo han pulsado, por mensaje. Es lo que hace el propio botón al
        // soltar el ratón, no necesita foco y vuelve enseguida; el Invoke de UI Automation, en
        // cambio, se queda dentro de la aplicación si el botón abre un diálogo modal, y mientras
        // tanto la aplicación no contesta a nada más.
        if (TiposUia.Corto(el.Current.ControlType.ProgrammaticName) == "Button" && el.Current.NativeWindowHandle is var h && h != 0)
        {
            var hwnd = new IntPtr(h);
            var padre = Nativo.GetParent(hwnd);
            if (padre != IntPtr.Zero && Nativo.Clase(hwnd).Contains("BUTTON", StringComparison.OrdinalIgnoreCase)
                && Nativo.PostMessage(padre, Nativo.WmCommand, (nint)((Nativo.BnClicked << 16) | (Nativo.GetDlgCtrlID(hwnd) & 0xFFFF)), hwnd))
                return;
        }
        if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec) && TiposUia.Corto(el.Current.ControlType.ProgrammaticName) is "MenuItem" or "ComboBox" or "SplitButton" or "TreeItem"
            && ((ExpandCollapsePattern)ec).Current.ExpandCollapseState != ExpandCollapseState.LeafNode)
        {
            var p = (ExpandCollapsePattern)ec;
            if (p.Current.ExpandCollapseState == ExpandCollapseState.Expanded) p.Collapse(); else p.Expand();
            return;
        }
        if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var inv)) { ((InvokePattern)inv).Invoke(); return; }
        if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tog)) { ((TogglePattern)tog).Toggle(); return; }
        if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel)) { ((SelectionItemPattern)sel).Select(); return; }
        if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out ec)) { ((ExpandCollapsePattern)ec).Expand(); return; }
        throw new InvalidOperationException(Lengua.T("este control no se puede pulsar sin ratón (no tiene Invoke, Toggle, SelectionItem ni ExpandCollapse)", "this control cannot be clicked without a mouse (it has no Invoke, Toggle, SelectionItem or ExpandCollapse)"));
    }

    private static void Escribir(AutomationElement el, string valor)
    {
        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var v))
        {
            var p = (ValuePattern)v;
            if (p.Current.IsReadOnly) throw new InvalidOperationException("el campo es de solo lectura");
            p.SetValue(valor);
            return;
        }
        throw new InvalidOperationException("este control no deja escribir por UI Automation (no tiene ValuePattern)");
    }

    private static void Elegir(AutomationElement el, string valor)
    {
        var expandido = false;
        if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec))
        {
            try { ((ExpandCollapsePattern)ec).Expand(); expandido = true; } catch (InvalidOperationException) { }
        }
        var opciones = el.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        AutomationElement? elegida = null;
        foreach (AutomationElement o in opciones)
            if (string.Equals(o.Current.Name.Trim(), valor.Trim(), StringComparison.OrdinalIgnoreCase)) { elegida = o; break; }
        if (elegida is null)
        {
            // Un desplegable editable acepta el texto tal cual.
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var v) && !((ValuePattern)v).Current.IsReadOnly)
            {
                ((ValuePattern)v).SetValue(valor);
                return;
            }
            throw new InvalidOperationException(Lengua.T($"no hay ninguna opción «{valor}»", $"there is no option “{valor}”"));
        }
        ((SelectionItemPattern)elegida.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        if (expandido)
        {
            try { ((ExpandCollapsePattern)ec!).Collapse(); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>
    /// Una tecla por mensaje (<c>WM_KEYDOWN</c> + <c>WM_CHAR</c> + <c>WM_KEYUP</c>) a la ventana del
    /// último control tocado, o a la de delante. Sin teclado de verdad no hay modificadores:
    /// Ctrl+S y compañía no llegan; para eso, la opción del menú.
    /// </summary>
    private void MandarTecla(string tecla)
    {
        if (tecla.Contains('+')) throw new InvalidOperationException(Lengua.T($"las combinaciones ({tecla}) no se pueden mandar sin teclado de verdad: usa la opción del menú", $"key combinations ({tecla}) cannot be sent without a real keyboard: use the menu option"));
        var codigo = Nativo.Tecla(tecla) ?? (tecla.Length == 1 ? ((int)char.ToUpperInvariant(tecla[0]), tecla[0]) : throw new InvalidOperationException(Lengua.T($"tecla desconocida «{tecla}»", $"unknown key “{tecla}”")));
        IntPtr hwnd = IntPtr.Zero;
        for (var e = _ultimo; e is not null && hwnd == IntPtr.Zero;)
        {
            try { hwnd = new IntPtr(e.Current.NativeWindowHandle); e = hwnd == IntPtr.Zero ? TreeWalker.ControlViewWalker.GetParent(e) : e; }
            catch (ElementNotAvailableException) { break; }
        }
        // Si hay un diálogo encima, la tecla es para él.
        var delante = Ventanas().FirstOrDefault();
        if (delante is not null && (hwnd == IntPtr.Zero || !EsPrincipal(delante) && !EsDe(_ultimo, delante)))
            hwnd = new IntPtr(delante.Current.NativeWindowHandle);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("no hay ninguna ventana a la que mandar la tecla");
        Nativo.PostMessage(hwnd, Nativo.WmKeyDown, codigo.Vk, 1);
        if (codigo.Caracter is { } c) Nativo.PostMessage(hwnd, Nativo.WmChar, c, 1);
        Nativo.PostMessage(hwnd, Nativo.WmKeyUp, codigo.Vk, unchecked((nint)0xC0000001));
    }

    private static bool EsDe(AutomationElement? el, AutomationElement ventana)
    {
        for (var e = el; e is not null;)
        {
            try
            {
                if (Automation.Compare(e, ventana)) return true;
                e = TreeWalker.ControlViewWalker.GetParent(e);
            }
            catch (ElementNotAvailableException) { return false; }
        }
        return false;
    }

    private static string TeclaDe(string valor) => valor.Trim().ToLowerInvariant() switch
    {
        "enter" or "intro" or "return" => "Enter",
        "tab" or "tabulador" => "Tab",
        "escape" or "esc" => "Escape",
        "backspace" or "borrar" => "Backspace",
        "arrowdown" or "down" or "abajo" or "flecha abajo" => "ArrowDown",
        "arrowup" or "up" or "arriba" or "flecha arriba" => "ArrowUp",
        "space" or "espacio" => "Space",
        "pagedown" or "avpág" => "PageDown",
        "pageup" or "repág" => "PageUp",
        _ => valor.Trim(),
    };

    // ------------------------------------------------------------------ selectores

    /// <summary>El selector duradero: el AutomationId si es fiable, si no el nombre; con la ventana si es un diálogo y el orden si hay gemelos.</summary>
    private string SelectorDe(AutomationElement el)
    {
        var corto = TiposUia.Corto(el.Current.ControlType.ProgrammaticName);
        var id = el.Current.AutomationId;
        var ventana = VentanaDe(el);
        var nombreVentana = ventana is not null && !EsPrincipal(ventana) ? Nombre(ventana) : null;
        var s = SelectorUia.IdDuradero(id)
            ? new SelectorUia(corto, Id: id, Ventana: nombreVentana)
            : new SelectorUia(corto, Nombre: Nombre(el), Ventana: nombreVentana);
        var iguales = Coincidencias(s);
        var n = iguales.FindIndex(x => Automation.Compare(x, el));
        return (n > 0 ? s with { N = n + 1 } : s).ToString();
    }

    private AutomationElement? VentanaDe(AutomationElement el)
    {
        var ventanas = Ventanas();
        for (var e = el; e is not null;)
        {
            try
            {
                if (ventanas.FirstOrDefault(v => Automation.Compare(v, e)) is { } v) return v;
                e = TreeWalker.ControlViewWalker.GetParent(e);
            }
            catch (ElementNotAvailableException) { return null; }
        }
        return null;
    }

    private List<AutomationElement> Coincidencias(SelectorUia s)
    {
        var lista = new List<AutomationElement>();
        // Sin «ventana», el control es de la principal (si no aparece allí, de cualquiera); con ella, de ese diálogo.
        var ventanas = Ventanas();
        var donde = s.Ventana is { } w
            ? ventanas.Where(v => Nombre(v) == w).ToList()
            : ventanas.Where(EsPrincipal).Concat(ventanas.Where(v => !EsPrincipal(v))).ToList();
        foreach (var ventana in donde)
        {
            // La primera ventana que tenga alguno: la principal contiene también los controles de sus diálogos.
            if (lista.Count != 0) break;
            var condiciones = new List<Condition>();
            if (s.Id is { } id) condiciones.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            if (s.Nombre is { } nombre) condiciones.Add(new PropertyCondition(AutomationElement.NameProperty, nombre));
            Condition cond = condiciones.Count == 1 ? condiciones[0] : new AndCondition(condiciones.ToArray());
            foreach (AutomationElement e in ventana.FindAll(TreeScope.Descendants, cond))
            {
                try
                {
                    if (s.Tipo == "*" || TiposUia.Corto(e.Current.ControlType.ProgrammaticName) == s.Tipo) lista.Add(e);
                }
                catch (ElementNotAvailableException) { }
            }
        }
        return lista;
    }

    private AutomationElement? Buscar(SelectorUia s)
    {
        var lista = Coincidencias(s);
        return lista.Count >= s.N ? lista[s.N - 1] : null;
    }

    // ------------------------------------------------------------------ lectura

    private static string Nombre(AutomationElement el)
    {
        try { return (el.Current.Name ?? "").Replace('\n', ' ').Trim(); }
        catch (ElementNotAvailableException) { return ""; }
    }

    private static string Valor(AutomationElement el)
    {
        try
        {
            if (el.Current.IsPassword) return "••••";
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var v)) return ((ValuePattern)v).Current.Value ?? "";
            if (el.TryGetCurrentPattern(SelectionPattern.Pattern, out var s))
                return string.Join(", ", ((SelectionPattern)s).Current.GetSelection().Select(x => x.Current.Name));
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException) { }
        return "";
    }

    private static string Estado(AutomationElement el)
    {
        var s = new List<string>();
        try
        {
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var t) && ((TogglePattern)t).Current.ToggleState == ToggleState.On) s.Add("marcado");
            if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec) && ((ExpandCollapsePattern)ec).Current.ExpandCollapseState == ExpandCollapseState.Expanded) s.Add("abierto");
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var si) && ((SelectionItemPattern)si).Current.IsSelected) s.Add("seleccionado");
            if (!el.Current.IsEnabled) s.Add("deshabilitado");
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException) { }
        return string.Join(", ", s);
    }

    private static string? TextoDe(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(TextPattern.Pattern, out var t)) return ((TextPattern)t).DocumentRange.GetText(MaxTexto);
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var v)) return ((ValuePattern)v).Current.Value;
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException) { }
        return null;
    }

    private static string Recortar(string s, int n) => s.Length <= n ? s : s[..n];

    private static string Str(JsonObject o, string clave) => o[clave] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[clave]?.ToString() ?? "";

    // ------------------------------------------------------------------ cerrar

    /// <summary>Cierra la aplicación (por su ventana; si a los 3 s sigue, se mata con sus hijos) y el escritorio oculto.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_proceso.HasExited)
            {
                try
                {
                    await EnEscritorioAsync(() =>
                    {
                        foreach (var v in Ventanas())
                            if (v.TryGetCurrentPattern(WindowPattern.Pattern, out var w)) ((WindowPattern)w).Close();
                    }, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception) { /* se mata abajo */ }
                if (!_proceso.WaitForExit(3000)) _proceso.Kill(entireProcessTree: true);
            }
        }
        catch (Exception) { /* ya estaba cerrada */ }
        finally
        {
            _proceso.Dispose();
            if (_escritorio != IntPtr.Zero) Nativo.CloseDesktop(_escritorio);
        }
    }
}
