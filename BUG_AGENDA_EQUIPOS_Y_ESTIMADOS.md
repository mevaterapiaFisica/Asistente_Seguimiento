# Hallazgos: agenda de equipos, estimados, y scraping SitraMed

**Fecha:** 2026-09-15/16
**Estado:** 5 bugs encontrados y arreglados (pusheados a `main`), 1 pendiente sin resolver, exploración
profunda pendiente (este doc es el punto de partida para esa exploración).

**Redactado para otra instancia de Claude que retoma el trabajo.** Contexto: usuario reportó paciente
(CAMPOLO, Carlos Alberto, HC `1-119858-0`) con turno real confirmado en SitraMed que no aparecía en la
agenda de MevaDash, y en cambio salía sugerido como "estimado" en fecha equivocada. La investigación de
ese caso puntual destapó una cadena de bugs más grandes. Este documento junta todo lo encontrado y da un
plan para seguir buscando bugs similares en otras partes del scraping/agenda que todavía no se revisaron.

---

## Resumen de bugs encontrados (orden cronológico del debugging)

### 1. Loop de estimados no excluía pacientes con turno real futuro
**Archivo:** `Meva.Rt.Web/Program.cs` (endpoint `/api/agenda`)
**Commit:** `2ce6406`

El loop que genera turnos "estimados" (proyección desde `dashboard_bootstrap.FollowUpPatients`) nunca
chequeaba si el paciente ya tenía un turno **real** (scrapeado) en alguna fecha futura. Resultado: el
paciente aparecía duplicado — turno real correcto + estimado en una fecha distinta (a veces muy
alejada) — o el estimado aparecía como si fuera la única referencia cuando el real estaba directamente
ausente de los datos (ver bug #4).

**Impacto medido:** cruzando `dashboard_bootstrap.json` contra todos los `agenda_*.json` futuros, **73
pacientes** tenían turno real Y seguían generando estimado.

**Fix:** antes de generar el estimado, se arma un `HashSet<string>` de HC con turno real en cualquier
fecha futura scrapeada (via `guid_hc_map.json`), y se skipea a esos pacientes.

### 2-3. Intentos de fix por timing (NO eran la causa raíz — dejados igual, no hacen daño)
**Commits:** `7bbfb77`, `26c4a7a`

Antes de encontrar la causa real (bug #4), se probaron dos mitigaciones que parecían funcionar en
pruebas manuales aisladas pero fallaban consistentemente en producción:
- Aumentar el wait fijo post-`blur()` de 400ms a 800ms.
- Reemplazar el wait fijo por un poll que espera a que el HTML de la tabla cambie de verdad (en vez de
  asumir que un tiempo fijo alcanza) — esto es una mejora real independientemente del bug de fondo,
  porque Playwright's `NetworkIdle` **no trackea WebSocket** y SitraMed usa Phoenix LiveView (empuja
  actualizaciones por WebSocket, no HTTP). Se mantiene en el código.

### 4. **Causa raíz real: orden de selección de equipo vs. fecha en la agenda de equipos**
**Archivo:** `Meva.Rt.Infrastructure.SitraMed/PlaywrightSitraMedClient.cs` — `DownloadAgendaHtmlAsync`
**Commit:** `8c8b32f`

SitraMed usa Phoenix LiveView (`phx-change="machine_calendar"`, `phx-debounce="blur"`) en el form de
agenda de equipos. El código seleccionaba el equipo **antes** de setear la fecha. Seleccionar el equipo
dispara un `phx-change` que resetea/pisa cualquier fecha pendiente no confirmada — el server siempre
terminaba usando la fecha default (**hoy**), sin importar qué mostrara el input del DOM.

Este mismo patrón (fecha antes del sub-item, con blur) **ya estaba documentado y arreglado en el
scraper de tomógrafos** (`DownloadTomographAgendaHtmlAsync`, ver comentario ahí y en
`SITRAMED_SCRAPING.md`), pero nunca se replicó al de equipos. Por eso el bug era **sistemático, todos
los días, todas las máquinas** de agenda de equipos — no un caso aislado de un paciente.

**Síntoma en los datos:** el conteo de pacientes por máquina/día quedaba **fijo** (siempre el mismo
número, siempre el mismo roster — literalmente el roster de "hoy" repetido bajo la etiqueta de
cualquier fecha futura pedida), en vez de variar día a día como es real. Pacientes con turnos
exclusivos de una fecha futura (como CAMPOLO) simplemente no entraban nunca al scrape.

**Fix:** reordenar a fecha+blur **antes** de seleccionar el equipo (mismo orden que tomógrafos).

**Cómo se confirmó (metodología reutilizable, ver sección más abajo):** capturando el HTML real
devuelto por Playwright vía el endpoint `/api/scraping/test-agenda` + `MEVA_SITRAMED_SAVE_AGENDA_HTML=true`,
se vio literalmente `<input id="search_date" value="2026-09-15">` (hoy) en la respuesta, pese a haber
pedido `2026-09-16`. Con el fix, el value coincide con la fecha pedida.

### 5. CenterName de estimados no reflejaba el equipo asignado
**Archivo:** `Meva.Rt.Web/Program.cs`
**Commit:** `25e2b79`

Al generar un turno estimado, `CenterName` se tomaba de `patient.CenterName` — el centro de
**registro/tratamiento** del paciente en el seguimiento de SitraMed — en vez de derivarlo del **equipo
realmente asignado** (`machineName`, que puede venir de ARIA con `PlannedMachineDisplayName`).

Con pacientes que se ingresan por un centro y se derivan a otro (confirmado por el usuario como causa
real), quedaban con `CenterName="MEVA-Central"` pero `MachineName="QUILMES - Equipo 2"` — inconsistente.
El dashboard agrupa por centro, así que esos estimados quedaban invisibles en la vista de Quilmes pese
a estar asignados a un equipo de Quilmes.

**Caso medido:** de 14 estimados en Quilmes Equipo 2 para el 23/09, 5 tenían `CenterName=MEVA-Central`
(4 pacientes + 1 duplicado, ver bug pendiente abajo).

**Fix:** `CenterName` del slot estimado ahora sale de `machines.FirstOrDefault(m => m.DisplayName ==
machineName)?.CenterName`, con fallback a `patient.CenterName` solo si el equipo no se encuentra en la
configuración.

### 6. Proyección de fecha estimada no consideraba cuánto tiempo lleva el paciente en su etapa
**Archivo:** `Meva.Rt.Web/Program.cs`
**Commit:** `25e2b79`

`estimatedStart = hoy + business_days(suma de ExpectedDays de todas las etapas restantes)` — es
**idéntico para todos los pacientes de la misma etapa**, sin restar cuánto tiempo ya lleva cada uno ahí.
Resultado observable: todos los pacientes en etapa F9 (ejemplo) caían exactamente en la misma fecha
proyectada, generando un salto artificial (ej: nada de estimados el 21/22, y de golpe 9-14 el 23/09).

El sistema ya calculaba `ProcessPatientSnapshot.DaysInStage` / `ExpectedDaysInStage` (usado en otras
partes — alertas, tendencias — pero no en esta proyección).

**Fix:** para la etapa actual del paciente, se resta `DaysInStage` de `ExpectedDays` de esa etapa
(`Math.Max(stages[stageIdx].ExpectedDays - patient.DaysInStage, 0)`) antes de sumar el resto de las
etapas siguientes completas. Esto escalona la proyección según el tiempo real que cada paciente lleva
en su etapa, en vez de asumir que todos arrancaron hoy.

---

## Bug pendiente — NO resuelto todavía

### Paciente duplicado en `FollowUpPatients` bajo dos etapas simultáneas

Caso: SARCHIONI, Natalia Elizabeth (`1-118582-0`) aparece en `bootstrap.FollowUpPatients` **dos veces**
el mismo día — una vez en etapa F9 ("Placa Verificadora") y otra en F11 ("Turno Equipo") — generando
**dos slots estimados** para el mismo paciente, mismo equipo, mismo día.

`SitraMedFollowUpExtractor.ParseRemoteSnapshots` dedupea por `PatientId|StageCode`
(`SitraMedExtractors.cs:321-327`), así que si SitraMed realmente tiene a la paciente cargada en dos
etapas distintas simultáneamente (dato real, o entrada vieja no depurada en el sistema de SitraMed), no
hay dedup que lo evite — el `StageCode` es distinto en cada fila, así que la key de dedup no colisiona.

**No investigado:**
- ¿Es un dato real de SitraMed (paciente genuinamente en dos etapas a la vez, por algún motivo clínico
  válido) o una entrada vieja/huérfana que debería haberse cerrado?
- Si es dato real: ¿el loop de estimados debería dedupear por paciente y quedarse con la etapa más
  avanzada (la más cercana a tratamiento)?
- ¿Cuántos pacientes en todo el sistema están en esta situación (no solo Quilmes)?

**Sugerencia de arranque:** comparar en vivo con `/follow_up_search` en SitraMed (filtrando por HC
`1-118582-0` o navegando a `medical_histories/d9c8da7c-2e8a-46b7-b14c-4af45d8fe2ec/overview`) si la
paciente está genuinamente en dos micro-estados a la vez.

---

## Metodología que funcionó — reutilizar para seguir buscando bugs

### 1. Nunca confiar en pruebas manuales de browser como ground truth sin recargar la página

Se perdió bastante tiempo con falsos negativos/positivos porque reutilizar una misma pestaña de
`agent-browser` entre varias pruebas (sin `open` fresco) deja estado corrupto — foco perdido, popups de
calendario reabiertos, fecha revertida. **Cada prueba de fecha/máquina distinta necesita navegación
fresca** (`agent-browser open <url>` de nuevo, no reusar la pestaña).

### 2. El endpoint `/api/scraping/test-agenda` es la herramienta más confiable

```
POST /api/scraping/test-agenda?machine=<DisplayName>&date=<yyyy-MM-dd>
```

Corre el código real de producción (`RunAgendaTestAsync` → `DownloadAgendaHtmlAsync` +
`TryExtractAgendaDomAsync`), aislado, sin concurrencia, con login fresco. Devuelve `agendaDomRows`
(conteo) y opcionalmente guarda el HTML completo si `MEVA_SITRAMED_SAVE_AGENDA_HTML=true` está seteado
en el proceso (`capturedFilePath` en la respuesta, carpeta `data/agenda-captures/`).

Esto fue lo que finalmente destapó el bug #4 — comparar manualmente en browser daba resultados
contradictorios (a veces bien, a veces mal) porque yo mismo introducía condiciones de carrera distintas
cada vez; el endpoint aislado, en cambio, reproducía el bug de forma 100% consistente, lo cual fue la
pista de que no era timing sino un bug determinístico de orden.

### 3. Probar cambios de código sin tocar el servicio de producción

Para iterar rápido sin parar/reiniciar el servicio Windows `MevaRT` (que afecta a usuarios reales):

```powershell
# Publicar a una carpeta temporal separada
dotnet publish Meva.Rt.Web/Meva.Rt.Web.csproj -c Release -o C:\MevaRT_test --self-contained false

# Correr en un puerto alterno con las env vars necesarias
$env:MEVA_SITRAMED_USER = "..."          # ya suele estar seteada en el entorno
$env:MEVA_SITRAMED_PASSWORD = "..."
$env:MEVA_SITRAMED_NO_FALLBACK = "true"
$env:MEVA_DATA_DIR = "C:\MevaRT\data"    # reusa los datos reales, solo lectura para este propósito
$env:MEVA_SITRAMED_SAVE_AGENDA_HTML = "true"
$env:ASPNETCORE_URLS = "http://localhost:5099"
Start-Process dotnet -ArgumentList "C:\MevaRT_test\Meva.Rt.Web.dll" -WorkingDirectory "C:\MevaRT_test" -PassThru

# Pegarle al endpoint de test
curl -s -X POST "http://localhost:5099/api/scraping/test-agenda?machine=QUILMES%20-%20Equipo%202&date=2026-09-16"

# Matar el proceso y borrar la carpeta temporal al terminar
```

Esto dio vuelta el ciclo de "cambiar código → confirmar si arregla el bug real" en minutos en vez de
depender de que el usuario corra `publish.ps1` + `refresh.bat` (varios minutos cada vez, y el servicio
real queda ocupado con el scrape completo de 15 días × todas las máquinas).

### 4. `/agent-browser` para inspección visual/manual puntual

Login: `https://sitramed.mevaterapia.com.ar/session/new`, credenciales en env vars
`MEVA_SITRAMED_USER` / `MEVA_SITRAMED_PASSWORD` (ya están seteadas en el entorno, no hace falta
pedírselas al usuario). Agenda de equipos: `/reception/appointments/machine`. Sirve para:
- Ver visualmente qué hay en una fecha/máquina puntual y comparar contra lo que devuelve el JSON local.
- Inspeccionar la estructura DOM real cuando algo no cuadra con lo que el código espera (ej: contar
  columnas reales de una fila con `eval` + `querySelectorAll('td')`).
- Probar hipótesis de JS rápido (ej: `document.querySelector('#search_date').datepicker` expone la
  instancia del widget `vanillajs-datepicker` con métodos `setDate`/`getDate`, útil para explorar cómo
  reacciona el form a distintas formas de setear la fecha).

**Pero:** para verificar de forma confiable si el SCRAPER (no el humano) tiene un bug, preferir el
endpoint `/api/scraping/test-agenda` (punto 2) — es el código real, sin el margen de error de manejar
el browser a mano.

---

## Plan de exploración profunda — qué falta revisar

Este bug de "orden fecha vs. sub-entidad con LiveView" era sistemático en el scraper de **equipos**.
Vale la pena auditar el resto del scraping con la misma sospecha: **¿hay otros flujos que seleccionan
un campo dependiente de LiveView en el orden equivocado, o que asumen que un wait fijo alcanza cuando
LiveView empuja por WebSocket?**

Puntos concretos a revisar, con prioridad sugerida:

1. **Seguimiento de pacientes (`follow_up_search`)** — `DownloadFollowUpAsync` en
   `PlaywrightSitraMedClient.cs`. Selecciona centro y luego micro-status, dispara búsqueda por click.
   ¿Usa LiveView también? ¿El orden centro→microstatus puede tener el mismo problema que
   equipo→fecha? Si microstatus se resetea o el filtro de centro no queda aplicado bajo ciertas
   condiciones, pacientes de un centro podrían aparecer bajo otro, o faltar directamente — mismo tipo
   de bug que encontramos, en la fuente de datos que alimenta TODO el sistema de seguimiento/proyección
   (no solo agenda).

2. **Tomógrafos** — el fix de LiveView ya está aplicado ahí, pero **nunca se verificó día a día contra
   SitraMed en vivo** como se hizo acá con Quilmes Equipo 2. Repetir la comparación (conteo por
   tomógrafo/día, local vs. `/api/scraping/test-agenda` equivalente si existe, o comparación manual
   cuidadosa con navegación fresca) para varios días.

3. **Otros centros/equipos además de Quilmes** — el bug #4 era sistemático (afectaba a todas las
   máquinas), pero solo se verificó a fondo Quilmes Equipo 2. Vale la pena correr la misma comparación
   (`/api/scraping/test-agenda` vs. SitraMed en vivo) para al menos un equipo de cada centro
   (MEVA-Central, MEVA-Viamonte, SAN JUSTO, RT MEDRANO, CETRO) para descartar quirks específicos de
   centro (ej: SAN JUSTO Equipo 1 está fuera de servicio, ver `machineCapacities` vs
   `machineCapabilities` — puede interactuar raro con el scraping).

4. **`SitraMedAttendedPatientsExtractor`** — depende de la MISMA página de agenda de equipos
   (`DownloadAgendaPageHtmlForMachineAsync`). Revisar si comparte el mismo bug de orden fecha/equipo (no
   se tocó en el fix del punto 4 de arriba — **revisar si `DownloadAgendaPageHtmlForMachineAsync` tiene
   su propia lógica de selección o reusa `DownloadAgendaHtmlAsync` ya arreglada**).

5. **Dedup de pacientes en múltiples etapas simultáneas** (bug pendiente arriba) — cuantificar cuántos
   pacientes en todo el sistema (no solo Quilmes) están en esta situación, y decidir si el loop de
   estimados en `Program.cs` debería dedupear por paciente quedándose con la etapa más avanzada.

6. **Regla general a verificar en cualquier código nuevo de Playwright contra SitraMed:** si el form
   tiene un campo con `phx-debounce="blur"` (buscar el atributo en el HTML de la página), el orden de
   interacción importa y hay que setear ese campo primero + disparar `blur()` explícito + esperar a que
   el contenido realmente cambie (no un timeout fijo) antes de tocar cualquier otro campo del mismo
   form. Grep rápido por `phx-debounce` o `phx-change` en el HTML capturado de cada página nueva que se
   scrapee, antes de asumir que `FillAsync` + `Enter` alcanza.

---

## Archivos tocados en esta sesión

- `Meva.Rt.Web/Program.cs` — loop de estimados (exclusión por turno real, CenterName por equipo,
  proyección escalonada por `DaysInStage`).
- `Meva.Rt.Infrastructure.SitraMed/PlaywrightSitraMedClient.cs` — orden fecha/equipo en
  `DownloadAgendaHtmlAsync`, poll de cambio real de tabla en vez de wait fijo, estabilización de conteo
  de filas en `ParseAgendaRowsAsync`.

Ver también `SITRAMED_SCRAPING.md` (doc general de arquitectura de scraping) — **su sección de "Agenda
de equipos" todavía no refleja el fix del bug #4** (orden fecha antes de equipo); actualizarla si se
retoma este código, para que no quede desalineada con el código real.
