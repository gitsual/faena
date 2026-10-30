#!/usr/bin/env bash
# The local agent demo on the two example webs (start them first, see README.md).
# Every run is headless and leaves its folder under $FAENA_TAREAS.
set -u
FAENA=${FAENA:-faena.exe}
SALIDA=${SALIDA:-$PWD/demo-faena}
export FAENA_TAREAS="$SALIDA/tareas" FAENA_HABILIDADES="$SALIDA/habilidades"
NOTAS=http://127.0.0.1:8770/index.html
MENSAJES=http://127.0.0.1:8771/mensajes.html

paso() { echo; echo "=== $*"; }

paso "1. Razonando desde cero, y aprende la habilidad"
"$FAENA" hacer "Crea una nota con título «Comprar pan» y texto «dos barras de la panadería»" \
  --url "$NOTAS" --fondo --capturas --aprender crear-nota; echo "salida $?"

paso "2. La misma tarea con otros valores: usa la habilidad"
"$FAENA" hacer "Apúntame una nota: título «Llamar al fontanero», texto «el grifo de la cocina gotea»" \
  --url "$NOTAS" --fondo; echo "salida $?"

paso "3. Sin modelo"
"$FAENA" hacer "nota de la compra" --habilidad crear-nota --sin-modelo \
  --param "title=Comprar leche" --param "text=sin lactosa" --fondo --capturas; echo "salida $?"

paso "4. Algo irreversible: se para antes de enviar"
"$FAENA" hacer "Escríbele a Marta con asunto «Llego tarde» diciéndole que llego a las diez, y mándaselo" \
  --url "$MENSAJES" --fondo --capturas; echo "salida $? (3 = pendiente de autorizar)"

paso "5. Con permiso para enviar"
"$FAENA" hacer "Escríbele a Marta con asunto «Llego tarde» diciéndole que llego a las diez, y mándaselo" \
  --url "$MENSAJES" --fondo --permitir enviar; echo "salida $?"

paso "6. Borrar: se para"
"$FAENA" hacer "Borra todos los mensajes enviados" --url "$MENSAJES" --fondo; echo "salida $?"

echo; echo "Informes en $FAENA_TAREAS"
