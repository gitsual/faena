*[Leer en español](AGENTE-LOCAL.es.md)*

# The local agent: `faena do`

You give it a task and the AI **does** it for you, in the background, and leaves you a report. It opens
the application, clicks and fills things in as it goes, and stops before anything that cannot be
undone. What goes well can be saved as a **skill** (see [HABILIDADES.md](HABILIDADES.md)) so that next
time it repeats it without thinking.

```
faena do "<goal>" --url <site>             :: a website
faena do "<goal>" --app <program.exe>      :: a desktop application (see ESCRITORIO.md)
faena do "<goal>" --use-skill <name>       :: wherever the skill starts
```

`faena hacer` (and the old `do-task`) is the same command; every flag below also has a Spanish alias
(in brackets).

| Option | What it does |
|---|---|
| `--app <exe>` / `--args "..."` / `--window-title <title>` (`--ventana`) | a desktop program, its arguments and part of its main window's title |
| `--show` (`--ver`) | with a visible window, to watch it. By default it runs **without a window**: it does not steal focus or touch the desktop |
| `--max-steps N` (30) / `--max-minutes M` (10) (`--max-pasos`, `--max-minutos`) | limits; when reached, it stops and says so in the report |
| `--allow enviar,borrar,pagar,aceptar,irreversible,*` (`--permitir`) | the irreversible things it may do without asking (repeatable; `send`, `delete`, `pay`, `accept` are accepted too) |
| `--background` (`--fondo`) | never ask: when something is irreversible, stop and record it in the report |
| `--screenshots` (`--capturas`) | a PNG screenshot after each step and one at the end |
| `--learn <name>` (`--aprender`) | if it goes well, save what was done as a skill; with `--skill`, also export it as an Agent Skill ([HABILIDADES.md](HABILIDADES.md#exporting-a-skill-as-an-agent-skill)) |
| `--use-skill <name>` (`--habilidad`) / `--no-skills` (`--sin-habilidades`) | force a skill / reason from scratch |
| `--no-model --param key=value` (`--sin-modelo`) | with `--use-skill`: replay it without any model, with those values |
| `--folder <dir>` (`--carpeta`) | where to put the log and the report |
| `--backend ollama\|claude\|openai`, `--model` | the model: `FAENA_LLM`, otherwise Claude if `ANTHROPIC_API_KEY` is set, otherwise the window's settings, otherwise Ollama; `FAENA_LLM_MODEL` (the `TRAMOYA_LLM*` variables work too); for an OpenAI-compatible server, `FAENA_LLM_URL` and `FAENA_LLM_KEY` |
| `--no-repair` (`--no-reparar`) | if a skill fails halfway and the model finishes the task, do not rewrite the skill |
| `--ask-in-folder` (`--preguntar-en-carpeta`) | the question about something irreversible goes through `pregunta.json` / `respuesta.json` in the task folder (the window uses this) |
| `--lang en\|es` | language of the console, the log and the report (also `FAENA_LANG`; English by default) |
| `--dry-run` | says what it would do (url, limits, matching skills) without opening anything |

**Exit codes**: `0` done, `1` unfinished (limit reached, the model gave up, error), `3` stopped before
something irreversible that nobody authorized, `2` usage error. Ctrl+C cuts it short and still leaves the
report.

## What it leaves behind

One folder per run in `%USERPROFILE%\Faena\tareas\<date-time>-<goal>\` (`FAENA_TAREAS` changes it). The
log and the report are written in the interface language (`--lang`); the keys of `informe.json` are always
the same, in Spanish.

- `registro.log`: every turn with its time, the action, the model's reasoning and the result.
- `informe.md`: for reading: status (✅ done / ⏸️ waiting for your authorization / ⚠️ unfinished), the
  model's summary, what is waiting for authorization and how to give it, the skill used, the steps,
  the warnings and the screenshots.
- `informe.json`: the same for machines; `faena skills learn <folder>` learns from it.
- `capturas/NN.png`, `capturas/final.png` with `--screenshots`.

## Irreversible actions

Before every action the **guard** (`src/Faena.Agente/Guardian.cs`) is consulted:

1. By the control's **name**: whole words for sending (enviar, mandar, responder, publicar, send,
   submit...), deleting (borrar, eliminar, vaciar, delete, remove...), paying (pagar, comprar, finalizar
   compra, checkout...) and accepting (aceptar, confirmar, aprobar, firmar, validar, imputar,
   transferir...). Cookie banners do not count. Names are recognized in Spanish and English whatever
   the interface language.
2. By the **model**: its JSON carries `"irreversible": true` when it believes the action cannot be undone
   (for example an Enter that sends a message). The prompt makes clear that saving, creating, editing or
   ticking are not irreversible. Prompts are in English; the model is asked to write its summary in the
   user's language.

If it is irreversible and its category is not in `--allow`: with a person at the console (and without
`--background`) it asks "Do it? [y/N]"; otherwise it **stops without doing it** and the report says what
it was about to do and why. The same applies when replaying a skill. It is a safety net, not a guarantee:
a "Continue" button that actually pays only stops it if the model flags it.

## The browser

Playwright's Chromium with its **own persistent profile** (`%LOCALAPPDATA%\Faena\navegador-tareas`):
sessions you log into stay for the next task. The Chromium is the one in `%LOCALAPPDATA%\Faena\navegador`;
if it is not there and tramoya has already downloaded its own (the same Playwright version), faena uses
that one instead of downloading another 150 MB. `faena navegador instalar` downloads it by hand. To log in
once to a site with a login: `do "..." --url ... --show` and sign in by hand when it opens (or let the
agent stop at the login screen). Without a window nothing shows up on the desktop; with `--show`, the
Chromium window does take focus when it opens.

## Examples (examples/)

```
cd examples\notas && python -m http.server 8770 --bind 127.0.0.1
faena do "Create a note titled 'Buy bread' with the text 'two loaves'" --url http://127.0.0.1:8770/index.html --learn crear-nota
faena do "Jot down a note: 'Call the plumber', 'the tap is dripping'" --url http://127.0.0.1:8770/index.html

cd examples && python -m http.server 8771 --bind 127.0.0.1
faena do "Write to Marta with the subject 'Running late' and send it" --url http://127.0.0.1:8771/mensajes.html          :: stops (exit 3)
faena do "Write to Marta with the subject 'Running late' and send it" --url http://127.0.0.1:8771/mensajes.html --allow send
```

Measurements at home (RTX 4080, Ollama with `qwen3-coder:30b`, 2026-10-28): creating the note by
reasoning, 5 turns and 30 s; the same task with the skill, 4 replayed steps + 1 checking turn, 13 s; the
message, 4 turns and 10 s until it stopped at "Send".

## In the window

`faena` with no arguments (or a double click) opens the window, dressed as Hollín (a poor boy who runs
errands and writes everything down in his notebook), with the same features and no console:

- **Run an errand** (sidebar or front page): what needs doing, where (the sites of other errands, another
  site or a program), what it may do without asking for a signature (send / delete / pay / accept),
  "Watch me do it", a photo of every step, **the day's wage** (at most so many steps and minutes) and "if
  it goes well, I'll learn it as a trade called...".
- **At work**: tally marks for the steps, the last step and **what it is writing down** (the live
  `registro.log`, on brown paper). Before something irreversible that is not allowed, **"Here I stop,
  sir."** appears with *sign, go ahead* / *stay right there*: the question really travels to the agent,
  which waits (30 min at most; with no answer, it does not do it).
- **The notebook**: done / your signature was missing (with "do it again with my signature") / half done,
  what it did line by line, the photos taped in, "learn it" and "do it again".
- **Errands** in the sidebar, with their status; **Trades**: skills as leather patches hung on the wall
  (parameters, steps, "do it" with this time's data, "without thinking" = without a model, "unpick it",
  export as an Agent Skill) and **Teach me a new one** from a tramoya project: its folder or its name, and
  its steps become a trade (with an "Export as skill" checkbox).
- **Settings**: language and the AI settings (see the [README](../README.md#languages-and-settings)).

Under the hood, each errand is a `faena do ... --folder <tareas\id> --background --ask-in-folder`: the
window reads what it leaves in the folder (`encargo.json`, `registro.log`, `informe.json`) and the question
goes back and forth through `pregunta.json` / `respuesta.json` (`src/Faena/Encargos.cs`, served by
`src/Faena/Servidor.cs`). `faena window --no-window` (alias `ventana`) starts only the server and prints
its address, so it can be opened in any browser. Closing the window stops the errands in progress (to leave
something working with the window closed, use the console: `do`).

## What we took from others (research of 2026-10-28)

Research with sources and cross-verification (every claim, 3 votes) on browser and desktop agents from
2024-2026 ([INVESTIGACION.md](INVESTIGACION.md)). What was integrated:

- **Observing the change after each action** (Agent-E, arXiv 2407.13032): after acting, the agent compares
  the new observation with the previous one without a model and tells the model what appeared, what is
  gone, which value or state changed and whether the page or window changed (`Protocol.Cambios`). If
  nothing changed, "maybe it did not work". Each step records `cambio`.
- **Only what changed something goes into a skill** (ASI, arXiv 2504.06821; SkillWeaver, arXiv
  2504.07079: a skill is verified before it is saved and every call must change the environment): when
  learning, clicks, keys or choices that changed nothing are discarded.
- **Skills as actions** (ASI): the repertoire's skills that match the goal are offered to the model as a
  `"habilidad"` action with `"parametros"`; it can use them several times (creating three notes: 1 choice +
  3 turns, 27 s, 2 out of 2).
- **Self-healing** (Stagehand v3's action cache): if a step of a skill fails, the model finishes the task
  and, if it goes well, the skill is rewritten with what worked (`--no-repair` to leave it alone). Tested
  by deliberately breaking the "New note" selector: it repairs itself and the next time it runs straight
  through (13 s).
- **Screen text is data, not instructions**: a rule in the prompt. Tested with
  `examples/mensajes-trampa.html` (a message that asks to delete everything): 3 out of 3 runs ignore it.
  This part was not backed by the research (no source on injection was verified) and the sample is small;
  the guard is still the safety net.

Left for later, also from the research:

- **An "api/script" action before the GUI** (UFO2, arXiv 2504.14603: COM/Office with GUI fallback, from 16
  to 6.6 steps; CoAct-1: code + GUI, 60.8 % on OSWorld versus 50.7 % GUI only): an action that calls Office
  COM or restricted PowerShell, going through the guard.
- **Visual fallback when UIA does not expose the control** (UFO2: UIA + OmniParser, 28.6 % versus 24.5 %
  UIA only on OSWorld-W).
- **Playwright's ARIA snapshot** as an alternative observation (Playwright MCP) and filtered views (fields
  only, text only) to save context with the local model.
- **A fingerprint of the expected state** in each step of a skill (url, title, ARIA/UIA subtree) to notice
  earlier that the site has changed.
- Measuring with **Windows Agent Arena** (154 tasks on real Windows).

## Where there is no local model

The agent needs a model that answers JSON on every turn; at home it is Ollama with `qwen3-coder:30b`
(18 GB, a GPU with 16 GB or more to get 2-3 s per turn). Where there is none:

- **Claude API**: `set ANTHROPIC_API_KEY=...` and that is it (or `--backend claude`). It needs access to
  `api.anthropic.com` (corporate proxies: `HTTPS_PROXY`) and permission to send the screen text outside:
  the site's content goes out on every turn, and with `--screenshots` the images too. Not for sensitive
  data.
- **An Ollama server on the network**: `set OLLAMA_HOST=http://server:11434` (for example the lab
  machine); the PC only needs to reach that port. What travels is the screen text, inside the network.
- **An OpenAI-compatible server**: `--backend openai` with `FAENA_LLM_URL` (and `FAENA_LLM_KEY` if it needs
  one), or the same in the window's Settings screen.
- **A smaller model on the CPU** (`qwen3:14b`, `qwen2.5:7b`): it works for short tasks and with skills
  (which save almost all the reasoning), but each turn takes tens of seconds and it makes more mistakes;
  use a high `--max-minutes`.
- **Skills only, no model**: `do "..." --use-skill crear-nota --no-model --param title="Buy milk" --param
  text="lactose-free"` replays the skill with those values without calling any model (8 s in the notes
  app). Nobody checks the result or fixes a failing step: it is fine for mechanical procedures that have
  already been tested, and anything irreversible still stops. Skills can be learned at home (with a model)
  and carried along in the folder.
