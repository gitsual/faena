*[Leer en español](ESCRITORIO.es.md)*

# Desktop applications (`Faena.Escritorio`)

The faena agent also drives Windows desktop applications, not just websites. For that there is a second
surface, `EscritorioSuperficie`, next to the web one (`WebSuperficie`). Both implement `ISuperficie`
(`src/Faena.Agente/Superficie.cs`), so the agent loop, the guard against irreversible actions, skills and
reports are the same.

From the command line: `faena do "<goal>" --app <program.exe> [--args "..."] [--window-title <title>]`
(see [AGENTE-LOCAL.md](AGENTE-LOCAL.md)).

## How to use it (API)

```csharp
await using var sup = await EscritorioSuperficie.AbrirAsync(
    app: @"C:\path\MyApp.exe",   // or a name on the PATH: "charmap"
    argumentos: null,
    oculto: true,                // on a Win32 desktop of its own that nobody sees (the default)
    titulo: "Notes",             // part of the main window's title; null = the first one that appears
    ct);
var r = await Encargo.RunAsync(sup, new AgentOptions("Add a note 'Bread'") { Modo = Modo.Tarea },
                               habilidades, null, backend, log, ct);
// When the using block ends: the app is closed (WindowPattern.Close; if it is still there after 3 s, the process tree is killed) and so is the desktop.
```

`sup.Tipo == "escritorio"`, `sup.Oculto`, `sup.NombreEscritorio`, `sup.Pid`. Errors when opening:
`FileNotFoundException` (it does not exist), `InvalidOperationException` (a Store app in hidden mode, or the
desktop could not be created), `TimeoutException` (no window opens within 20 s).

## Why a hidden desktop

The agent works in the background: it cannot move the mouse, really type or bring windows to the front
(it would steal them from the person, and GlazeWM would rearrange them). So:

1. `CreateDesktop` creates a new Win32 desktop in the `WinSta0` window station, which is never activated.
2. The app is launched with `CreateProcess` and `STARTUPINFO.lpDesktop = "WinSta0\\faena-..."`: its
   windows are born there and do not appear on the person's desktop or in the window manager.
3. Each UI Automation call runs on a new MTA thread whose first step is `SetThreadDesktop`: UIA only sees
   the windows of its thread's desktop. It has to be a thread with no windows, so a COM STA thread will not
   do, because those already have one and `SetThreadDesktop` returns error 170 (busy).
4. Every action goes through **UIA patterns**, with no mouse and no `SendInput`, which would go to the
   input desktop (the real one).

Tested on 2026-10-28 on Windows 11 (26200) with the WinForms example app: it observes, types, presses,
opens and answers a modal dialog, and the screenshot (`PrintWindow`) comes out fine. Meanwhile nothing
appeared on the real desktop.

## What the model sees

The top-level windows of the hidden desktop (in visible mode, those of the process) and the **dialogs**.
UIA hangs a dialog (a `MessageBox`, a "Save as") from its owner window, not from the desktop, so they are
looked for inside each window and come first, labelled `[marco: diálogo «...» (delante)]`. The main window
is recognized by its HWND, because a dialog can have the same name.

The control view is walked (`ControlViewWalker`, depth ≤ 25, at most 150) **without the title bar**:
minimize, maximize, close and the system menu do not belong to the program. The control types it acts on
are named as in the web (in the model's text): Button/SplitButton "the button", Edit "the field", Document
"the document", MenuItem "the menu option", ComboBox "the drop-down", CheckBox "the checkbox", RadioButton
"the option", ListItem "the list item", TabItem "the tab", TreeItem "the item", Hyperlink "the link",
DataItem "the row", Slider, Spinner. Value = `ValuePattern` (passwords as `••••`) or the selection; state =
checked / expanded / selected / disabled. The "visible text" is the title of each window, the
`Text`/`Header`/`StatusBar` controls and the content of `Document` controls (`TextPattern`), up to 2000
characters. The observation's "url" is `process — title`.

## Actions

| action | how |
|---|---|
| clic | Win32 button with an HWND (including WinForms and dialog buttons): `PostMessage(parent, WM_COMMAND, BN_CLICKED\|id, hwnd)`. Otherwise: `ExpandCollapse` (menus, drop-downs, trees), `Invoke`, `Toggle`, `SelectionItem.Select`. The press has a 3 s limit: if it does not return, it is taken as done. |
| escribir | `ValuePattern.SetValue` (replaces). Without `ValuePattern` → an explained error. |
| elegir | `ExpandCollapse.Expand` on the drop-down, `SelectionItem.Select` on the `ListItem` with that name (case-insensitive); if it is editable and there is no such option, `SetValue`. |
| tecla | `WM_KEYDOWN`/`WM_CHAR`/`WM_KEYUP` through `PostMessage` to the window of the last control touched (or to the dialog in front). Enter, Tab, Escape, arrows, Page Up/Down, Home/End, Delete, Backspace, Space and single characters. |
| señalar | only checks that the control is there (there is no cursor to move). |
| ir | does not exist: there are no urls. |

**Why buttons do not go through `Invoke`:** the WinForms UIA provider (and the MSAA one in general) runs
`Invoke` synchronously inside the app. If the button opens a modal dialog, the call does not return until
the dialog is closed, and meanwhile the app does not answer any other UIA request, so the next observation
hangs until the UIA timeout (~80 s). `WM_COMMAND` is exactly what the button itself sends to its parent
when released: it is asynchronous and needs no focus.

## Durable selector

The recorded steps (and those of skills) use the usual step format (the one of tramoya projects) with a
selector of their own:

```
uia=<ControlType>[ventana="<dialog title>"][id="<AutomationId>"|name="<name>"][n="<k>"]
```

- `id`: the `AutomationId` if it is reliable (not empty, not a number of 5+ digits, not a runtime id like
  `42.1.2.3`); otherwise, `name`. One of the two is written.
- `ventana`: only when the control is in a dialog; without it the main window is searched first.
- `n`: which of the matches (from 1), if there are twins.
- `<ControlType>` without the `ControlType.` prefix (`Button`, `Edit`...) or `*`.
- Examples: `uia=Edit[id="titulo"]`, `uia=Button[name="Añadir"]`, `uia=Button[ventana="Notas"][name="Sí"]`.

`RepetirAsync` resolves it (retries for up to 10 s, or 3 s if the step is `opcional`) and performs the same
action. `SelectorUia.Parse`/`ToString` round-trip.

## What does not work (or only partly)

- **Store / packaged apps** (Notepad, Calculator, Paint and Snipping Tool on Windows 11): the System32
  executable is a launcher that activates the app through the shell, **on the real desktop**. In hidden
  mode they are rejected up front (`notepad`, `calc`, `mspaint`, `snippingtool`, `wordpad`, `write`,
  `explorer`, `control`, and any path under `\WindowsApps\`). With `oculto: false` they can be used, but
  they are visible. If an unknown app hands off to another one, `AbrirAsync` throws `TimeoutException` ("it
  closed without opening a window") and something may have appeared on the real desktop: there is no way to
  know beforehand.
- **Key combinations** (Ctrl+S, Alt+F4): without a real keyboard there are no modifiers; they are rejected
  with a message asking to use the menu option.
- **Keys in general**: they go by message and are seen by the window they are sent to; apps that read the
  keyboard with `GetKeyState`/raw input, or that are not classic Win32, may not notice.
- **Controls without patterns** (hand-drawn, games, canvases): they cannot be pressed without a mouse.
- **Screenshots**: `PrintWindow(PW_RENDERFULLCONTENT)`. On the hidden desktop there is no composition; with
  WinForms/Win32 they come out fine (tested); apps that paint through DirectX/WinUI may come out black.
- **Win32 pop-up menus** (`#32768`) on the hidden desktop: not tested.
- **Elevation**: an app that asks for UAC cannot be opened on the hidden desktop (the UAC dialog goes to the
  secure desktop).
- **Session**: an unlocked interactive session in the `WinSta0` station is required. From a service or
  session 0 there is no desktop in which to create the hidden one.

## Tests

- `tests/Faena.Tests/DesktopTests.cs`: selector, types, rejection of Store apps and prompt, always. With
  `FAENA_DESKTOP_TESTS=1`, the notes app in `examples/escritorio/NotasEscritorio` (built automatically if
  missing) on a hidden desktop: observe, type, press, replay by selector, the "Borrar todo" modal dialog
  answered with "No", and the screenshot.
- Demo with Ollama: `examples/escritorio/Demo` (`dotnet run -c Release -- <exe> <folder> todas`).

## Antivirus

Launching a program on a desktop nobody sees is the same thing some malware does, and an antivirus with
behavioural heuristics may distrust a freshly built exe. With Malwarebytes, the examples'
`NotasEscritorio.dll`, built on the spot, ends up in quarantine as soon as faena opens it on the hidden
desktop (the app closes without a window and faena says "it closed without opening a window"). If that
happens, add an exclusion for the program's folder (or the repo's).
