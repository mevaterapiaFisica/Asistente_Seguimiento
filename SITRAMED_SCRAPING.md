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

**Desde 2026-11 el form es Phoenix LiveView** y el resultado carga de forma asíncrona (websocket) detrás
de un spinner `.loader-container`. El scraper ya no llena el form: navega directo a la URL de búsqueda.

Una vez por corrida (`ReadFollowUpCenterGuidsAsync`) se lee el dropdown `#filters_attention_center_id`
→ diccionario nombre-de-centro → GUID. Después, para cada `(centro, etapa)` (`BuildFollowUpUrl`):

```
/follow_up_search?filters[attention_center_id]=<GUID>&filters[micro_status]=<SitraMicroStatus>
 &filters[mode]=search&filters[report_type]=by_status
 &filters[from_date]=&filters[until_date]=&filters[medical_history_id]=&filters[treatment_type]=
```

**Todas las claves son obligatorias.** Sin `report_type`, o sin las 4 claves vacías, el LiveView no
termina y el loader queda para siempre (probado). El `micro_status=` suelto que agrega el sitio no hace falta.

### Espera del resultado

`WaitForFollowUpResultAsync` (poll 250 ms):
- Termina si no hay `.loader-container` y hay filas en `#follow-up-tables > tbody > tr`.
- Resultado vacío (no hay tabla ni mensaje): termina tras 3 chequeos seguidos sin loader y con `.phx-connected`.
- Si pasa `MEVA_SITRAMED_TIMEOUT_SECONDS` (default 30s) sin terminar → `TimeoutException` → `HasScrapingError`
  (antes seguía con HTML vacío = "0 pacientes" silencioso).

Post-condición (`DownloadFollowUpAsync`): tras cargar se relee `#filters_attention_center_id` y
`#filters_micro_status`; si no coinciden con lo pedido se reintenta una vez y luego se lanza error.

> **Bug 2026-11-05 (scraping de seguimiento devolvía 0 filas en todo):** la versión vieja seleccionaba
> centro/fase, hacía click y esperaba con `text=No hay` (que siempre matchea el "No hay resultados" del buscador
> de pacientes) o "el HTML cambió" (el spinner ya lo cambia). Capturaba la página con el spinner (~54 KB, sin tabla)
> y caía al fallback regex con 0 filas, sin error.

### Salud del scrape (cómo se evita fallar en silencio)

El scrape de seguimiento nunca debe pisar datos sanos con datos vacíos o parciales sin avisar. Capas:

1. **Canario** (`DownloadFollowUpPagesAsync`): la primera combinación corre sola, con 1 reintento. Si falla
   (form/query cambiado, red caída) se aborta todo en segundos y cada combinación queda `HasScrapingError`.
2. **Validación de estructura** (`ValidateFollowUpTableAsync`): si hay tabla, debe tener las columnas
   "Nombre y Apellido" y "Nro. HC" y filas de ≥20 celdas; si no, error explícito en esa combinación.
3. **Errores con mensaje**: `FollowUpHtmlSnapshot.ScrapingError` → `FollowUpExtractionResult.Errors/EmptyCombos`.
4. **`BootstrapService.ApplyFollowUpHealth`** decide el estado (queda en `dashboard_bootstrap.json` → `FollowUpHealth`,
   llega al front en `/api/home` como `followUpHealth`):
   - `broken`: ≥50 % de combinaciones con error, **o** 0 pacientes con previo > 0, **o** el total cae ≥40 % vs el
     snapshot previo (si el previo tenía ≥20). Se descarta el scrape y se conserva todo el snapshot previo (`IsStale=true`).
   - `degraded`: hay combinaciones con error pero menos del umbral. Se conservan solo los pacientes previos de esas
     combinaciones (`IsStale=true`, sin duplicar pacientes que ya aparecen en otra etapa).
   - `ok`: sin errores. Las combinaciones vacías que antes tenían pacientes se listan como info (`kind: "empty"`) pero
     **no** cambian el estado ni arrastran pacientes: pueden ser legítimas (avanzaron de etapa).
   - Umbrales en constantes `BrokenFailedRatio`, `BrokenDropRatio`, `MinPreviousForDrop` (Contracts.cs).
5. **Banner** en el dashboard (`#scrape-health-banner`, `renderScrapeHealth` en app.js): naranja = degraded, rojo = broken,
   con detalle por combinación y la fecha del último scrape correcto.
6. **Log a archivo**: `Program.cs` duplica `Console.Error` en `MEVA_DATA_DIR/logs/meva_yyyyMMdd.log` (`FileTeeWriter`,
   14 días de retención). Todos los `[SitraMed]` / `[BuildAsync]` quedan ahí aunque corra como servicio.

7. **Mail de alerta** (`ScrapeAlertService`, llamado desde `BuildAsync` tras `ApplyFollowUpHealth`): si el estado es
   `degraded` o `broken` envía un mail a `pablo.aberbuj@mevaterapia.com.ar` con resumen, último OK y errores agrupados.
   **Máximo uno por día calendario** (hora local), persistido en `MEVA_DATA_DIR/alert_mail_state.json`; si sigue roto
   al día siguiente llega otro recordatorio. El estado solo se marca como enviado si el SMTP respondió OK (si falla,
   reintenta en el próximo refresh); un fallo de mail nunca rompe el refresh. Si faltan credenciales queda un
   `[Alert] ... NO se pudo enviar mail` en el log.
   - SMTP: `AlertMailClient` (Infrastructure.Mail). Por defecto envía **desde la casilla TBI** (`MEVA_TBI_MAIL_USER` /
     `MEVA_TBI_MAIL_APP_PASSWORD`, ya en el registro del servicio) vía `smtp.gmail.com:465`.
   - Overrides opcionales: `MEVA_ALERT_MAIL_TO`, `MEVA_ALERT_MAIL_USER`, `MEVA_ALERT_MAIL_APP_PASSWORD`,
     `MEVA_ALERT_SMTP_HOST`, `MEVA_ALERT_SMTP_PORT`.
   - `POST /api/scraping/test-alert-mail` manda un mail de prueba (ignora el límite diario) para validar credenciales.

> Nota: `generatedAtUtc` se actualiza aunque el scrape esté `broken` (se conserva data vieja con hora nueva). La fuente
> de verdad de frescura es `followUpHealth.lastOkUtc`.

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
3. Llena fecha en `#search_date` con formato `yyyy-MM-dd`
4. **Dispara blur** (`focus()` + `blur()`) — este form también usa Phoenix LiveView
   (`phx-debounce="blur"`), igual que el de tomógrafos (ver quirk más abajo)
5. **Recién ahora** selecciona equipo por `machine.SitraName` en `#search_machine_id`
6. Presiona Enter
7. **Poll de cambio real de contenido** — antes de Enter se guarda un snapshot del HTML de la tabla; después
   se hace poll (hasta 3s, 300ms por intento) esperando que cambie de verdad, en vez de un timeout fijo o
   `NetworkIdle` — LiveView empuja la tabla actualizada por WebSocket, que Playwright's `NetworkIdle` no
   trackea
8. Espera a que aparezca `#machineDrag` o `#machine_drag` o `table tbody tr`

> **Bug histórico (fix 2026-09-16, ver `BUG_AGENDA_EQUIPOS_Y_ESTIMADOS.md`):** el orden original
> seleccionaba equipo **antes** de la fecha — eso reseteaba la fecha al default del server (hoy) vía el
> `phx-change` del equipo, sin importar qué mostrara el input. Bug sistemático: cualquier fecha futura
> pedida devolvía el roster de hoy repetido, sin lanzar error. Mismo patrón que el quirk de tomógrafos
> (abajo), nunca replicado acá hasta ese fix.
>
> **Post-condición (fix 2026-09-16b):** el fix de orden por sí solo no garantizaba nada — nada
> volvía a chequear que el round-trip de LiveView realmente haya terminado en la fecha pedida.
> Ahora, tras el poll de cambio de tabla, se relee el `value` real de `#search_date`; si no
> coincide con lo pedido se reintenta una vez el bloque fecha→blur→equipo→enter, y si sigue sin
> coincidir se lanza excepción (antes: silencio total). Mismo tratamiento en tomógrafos
> (`DownloadTomographAgendaHtmlAsync`). Este chequeo es el que habría cazado el bug de arriba
> desde el primer día, en vez de necesitar meses + un reporte de paciente puntual.

### Parseo de filas de agenda (DOM)

`ParseAgendaRowsAsync` itera `#machineDrag tbody tr`:

**Filtros de filas a descartar:**
- `data-type` contiene "Finalizado" — tratamientos finalizados que quedan en gris
- `fechaFin` (offset+9) es anterior a la fecha pedida — también son past-treatment remnants

**Columnas (offset ancla en la celda "inicio", NO en saltear celdas vacías):**
```
offset+0  → StartTime (hora inicio)
offset+1  → PatientName
offset+3  → Priority
offset+7  → Treatment
offset+10 → EndTime (hora fin)
```

> **Bug encontrado y arreglado (2026-09-16, auditoría de scraping):** `FindAgendaColumnOffset`
> ubicaba la columna "inicio" saltando celdas iniciales en blanco (la columna `signs`). Pero
> SitraMed puebla esa columna con un ícono/texto visible ("URG") en turnos marcados **urgentes**
> — el loop paraba una celda antes de lo debido y corría todo el mapeo una columna: `PatientName`
> terminaba siendo la hora de inicio ("13:10"), `Treatment` el texto genérico "Tratamiento" en vez
> de la técnica real, etc. Afectaba silenciosamente cualquier fila con esa marca. Confirmado con
> HTML real capturado (`agenda-captures/...MEVA-Viamonte...`) comparando una fila URG contra una
> normal. **Fix:** el offset ahora ancla en la primera celda con formato `H:MM`/`HH:MM` (la celda
> "inicio" siempre tiene ese formato, a diferencia de la columna `signs` que es de contenido
> variable), con el skip-de-blancos viejo como fallback si ninguna celda matchea.

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

> **Post-condición (fix 2026-09-16b):** igual que en agenda de equipos, se relee `#search_date`
> tras el flujo completo y se reintenta una vez si no coincide con lo pedido, en vez de asumir
> que el orden alcanza.

> **`/api/scraping/test-tomograph` corría un camino separado (fix 2026-09-16b):**
> `RunTomographTestAsync` tenía ~150 líneas de lógica de diagnóstico duplicada inline (selects,
> blur, waits propios) que **nunca llamaba** a `DownloadTomographAgendaHtmlAsync` — el endpoint de
> test no probaba el código que corre en producción. Ahora reusa
> `DownloadTomographAgendaHtmlAsync` + `TryExtractTomographAgendaDomAsync`, igual que
> `RunAgendaTestAsync` ya hacía para equipos, y devuelve las filas parseadas en `AgendaRows`.

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
            ├─ ReadFollowUpCenterGuidsAsync()  ← 1 lectura del dropdown (nombre → GUID)
            ├─ SemaphoreSlim(2) + Task.WhenAll sobre N combinaciones centro×etapa
            │    cada tarea:
            │    ├─ DownloadFollowUpAsync()
            │    │    ├─ GotoAsync(BuildFollowUpUrl(guid, micro_status))
            │    │    ├─ WaitForFollowUpResultAsync()  (loader/filas, timeout = error)
            │    │    ├─ post-condición: centro/fase del form == lo pedido (1 reintento)
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
