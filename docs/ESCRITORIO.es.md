*[Read in English](ESCRITORIO.md)*

# Aplicaciones de escritorio (`Faena.Escritorio`)

El agente de faena maneja también aplicaciones de escritorio de Windows, no solo webs. Para eso hay
una segunda superficie, `EscritorioSuperficie`, junto a la web (`WebSuperficie`). Las dos implementan
`ISuperficie` (`src/Faena.Agente/Superficie.cs`), así que el bucle del agente, el guardián de lo
irreversible, las habilidades y los informes son los mismos.

## Cómo se usa (API)

```csharp
await using var sup = await EscritorioSuperficie.AbrirAsync(
    app: @"C:\ruta\MiApp.exe",   // o un nombre del PATH: «charmap»
    argumentos: null,
    oculto: true,                // en un escritorio Win32 propio que nadie ve (por defecto)
    titulo: "Notas",             // parte del título de la ventana principal; null = la primera que salga
    ct);
var r = await Encargo.RunAsync(sup, new AgentOptions("Añade una nota «Pan»") { Modo = Modo.Tarea },
                               habilidades, null, backend, log, ct);
// Al salir del using: se cierra la app (WindowPattern.Close; si a los 3 s sigue, Kill del árbol) y el escritorio.
```

`sup.Tipo == "escritorio"`, `sup.Oculto`, `sup.NombreEscritorio`, `sup.Pid`. Errores de apertura:
`FileNotFoundException` (no existe), `InvalidOperationException` (app de la Tienda en modo oculto, o no se
pudo crear el escritorio), `TimeoutException` (no abre ventana en 20 s).

## Por qué un escritorio oculto

El agente trabaja en segundo plano: no puede mover el ratón, teclear de verdad ni traer ventanas al frente
(se las robaría a la persona, y GlazeWM las recolocaría). Por eso:

1. `CreateDesktop` crea un escritorio Win32 nuevo en la estación `WinSta0`, que nunca se activa.
2. La app se lanza con `CreateProcess` y `STARTUPINFO.lpDesktop = "WinSta0\\faena-…"`: sus ventanas
   nacen allí y no aparecen en el escritorio de la persona ni en el gestor de ventanas.
3. Cada llamada de UI Automation va en un hilo nuevo MTA que lo primero que hace es `SetThreadDesktop`:
   UIA solo ve las ventanas del escritorio de su hilo. Tiene que ser un hilo sin ventanas, así que no
   vale uno STA de COM, porque esos ya tienen una y `SetThreadDesktop` devuelve el error 170 (ocupado).
4. Todas las acciones van por **patrones de UIA**, sin ratón ni `SendInput`, que irían al escritorio
   de entrada (el de verdad).

Probado el 28-10-2026 en Windows 11 (26200) con la app de ejemplo en WinForms: se observa, se escribe, se
pulsa, se abre y se contesta un diálogo modal, y la captura (`PrintWindow`) sale bien. Mientras tanto no
apareció nada en el escritorio real.

## Lo que ve el modelo

Las ventanas de nivel superior del escritorio oculto (en el modo visible, las del proceso) y los
**diálogos**. UIA cuelga un diálogo (un `MessageBox`, un «Guardar como») de su ventana propietaria, no del
escritorio, así que se buscan dentro de cada ventana y van primero, con la etiqueta `[marco: diálogo «…»
(delante)]`. La principal se reconoce por su HWND, porque un diálogo puede llamarse igual que ella.

Se recorre la vista de controles (`ControlViewWalker`, profundidad ≤ 25, tope 150) **sin la barra de
título**: minimizar, maximizar, cerrar y el menú del sistema no son del programa. Los tipos con los que se
actúa se nombran como en la web: Button/SplitButton «el botón», Edit «el campo», Document «el documento»,
MenuItem «la opción del menú», ComboBox «el desplegable», CheckBox «la casilla», RadioButton «la opción»,
ListItem «el elemento de la lista», TabItem «la pestaña», TreeItem «el elemento», Hyperlink «el enlace»,
DataItem «la fila», Slider, Spinner. Valor = `ValuePattern` (contraseñas como `••••`) o la selección;
estado = marcado / abierto / seleccionado / deshabilitado. El «texto visible» son el título de cada ventana,
los `Text`/`Header`/`StatusBar` y el contenido de los `Document` (`TextPattern`), hasta 2000 caracteres.
La «url» de la observación es `proceso — título`.

## Acciones

| acción | cómo |
|---|---|
| clic | Botón Win32 con HWND (incluye WinForms y los de los diálogos): `PostMessage(padre, WM_COMMAND, BN_CLICKED\|id, hwnd)`. Si no: `ExpandCollapse` (menús, desplegables, árboles), `Invoke`, `Toggle`, `SelectionItem.Select`. La pulsación tiene un tope de 3 s: si no vuelve, se da por hecha. |
| escribir | `ValuePattern.SetValue` (sustituye). Sin `ValuePattern` → error explicado. |
| elegir | `ExpandCollapse.Expand` del desplegable, `SelectionItem.Select` del `ListItem` con ese nombre (sin distinguir mayúsculas); si es editable y no hay opción, `SetValue`. |
| tecla | `WM_KEYDOWN`/`WM_CHAR`/`WM_KEYUP` por `PostMessage` a la ventana del último control tocado (o al diálogo de delante). Enter, Tab, Escape, flechas, Re/AvPág, Inicio/Fin, Supr, Retroceso, Espacio y caracteres sueltos. |
| señalar | solo comprueba que el control está (no hay cursor que mover). |
| ir | no existe: no hay urls. |

**Por qué los botones no van por `Invoke`:** el proveedor de UIA de WinForms (y el de MSAA en general)
ejecuta `Invoke` de forma síncrona dentro de la app. Si el botón abre un diálogo modal, la llamada no vuelve
hasta que se cierra el diálogo, y mientras tanto la app no contesta a ninguna otra petición de UIA, de modo
que la siguiente observación se queda colgada hasta el timeout de UIA (~80 s). `WM_COMMAND` es justo lo que
manda el propio botón a su padre al soltarlo: es asíncrono y no necesita foco.

## Selector duradero

Los pasos que se apuntan (y los de las habilidades) usan el formato de pasos de siempre (el de los proyectos de tramoya) con un selector propio:

```
uia=<ControlType>[ventana="<título del diálogo>"][id="<AutomationId>"|name="<nombre>"][n="<k>"]
```

- `id`: el `AutomationId` si es fiable (no vacío, no un número de 5+ cifras, no un id de runtime
  `42.1.2.3`); si no, `name`. Se escribe uno de los dos.
- `ventana`: solo cuando el control está en un diálogo; sin ella se busca primero en la principal.
- `n`: cuál de los que coinciden (desde 1), si hay gemelos.
- `<ControlType>` sin el prefijo `ControlType.` (`Button`, `Edit`…) o `*`.
- Ejemplos: `uia=Edit[id="titulo"]`, `uia=Button[name="Añadir"]`, `uia=Button[ventana="Notas"][name="Sí"]`.

`RepetirAsync` lo resuelve (reintenta hasta 10 s, o 3 s si el paso es `opcional`) y hace la misma
acción. `SelectorUia.Parse`/`ToString` son ida y vuelta.

## Lo que no va (o va a medias)

- **Apps de la Tienda / empaquetadas** (Bloc de notas, Calculadora, Paint y Recortes de Windows 11): el
  ejecutable de System32 es un lanzador que activa la app por el shell, **en el escritorio de verdad**. En
  modo oculto se rechazan de antemano (`notepad`, `calc`, `mspaint`, `snippingtool`, `wordpad`, `write`,
  `explorer`, `control`, y cualquier ruta bajo `\WindowsApps\`). Con `oculto: false` se pueden usar, pero
  se ven. Si una app desconocida delega en otra, `AbrirAsync` da `TimeoutException` («se ha cerrado sin abrir
  ventana») y puede que algo haya salido en el escritorio real: no hay forma de saberlo antes.
- **Combinaciones de teclas** (Ctrl+S, Alt+F4): sin teclado de verdad no hay modificadores; se rechazan
  con un mensaje que pide usar la opción del menú.
- **Teclas en general**: van por mensaje y las ve la ventana a la que se mandan; las apps que leen el
  teclado con `GetKeyState`/raw input, o las que no son Win32 clásicas, pueden no enterarse.
- **Controles sin patrones** (dibujados a mano, juegos, lienzos): no se pueden pulsar sin ratón.
- **Capturas**: `PrintWindow(PW_RENDERFULLCONTENT)`. En el escritorio oculto no hay composición; con
  WinForms/Win32 salen bien (probado); las apps que pintan por DirectX/WinUI pueden salir en negro.
- **Menús emergentes Win32** (`#32768`) en el escritorio oculto: no probados.
- **Elevación**: una app que pide UAC no puede abrirse en el escritorio oculto (el diálogo de UAC va al
  escritorio seguro).
- **Sesión**: hace falta una sesión interactiva desbloqueada en la estación `WinSta0`. Desde un servicio o
  la sesión 0 no hay escritorio en el que crear el oculto.

## Pruebas

- `tests/Faena.Tests/DesktopTests.cs`: selector, tipos, rechazo de apps de la Tienda y prompt, siempre.
  Con `FAENA_DESKTOP_TESTS=1`, la app de notas de `examples/escritorio/NotasEscritorio` (se
  compila sola si falta) en un escritorio oculto: observar, escribir, pulsar, repetir por selector, el
  diálogo modal de «Borrar todo» contestado con «No», y la captura.
- Demo con Ollama: `examples/escritorio/Demo` (`dotnet run -c Release -- <exe> <carpeta> todas`).

## Antivirus

Lanzar un programa en un escritorio que no se ve es lo mismo que hacen algunos programas maliciosos, y
un antivirus con heurística de comportamiento puede desconfiar de un exe recién compilado. Con
Malwarebytes, la `NotasEscritorio.dll` de los ejemplos, compilada en el momento, acaba en cuarentena en
cuanto faena la abre en el escritorio oculto (la app se cierra sin ventana y faena dice «se ha cerrado sin
abrir ventana»). Si pasa, hay que añadir una exclusión para la carpeta del programa (o la del repo).

