# Documentación de Scraping en SitraMed

> Redactada para otra instancia de Claude que retoma el desarrollo de este proyecto.
> Referencia de código: `Meva.Rt.Infrastructure.SitraMed\`
> Fecha: 2026-06-23

---

## Qué es SitraMed y qué se extrae

SitraMed (`https://sitramed.mevaterapia.com.ar`) es el sistema de gestión clínica de Meva Terapia. Es una **web app interna** que no tiene API pública. Toda la integración es web scraping con **Playwright** (Chromium headless).

Se extraen tres tipos de datos:

| Datos | URL de SitraMed | Extractor |
|---|---|---|
| Pacientes en seguimiento (por etapa y centro) | `/follow_up_search` | `SitraMedFollowUpExtractor` |
| Agenda de equipos de irradiación | `/reception/appointments/machine` | `SitraMedAgendaExtractor` |
| Agenda de tomógrafos | `/reception/appointments/tomograph` | `SitraMedTomographExtractor` |
| GUIDs de pacientes ya atendidos | `/reception/appointments/machine` | `SitraMedAttendedPatientsExtractor` |
| HC numérico desde GUID interno | `/medical_histories/{guid}/overview` | `SitraMedPatientHcFetcher` |

---

## Arquitectura general de la capa de scraping

```
SitraMedAgendaExtractor          (IAgendaExtractor)
SitraMedFollowUpExtractor        (IFollowUpExtractor)
SitraMedTomographExtractor       (ITomographAgendaExtractor)
SitraMedAttendedPatientsExtractor (IAttendedPatientsExtractor)
SitraMedPatientHcFetcher         (IPatientHcResolver)
    └─ todos usan PlaywrightSitraMedClient (el único que sabe de Playwright)
          └─ abre sesión, hace login una sola vez, devuelve IBrowserContext
```

Los extractores de alto nivel no conocen Playwright. Solo reciben `FollowUpHtmlSnapshot` / `AgendaHtmlSnapshot` / `TomographAgendaHtmlSnapshot` con HTML crudo y opcionalmente `DomRows` ya parseados.

---

## PlaywrightSitraMedClient — el corazón del scraping

**Archivo:** `PlaywrightSitraMedClient.cs`

### Sesión y paralelismo

```csharp
private async Task<PlaywrightSession> CreateLoggedPageAsync(CancellationToken ct)
```

- Crea un `IBrowserContext` y hace **un único login**.
- Los métodos que paralelizan (Follow-up y Agenda de equipos) crean múltiples `IPage` que **comparten cookies del mismo contexto** — no hay re-login.
- `SemaphoreSlim(MaxParallelPages = 2)` limita a 2 páginas corriendo en simultáneo.
- `Task.WhenAll` sobre todas las combinaciones centro×etapa (follow-up) o máquina×fecha (agenda).

**Tomógrafos** son distintos: corren secuencialmente (`foreach`) con una sola página porque Phoenix LiveView mantiene estado de servidor por sesión y el paralelismo con múltiples páginas causaría interferencia.

### Disposable pattern

`PlaywrightSession : IAsyncDisposable` cierra contexto → browser → playwright en ese orden. Se usa siempre con `await using`.

---

## Login

```
URL: https://sitramed.mevaterapia.com.ar/session/new
```

Credenciales en env vars: `MEVA_SITRAMED_USER` / `MEVA_SITRAMED_PASSWORD`.

El login prueba múltiples selectores CSS en cascada para cada campo (el sitio puede cambiar IDs):

```
Email: #user_email | input[name='email'] | input[name='session[email]'] | input[type='email']
Pass:  #user_password | input[name='password'] | ... | input[type='password']
Btn:   .is-flex > .button:nth-child(1) | button[type='submit'] | button:has-text('Ingresar')
```

Si tras el submit la URL sigue siendo `/session/new`, lanza `InvalidOperationException`.

### Normalización de labels de dropdown

`SelectFirstByLabelAsync` usa `Normalize()` — convierte a Form D (descompone diacríticos), filtra `NonSpacingMark`, lowercase — para que "MEVA Central" matchee "Meva-Central" o variantes con tilde. Es la única forma robusta de seleccionar opciones cuando SitraMed cambia el texto.

---

## Seguimiento de pacientes (Follow-up)

### URL y formulario

```
https://sitramed.mevaterapia.com.ar/follow_up_search
```

Para cada combinación `(centro, etapa)`:
1. Navega a la URL
2. Selecciona centro en `#filters_attention_center_id`
3. Selecciona micro-status por **value** (`stage.SitraMicroStatus`); si falla, por label visible
4. Hace click en el botón "Buscar seguimientos" (prueba 13 selectores en cascada)
5. Fallback si click falla: `form.requestSubmit()` vía JS

### Espera del resultado

`WaitForFollowUpSearchEffectAsync` — bucle activo mientras no se detecte:
- Filas en `#follow-up-tables tbody tr` (o variantes)
- Mensaje de cero resultados ("No se encontraron", "sin resultados", "no hay pacientes")
- Cambio en el HTML respecto al baseline pre-búsqueda

Timeout configurable via `MEVA_SITRAMED_TIMEOUT_SECONDS` (default 30s). Al timeout, continúa en lugar de lanzar — el extractor downstream maneja HTML vacío.

### Datos extraídos por fila de follow-up (DOM parsing)

`ParseFollowUpRowsAsync` recorre cada `<tr>` de `#follow-up-tables tbody`:

| Campo | Fuente |
|---|---|
| `PatientName` | `cells.Nth(2).InnerTextAsync()` |
| `SitraMedId` | Primer `<td>` cuyo textContent matchea `^\d{1,3}-\d{4,7}-\d{1,3}$`; fallback al GUID del href |
| `SitraMedGuid` | `href` del link `a[href*='overview']` en la celda de nombre → regex `medical_histories/([^/]+)/overview` |
| `AssignedPhysicist` | `select[id*="physicist"]` → `options[selectedIndex].text` |
| `TreatmentZone` | Ver sección "Extracción de técnica" abajo |
| `Priority` | `cells.Nth(0)` (columna 0 es prioridad numérica) |
| `StageEntryDate` | `FollowUpDateParser.ExtractStageEntryDate(rowHtml, stage.Code)` |
| `TomographyDate` | `FollowUpDateParser.ExtractTomographyDate(rowHtml)` |
| `ResponsibleDoctor` | `FollowUpDateParser.ExtractResponsibleDoctor(rowHtml)` |
| `PostponedUntil` | `FollowUpDateParser.ExtractPostponedUntil(rowHtml)` |

### Extracción de técnica de tratamiento (TreatmentZone)

**Fuente primaria:** link `a[href*="conduct_definitions"]` en cualquier `<td>` de la fila.

**Fallback por keywords:** busca en cada `<td>` (desde índice 2) por palabras clave:
```
['tridimensional', '3d', 'modulada', 'imrt', 'sbrt', 'igrt', 'tbi', 'irradiaci',
 'radiocirug', 'vmat', 'arco', 'braquiterapia', 'intraoperatoria', 'iort', 'rxcx']
```

**Regla crítica:** salta celdas que contengan `.modal` o `button.modal-button`. La celda "Comunicaciones Internas" (índice 5) contiene el HTML completo del modal con notas históricas que pueden mencionar técnicas anteriores — sin esta exclusión, un paciente VMAT podría clasificarse como "3DC" si sus notas mencionan esa técnica.

BQT/IORT son "secundarias" — si se encuentran pero también hay otro keyword primario, se usa el primario.

---

## Agenda de equipos de irradiación

### URL y formulario

```
https://sitramed.mevaterapia.com.ar/reception/appointments/machine
```

Para cada máquina:
1. Selecciona centro en `#search_center_id`
2. **Espera activa** a que `#search_machine_id` tenga `options.length > 1` (AJAX rellena el dropdown tras seleccionar centro — sin esta espera se selecciona sin equipo y devuelve 0 pacientes)
3. Selecciona equipo por `machine.SitraName` en `#search_machine_id`
4. Llena fecha en `#search_date` con formato `yyyy-MM-dd`
5. Presiona Enter, espera `NetworkIdle`
6. Espera a que aparezca `#machineDrag` o `#machine_drag` o `table tbody tr`

### Parseo de filas de agenda (DOM)

`ParseAgendaRowsAsync` itera `#machineDrag tbody tr`:

**Filtros de filas a descartar:**
- `data-type` contiene "Finalizado" — tratamientos finalizados que quedan en gris
- `fechaFin` (offset+9) es anterior a la fecha pedida — también son past-treatment remnants

**Columnas (tras saltear columna vacía `signs`):**
```
offset+0  → StartTime (hora inicio)
offset+1  → PatientName
offset+3  → Priority
offset+7  → Treatment
offset+10 → EndTime (hora fin)
```

Si hay menos de 11 celdas, usa heurística `LooksLikePersonName` para encontrar el nombre.

**GUID del paciente:** extrae `href` del link `a[href*='overview']` en la fila.

### Parseo regex (fallback HTML)

`ParseAgendaSnapshots` en `SitraMedAgendaExtractor` usa una regex compleja:
```csharp
"<tr(?<trAttrs>[^>]*)>\s*(?:<td[^>]*>(?:\s|<[^>]+>)*</td>\s*)?<td[^>]*>(?<inicio>...)...</td>"
```
Captura grupos: `trAttrs`, `inicio`, `paciente`, `equipo`, `prioridad`, `observaciones`, `institucion`, `tipo`, `tratamiento`, `fechaInicio`, `fechaFin`, `horaFin`, `estado`.

Filtra filas con fondo gris detectado en `trAttrs` (color gris = turno de otro día mezclado).

**Detección de gris (`IsGreyBackground`):**
- Literal: "gray", "grey", "silver"
- Hex `#RRGGBB`: `max-min < 25 && max > 100 && max < 245`
- RGB/RGBA: misma fórmula

### Múltiples fechas (scrape-upcoming)

`DownloadAgendaPagesForDatesAsync` paralleliza combinaciones fecha×máquina con el mismo `SemaphoreSlim(2)`.

---

## Agenda de tomógrafos

### Quirk crítico: Phoenix LiveView y el orden de selección de fecha

```
URL: https://sitramed.mevaterapia.com.ar/reception/appointments/tomograph
```

SitraMed usa **Phoenix LiveView** en el formulario de tomógrafos. LiveView mantiene estado en el servidor con `phx-debounce="blur"` en el input de fecha.

**El problema:** si se selecciona el tomógrafo primero y luego la fecha, el `phx-change` del tomógrafo se envía al servidor con la fecha anterior (hoy). El servidor ignora el cambio de fecha DOM que todavía no se comunicó.

**La solución (orden obligatorio):**
1. Seleccionar centro (AJAX rellena tomógrafo dropdown)
2. `await WaitForTimeoutAsync(600)` — esperar que se pueble
3. **Setear la fecha ANTES de seleccionar el tomógrafo:**
   - Intentar via flatpickr API: `di._flatpickr.setDate(new Date(...), false)` con `triggerChange=false`
   - Fallback si no hay flatpickr: `FillFirstAsync` con ISO `yyyy-MM-dd`
4. **Disparar blur** para que LiveView sincronice la fecha al servidor:
   ```js
   di?.focus(); di?.blur();
   ```
5. `await WaitForTimeoutAsync(400)` + `WaitForLoadStateAsync(NetworkIdle, 8000)`
6. **Ahora** seleccionar el tomógrafo — el `phx-change` lleva la fecha ya sincronizada

### Parseo de filas de tomógrafo

`ParseTomographAgendaRowsAsync` escanea **todas las celdas** a partir de `offset+2` buscando un keyword de tratamiento válido (no asume posición fija):
```
["3D", "IMRT", "SBRT", "RxCx", "Modulada", "Tridimensional", "Braquiterapia", 
 "Radiocirug", "Intraoperatoria", "IORT", "IGRT", "TBI"]
```

Excluye celdas que contengan "actividad" (para no confundir con turnos de actividad interna).

`StripSitraMedAlerts`: SitraMed agrega texto de alerta al campo tipo-turno (ej: "NO REGISTRA CONSENTIMIENTO MÉDICO FIRMADO"). Se recorta desde " NO REGISTRA".

---

## FollowUpDateParser — parseo de fechas de etapa desde HTML

**Archivo:** `FollowUpDateParser.cs`

SitraMed embebe **comentarios HTML** como marcadores de sección en cada fila de seguimiento:

```html
<!-- f0 -->, <!-- f1 -->, <!-- f2 -->, <!-- f3 -->, <!-- f4 -->, 
<!-- f5 -->, <!-- f6 -->, <!-- f7 -->, <!-- f8 -->, <!-- f12 -->, <!-- f13 -->
```

### Estructura de columnas por sección

```
f0: F.PrimeraConsulta(1)  Nombre(2)        Institución(3)  MédicoHC(4)     ComunInternas(5)
f1: F.Solicitud(1)        Usuario(2)        F.DefConduct(3) Usuario(4)
f2: F.Pedido(1)           F.Recepción(2)   F.Autorización(3) F.Pospuesto(4) Acciones(5)
f3: Nro.HC(1)             F.Ingreso(2)     Tratam-Zona(3)
f4: Pospuesto(1)          F.TAC(2)         Contraste(3)    Físico(4)       Médico(5)
    Técnico(6)            MarcóISO(7)      CentroDerivación(8) TurnosAsignados(9) Acciones(10)
f5: Patología(1)          Delimitado(2)    F.Delimitado(3) Acciones(4)
f6: Etapa(1)              F.FinEtapa(2)    ReplanifResp(3) F.AsignaciónResp(4)  FísicoP(5)
    F.Realización(6)      AprobaciónMédico(7) SistMod(8)  QAPaciente(9)   Replanific.(10)
    AprobaciónFísico(11)  Acciones(12)
f7: Físico(1)             F.Cálculo(2)     NoCorresp(3)    Acciones(4)
f8: Físico(1)             F.Chequeo(2)     RespProtecciones(3) F.Protecciones(4)
f12: F.Turno(1)           Equipo(2)        Acciones(3)
f13: MédicoCorrección(1)  FechaCorrección(2) MédicoAprueba(3) FechaOK(4)
```

### Mapeo etapa → (sección, td-index, estado-turno)

| Etapa | Sección | TD # | Estado turno |
|---|---|---|---|
| F1 | `<!-- f0` | 1 | — |
| F2A | `<!-- f1` | 3 | — |
| F2B | `<!-- f2` | 1 | — |
| F3 | `<!-- f2` | 2 | — |
| F4 | `<!-- f3` | 2 | — |
| F4B | `<!-- f4` | 9 | "Pendiente" |
| F5 | `<!-- f4` | 9 | "Atendido" |
| F6A | `<!-- f5` | 3 | — |
| F6B | `<!-- f6` | 4 | — |
| F6C | `<!-- f6` | 6 | — |
| F6D | `<!-- f6` | 7 | — |
| F6F | `<!-- f6` | 8 | — |
| F6G | `<!-- f6` | 9 | — |
| F7A | `<!-- f7` | 2 | — |
| F7B | `<!-- f6` | 11 | — |
| F7C | `<!-- f6` | 11 | — |
| F8 | `<!-- f8` | 2 | — |
| F9 | `<!-- f8` | 2 | — |
| F10 | `<!-- f12` | 1 | — |
| F11 | `<!-- f13` | 4 | — |

### Comportamiento de fallback

`ExtractStageEntryDate(rowHtml, stageCode)` itera hacia atrás por `StageOrder` si no encuentra fecha para la etapa actual. Así un paciente en F6B que aún no tiene fecha F6B asignada puede retornar la fecha de F6A o F5.

### Columna TurnosAsignados (F4B y F5)

La columna 9 de sección f4 contiene entradas del tipo:
```
DD/MM/YYYY HH:MMhs - TECNICA - Estado
```
donde Estado puede ser "Pendiente" (turno agendado, F4B) o "Atendido" (tomo realizada, F5).

Se extrae la entrada más reciente con el estado deseado.

### Otros datos extraídos

| Método | Descripción | Sección/TD |
|---|---|---|
| `ExtractTomographyDate` | Turno "Atendido" más reciente en TurnosAsignados | f4, td 9 |
| `ExtractResponsibleDoctor` | Médico responsable (columna "Usuario") | f1, td 4 |
| `ExtractPostponedUntil` | Fecha de postergación por paciente | f4, td 1 |

---

## Pacientes atendidos (SitraMedAttendedPatientsExtractor)

Descarga el HTML de la agenda de un equipo y fecha específicos. Luego busca filas que contengan un botón `<button>Atendido</button>` y extrae el GUID del link `medical_histories/{guid}/overview` en esa misma fila.

**Uso:** tab Derivación — detecta qué pacientes ya recibieron su sesión en el equipo fuera de servicio para no derivarlos.

---

## Resolución GUID → HC (SitraMedPatientHcFetcher)

Algunos pacientes en SitraMed tienen un GUID interno (`0269ce85-...`) en lugar de HC numérico (`1-117505-0`). Para correlacionar con ARIA se necesita el HC.

El fetcher navega a `/medical_histories/{guid}/overview` y busca el HC con dos estrategias:

1. **Match exacto en nodos hoja:** `querySelectorAll('td, dd, dt, span, p, h1...')` — busca textContent que sea exactamente el patrón HC.
2. **TreeWalker:** recorre todos los nodos de texto buscando el patrón `\b(\d{1,3}-\d{4,7}-\d{1,3})\b`.

El HC encontrado se valida contra `^\d{1,3}-\d{4,7}-\d{1,3}$` antes de guardarse. El resultado se persiste en `data/guid_hc_map.json`.

---

## SitraMedRuntimeOptions — configuración

| Propiedad | Env var | Default | Descripción |
|---|---|---|---|
| `Username` | `MEVA_SITRAMED_USER` | — | Email de login |
| `Password` | `MEVA_SITRAMED_PASSWORD` | — | Contraseña |
| `Headless` | `MEVA_SITRAMED_HEADFUL` | `true` | Si es `false`, muestra el browser |
| `EnableDiagnostics` | `MEVA_SITRAMED_DIAGNOSTICS` | `false` | Guarda screenshots antes/después de búsqueda |
| `SaveAgendaHtmlCapture` | `MEVA_SITRAMED_SAVE_AGENDA_HTML` | `false` | Guarda HTML crudo de agenda |
| `TimeoutSeconds` | `MEVA_SITRAMED_TIMEOUT_SECONDS` | 30 | Timeout Playwright por página |
| `UseLocalExamplesFallback` | — | `false` | Usa archivos HTML locales si no hay credenciales |
| `DiagnosticsDirectory` | — | `data/diagnostics/` | Carpeta para screenshots diagnósticos |

### Diagnósticos

Con `MEVA_SITRAMED_DIAGNOSTICS=true`, para cada combinación centro/etapa de follow-up guarda:
- `01_before_search.png` — screenshot antes de click Buscar
- `01_before_search.html` — HTML antes de click
- `02_after_search.png` — screenshot post-resultado
- `02_after_search.html` — HTML post-resultado
- `metadata.json` — URL final, title, valor de selects, acción del botón usado

En producción se activa en el registro de Windows (`HKLM:\SYSTEM\CurrentControlSet\Services\MevaRT\Environment`).

---

## DOM vs HTML regex — cuál tiene preferencia

Todos los extractores siguen el mismo patrón:

```csharp
if (snapshot.DomSnapshots is { Count: > 0 })
    return snapshot.DomSnapshots;   // preferido: ya parseado por Playwright en vivo
return ParseXxxSnapshots(snapshot); // fallback: regex sobre HTML crudo
```

La extracción DOM con Playwright es más robusta (puede leer elementos ocultos, maneja JS-rendered content, extrae GUIDs directamente de hrefs). El fallback regex existe por compatibilidad y como seguridad ante cambios de estructura.

---

## Flujo completo de follow-up

```
BootstrapService.BuildAsync()
  └─ SitraMedFollowUpExtractor.ExtractAsync()
       └─ PlaywrightSitraMedClient.DownloadFollowUpPagesAsync(centers, stages)
            ├─ CreateLoggedPageAsync()  ← 1 login
            ├─ SemaphoreSlim(2) + Task.WhenAll sobre N combinaciones centro×etapa
            │    cada tarea:
            │    ├─ DownloadFollowUpAsync()
            │    │    ├─ GotoAsync(follow_up_search)
            │    │    ├─ SelectFirstByLabelAsync(centro)
            │    │    ├─ SelectFirstSafeAsync(micro_status, byValue)
            │    │    ├─ TriggerFollowUpSearchAsync()
            │    │    ├─ WaitForFollowUpSearchEffectAsync()
            │    │    └─ TryExtractFollowUpDomAsync()  → List<FollowUpPatientDomRow>
            │    └─ devuelve FollowUpHtmlSnapshot { Html, DomRows }
            └─ retorna IReadOnlyList<FollowUpHtmlSnapshot>
       └─ ParseRemoteSnapshots(snapshots)
            para cada snapshot:
            ├─ si DomRows.Count > 0 → convierte a ProcessPatientSnapshot (días hábiles)
            └─ else → ParseSnapshots(htmlSource) con regexes
            dedup por PatientId|StageCode → ordenado por centro/etapa/nombre
```

---

## Quirks y bugs conocidos (historial)

### Técnica RC y SBRT — variantes sin tilde (sesión 2026-06-19b)

`TreatmentClassifier.Classify` y el regex `TreatmentZoneRegex` en el extractor usan `OrdinalIgnoreCase`. En C#, `OrdinalIgnoreCase` NO normaliza diacríticos: `"Radiocirugía"` (con `í` U+00ED) no es igual a `"radiocirugia"`. Se agregaron variantes sin tilde explícitamente. Todos los pacientes RC caían a "3DC" antes de este fix.

### Celda Comunicaciones Internas (sesión 2026-06-18)

La extracción de técnica por fallback de keywords encontraba la celda índice 5 ("Comunicaciones Internas") que contiene el modal HTML completo con notas históricas. Si las notas mencionaban técnicas antiguas, se clasificaba mal al paciente. Fix: saltar celdas con `.modal` o `button.modal-button`.

### "Intensidad Modulada" como alias de IMRT (sesión 2026-06-18)

SitraMed muestra "Intensidad Modulada" en lugar de "IMRT" o "VMAT". Se agregó el alias al clasificador.

### Orden de selección en tomógrafo — LiveView (sin date específica)

Ver sección "Quirk crítico: Phoenix LiveView" arriba. Debe setearse la fecha y disparar blur ANTES de seleccionar el tomógrafo; de lo contrario el servidor retorna turnos de hoy para cualquier fecha pedida.

### Filas "Finalizado" en agenda

SitraMed mezcla en la vista del día actual algunos turnos de tratamientos ya finalizados (pasados), renderizados en gris. Se filtran por:
1. `data-type` contiene "Finalizado"
2. `fechaFin` (columna estimada de fin de tratamiento) anterior a la fecha solicitada

### Dedup de pacientes

`ParseRemoteSnapshots` agrupa por `PatientId|StageCode` y queda con el primero (`group.First()`). Un mismo paciente puede aparecer en múltiples snapshots si está en múltiples centros o si la paginación de SitraMed devuelve duplicados.

---

## Tipos de datos de salida relevantes

### `FollowUpPatientDomRow` (resultado DOM de follow-up)

```csharp
PatientName      string   // Nombre completo
SitraMedId       string   // HC numérico (1-XXXXXX-X) o GUID si no hay HC
SitraMedGuid     string   // GUID interno (ej: 0269ce85-...)
AssignedPhysicist string  // Físico asignado (null si "-- Seleccione --")
TreatmentZone    string   // Texto de técnica (antes de clasificar)
FirstConsultDate string   // Fecha de inicio de etapa (dd-MM-yyyy)
TomographyDate   DateOnly? // Fecha de tomosimuación (turno Atendido en f4)
ResponsibleDoctor string? // Médico responsable (f1, td4)
PostponedUntil   DateOnly? // Hasta cuándo está postergado (f4, td1)
Priority         int?     // Prioridad (1, 2, 3)
CenterId/CenterName/StageCode
```

### `AgendaHtmlSnapshot` (resultado de agenda de equipos)

```csharp
CenterName           string
MachineDisplayName   string
AgendaDate           DateOnly
Html                 string        // HTML crudo completo
HasScrapingError     bool          // true si Playwright lanzó excepción
DomSnapshots         List<MachineAppointmentSnapshot>? // preferido si presente
```

### `MachineAppointmentSnapshot` (turno agendado)

```csharp
CenterName    string
MachineName   string   // DisplayName del equipo o tomógrafo
PatientName   string
AgendaDate    DateOnly
StartTime     string   // "08:00"
EndTime       string   // "08:15" (vacío para tomógrafos)
Treatment     string   // Clasificado: "VMAT", "SBRT", "3DC", etc.
SitraMedGuid  string?  // GUID del link overview
Priority      int?
```

---

## Notas para desarrollo

- **No mockear la sesión de Playwright**: Playwright.CreateAsync() abre un proceso de Chromium real. En tests unitarios, usar HTML local + `ParseAgendaSnapshots` directamente.
- **El fallback local** existe: con `UseLocalExamplesFallback=true`, el follow-up extractor lee archivos `.html` del directorio `Ejemplos seguimiento/` si no hay credenciales.
- **Timeout de Playwright** se configura por página: `page.SetDefaultTimeout` y `page.SetDefaultNavigationTimeout` — en segundos × 1000.
- **`CanUseRemoteScraping()`** retorna false si no hay Username o Password → todos los métodos que usan Playwright retornan vacío en lugar de fallar.
- **El log de errores** va a `Console.Error` con prefijo `[SitraMed]`. No hay logger inyectado — los mensajes van al stderr del proceso (visible en el log del Windows Service).
