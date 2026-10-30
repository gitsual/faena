# Habilidades: procedimientos que el agente aprende

Una **habilidad** es un procedimiento que ya salió bien una vez, guardado para repetirlo sin
pensar: los pasos con sus selectores duraderos, y lo que cambia de una vez a otra convertido en
**parámetros**. `faena hacer` las mira antes de razonar: si una encaja con el objetivo, el
modelo solo la elige y rellena los valores, los pasos se repiten tal cual y el modelo comprueba
al final que el objetivo está cumplido. En la app de notas de `examples/`, «crear una nota»
pasa de 30 s y 5 llamadas al modelo a 13 s y 2.

## Dónde viven

Un archivo `.toml` por habilidad en `%USERPROFILE%\Faena\habilidades\` (en un kit portátil,
`<kit>\habilidades\`; `FAENA_HABILIDADES` cambia la carpeta). Son texto para leer y retocar a
mano: renombrar parámetros, afinar `cuando`, quitar un paso de más.

## Cómo se crean

```
:: al hacer una tarea: si sale bien, se guarda
faena hacer "Crea una nota con título «Comprar pan» y texto «dos barras»" --url http://127.0.0.1:8770/index.html --aprender crear-nota

:: de una tarea ya hecha (su carpeta, o el nombre de la carpeta dentro de Faena\tareas)
faena habilidades aprender 2026-10-28-182652-crea-una-nota-con-titulo... --nombre crear-nota

:: de un proceso grabado en tramoya (a mano o por su IA): su id o su carpeta (la de proyecto.json)
faena habilidades aprender dar-de-alta-un-cliente --nombre alta-cliente --cuando "alta de cliente; cliente nuevo"

:: las que aprendió tramoya cuando el agente vivía allí (%USERPROFILE%\Tramoya\habilidades)
faena habilidades importar

faena habilidades lista
faena habilidades ver crear-nota
```

Al aprender:

- Cada paso `escribir` o `elegir` pasa a usar un parámetro con el nombre del campo
  («Escribe «Comprar pan» en el campo «Title»» → `title`), con el valor de aquella vez como
  ejemplo. Si el campo no tiene nombre, `texto`, `texto2`…
- Esos valores también se quitan de la descripción («Crea una nota con título «{title}»…»),
  para que el modelo la reconozca con otros valores.
- `cuando` sale de las palabras con contenido del nombre y la descripción; conviene
  afinarlo con `--cuando "a; b"` o a mano.
- Los pasos quitados en tramoya (`quitado = true`) y lo que no es una acción se descartan; la
  frase del narrador de tramoya (`texto`) no se guarda.

## Formato

```toml
nombre = "crear-nota"                      # obligatorio; también es el nombre del archivo
descripcion = "Crea una nota con título «{title}» y texto «{text}»"   # lo que lee el modelo para elegir
cuando = ["crear", "nota", "apuntar"]      # palabras o expresiones que la delatan en un objetivo
superficie = "web"                         # "web" o "escritorio"
inicio = "http://127.0.0.1:8770/index.html"  # dónde empieza (url o programa); `hacer --habilidad X` lo usa si no se da --url/--app
origen = "tarea 2026-10-28-182652-…"       # informativo
aprendida = "2026-10-28 18:27"             # informativo

[parametros]          # nombre = valor de ejemplo
title = "Comprar pan"
text = "dos barras de la panadería"

[[pasos]]             # en orden; el mismo formato de pasos que los proyectos de tramoya
tipo = "clic"         # clic | señalar | escribir | elegir | tecla | ir | esperar
selector = "internal:role=button[name=\"New note\"i]"
desc = "Pulsa el botón «New note»"

[[pasos]]
tipo = "escribir"
selector = "internal:role=textbox[name=\"Title\"i]"
valor = "{title}"     # los {huecos} se rellenan en valor, desc y selector
desc = "Escribe «{title}» en el campo «Title»"

[[pasos]]
tipo = "clic"
selector = "#cookies-ok"
desc = "Pulsa «Aceptar» (aviso de cookies: si no sale, se salta)"
opcional = true       # si no está, no es un fallo
```

- `selector`: en web, cualquier selector de Playwright (los que genera el grabador o el agente,
  con marcos `… >> internal:control=enter-frame >> …`); en escritorio, el formato `uia=` de
  `ESCRITORIO.md`. Obligatorio en `clic`, `señalar`, `escribir` y `elegir`.
- `valor`: el texto que se escribe, la opción que se elige (su texto visible), la tecla
  (`Enter`, `Tab`, `Escape`… o como la diga una persona: «Intro») o la url de `ir`.
- `desc`: para el informe, para la receta que ve el modelo y para el guardián: un `clic` cuya
  desc nombra algo irreversible («Pulsa el botón «Enviar»») se para igual que si lo decidiera el
  modelo. Las desc que generan faena y tramoya acaban con el nombre del control entre «».

## Cómo se usan

1. **Candidatas**: las de la misma superficie que comparten palabras con el objetivo (cada
   expresión de `cuando` que aparece cuenta 2, cada palabra del nombre o la descripción 1, y
   empezar en el mismo sitio suma 1). Se quedan las 3 mejores.
2. **Elección**: una llamada al modelo con las candidatas y sus parámetros; contesta
   `{"habilidad": "…" | "", "parametros": {…}}`. Si ninguna encaja del todo, razona desde cero.
   Con `--habilidad X` no se elige, pero los valores salen igual del objetivo. `--sin-habilidades`
   se las salta.
3. **Repetición**: cada paso con el lenguaje de pasos de tramoya (`Steps.RunAsync` sobre una
   `EscenaWeb`, a ritmo de ensayo), pasando por el guardián de lo irreversible.
4. **Relevo**: el modelo sigue con la receta (los pasos numerados, ya con los valores) en el
   prompt y los pasos repetidos como hechos. Si todo se repitió, normalmente solo comprueba y
   dice «terminar»; si un paso falló (la web cambió), sigue él desde ahí.

Sin modelo (`--habilidad X --sin-modelo --param clave=valor`), solo el paso 3: los parámetros
que no se den conservan el valor de ejemplo, y sale bien si se repiten todos los pasos.

El informe de la tarea dice qué habilidad se usó, con qué valores y cuántos pasos se repitieron.

## Límites

- Una habilidad es lineal: no tiene condiciones ni bucles. Lo que varía de verdad (un aviso que
  a veces sale) se marca `opcional`; lo demás lo resuelve el modelo en el relevo.
- Los selectores son los de aquella vez: si la web cambia mucho, la habilidad falla pronto y el
  modelo hace el resto; conviene volver a aprenderla con `--force`.
- Los perfiles `tramoya.toml` de tramoya (líneas de pasos de `direct`) aún no se importan como habilidades.
