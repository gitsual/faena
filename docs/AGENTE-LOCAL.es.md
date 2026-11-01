*[Read in English](AGENTE-LOCAL.md)*

# El agente local: `faena hacer`

Le encargas una tarea y la IA la **hace** por ti, en segundo plano, y te deja un informe. Abre la
aplicación, va haciendo clics y rellenando, y se para antes de cualquier cosa que no tenga vuelta
atrás. Lo que sale bien se puede guardar como **habilidad** (ver [HABILIDADES.es.md](HABILIDADES.es.md)) para que la
próxima vez lo repita sin pensar.

```
faena hacer "<objetivo>" --url <web>            :: una web
faena hacer "<objetivo>" --app <programa.exe>   :: una aplicación de escritorio (ver ESCRITORIO.es.md)
faena hacer "<objetivo>" --habilidad <nombre>   :: donde empiece la habilidad
```

| Opción | Qué hace |
|---|---|
| `--app <exe>` / `--args "…"` / `--ventana <título>` | un programa de escritorio, sus argumentos y parte del título de su ventana principal |
| `--ver` | con ventana, para mirarlo. Por defecto va **sin ventana**: no roba el foco ni toca el escritorio |
| `--max-pasos N` (30) / `--max-minutos M` (10) | topes; al llegar, se para y lo dice en el informe |
| `--permitir enviar,borrar,pagar,aceptar,irreversible,*` | lo irreversible que puede hacer sin preguntar (repetible; valen también send, delete, pay, accept) |
| `--fondo` | nunca preguntar: ante lo irreversible, parar y dejarlo en el informe |
| `--capturas` | una captura PNG tras cada paso y otra al final |
| `--aprender <nombre>` | si sale bien, guardar lo hecho como habilidad; con `--skill`, exportarla además como skill para agentes ([HABILIDADES.es.md](HABILIDADES.es.md#exportar-una-habilidad-como-skill-para-agentes)) |
| `--habilidad <nombre>` / `--sin-habilidades` | forzar una habilidad / razonar desde cero |
| `--sin-modelo --param clave=valor` | con `--habilidad`: repetirla sin ningún modelo, con esos valores |
| `--carpeta <dir>` | dónde dejar el registro y el informe |
| `--backend ollama\|claude\|openai`, `--model` | el modelo: `FAENA_LLM`, si no Claude con `ANTHROPIC_API_KEY`, si no los ajustes de la ventana, si no Ollama; `FAENA_LLM_MODEL` (valen también las `TRAMOYA_LLM*`); para un servidor compatible con OpenAI, `FAENA_LLM_URL` y `FAENA_LLM_KEY` |
| `--no-reparar` | si una habilidad falla a medias y el modelo termina, no reescribirla |
| `--preguntar-en-carpeta` | la pregunta de lo irreversible va por `pregunta.json` / `respuesta.json` en la carpeta del encargo (lo usa la ventana) |
| `--lang es\|en` | idioma de la consola, el registro y el informe (también `FAENA_LANG`; por defecto, inglés) |
| `--dry-run` | dice qué haría (url, límites, habilidades que encajan) sin abrir nada |

**Salida**: `0` hecha, `1` sin terminar (tope, el modelo se rindió, error), `3` parada ante algo
irreversible que nadie autorizó, `2` error de uso. Ctrl+C la corta y deja el informe igual.

## Qué deja

Una carpeta por ejecución en `%USERPROFILE%\Faena\tareas\<fecha-hora>-<objetivo>\`
(`FAENA_TAREAS` la cambia):

- `registro.log`: cada turno con su hora, la acción, el porqué del modelo y el resultado.
El registro y el informe salen en el idioma de la interfaz (`--lang`); las claves de `informe.json` son
siempre las mismas, en español.

- `informe.md`: para leer: estado (✅ hecha / ⏸️ falta tu autorización / ⚠️ sin terminar), el
  resumen del modelo, lo pendiente de autorizar y cómo hacerlo, la habilidad usada, los pasos,
  los avisos y las capturas.
- `informe.json`: lo mismo para máquinas; `faena habilidades aprender <carpeta>` aprende de él.
- `capturas/NN.png`, `capturas/final.png` con `--capturas`.

## Lo irreversible

Antes de cada acción se pregunta al **guardián** (`src/Faena.Agente/Guardian.cs`):

1. Por el **nombre** del control: palabras enteras de enviar (enviar, mandar, responder,
   publicar, send, submit…), borrar (borrar, eliminar, vaciar, delete, remove…), pagar (pagar,
   comprar, finalizar compra, checkout…) y aceptar (aceptar, confirmar, aprobar, firmar,
   validar, imputar, transferir…). El aviso de cookies no cuenta.
2. Por el **modelo**: su JSON lleva `"irreversible": true` cuando cree que la acción no tiene
   vuelta atrás (por ejemplo un Intro que manda un mensaje). El prompt le aclara que guardar,
   crear, editar o marcar no lo son. Los prompts van en inglés; el resumen se le pide en el idioma
   de la persona.

Los nombres se reconocen en español y en inglés sea cual sea el idioma de la interfaz.

Si es irreversible y su categoría no está en `--permitir`: con una persona en la consola (y sin
`--fondo`) pregunta «¿La hago? [s/N]»; si no, **se para sin hacerla** y el informe dice qué iba
a hacer y por qué. Lo mismo al repetir una habilidad. Es una red, no una garantía: un botón
«Continuar» que en realidad paga solo se para si el modelo lo marca.

## El navegador

Chromium de Playwright en un **perfil persistente propio** (`%LOCALAPPDATA%\Faena\navegador-tareas`):
las sesiones iniciadas se quedan para la siguiente tarea. El Chromium es el de
`%LOCALAPPDATA%\Faena\navegador`; si no está y tramoya ya descargó el suyo (la misma versión de
Playwright), faena usa ese en vez de bajar otros 150 MB. `faena navegador instalar` lo descarga a mano. Para iniciar sesión una vez en una web con login: `hacer "…" --url … --ver` y entrar a
mano cuando se abra (o dejar que el agente se pare en la pantalla de login). Sin ventana, nada
aparece en el escritorio; con `--ver`, la ventana de Chromium sí coge el foco al abrirse.

## Ejemplos (examples/)

```
cd examples\notas && python -m http.server 8770 --bind 127.0.0.1
faena hacer "Crea una nota con título «Comprar pan» y texto «dos barras»" --url http://127.0.0.1:8770/index.html --aprender crear-nota
faena hacer "Apúntame una nota: «Llamar al fontanero», «el grifo gotea»" --url http://127.0.0.1:8770/index.html

cd examples && python -m http.server 8771 --bind 127.0.0.1
faena hacer "Escríbele a Marta con asunto «Llego tarde» y mándaselo" --url http://127.0.0.1:8771/mensajes.html          :: se para (salida 3)
faena hacer "Escríbele a Marta con asunto «Llego tarde» y mándaselo" --url http://127.0.0.1:8771/mensajes.html --permitir enviar
```

Mediciones en casa (RTX 4080, Ollama con `qwen3-coder:30b`, 28-10-2026): crear la nota
razonando, 5 turnos y 30 s; la misma tarea con la habilidad, 4 pasos repetidos + 1 turno de
comprobación, 13 s; el mensaje, 4 turnos y 10 s hasta pararse ante «Enviar».

## En la ventana

`faena` sin argumentos (o con doble clic) abre la ventana, vestida de Hollín (un mozo pobre que hace
recados y lo apunta todo en su libreta), con lo mismo sin consola:

- **Dar un recado** (lateral o portada): qué hay que hacer, dónde (las webs de otros recados, otra web
  o un programa), lo que puede hacer sin pedir firma (enviar / borrar / pagar / aceptar), «Mirar cómo
  lo hago», una foto de cada paso, el jornal (pasos y minutos como mucho) y «si sale bien, lo aprendo
  de oficio como…».
- **Faenando**: los palotes de los pasos, el último paso y **lo que va apuntando** (el `registro.log`
  en vivo, en papel de estraza). Ante algo irreversible no permitido sale **«Aquí me paro, señor»**
  con *firmo, adelante* / *quieto ahí*: la pregunta viaja de verdad al agente, que espera (30 min
  como mucho; sin respuesta, no lo hace).
- **La libreta**: hecho / faltó su firma (con «repetirlo con su firma») / a medias, lo que hizo
  renglón a renglón, las fotos pegadas con cinta, «que lo aprenda» y «repetirlo».
- **Recados** en el lateral, con su estado; **Oficios**: las habilidades como remiendos de cuero
  colgados (parámetros, pasos, «hacerlo» con los datos de esta vez, «sin pensar» = sin modelo,
  «descoserlo», exportarlo como skill para agentes) y **Enseñarme uno nuevo** desde un proyecto de
  tramoya: su carpeta o su nombre, y sus pasos pasan a los oficios (con la casilla «Exportar como skill»).
- **Ajustes**: el idioma y los de la IA (ver el [README](../README.es.md#idioma-y-ajustes)).

Por debajo, cada encargo es un `faena hacer … --carpeta <tareas\id> --fondo
--preguntar-en-carpeta`: la ventana lee lo que deja en la carpeta (`encargo.json`, `registro.log`,
`informe.json`) y la pregunta va y viene por `pregunta.json` / `respuesta.json`
(`src/Faena/Encargos.cs`, servido por `src/Faena/Servidor.cs`). `faena ventana --no-window` deja solo
el servidor y escribe su dirección, para abrirla en cualquier navegador. Al cerrar la ventana se paran los encargos en marcha (para dejar
algo trabajando con la ventana cerrada, la consola: `hacer`).

## Lo que hemos tomado de otros (investigación del 28-10-2026)

Una investigación con fuentes y verificación cruzada (cada afirmación, 3 votos) sobre agentes de
navegador y de escritorio de 2024-2026. Lo integrado:

- **Observar el cambio después de cada acción** (Agent-E, arXiv 2407.13032): tras actuar, el
  agente compara la observación nueva con la anterior sin modelo y le cuenta al modelo qué ha
  aparecido, qué ya no está, qué valor o estado cambió y si cambió la página o la ventana
  (`Protocol.Cambios`). Si no cambió nada, «quizá no ha funcionado». Cada paso apunta `cambio`.
- **Solo entra en una habilidad lo que cambió algo** (ASI, arXiv 2504.06821; SkillWeaver, arXiv
  2504.07079: una habilidad se verifica antes de guardarse y cada llamada debe cambiar el
  entorno): al aprender, los clics, teclas o elecciones que no cambiaron nada se descartan.
- **Habilidades como acciones** (ASI): las obras del repertorio que encajan con el
  objetivo se le ofrecen al modelo como acción `"habilidad"` con `"parametros"`; puede usarlas
  varias veces (crear tres notas: 1 elección + 3 turnos, 27 s, 2 de 2).
- **Autocuración** (caché de acciones de Stagehand v3): si un paso de una habilidad falla, el
  modelo termina la tarea y, si sale bien, la habilidad se reescribe con lo que funcionó
  (`--no-reparar` para no tocarla). Probado rompiendo a propósito el selector de «New note»: se
  repara sola y la siguiente vez va del tirón (13 s).
- **El texto de la pantalla son datos, no órdenes**: regla en el prompt. Probado con
  `examples/mensajes-trampa.html` (un mensaje que pide borrar todo): 3 de 3
  ejecuciones lo ignoran. Esta parte no salió respaldada por la investigación (no se verificó
  ninguna fuente sobre inyección) y la muestra es pequeña; el guardián sigue siendo la red.

Queda para después, también de la investigación:

- **Acción «api/script» antes que la GUI** (UFO2, arXiv 2504.14603: COM/Office con respaldo de GUI,
  de 16 a 6,6 pasos; CoAct-1: código + GUI, 60,8 % en OSWorld frente a 50,7 % solo GUI): una acción
  que llame a COM de Office o a PowerShell restringido, pasando por el guardián.
- **Respaldo visual cuando UIA no expone el control** (UFO2: UIA + OmniParser, 28,6 % frente a
  24,5 % solo UIA en OSWorld-W).
- **Snapshot ARIA de Playwright** como observación alternativa (Playwright MCP) y vistas filtradas
  (solo campos, solo texto) para ahorrar contexto con el modelo local.
- **Huella del estado esperado** en cada paso de una habilidad (url, título, subárbol ARIA/UIA)
  para detectar antes que la web ha cambiado.
- Medir con **Windows Agent Arena** (154 tareas en Windows real).

## Dónde no hay modelo local

El agente necesita un modelo que conteste JSON en cada turno; en casa es Ollama con
`qwen3-coder:30b` (18 GB, GPU de 16 GB o más para ir a 2-3 s por turno). Donde no lo hay:

- **API de Claude**: `set ANTHROPIC_API_KEY=…` y ya (o `--backend claude`). Hace falta salida a
  `api.anthropic.com` (proxys corporativos: `HTTPS_PROXY`) y permiso para mandar fuera el texto
  de las pantallas: el contenido de la web va en cada turno, y con `--capturas` también las
  imágenes. Para datos sensibles, no.
- **Un servidor Ollama en la red**: `set OLLAMA_HOST=http://servidor:11434` (por ejemplo la
  máquina del laboratorio); el PC solo necesita llegar a ese puerto. Lo que viaja es el texto de
  la pantalla, dentro de la red.
- **Un modelo más pequeño en la CPU** (`qwen3:14b`, `qwen2.5:7b`): funciona en tareas cortas
  y con habilidades (que ahorran casi todo el razonamiento), pero cada turno tarda decenas de
  segundos y se equivoca más; `--max-minutos` alto.
- **Solo habilidades, sin modelo**: `hacer "…" --habilidad crear-nota --sin-modelo --param
  title="Comprar leche" --param text="sin lactosa"` repite la habilidad con esos valores sin
  llamar a ningún modelo (8 s en la app de notas). Nadie comprueba el resultado ni arregla un
  paso que falle: vale para procedimientos mecánicos ya probados, y lo irreversible se para
  igual. Las habilidades se pueden aprender en casa (con modelo) y llevarse en la carpeta.
