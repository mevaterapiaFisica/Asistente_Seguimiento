# Meva.Rt — Documentación del Proyecto

> Sistema de seguimiento de pacientes en radioterapia oncológica para **Meva Terapia** (mevaterapia.com.ar).
> Tecnologías: .NET 9, ASP.NET Core Minimal APIs, Playwright, Entity Framework 6, Windows Service.
> Última actualización: 2026-07-24

---

## Problema que resuelve

Los pacientes de radioterapia pasan por un proceso largo y multietapa: desde la solicitud de planificación hasta la primera sesión de tratamiento. El equipo de física médica necesita saber en qué etapa está cada paciente, cuántos días lleva ahí, y si hay demoras que requieren intervención.

Los datos viven en dos sistemas separados:
- **SitraMed** — sistema de gestión clínica (web app interna), contiene seguimiento de pacientes y agenda de equipos.
- **ARIA** — sistema Varian de planificación de radioterapia (base de datos SQL Server local en cada centro), contiene los planes de tratamiento aprobados.

Meva.Rt integra ambos sistemas en un **dashboard web unificado**.

---

## Arquitectura general

```
Meva.Rt.Core                      ← Modelos de dominio, TreatmentClassifier (sin dependencias externas)
Meva.Rt.Application                ← Interfaces + BootstrapService (orquestador) + BusinessDayCalculator
Meva.Rt.Infrastructure.SitraMed   ← Web scraping con Playwright (paralelo, SemaphoreSlim 2)
Meva.Rt.Infrastructure.Aria        ← Integración ARIA via AriaQ.dll
Meva.Rt.Infrastructure.Storage    ← Persistencia JSON en disco
Meva.Rt.Web                       ← ASP.NET Core, API REST + frontend estático
Meva.Rt.AriaRunner                ← Ejecutable independiente para la PC con ARIA
```

El frontend es un HTML/JS estático servido por el mismo servidor .NET (en `Meva.Rt.Web/wwwroot`).
La app corre como **Windows Service** en el puerto 5000.

---

## Entidades principales (Meva.Rt.Core/DomainModels.cs)

| Entidad | Descripción |
|---|---|
| `RtCenter` | Centro de radioterapia. Tiene flag `AriaEnabled`. |
| `RtMachine` | Equipo de irradiación. Tres nombres: `SitraName`, `AriaName`, `DisplayName`. |
| `RtTomograph` | Tomógrafo. Mismo esquema de nombres. |
| `RtMachineCapabilities` | Capacidades por equipo: `CanDoVMAT`, `CanDoSBRT`, `CanDoRC`, `CanDoTBI`, `CanDoTSET`, `CanDoIGRT`, `CanDoElectrones`, `HighEnergyBeams[]`. |
| `ProcessStageDefinition` | Etapa del proceso. Código (`F3`, `F6A`...), nombre, días esperados, microstatus de SitraMed. |
| `ProcessPatientSnapshot` | Un paciente en una etapa. Campos clave: HC, nombre, centro, máquina planeada, `DaysInStage` (días **hábiles**), `IsLongWait`, `TreatmentTechnique`, `IrradiationModality`, `ExactBeamEnergy`, `TreatmentLabel`, `Priority`, `ResponsibleDoctor`, `AssignedPhysicist`, `TomographyDate`. |
| `MachineAppointmentSnapshot` | Turno agendado: fecha, hora, paciente, `TreatmentLabel`, `Priority`. |
| `AriaPlanSnapshot` | Plan activo en ARIA: máquina, estado, `BeamType`, fracciones, `IrradiationModality`, `ExactBeamEnergy`, `Plans` (lista `AriaPlanInfo`: todos los planes candidatos del paciente — `PlanId`, `PlanName`, `Status`, `IrradiationModality`, `MachineDisplayName`). |
| `QaEspecificoItem` | Item de la pestaña QA Paciente Específico: `Id`, `Origin` (Manual/Auto), `Pinned`, `Excluded`, `PatientId`, `PlanId`, `Plan`, `EquipoName`, `Observaciones`. |
| `PatientProcessEvent` | Evento de proceso: `TechniqueChanged` o `StageRegressed`. Se detecta automáticamente en cada refresh comparando con el snapshot previo. |
| `StageSummaryItem` | Resumen por etapa/centro: conteo, promedio de días, demorados, long-wait. |
| `RtSystemConfiguration` | Config completa: centros, máquinas, etapas, capacidades. Se puede sobrescribir vía `data/rt_configuration.json`. |

---

## TreatmentClassifier (Meva.Rt.Core)

Dos métodos estáticos:
- **`Classify(treatmentText)`**: clasifica el texto de SitraMed → `TSET` > `TBI` > `SBRT` > `RC` > `VMAT` > `IMRT` > `BQT` > `IORT` > `IGRT` > `3DC` (default). **Técnicas válidas reales de SitraMed: solo `3DC`, `IMRT`, `TBI`, `BQT`, `TSET`, `RC`, `SBRT`** — `VMAT` no es una técnica que SitraMed asigne por sí sola, es siempre un refinamiento de ARIA (ver `BuildLabel`); el trigger de `VMAT` en `Classify()` quedó reservado al match literal de la palabra "VMAT" en el texto (sesión 2026-07-24: se sacó el keyword genérico `"arco"`, que daba falso positivo con anatomía tipo "arco costal").
- **`BuildLabel(tech, modality, energy, beamFallback)`**: combina técnica de SitraMed + modalidad ARIA (`Modalidad()`/`ResolveIrradiationModality()`: `IMRT`, `VMAT`, `3DC`, `ArcoConformado`, `Indefinido`) en un string legible. Mapeo por técnica (sesión 2026-07-24, confirmado con el usuario):
  - `IMRT` → ARIA refina a `"IMRT - estático"` o `"VMAT"`.
  - `3DC` → ARIA **solo** refina energía (`"3DC e-"`, `"3DC 10X/15X/18X"`, `"3DC - 6X"` default) — **nunca** se promueve a IMRT/VMAT aunque ARIA diga VMAT.
  - `RC`/`SBRT` → `"RC/SBRT - VMAT"` o `"RC/SBRT - arcos"` (modalidad `ArcoConformado`: técnica ARC de ARIA pero MLC no-VMAT) o `"RC/SBRT - haz SRS"` (energía SRS, prioridad más alta) o plano.
  - `IGRT` → `"IGRT - estático"` / `"IGRT - VMAT"` (sin cambios).
  - `TBI`/`TSET`/`BQT` → sin refinamiento ARIA, se devuelve la técnica tal cual.

La clasificación BQT/IORT se ignora si el paciente tiene otra técnica en SitraMed (`ResolveTreatmentZone` en extractores).

**Fixes de clasificación (sesión 2026-06-19b):**
- **RC:** agrega variante sin tilde `"radiocirugia"` (OrdinalIgnoreCase no normaliza diacríticos; `"Radiocirugía"` con `í` U+00ED ≠ `"radiocirugia"`). Todos los pacientes RC caían al fallback `"3DC"`.
- **SBRT extracraneal:** ídem, variante sin tilde agregada.
- **Labels RC renombrados:** `"RC fraccionada"` → `"RC"` (15 min), `"RC fracción única"` → `"RC - haz SRS"` (20/45 min). Migración automática al cargar el JSON persistido.

**Fix extracción de técnica en SitraMed (sesión 2026-06-18):**
- `PlaywrightSitraMedClient.cs`: extrae primero del link `conduct_definitions` (fuente autoritativa). En el fallback por keywords, salta celdas que contengan `.modal` o `button.modal-button` (la celda "Comunicaciones Internas" —índice 5— contenía el HTML completo del modal con notas históricas que mencionaban técnicas anteriores).
- `TreatmentClassifier.cs`: agrega `"Intensidad Modulada"` como alias de IMRT.
- `ResolveTreatmentZone`: ordena candidatos no-BQT/IORT por fecha descendente.

---

## Etapas del proceso (F-stages)

| Código | Etapa | Días esperados |
|---|---|---|
| F3 | Ingreso | 2 |
| F4 | Turno Tomosimuación | 3 |
| F5 | Marcación | 2 |
| F6A | Asignación de Plan | 2 |
| F6B | Espera de Plan | 2 |
| F6C | Aprobación Médica | 2 |
| F6F | Control de Calidad | 1 |
| F6G | Cálculo Independiente | 1 |
| F7A | Aprobación Físico | 1 |
| F7C | Chequeo General | 1 |

Hay 19 etapas en total (F1–F11), las críticas de planificación activa son las anteriores.

**Días hábiles:** `DaysInStage` se calcula con `BusinessDayCalculator` (excluye sábados, domingos y feriados de `feriados.txt`). Se usa tanto para `DaysInStage` de pacientes como para transiciones.

**Larga espera (long-wait):** `DaysInStage > LongWaitThresholdDays` (default: 40 días) → `IsLongWait = true`. Se muestra en gris en el frontend y se excluye de promedios y de transiciones semanales.

**Marcadores HTML de SitraMed para calcular fecha inicio de etapa:**
- F3/F4 → `<!-- f3`
- F5 → `<!-- f4`
- F6A → `<!-- f5`
- **F6B / F6C / F6F / F6G → `<!-- f6`** (la primera `<td>` es la fecha de asignación del físico)
- F7A / F7C → `<!-- f6` (pendiente: usar última fecha de f6, no la primera)

---

## Centros y equipos

### Mapeado completo

| Centro | SitraName | AriaName | DisplayName | AriaEnabled |
|---|---|---|---|---|
| MEVA-Central | Equipo 1 | Equipo1 | MEVA-Central - Equipo 1 | ✓ |
| MEVA-Central | Equipo 2 | Equipo 2 6EX | MEVA-Central - Equipo 2 | ✓ |
| MEVA-Central | Equipo 3 | Equipo3 | MEVA-Central - Equipo 3 | ✓ |
| MEVA-Central | Equipo 4 | D-2300CD | MEVA-Central - Equipo 4 | ✓ |
| CETRO | Cetro | Varian 21 EX | CETRO - Cetro | ✓ |
| QUILMES | Quilmes - Equipo 1 | QBA_600CD_523 | QUILMES - Equipo 1 | ✓ |
| QUILMES | Quilmes - Equipo 2 | EQ2_iX_827 | QUILMES - Equipo 2 | ✓ |
| SAN JUSTO | San Justo - Equipo 1 | 6oo C/D | SAN JUSTO - Equipo 1 | ✓ (fuera de servicio desde 2026-06-19, temporario) |
| SAN JUSTO | San Justo - Equipo 2 | (no confirmado) | SAN JUSTO - Equipo 2 | ✓ |
| RT MEDRANO | RT Medrano | CL21EX | RT MEDRANO - RT Medrano | ✓ |
| MEVA-Viamonte | Viamonte - Equipo 1 | (no confirmado) | MEVA-Viamonte - Equipo 1 | ✗ |
| MEVA-Viamonte | Viamonte - Equipo 2 | (no confirmado) | MEVA-Viamonte - Equipo 2 | ✗ |

### Tomógrafos

| Centro | SitraName | DisplayName |
|---|---|---|
| MEVA-Central | Tomografo | MEVA-Central - Tomografo |
| MEVA-Viamonte | Tomografo Viamonte | MEVA-Viamonte - Tomografo |
| QUILMES | Tomografo Quilmes | QUILMES - Tomografo |
| SAN JUSTO | Tomografo San Justo | SAN JUSTO - Tomografo |

Fuente de verdad: `AppConfiguration.cs`. Se puede sobrescribir vía `data/rt_configuration.json` o `PUT /api/configuration`.

---

## Componentes de infraestructura

### SitraMed (web scraping)

`Meva.Rt.Infrastructure.SitraMed` usa **Playwright** para autenticarse y extraer datos.

**Paralelización:** Login único (`CreateLoggedPageAsync` → `IBrowserContext`), luego `SemaphoreSlim(MaxParallelPages = 2)`. Múltiples `IPage` comparten cookies de sesión. `Task.WhenAll` para todas las combinaciones centro×etapa. Agenda y seguimiento se lanzan en paralelo desde `BootstrapService`.

- **`SitraMedAgendaExtractor`** — turnos de equipos. Parsea HTML con regex. `HasScrapingError` indica fallo parcial.
- **`SitraMedFollowUpExtractor`** — pacientes por centro+etapa. Calcula `DaysInStage` con `BusinessDayCalculator`.
- **`SitraMedTomographExtractor`** — análogo a agenda para tomógrafos.
- **`SitraMedPatientHcFetcher`** — resuelve GUID interno → HC. Necesario para correlacionar con ARIA.
- **`FollowUpDateParser`** — parsea fechas de etapa del HTML de SitraMed (marcadores `<!-- fX -->`). También extrae `ResponsibleDoctor` (médico responsable) y `TomographyDate`.
- **`SitraMedAttendedPatientsExtractor`** — scrapea la agenda del equipo fuera de servicio y detecta botones "Atendido" para extraer los GUIDs de pacientes ya atendidos. Usado por el tab Derivación.

### ARIA (base de datos Varian)

`Meva.Rt.Infrastructure.Aria` integra via **AriaQ.dll** (Entity Framework 6).

- **`AriaPlanResolver`** — dado HCs de pacientes, devuelve plan activo. En producción consulta BD; si no hay conexión, lee `aria_plans_mock.json`.
- **Selección de plan activo (`SelectActivePlan`/`PlanActivo`, sesión 2026-07-24):** dos barreras antes de priorizar por `Status` (`TreatApproval` > `PlanApproval` > `Unapproved`): (1) excluye cursos ya `Completed` (`Course.CompletedDateTime` seteado o `ClinicalStatus=="Completed"` — `CompletedDateTime` es la señal confiable, `ClinicalStatus` no se pudo verificar contra ARIA real); (2) prefiere planes creados hace <30 días, con fallback al pool completo si ninguno es reciente. Sin esto, un curso viejo ya tratado (`TreatApproval`) le ganaba a un `PlanApproval` real y vigente de un curso nuevo — rompía `PlannedMachineDisplayName`/Equipo asignado en toda la app, no solo QA. Detalle en `BUG_PACIENTE_1-114893_ARIA_NO_MATCH.md` (Bug 4).
- **Clasificación de tipo de haz:** `Electrones` (RadiationType=="E"), `SRS` (técnica contiene SRS/STEREO), `AltaE` (energía ≥ 10000 keV), `6X` (default).
- **`IrradiationModality`:** técnica `STATIC`/`STATIC-I` (prefijo, sesión 2026-07-24 — antes exacto `"STATIC"` no matcheaba la variante `"STATIC-I"`) +>40CP→`IMRT`, ≤40CP→`3DC`. `ARC` con `DoseRate==1000`→**`ArcoConformado`**, cualquier otro `DoseRate` (ej. 600)→`VMAT` (sesión 2026-07-24: técnica `ARC` no es sinónimo de VMAT; discriminador **corregido en sesión 2026-08-03** — ver detalle y estado "temporal/pendiente" en la sección QA Paciente Específico más abajo). Viene del **primer haz de tratamiento por `RadiationSer` ascendente, excluyendo campos de setup** (`SetupFieldFlag=1`, ej. kV/CBCT — fix sesión 2026-08-03: antes tomaba el primer haz sin filtrar, colando campos de imagen y rompiendo la clasificación de técnica/CPs).
- **`ExactBeamEnergy`:** máximo sobre **todos** los campos del plan (si un campo es 10X/15X/18X, se usa esa energía aunque otros sean 6X).

### Persistencia (Storage)

`Meva.Rt.Infrastructure.Storage` usa JSON en disco.

Archivos clave en `data/`:
- `dashboard_bootstrap.json` — snapshot completo del dashboard
- `agenda_YYYY-MM-DD.json` — agenda de equipos por fecha
- `tomograph_agenda_YYYY-MM-DD.json` — agenda de tomógrafos por fecha
- `aria_plans_mock.json` — planes ARIA (input del dashboard; se **mergea** al actualizar, no se reemplaza)
- `aria_results_*.json` — salida cruda de AriaRunner
- `guid_hc_map.json` — caché GUID → HC
- `patient_process_events.json` — eventos de proceso detectados (TechniqueChanged, StageRegressed); store append-only con escritura atómica vía `.tmp`
- `weekly_stats.json` — estadísticas semanales acumuladas (requiere 4 semanas para usarse en estimaciones)
- `stage_transitions.json` — transiciones de etapa registradas
- `pedidos.json` — pestaña Pedidos (Física): CRUD manual + auto-generado
- `qa_especifico.json` — pestaña QA Paciente Específico (Física): CRUD manual + auto-generado por plan ARIA
- `feriados.txt` — una fecha por línea en formato `YYYY-MM-DD`

---

## API REST (Meva.Rt.Web/Program.cs)

### Dashboard y estado

| Endpoint | Descripción |
|---|---|
| `GET /api/home` | Devuelve el dashboard. Usa caché según `MEVA_HOME_REFRESH_MODE`. |
| `GET /api/status` | `{ generatedAtUtc, appVersion }` — para polling de auto-refresh en el frontend. |
| `POST /api/home/refresh` | Refresco completo: scraping + ARIA + guardar snapshot. |
| `POST /api/home/refresh-no-aria` | Refresco sin ARIA. Exporta IDs a `pacientes.json`. |
| `POST /api/home/apply-aria` | Enriquece snapshot existente con planes de `aria_plans_mock.json`. |

### Agenda de equipos

| Endpoint | Descripción |
|---|---|
| `GET /api/agenda?date=YYYY-MM-DD` | Agenda para una fecha. Devuelve `{ slots, scrapingErrors }`. Fechas futuras incluyen citas estimadas. |
| `GET /api/agenda/available-dates` | Lista de fechas con snapshots guardados. |
| `POST /api/agenda/scrape-upcoming?days=15` | Scrape N días hábiles futuros (máx 30). |

### Agenda de tomógrafos

| Endpoint | Descripción |
|---|---|
| `GET /api/tomograph-agenda?date=YYYY-MM-DD` | Agenda tomógrafos para una fecha. |
| `GET /api/tomograph-agenda/available-dates` | Lista de fechas con snapshots. |
| `POST /api/tomograph-agenda/scrape-upcoming?days=15` | Scrape N días hábiles futuros. |

### ARIA — flujo batch

| Endpoint | Descripción |
|---|---|
| `GET /api/aria/export-patient-ids` | Lista de HCs de pacientes en etapas de planificación. |
| `GET /api/aria/query-status` | `{ isRunning, progressPct, currentPatient, totalPatients, lastRunSucceeded }`. Lee log del AriaRunner para calcular progreso. |
| `POST /api/aria/import-results` | Lee `aria_results_*.json`, genera/mergea `aria_plans_mock.json`. |
| `POST /api/aria/run-query` | Lanza AriaRunner.exe; devuelve **202 Accepted** inmediatamente (fire-and-forget). El progreso se consulta con `GET /api/aria/query-status`. |

### Física — Pedidos y QA Paciente Específico

| Endpoint | Descripción |
|---|---|
| `GET/POST/PUT/DELETE /api/pedidos` | CRUD de pedidos (Física). `POST .../{id}/complete` marca completado. |
| `GET/POST/PUT/DELETE /api/qa-especifico` | CRUD de QA paciente-específico (Física). `POST .../{id}/pin` togglea fijado. `POST` es idempotente por (`PatientId`,`PlanId`) para items `Origin=Auto`: si ya existe uno no-excluido, devuelve ese en vez de duplicar. |

### Derivación

| Endpoint | Descripción |
|---|---|
| `GET /api/derivation/attended-patients?machine=...&date=...` | Lista de GUIDs de pacientes ya atendidos en el equipo para esa fecha. Scrapea la agenda de SitraMed y detecta botones "Atendido". |

### Estadísticas, eventos y configuración

| Endpoint | Descripción |
|---|---|
| `GET /api/stats/weekly` | Estadísticas semanales acumuladas. |
| `GET /api/patient-events?days=N&type=X&center=Y` | Eventos de proceso recientes (TechniqueChanged, StageRegressed). |
| `GET /api/alerts/feriados` | Alerta de fin de año: si quedan ≤10 días para el 31/12 y `feriados.txt` no tiene el año siguiente. |
| `GET /api/configuration` | Config actual (centros, máquinas, etapas, capacidades). |
| `PUT /api/configuration` | Guarda config en `data/rt_configuration.json`. |
| `POST /api/scraping/test` | Prueba scraping sin guardar. |
| `POST /api/scraping/test-agenda` | Prueba agenda de equipos. |
| `POST /api/scraping/test-tomograph` | Prueba agenda de tomógrafos. |
| `POST /api/scraping/test-followup-full` | Prueba seguimiento completo. |

---

## AriaRunner — ejecutable independiente

### Por qué existe

El servidor web **no tiene acceso a la red de ARIA**. `AriaRunner.exe` corre en una PC que sí tiene ARIA instalado.

### Flujo de uso

```
1. [Esta PC]    GET /api/aria/export-patient-ids   → lista de HCs
2. [Manual]     Copiar como input_patients.json a la PC con ARIA
3. [PC con ARIA] AriaRunner.exe
                → lee input_patients.json
                → 6 consultas WHERE IN a la BD ARIA (~7 segundos para 619 pacientes)
                → genera aria_results_YYYYMMDD_HHMMSS.json
4. [Manual]     Copiar aria_results_*.json a data/ de esta PC
5. [Esta PC]    POST /api/aria/import-results  → mergea en aria_plans_mock.json
                POST /api/home/apply-aria       → enriquece dashboard
```

O bien: `POST /api/aria/run-query` (si `MEVA_ARIA_RUNNER_EXE` está configurado) lo hace en background con polling de progreso desde el frontend.

### Bulk query ARIA (desde sesión 2026-06-10)

Reemplaza las 938 consultas individuales por 6 consultas WHERE IN:
1. Patients + PatientDoctors WHERE PatientId IN (...)
2. Courses WHERE PatientSer IN (...)
3. PlanSetups + Prescription + RTPlans WHERE CourseSer IN (...) AND Status != 'Rejected'
4. Radiations + RadiationDevice + EnergyMode + Technique WHERE PlanSetupSer IN (...)
5. ExternalFieldCommons GROUP BY RadiationSer COUNT(ControlPoints)
6. Ensamblado en memoria con Lookups

`QueryInBatches` fragmenta listas > 500 keys (límite SQL Server de 2100 parámetros).

**Resultado:** 17 min → 7 segundos. Tiempo total de refresh completo: ~22 min → 3.1 min.

### Detalles técnicos

- Usa Entity Framework 6 (.NET Standard 2.0, compatible con .NET 9)
- `AriaQ.dll` (Varian) en la misma carpeta
- Connection string: env var `ARIA_CONNECTION_STRING` o argumento `--conn=`
- Impersonación Windows opcional: env `ARIA_VARIAN_PASSWORD` → `ECL-FISICA2\varian`
- Selección del plan activo: `TreatApproval` > `PlanApproval` > `Unapproved` (más reciente)
- Excluye cursos con nombre que contenga "QA" o "Fisica"
- Argumento `--workers=N` disponible (por defecto 1 worker secuencial; paralelización no justifica complejidad)
- AriaRunner.exe desplegado en `C:\MevaRT\AriaRunner\` (net9.0-windows: exe + dll)

---

## Frontend (Meva.Rt.Web/wwwroot)

### Navegación y tabs

Desde la sesión 2026-06-19b, los tabs se organizan en **grupos de navegación** (pills de primer nivel), con sub-tabs que se muestran al seleccionar cada grupo:

| Grupo | Tabs |
|---|---|
| **Pacientes** | Alertas · Pacientes · Inicios · Seguimiento |
| **Agendas** | Agenda Tomógrafos · Agenda Equipos · Turnos Reservados · Derivación |
| **Análisis** | Tendencias · Física · Técnicas Especiales |
| **Admin** | Configuración |

El tab activo al cargar es **Seguimiento** (grupo Pacientes). El menú de actualización de datos (Actualizar Sitramed / ARIA / todo) es un dropdown tipo "ghost button" en la esquina derecha de la nav, siempre visible.

### Tab Alertas

- **A1** — Centros con etapas demoradas: grid de tarjetas por centro, días reales vs. referencia.
- **A2** — Tiempo estimado de planificación: dentro de cada tarjeta de centro, calculado con `weeklyStats` por centro.
- **B1** — Agenda de equipos: agrupado por centro.
- **B3** — Turnos superpuestos: cuenta pares de superposición (no equipos). Ignora superposiciones donde ambos turnos son del mismo paciente.
- **C2/C3** — Eventos recientes: TechniqueChanged y StageRegressed (StageRegressed muestra `displayName` de etapa, no código).
- Alerta fin de año: banner cuando quedan ≤10 días para el 31/12 y no hay feriados del año siguiente.

### Tab Pacientes (desde sesión 2026-06-16)

Buscador global + tarjetas de paciente. Tres modos de tarjeta:
- **followup** — solo en seguimiento: estimados, disponibilidad en equipo, tiempo desde ingreso, demora.
- **both** — en seguimiento + tiene turnos agendados: turnos reales, sin estimados.
- **agenda** — solo en agenda: nombre, equipo, turnos.

`_pacienteFirstAvailableSlot(p)`: busca el primer slot libre en el equipo planeado del paciente en la agenda disponible. Excluye slots BQT/IORT.

### Tab Inicios (desde sesión 2026-06-18)

Pacientes que **inician tratamiento** en los próximos 3 días hábiles (D+1, D+2, D+3).

**Lógica de detección:** un slot es "inicio" en el día D si:
- Aparece en la agenda real (no estimada, no BQT/IORT) de D, **Y**
- No aparece en los 2 días hábiles inmediatamente anteriores a D (prev1 y prev2 se calculan respecto a D, no a hoy)

La clave de paciente es `sitraMedGuid` si existe, sino `patientName.toLower()`.

**Lookback por día:**
- D+1: prev1=hoy (de `homeData.agenda`), prev2=D-1 (ayer)
- D+2: prev1=D+1, prev2=hoy
- D+3: prev1=D+2, prev2=D+1

Esto garantiza que un paciente en D+1 y D+2 aparece solo en D+1 (ya que D+2 lo excluye por estar en prev1=D+1).

**Datos cargados en `loadIniciosTab()`:**
- Días pasados (D-1, D-2) — silencioso si no hay archivo
- Días futuros disponibles según `GET /api/agenda/available-dates`
- Fechas faltantes → warning visible + `POST /api/agenda/scrape-upcoming?days=7` en background + reintento a los 15s
- Hoy → `state.homeData.agenda` (sin llamada extra a la API)

**Layout:** sección por día con encabezado (día de semana + fecha + conteo) → grilla 2 columnas de cards por equipo → subcards de paciente (nombre linkea a SitraMed, HC, horario, etapa, técnica).

**Filtro de centro:** pills reconstruidas en cada `renderIniciosTab()` para reflejar el estado activo. `machineName` ya incluye el centro en todos los equipos (formato `"Centro - Equipo"`), se usa directo sin transformación.

### Tab Seguimiento

Tabla de pacientes por etapa y centro. Los pacientes con larga espera van al fondo. Resto ordenado por días de demora descendente.

### Tab Agenda Equipos / Tomógrafos

- Slots BQT/IORT excluidos del conteo y de la vista (`isExcludedSlot(slot)`).
- Equipos con error de scraping muestran borde naranja + "⚠ Error de scraping".

### Tab Turnos Reservados (desde sesión 2026-06-19b)

Muestra **todas** las reservas activas (sin límite de días, a diferencia de Inicios). Datos de `GET /api/reservations` (caché 5 min en `state.reservations`).

- **Filtro de centro:** pills igual que Inicios.
- **Contador:** `"N turnos reservados (X mañana, Y en 2 días, ...)"`.
- **Agrupación:** por fecha ASC → por equipo → subcards de pacientes. Dentro de cada equipo: prioridad ASC (P1 primero), luego hora.
- **Subcard expandible:** click expande/colapsa (solo una por card de equipo). Estado expandido muestra observaciones + botones Editar/Eliminar que reutilizan los modales existentes.
- **Integración:** tras guardar o eliminar desde cualquier tab, `window.activeReservations` se actualiza en memoria y se re-renderiza si el tab activo es Turnos Reservados.

### Tab Derivación

Herramienta organizativa (no modifica SitraMed/ARIA) para cuando un equipo está fuera de servicio.

- Columna izquierda (50%): lista de pacientes con turnos en el equipo fallido + pacientes en planificación para ese equipo. Cada paciente: nombre, HC, badge técnica, badge prioridad, horario actual, 3 botones rápidos de equipos compatibles + dropdown "Otros...". Botones de estado: derivar, ⊗ suspender, ✓ ya atendido.
- Panel derecho (50%): tarjetas de equipos destino con turnos libres y pacientes derivados.
- Barra inferior: Total / Derivados / Suspendidos / Atendidos / Sin asignar.
- Exporta HTML autocontenido con tabla de derivaciones.
- Compatibilidad por TreatmentLabel (VMAT→canDoVMAT, SBRT→canDoSBRT, etc.). IGRT no es mandatorio pero muestra ⚠ si el equipo no lo hace.

### Tab Tendencias (ex-Resumen)

Estadísticas semanales de flujo de pacientes. Requiere 4 semanas de datos reales para mostrar estadísticas; antes muestra valores de referencia con leyenda explicativa.

### Tab Física

- Recomendación de equipo para un paciente seleccionado.
- Tarjetas de técnica filtradas por paciente.
- Ranking de equipos por disponibilidad real (`capacidad_total - agendaPatients`); soporta valores negativos (sobrecapacidad).
- Estimación de fecha de inicio usando `weeklyStats` si hay ≥4 semanas, sino `expectedDays`.

### Tab Pedidos (Física)

Lista manual/auto de pedidos (Paciente/Equipo/Recordatorio) con fecha límite, médico, motivo, etc.

**Alta automática:** pacientes con turno agendado o reserva activa, etapa ≤ F7C (Chequeo General),
técnica no BQT/IORT, y solo si la fecha límite calculada (turno más próximo) no es anterior a hoy.

**Limpieza automática (`computeAutoPedidos()`, corre en cada sync y al reservar turno):** borra
pedidos automáticos no completados que ya no aplican — técnica BQT/IORT, fecha límite vencida, **o**
el paciente ya avanzó de etapa más allá de F7C (evita que quede un pedido con Tarea/Fecha
desactualizada de cuando se creó, aunque el paciente ya esté en Placa Verificadora u otra etapa
posterior). También hace backfill de Tarea/Fecha Límite/Solicita en pedidos automáticos viejos que
quedaron sin esos datos por versiones anteriores de la lógica.

**Campo Tarea = "Etapa actual" (sesión 2026-09-01):** en pedidos automáticos, `Tarea` muestra
`"Etapa actual: <etapa de seguimiento>"` y se resincroniza en cada `computeAutoPedidos()` si el
paciente cambió de etapa (`_etapaActualLabel()`).

**Anti-duplicado por paciente (sesión 2026-09-01):** antes de crear un pedido automático,
`_findExistingPedidoForPatient()` busca un pedido no completado del mismo paciente (coincidente por
Apellido y Nombre + HC/`patientId`). Si ya existe, no se duplica — en cambio se agrega a su Nota
(`observaciones`) el texto de fecha y equipo de inicio (`_pedidoInicioNota()`), sin repetir la línea
si ya estaba agregada.

**Fijar pedido (sesión 2026-09-02):** `PedidoItem.Pinned` (`POST /api/pedidos/{id}/pin`, botón
"Fijar/Desfijar" en la action bar) — igual patrón que "Fijar QA". Un pedido automático fijado no se
borra por la regla de limpieza de etapa > F7C (Chequeo General) en `computeAutoPedidos()`; las otras
causas de limpieza (técnica BQT/IORT, fecha límite vencida) siguen aplicando aunque esté fijado.

### Tab QA Paciente Específico (Física, desde sesión 2026-07-22, revisada 2026-07-24)

Lista **por plan de ARIA** (no por paciente — un paciente con 2 planes puede tener 2 filas).

**Reglas de entrada/salida** (motor `computeQaEspecifico()`/`_qaEspecificoEligiblePlans()`, `app.js`):
- **Entra:** plan con `IrradiationModality` ARIA = IMRT o VMAT, **y** (`Status` = `PlanApproval` **o** paciente completó etapa SitraMed F6C) — **y nunca** si ese plan ya está en `Status = TreatApproval` (plan ya tratado/cerrado, aunque el paciente esté más allá de F6C por otro curso).
- **Sale (3 triggers independientes):**
  1. Ese plan puntual pasa a `Status = TreatApproval` en ARIA (sale solo ese plan).
  2. Paciente completa etapa F7A en SitraMed (salen todos sus planes).
  3. Paciente completa etapa **F6F** (Control de Calidad) en SitraMed — dispara antes que el trigger de F7A ya que F6F es anterior en `_STAGE_ORDER`, por lo que en la práctica es el que corta primero (salen todos sus planes).
- **Fijar QA:** un item fijado nunca desaparece solo por ninguno de los 3 triggers de salida — solo lo saca "Eliminar" manual.
- **Eliminar** sobre item Auto → exclusión permanente para ese (paciente, plan) puntual (`Excluded=true`, no se regenera). Sobre item Manual → borrado total.
- `POST /api/qa-especifico` es idempotente por (`PatientId`,`PlanId`) para items `Origin=Auto` — evita duplicados si el motor corre 2 veces casi en simultáneo (ej. 2 pestañas abiertas).

**UI:**
- Tabla: HC, Apellido y Nombre, Plan (`PlanId` de ARIA — **no** `PlanName`, que suele venir `null`), Equipo, Etapa de seguimiento (en vivo, join contra `state.homeData.patients` por HC), Observaciones.
- Orden por defecto: Equipo asc. Filtro de centro por defecto: MEVA-Central + RT MEDRANO. Pills de centro con selección múltiple (`centerFilters: Set`).
- Ordenado por Equipo → agrupado visualmente: banda alterna (`--info-bg`) + regla superior gruesa (`--accent`) en el primer row de cada equipo nuevo, nombre del equipo en negrita solo ahí. Se desactiva si se ordena por otra columna.
- Botones: Agregar Manual, Editar (Plan/Observaciones), Fijar QA (toggle, pin gana sobre toda salida automática), Eliminar.

**Datos ARIA que alimentan el motor** (`AriaPlanSnapshot.Plans` → `ProcessPatientSnapshot.Plans` → `patients[].plans[]` en `/api/home`, cada uno `{PlanId, PlanName, Status, IrradiationModality, MachineDisplayName}`):
- Camino real de producción: `AriaQuery.cs` (bulk, `ParseAriaOutput` en `Program.cs`) — candidatos = `ActivePlan` ∪ `AllPlans` con `Status` `PlanApproval`/`TreatApproval`.
- Camino vivo (sin uso real en producción hoy): `AriaAdapter.cs` — candidatos = `PlanActivo` ∪ `PlanesPlanApproval` ∪ `PlanesTreatApproval` (`MetodosParaWebScrap.cs`).
- **HC con sufijo por curso concurrente:** SitraMed sufija el HC por seguimiento (`1-114893-1` para un 2do curso), pero ARIA guarda al paciente bajo un único `PatientId` (típicamente sufijo `-0`). `BootstrapService.NormalizeAriaBaseId`/`TryFindAriaPlan` (`Contracts.cs`) prueban también la variante `-0` al consultar y mergear ARIA — sin esto, el paciente queda invisible para QA aunque tenga plan real aprobado. Detalle en `BUG_PACIENTE_1-114893_ARIA_NO_MATCH.md`.
- **Técnica `ARC` ≠ VMAT (sesión 2026-08-03, corrige Bug 3 de `BUG_PACIENTE_1-114893_ARIA_NO_MATCH.md`):** arco conformado (típico SRS/SBRT) usa la misma `TechniqueId="ARC"` que VMAT en ARIA. `MLCPlan.MLCPlanType` **no sirve** de discriminador — da `"DynMLCPlan"` en ambos casos (verificado con dump crudo contra ARIA real, pacientes `1-119097-0` VMAT vs `1-119477-0` arco conformado). **Solución temporal actual:** `ExternalField.DoseRate` — `1000` (haz SRS de alta tasa) → `ArcoConformado`, cualquier otro valor (ej. `600` normal) → `VMAT` (heurística del físico, cubre la mayoría de los casos pero no es 100% preciso). **Mejor solución pendiente:** comparar `DoseRate` entre control points del mismo haz (dosis variable = VMAT real, dosis constante = arco conformado estático) — descartado por ahora por costo de query por paciente. Implementado en `Modalidad()` (`AriaQuery.cs`) y `ResolveIrradiationModality()` (`AriaAdapter.cs`).

### Tab Técnicas Especiales (desde sesión 2026-06-10)

Pacientes con técnica SBRT o RC, desde etapa F4B en adelante.
- Pills de filtro por Técnica y Etapa.
- Tabla: HC | Nombre | Técnica | Fecha Tomo | Etapa | Días | Médico | Físico.
- Colores de días: verde ≤esperado, amarillo ≤2×esperado, rojo >2×esperado.
- Orden: prioridad ASC → sortOrder etapa → días DESC.

### Auto-refresh (desde sesión 2026-06-16)

Polling cada 3 minutos a `GET /api/status`. Si `appVersion` cambió: banner azul + `location.reload(true)` en 2.5s. Si solo `generatedAtUtc` cambió: banner + reload en 1.5s. El primer check es a los 10s (establece baseline sin recargar).

### Badges y helpers

- `renderTreatmentLabel(item)` — función única para mostrar técnica (reemplaza badges separados).
- `priorityBadge(p)` — P1 en rojo negrita, P2 en gris, P3/null sin badge.
- `ariaBadges(p)` — solo muestra `▸ máquina` (sin haz ni modalidad, ya están en el label).
- `isExcludedSlot(slot)` — excluye BQT e IORT de agenda y búsqueda de disponibilidad.
- `hcTag(hc)` / `fmtHc(hc)` — oculta GUIDs de SitraMed (tipo `0269ce85-...`) mostrando `Sin HC`.
- `_addBusinessDays(dateStr, n)` / `_subtractBusinessDays(dateStr, n)` — suma/resta N días hábiles (excluye sábado y domingo; no usa feriados en frontend).
- `_fmtDayOfWeek(dateStr)` — nombre del día en español (`"Lunes"`, `"Martes"`, etc.).

---

## Variables de entorno

| Variable | Default | Descripción |
|---|---|---|
| `MEVA_SITRAMED_USER` | — | Usuario SitraMed |
| `MEVA_SITRAMED_PASSWORD` | — | Contraseña SitraMed |
| `MEVA_HOME_REFRESH_MODE` | `snapshot_first` | `snapshot_first` / `snapshot_only` / `every_request` |
| `MEVA_SITRAMED_HEADFUL` | `false` | Muestra el browser Playwright |
| `MEVA_SITRAMED_DIAGNOSTICS` | `false` | Guarda screenshots de diagnóstico |
| `MEVA_SITRAMED_SAVE_AGENDA_HTML` | `false` | Guarda HTML crudo de agenda |
| `MEVA_SITRAMED_TIMEOUT_SECONDS` | `30` | Timeout de scraping |
| `MEVA_DATA_DIR` | `{AppRoot}/data` | Directorio de snapshots |
| `MEVA_FERIADOS_PATH` | `data/feriados.txt` | Archivo de feriados |
| `MEVA_ARIA_MAP_PATH` | `config/mapEquiposAriaSitra.txt` | Mapa máquinas ARIA↔Sitra |
| `MEVA_ARIA_MOCK_JSON` | `data/aria_plans_mock.json` | Planes mock ARIA |
| `MEVA_ARIA_RUNNER_EXE` | — | Path a AriaRunner.exe (opcional para run-query automático) |

---

## Flujo completo del sistema

### 1. Carga del dashboard

```
GET /api/home
  └─ RefreshMode = snapshot_first?
       ├─ SÍ → lee dashboard_bootstrap.json (si existe)
       └─ NO → BootstrapService.BuildAsync()
                 ├─ En paralelo:
                 │   ├─ SitraMedAgendaExtractor → agendas de equipos
                 │   └─ SitraMedFollowUpExtractor → pacientes en seguimiento
                 ├─ SitraMedPatientHcFetcher → GUID → HC
                 ├─ AriaPlanResolver → planes ARIA (mock o real)
                 ├─ Propaga IrradiationModality + ExactBeamEnergy → TreatmentLabel
                 ├─ Detecta PatientProcessEvents (vs snapshot previo)
                 ├─ Calcula StageSummary + transiciones semanales (excluye long-wait)
                 └─ Persiste → dashboard_bootstrap.json + patient_process_events.json
```

### 2. Actualización batch ARIA

```
POST /api/home/refresh-no-aria   → scrape sin ARIA
POST /api/aria/run-query         → 202 Accepted; AriaRunner en background
GET  /api/aria/query-status      → polling de progreso (frontend hace polling c/4s)
POST /api/aria/import-results    → parsea aria_results_*.json → mergea aria_plans_mock.json
POST /api/home/apply-aria        → enriquece snapshot con planes ARIA
```

### 3. Task Scheduler (automático)

`scripts/refresh.bat` en el Task Scheduler de Windows:
- Llama `POST /api/home/refresh`
- Llama `POST /api/agenda/scrape-upcoming`
- El servidor debe estar corriendo en el puerto 5000

---

## Directorio del proyecto

```
C:\Pablo\Meva.Rt\
├── Meva.Rt.Core\
│   ├── DomainModels.cs              ← Todas las entidades de dominio
│   └── TreatmentClassifier.cs       ← Classify() y BuildLabel()
├── Meva.Rt.Application\
│   ├── Contracts.cs                 ← Interfaces + BootstrapService (orquestador)
│   └── BusinessDayCalculator.cs     ← Días hábiles (excluye feriados)
├── Meva.Rt.Infrastructure.SitraMed\
│   ├── SitraMedExtractors.cs        ← Extractores de agenda, seguimiento, tomógrafo
│   ├── PlaywrightSitraMedClient.cs  ← Cliente web scraping (paralelo, SemaphoreSlim 2)
│   ├── SitraMedPatientHcFetcher.cs  ← Resolución GUID → HC
│   └── FollowUpDateParser.cs        ← Parseo de fechas de etapas del HTML de SitraMed
├── Meva.Rt.Infrastructure.Aria\
│   ├── AriaAdapter.cs               ← AriaPlanResolver
│   └── MetodosParaWebScrap.cs       ← Helpers AriaQ.dll
├── Meva.Rt.Infrastructure.Storage\
│   ├── JsonSnapshotStore.cs         ← Persistencia JSON principal
│   ├── WeeklyStatsStore.cs          ← Estadísticas semanales
│   ├── StageTransitionStore.cs      ← Transiciones de etapa
│   └── PatientProcessEventStore.cs  ← Eventos de proceso (append-only, escritura atómica)
├── Meva.Rt.Web\
│   ├── Program.cs                   ← Todos los endpoints de la API
│   ├── AppConfiguration.cs          ← Config hardcodeada de centros/equipos/etapas
│   ├── RtConfigurationHolder.cs     ← Override de config desde JSON
│   ├── AriaJobState.cs              ← Singleton: estado del job ARIA (TryStart/Complete/ReadProgress)
│   └── wwwroot/                     ← Frontend estático
│       ├── index.html               ← Estructura HTML (tabs, versión en ?v=...)
│       ├── app.js                   ← Toda la lógica frontend
│       └── styles.css               ← Estilos
├── Meva.Rt.AriaRunner\
│   ├── Program.cs                   ← Entry point (usa QueryAllPatients, sin workers)
│   ├── AriaQuery.cs                 ← 6 bulk queries WHERE IN a BD ARIA
│   ├── Models.cs                    ← DTOs de entrada/salida
│   ├── Logger.cs                    ← Logging thread-safe con timestamps
│   └── README_INSTRUCCIONES.txt     ← Instrucciones para PC con ARIA
├── data\                            ← Snapshots en runtime (no en git)
│   ├── dashboard_bootstrap.json
│   ├── aria_plans_mock.json
│   ├── patient_process_events.json
│   ├── weekly_stats.json
│   ├── stage_transitions.json
│   └── feriados.txt
└── scripts\
    └── refresh.bat                  ← Script para Task Scheduler
```

---

## Deployment

El sistema corre como **Windows Service** (`MevaRT`) en la PC servidora.

| Situación | Qué corre | Directorio | Puerto |
|---|---|---|---|
| Uso diario | Servicio Windows (automático) | `C:\MevaRT\` | 5062 |
| Modificando código | `dotnet run` (servicio detenido) | `C:\Pablo\Meva.Rt\` | 5063 |

- **Publicar al servicio:** `C:\Pablo\Meva.Rt\scripts\publish.ps1` (como Administrador)
- **Publish manual:** `dotnet publish Meva.Rt.Web/Meva.Rt.Web.csproj -c Release -o C:\MevaRT --nologo`
- **Datos compartidos:** ambos entornos usan `C:\MevaRT\data\` (mismos snapshots)
- **Diagnósticos SitraMed:** agregar `MEVA_SITRAMED_DIAGNOSTICS=true` en `HKLM:\SYSTEM\CurrentControlSet\Services\MevaRT\Environment` y reiniciar el servicio. Screenshots en `C:\MevaRT\data\diagnostics\`
- **Forzar rescrape:** `Invoke-RestMethod -Uri "http://localhost:5062/api/home/refresh" -Method POST`

---

## Notas de contexto

- **Puertos:** producción en `http://localhost:5062`, desarrollo en `http://localhost:5063`.
- **Branding (sesión 2026-06-19b):** el sistema se llama **MevaDash** (`<title>`, `<h1>`, export HTML de derivación). Header reestructurado: logo institucional a la derecha (`LogoMeva.png`, 67px alto), título a la izquierda. Eliminados el eyebrow "RADIOTERAPIA" y el subtítulo.
- **Logo:** `LogoMeva.png` en `wwwroot/`. El PNG definitivo usa fondo blanco puro (R=G=B=255) — versiones anteriores tenían el patrón de tablero de ajedrez bakeado como píxeles grises desde la exportación original. Verificado en `C:\MevaRT\wwwroot\` (producción). Cache-buster: `?v=20260622b`.
- **AriaQ.dll no está en el repo.** DLL propietaria de Varian. Se copia manualmente a `C:\MevaRT\AriaRunner\` antes de desplegar.
- **AriaRunner desplegado** en `C:\MevaRT\AriaRunner\AriaRunner.exe` (net9.0-windows: exe apphost + dll managed).
- **SAN JUSTO Equipo 2 y MEVA-Viamonte** — los AriaNames no están confirmados (vacíos en config).
- **`data/rt_configuration.json`** — si existe, sobrescribe los defaults de `AppConfiguration.cs`.
- **`aria_plans_mock.json` se mergea** al actualizar desde AriaRunner: pacientes agenda-pura conservan sus datos indefinidamente.
- **Cache HTML:** `index.html` tiene `Cache-Control: no-store` tanto en el meta tag como en `UseStaticFiles` de Program.cs. El auto-refresh del frontend detecta cambios de `appVersion` (basada en timestamp de `app.js`) y recarga automáticamente.
- **CSS (sesión 2026-06-22):** correcciones de consistencia: `--border` definido como alias de `--line` (4 bordes de tabla eran invisibles), banners de feriados/auto-update usan clases CSS con paleta del sistema (antes usaban inline styles con colores Bootstrap), badges P1/P2 referencian `--red`/`--muted` correctamente, hover y `:focus-visible` en todos los botones. Dead code removido: `.eyebrow`, `.subtitle`.
- **weekly_stats.json:** requiere 4 semanas de datos reales acumulados antes de usarse para estimaciones de fecha de inicio. Los archivos históricos masivos (importación inicial) están renombrados a `.backup.json`.
- **PatientProcessEvents:** solo detecta TechniqueChanged y StageRegressed. La desaparición de un paciente del seguimiento = inició tratamiento (no se detecta como suspensión).
- **Feriados:** `data/feriados.txt` con una fecha por línea en formato `YYYY-MM-DD`. La alerta de fin de año avisa cuando falta agregar el año siguiente.
- **Flujo de archivos de datos (dev vs prod):** ver `CLAUDE.md` en la raíz del repo — quién setea `MEVA_DATA_DIR` en cada entorno y las 2 inconsistencias conocidas.
- **QA Paciente Específico (sesión 2026-07-22):** el endpoint real que usa `refresh.bat` es `POST /api/home/apply-aria` (no `BootstrapService.BuildAsync`) — ahí es donde hay que propagar campos nuevos de ARIA (`Plans`), no solo en el camino "vivo" de `Contracts.cs`. Bug real encontrado: `apply-aria` copiaba `IrradiationModality`/`BeamType`/etc. pero no `Plans`, dejando la lista vacía en producción hasta corregirlo (`Program.cs`).
- **QA Paciente Específico / clasificación ARIA — 6 bugs reales (sesión 2026-07-24):** validando contra producción con el paciente real `1-114893-1` (2 seguimientos concurrentes, costal IMRT + húmero) aparecieron en cadena: HC con sufijo por curso no matcheaba ARIA; plan viejo ya tratado (`TreatApproval`) colaba en la alta de QA; técnica `ARC` clasificada como VMAT sin mirar `MLCPlanType`; cursos `Completed` y planes viejos ganándole al plan vigente en Equipo asignado; `"STATIC-I"` no matcheaba el check exacto de `"STATIC"`; y `TreatmentClassifier` promoviendo `3DC`→VMAT indebidamente + `Classify()` con falso positivo de VMAT por la palabra "arco" (anatomía, no técnica). Detalle completo, causas raíz y fixes en `BUG_PACIENTE_1-114893_ARIA_NO_MATCH.md`.
