*[Read in English](INVESTIGACION.md)*

# Investigación: agentes que manejan aplicaciones en local (28-10-2026)

Hecha con un flujo de investigación: 5 frentes de búsqueda, 23 fuentes leídas, 106 afirmaciones extraídas, 25 verificadas por 3 votos adversarios (21 confirmadas, 4 refutadas).

## Resumen

Lo verificado apunta a cinco técnicas que faena puede adoptar. (1) Representar la página como un snapshot de texto del árbol de accesibilidad con refs efímeras (Playwright MCP): sale barato en tokens, apunta al elemento exacto y, cuando una ref caduca, da un error explícito que sirve para recuperarse. A eso se suman vistas destiladas según la tarea y una «observación del cambio» después de cada acción (Agent-E). (2) En escritorio Windows, el patrón más fuerte es el híbrido de UFO2. Primero, la detección de controles combina UIA con visión (OmniParser): con o1 en OSWorld-W, 28,6 % frente a 24,5 % solo con UIA y 14,3 % solo con visión. Segundo, se prefieren las APIs nativas y la GUI queda como respaldo: +6,1 a +8,2 puntos y, con o1, de 16,0 a 6,6 pasos. Tercero, se puede ejecutar en un escritorio anidado para no pelear con el usuario por el ratón. CoAct-1 confirma que combinar código y GUI supera a cualquiera de los dos por separado (60,76 % frente a 50,68 % y 35,73 % en OSWorld). (3) En memoria procedimental, la tendencia de 2025 es representar las habilidades como programas parametrizados (ASI, SkillWeaver) y no como notas de texto. Una habilidad solo entra en la biblioteca después de verificarse: un juez la da por resuelta, se llama a la habilidad y cada llamada cambia el entorno. Con eso, SkillWeaver mejora entre un 31,8 % y un 39,8 % relativo. La caché de Stagehand repite los pasos sin LLM y, ante un fallo de caché, vuelve a llamar al modelo; es el modelo directo para las habilidades TOML de faena. Sobre seguridad y human-in-the-loop (4) y sobre modelos locales pequeños para GUI (5), ninguna afirmación pasó la verificación, así que esas recomendaciones quedan sin respaldo en este informe.

## Hallazgos verificados

### (high, voto 3-0 (x3))

Representación de la página web: usar un snapshot de texto del árbol de accesibilidad donde cada nodo expuesto lleva una ref efímera (e5, f2e7 dentro de iframes). La ref es única dentro del snapshot y vale hasta que la página cambie. Las acciones aceptan una ref (lo preferido) o un selector, y una ref caducada devuelve un error explícito («Ref not found... Try capturing new snapshot») que sirve de señal de recuperación. Frente a las capturas, cuesta menos tokens, apunta al elemento exacto en vez de a coordenadas aproximadas y no necesita modelo de visión. Recomendación para faena: que el bucle observar→decidir JSON reciba refs de un snapshot ARIA de Playwright (en .NET, Locator.AriaSnapshotAsync o equivalente). Si una ref caduca, hay que volver a hacer el snapshot automáticamente en lugar de fallar. La captura queda solo para canvas o gráficos.

Fuentes: https://playwright.dev/mcp/snapshots

Matiz: Tres afirmaciones de la documentación oficial de Playwright MCP, todas 3-0. Matiz: en páginas grandes el snapshot se encarece y se acumula en el contexto a lo largo de varios pasos; hay informes de unos 114k tokens en Salesforce. La propia documentación recomienda limitarlo con depth/target o browser_find.

### (medium, voto 3-0 (x3))

DOM destilado según la tarea y observación del cambio después de cada acción (Agent-E). Hay tres vistas del DOM: text_only, input_fields y all_fields. El agente elige la que encaja con el paso y cambia a otra si esa no funciona. Cada acción devuelve en palabras qué cambió (por ejemplo, que se abrió un popup), detectado por código con MutationObserver y aria-expanded, sin LLM. A diferencia de Reflexion, esto ocurre en cada acción y no solo tras un fallo. Agent-E llegó a un 73,1 % en WebVoyager (643 tareas), frente al 52,6 % de WILBUR y el 57,1 % del agente WebVoyager, aunque se quedó en un 27,3 % en Booking y un 35,7 % en Google Flights. Recomendación: añadir a la observación de faena un campo de diff o cambio calculado sin modelo (en web, nodos nuevos o eliminados en el árbol ARIA; en escritorio, eventos UIA de StructureChanged o ventanas nuevas). Y ofrecer al modelo vistas filtradas (solo campos de entrada o solo texto) para reducir tokens con qwen3-coder local.

Fuentes: https://arxiv.org/html/2407.13032v1

Matiz: Una sola fuente primaria, de julio de 2024 (anterior a la ventana 2025-2026). Las cifras son autoinformadas y ya están superadas, aunque las técnicas siguen siendo válidas. Votos 3-0.

### (high, voto 3-0 (x2))

Escritorio Windows: detección híbrida de controles UIA + visión. UFO2 fusiona los metadatos de UIA con OmniParser-v2 para los controles no estándar. Con o1 en OSWorld-W: solo UIA, 24,5 %; solo OmniParser, 14,3 %; híbrido, 28,6 %. En WAA con o1: 27,9 % frente a 25,3 % y 20,8 %. El híbrido recupera el 9,86 % de los fallos de UIA. Recomendación: mantener UIA como vía principal en faena y añadir un respaldo visual (un parser de capturas o un VLM local) solo cuando UIA no exponga el control, por ejemplo cuando un elemento personalizado tiene nombre vacío o el árbol está vacío.

Fuentes: https://arxiv.org/abs/2504.14603v2, https://microsoft.github.io/UFO/ufo2/overview

Matiz: El artículo y la documentación oficial de Microsoft coinciden, 3-0. Matices: con GPT-4o en WAA, la visión sola (26,6 %) superó a UIA sola (23,4 %), y en OSWorld-W hubo empate (22,4 %). El modo híbrido es configurable y no viene activado por defecto (control_backend: uia).

### (high, voto 3-0 (x4))

Acción híbrida API + GUI: prioridad a la API nativa y la GUI como respaldo. UFO2 usa xlwings para Excel, win32com para Outlook y python-pptx para PowerPoint, y solo recurre a la GUI cuando ninguna API cubre el paso. En 27 tareas ofimáticas de OSWorld: con GPT-4o, del 16,3 % al 22,4 %; con o1, del 16,3 % al 24,5 %, y de 16,0 a 6,6 pasos de media. CoAct-1 (Salesforce, 2025-2026) generaliza la idea: un orquestador reparte cada subtarea entre un operador GUI (VLM) y un programador que escribe y ejecuta Python o Bash. En OSWorld: solo programador, 35,73 % (1,14 pasos); solo GUI, 50,68 % (11,20 pasos); combinado, 60,76 % (10,15 pasos). Recomendación: añadir a faena un tipo de acción «api» o «script», con COM/Office Interop desde C# o PowerShell en un entorno restringido, que el modelo pueda elegir antes que la GUI. Debe pasar por el guardián de irreversibles igual que las acciones de GUI.

Fuentes: https://arxiv.org/abs/2504.14603v2, https://microsoft.github.io/UFO/ufo2/overview, https://arxiv.org/pdf/2508.03923v3

Matiz: Dos sistemas independientes (Microsoft y Salesforce) llegan a la misma conclusión, 3-0. Matices: muestras pequeñas (27 tareas y 12 APIs hechas a mano), resultados medidos por los propios autores, y CoAct-1 evaluado en Ubuntu, no en Windows.

### (medium, voto 3-0 (x2))

Benchmarks de escritorio Windows. UFO2 alcanza un 27,9 % en WindowsAgentArena y un 28,6 % en OSWorld-W con GPT-4o (30,5 % y 32,7 % con o1). Las líneas base: Operator, 20,8 % y 14,3 %; UFO original, 19,5 % y 12,2 %; Agent S, 18,2 % y 12,2 %; NAVI, 13,3 % y 10,2 %. Todos limitados a 30 pasos y medidos por los autores. Windows Agent Arena (ICML 2025) tiene más de 150 tareas (154) en un Windows real y es el banco natural para evaluar faena.

Fuentes: https://arxiv.org/abs/2504.14603v2, https://icml.cc/virtual/2025/poster/45035

Matiz: Las fuentes son primarias, pero las cifras son autoinformadas y de abril de 2025, así que probablemente ya están superadas. Se refutó la afirmación de que el mejor agente de WAA llega al 19,5 % frente al 74,5 % humano.

### (medium, voto 3-0)

Ejecución en segundo plano. UFO2 ejecuta el agente en un escritorio virtual anidado (Picture-in-Picture mediante un Remote Desktop en loopback), de modo que el usuario sigue trabajando mientras el agente automatiza, sin competir por el ratón ni el teclado. Recomendación: estudiarlo para que faena funcione sin bloquear al usuario. Una sesión RDP local hacia el mismo equipo tiene su propio escritorio interactivo, que UIA puede manejar.

Fuentes: https://microsoft.github.io/UFO/ufo2/overview

Matiz: Solo lo confirma la documentación de UFO. El README de GitHub no menciona PiP, y no consta si viene activado por defecto ni qué configuración necesita.

### (medium, voto 3-0 (x2))

Caché de acciones y repetición sin LLM (Stagehand v3), equivalente directo a las habilidades TOML. Con cacheDir, la primera ejecución guarda cada paso de act() y agent(), y las siguientes los repiten sin llamar al LLM ni gastar tokens, tanto en local como en Browserbase. En la caché de servidor, la clave combina la instrucción, el contenido o la estructura de la página, la URL y las opciones. Si la página cambia, la clave no coincide, se vuelve a llamar al LLM y la entrada nueva sirve para las siguientes ejecuciones, lo que en la práctica es autocuración. Recomendación: guardar en cada paso TOML una huella del estado esperado (hash del subárbol ARIA o UIA alrededor del objetivo, URL normalizada o título de ventana). Al repetir, si la huella no coincide o el localizador falla, invocar al modelo solo para ese paso, reescribir el paso en el TOML y continuar.

Fuentes: https://docs.stagehand.dev/v3/best-practices/caching.md

Matiz: Documentación oficial, 3-0. Pero el reintento automático con LLM solo está documentado para la caché de servidor de Browserbase; en la caché local hay que borrarla a mano. Se refutó (0-3) que la clave use los nombres de las variables y no sus valores, así que no se puede afirmar que una entrada sirva para varios parámetros.

### (high, voto 3-0 (x4))

Memoria procedimental: habilidades como programas parametrizados y verificados. En ASI (CMU, 2025) las habilidades son funciones Python con parámetros y docstring que encapsulan primitivas (click, fill, hover), por ejemplo search_product(name: str). Tienen de 2 a 5 pasos, pueden llamar a otras habilidades y entran en el espacio de acciones del agente, a diferencia del texto libre de AWM. Una habilidad solo entra en la biblioteca si, al volver a ejecutar la trayectoria reescrita, (1) un juez LLM da la tarea por resuelta, (2) se llama al menos a una habilidad nueva y (3) cada llamada cambia el entorno. SkillWeaver (abril de 2025) propone habilidades, las practica y las destila en APIs, con mejoras relativas del 31,8 % en WebArena y del 39,8 % en webs reales. Recomendación: exponer las habilidades TOML de faena como acciones invocables por el modelo (skill: nombre con parámetros y una descripción). Validarlas antes de guardarlas con una repetición más un juez más la comprobación de que cada paso cambió el estado. Y permitir que se compongan unas con otras.

Fuentes: https://arxiv.org/abs/2504.06821, https://arxiv.org/abs/2504.07079

Matiz: Dos artículos primarios e independientes de 2025, 3-0. Cifras medidas por los autores; las ganancias relativas sobre líneas base bajas equivalen a menos puntos absolutos. Se refutó (1-2) la transferencia de un agente fuerte a uno débil de hasta un 54,3 %, así que no está demostrado que construir habilidades con Claude y repetirlas con qwen local funcione igual de bien.

## Limitaciones

Ninguna afirmación verificada cubre la seguridad y el human-in-the-loop (inyección de prompts desde páginas, sandboxing, confirmación de acciones sensibles) ni los modelos locales pequeños para GUI (UI-TARS, Qwen2.5/3-VL, OpenCUA, GUI-Actor) con sus cifras. Esas dos partes de la pregunta (puntos 4 y 5) siguen sin respaldo verificado, y cualquier recomendación sobre ellas tendría que salir de otra investigación. Tampoco hay afirmaciones verificadas sobre browser-use, Skyvern, LaVague, Nanobrowser, Agent S2, OS-Copilot, Windows-MCP ni el uso de ordenador de Claude u OpenAI, salvo Operator como línea base. Casi todas las cifras las publican los propios autores en sus artículos o documentación, con muestras pequeñas (27 tareas ofimáticas, unas 154 tareas de WAA). Varias son de 2024 o principios de 2025 (Agent-E, julio de 2024; UFO2, abril de 2025) y el estado del arte avanza rápido. Se refutaron cuatro afirmaciones: la caché de Stagehand con clave por nombre de variable, el 19,5 % frente al 74,5 % humano en WAA, la comparación de CoAct-1 con Agent S2.5 y GTA-1, y la transferencia de habilidades a modelos débiles (54,3 %). CoAct-1 y SkillWeaver se evaluaron en Linux o en la web, no en Windows, así que su aplicación a faena es por analogía.

## Preguntas abiertas

- ¿Qué modelo local pequeño (UI-TARS, Qwen3-VL u OpenCUA de 7 a 32B) rinde mejor en WindowsAgentArena u OSWorld sobre hardware de consumo, y puede sustituir a qwen3-coder:30b como respaldo visual cuando UIA no expone el control?
- ¿Qué defensas concretas contra la inyección de prompts desde el contenido de páginas o ventanas, y qué políticas de confirmación, usan los agentes de 2025-2026 (Operator, uso de ordenador de Claude, browser-use), y cuáles se pueden llevar al guardián de faena?
- ¿Funcionan las habilidades aprendidas con un modelo fuerte (Claude) al repetirlas o repararlas con un modelo local débil? La afirmación de transferencia de SkillWeaver se refutó.
- ¿Cómo mantener una huella estable para invalidar la caché de habilidades en UIA y en ARIA sin que el estado de ejecución, cambiante, provoque fallos de caché constantes?

## Refutadas

- (0-3) When an action uses variables, Stagehand builds the cache key from the variable names, not their values, so a single cached entry serves many parameterized inputs. This is equivalent to faena's TOML skills with parameters. — https://docs.stagehand.dev/v3/best-practices/caching.md
- (0-3) In WAA, the best multimodal OS agent evaluated reaches only a 19.5% success rate. Humans reach 74.5%, which leaves a large gap for Windows desktop agents. — https://icml.cc/virtual/2025/poster/45035
- (0-3) On OSWorld, CoAct-1 reaches a 60.76% success rate (100-step budget) and needs 10.15 steps per task on average. Agent S2.5 w/ o3 scores 55.98% and GTA-1 w/ o3 scores 53.07%. GTA-1 averages 15.22 steps and UI-TARS 14.90. — https://arxiv.org/pdf/2508.03923v3
- (1-2) Skill APIs synthesized by a strong agent transfer to weaker agents and improve their WebArena success by up to 54.3%. This supports building a skill library with a strong model (e.g. Claude) and replaying it with a weaker local model. — https://arxiv.org/abs/2504.07079