# Bugs de datos ARIA en QA Paciente Específico (sesión 2026-07-24)

Registro de 3 bugs encontrados y corregidos el mismo día, validando la pestaña QA Paciente
Específico contra datos reales de producción.

## Bug 1: paciente 1-114893-1 no aparece en QA Paciente Específico (ARIA no lo encuentra)

**Fecha:** 2026-07-24
**Estado:** fix aplicado y verificado en producción tras `publish.ps1` + `refresh.bat` real.

### Reporte

Paciente `1-114893-1` (CASTILLO, Guillermo Carlos) tiene en SitraMed dos seguimientos activos en
paralelo: un plan IMRT (arco costal izquierdo, ya en etapa F6F/Control de Calidad, plan ARIA
`5toCostIzq_sk` con `PlanApproval` en MEVA-Central Equipo 2) y un plan 3DC (húmero, etapa F6B). El
plan IMRT cumple todas las condiciones de entrada a QA Paciente Específico y no aparece.

### Causa raíz confirmada

No es un bug de la pestaña QA — el feature funciona correctamente sobre los datos que recibe.
El problema es upstream: **ARIA nunca devolvió datos para este paciente.**

- `state.homeData.patients` trae 2 filas idénticas para `1-114893-1` (mismo `patientId`, mismo
  `sitraMedGuid`) — una por cada etapa/curso donde el scraper lo encontró — ambas con
  `plans: []` e `irradiationModality: null`.
- `C:\MevaRT\data\pacientes.json` (lista exportada a AriaRunner) confirma que se pidió exactamente
  `"1-114893-1"` — el HC tal como lo muestra SitraMed (`PatientId = row.SitraMedId` en
  `SitraMedExtractors.cs:284/377`, sin transformación).
- `C:\MevaRT\data\aria_plans_mock.json` (resultado de la última consulta bulk a ARIA) **no tiene
  ninguna entrada** para `1-114893-1` ni para la variante `1-114893-0` — la consulta
  `WHERE PatientId IN (...)` en `AriaQuery.cs` no encontró coincidencia con ese string exacto.

Conclusión: el `PatientId` que ARIA tiene almacenado para este paciente físico no coincide con el HC
que muestra SitraMed en este seguimiento. Confirmado en ARIA (Patient Explorer, captura de pantalla
del usuario): `ID1 = 1-114893-0`. SitraMed muestra `1-114893-1` para el seguimiento del segundo curso
concurrente (arco costal/IMRT) — ARIA no tiene noción de ese sufijo por-curso.

### Fix aplicado

`BootstrapService.NormalizeAriaBaseId(hc)` (`Meva.Rt.Application/Contracts.cs`) normaliza el último
segmento `-N` de un HC a `-0`. `BootstrapService.TryFindAriaPlan(...)` intenta el HC exacto primero y,
si no tiene datos ARIA reales (`Plans` vacío + campos ARIA nulos — cubre el caso de `AriaAdapter` que
siempre crea una entrada "vacía" por cada id consultado, exista o no), reintenta con la variante
normalizada `-0`.

Aplicado en 3 puntos que arman la lista de HCs a consultar en ARIA (agregan también la variante `-0`)
y en los 2 puntos de merge que hoy hacían `ariaByPatient.TryGetValue` directo:
- `Contracts.cs` (`BootstrapService.BuildAsync`, camino live/refresh completo)
- `Program.cs` (`/api/home/refresh-no-aria` — arma `pacientes.json`, el que usa `refresh.bat` en la
  práctica; `/api/home/apply-aria`; `/api/aria/run-query`; `/api/aria/export-patient-ids`)

Verificado con un proyecto descartable que `TryFindAriaPlan` no queda tapado por el shell vacío del
HC exacto y sí encuentra los datos reales bajo `-0`.

### Relación con el bug ya conocido (duplicado en Buscar/Seguimiento)

Mismo paciente, mismo síntoma raíz: SitraMed expone el mismo HC (`1-114893-1`) en dos páginas de
etapa distintas (una por cada curso/seguimiento concurrente), y el scraper genera una fila de
`ProcessPatientSnapshot` por cada aparición — de ahí que aparezca "duplicado" en Buscar Pacientes y
Seguimiento. **Pendiente de evaluación** (por decisión del usuario, no resuelto en esta sesión): dejarlo
así o deduplicar por `sitraMedGuid`+`PatientId` en algún punto del pipeline.

## Bug 2: plan viejo ya tratado (`TreatApproval`) colaba en la alta

Tras el fix del HC, el plan IMRT (`5toCostIzq_sk`, `PlanApproval`) apareció correctamente — pero
**también** apareció `Plan3`, un plan VMAT viejo (creado 10/7/25) del mismo paciente, ya con
`Status: "TreatApproval"` en ARIA (o sea, un curso ya tratado hace más de un año, no relacionado al
seguimiento actual). Visualmente se veía como "paciente duplicado" en la tabla — en realidad eran 2
filas legítimas (una por plan), pero una de ellas no correspondía.

**Causa:** `_qaEspecificoEligiblePlans()` (`app.js`) dejaba entrar cualquier plan IMRT/VMAT del
paciente en cuanto `stagePastF6C` era verdadero (la condición OR de entrada), sin excluir los que ya
estaban en `TreatApproval` — es decir, planes de cursos **ya tratados y cerrados** colaban por la
puerta pensada para "SitraMed ya avanzó pero ARIA todavía no marcó `PlanApproval`".

**Fix:** excluir explícitamente `plan.status === 'TreatApproval'` de la condición de alta (siguen
disparando la salida de items ya existentes con ese `planId`, solo no generan altas nuevas).

Corregido también: `PlanesPlanApproval`/`PlanesTreatApproval` en `MetodosParaWebScrap.cs` tenían el
filtro de antigüedad de 30 días invertido (`(p.CreationDate - DateTime.Today).Days < 30` — siempre
`true`, nunca filtraba). Cambiado a `(DateTime.Today - p.CreationDate).Days < 30`.

## Bug 3: técnica ARC ≠ VMAT (paciente 1-118543-0)

Reportado por el usuario: `1-118543-0` (arco conformado / TBI vía arco) apareció en QA Paciente
Específico como si fuera VMAT. Comparación con `1-119096-0` (VMAT real, correcto).

**Causa raíz:** `Modalidad()` (`AriaQuery.cs`) y `ResolveIrradiationModality()` (`AriaAdapter.cs`)
clasificaban **cualquier** plan con `Technique.TechniqueId == "ARC"` como `"VMAT"`. Pero `ARC` es la
técnica base de arco (gantry en movimiento continuo) — VMAT, arco conformado (3D conformal arc) y
algunos TBI entregados por arco comparten esa misma técnica en ARIA. El discriminador real es el tipo
de MLC (`MLCPlan.MLCPlanType`, entidad `AriaQ.MLCPlan` descubierta por reflexión sobre `AriaQ.dll`,
no documentada en el repo): TBI típicamente sin MLC, arco conformado con MLC estático/dynamic arc,
VMAT con MLC de tipo VMAT.

**Fix:** `Modalidad()`/`ResolveIrradiationModality()` ahora, para técnica `ARC`, buscan el
`MLCPlanType` del primer haz (`ExternalFieldCommon.MLCPlans`) y solo devuelven `"VMAT"` si ese string
contiene "VMAT" (case-insensitive); cualquier otro caso (static, dynamic arc, o sin MLC) cae a
`"3DC"`. En el bulk query (`AriaQuery.cs`) se agregó un lookup `mlcTypes` por `RadiationSer` (mismo
patrón que `cpCounts`: `GROUP BY` separado para no generar el join cartesiano que ya se evita para
ControlPoints).

**Sin verificar contra ARIA real** — no hay acceso a la base desde este entorno. El valor exacto que
usa `MLCPlanType` para VMAT (¿"VMAT"? ¿"VMAT Arc"?) se infirió por nombre de campo + contains
case-insensitive (tolera variantes). **Verificar en el próximo refresh real** que `1-118543-0` deja de
aparecer y `1-119096-0` sigue apareciendo. Si `1-118543-0` persiste, pedir el valor real de
`MLCPlanType` para ese plan (Patient Explorer o consulta directa) para ajustar el match.

## Bug 4: el mismo plan viejo (`Plan3`) también rompía el Equipo asignado fuera de QA

Reportado por el usuario: `1-114893-1` aparecía asignado al **Equipo 3** de MEVA-Central (por `Plan3`,
viejo, ya tratado) en vez de **Equipo 2** (plan real vigente, `5toCostIzq_sk`). No es exclusivo de QA
Paciente Específico — afecta a `PlannedMachineDisplayName`/`IrradiationModality`/etc. en toda la app,
porque `SelectActivePlan()` (`AriaQuery.cs`) prioriza `Status` (`TreatApproval` > `PlanApproval` >
`Unapproved`) **sin mirar antigüedad ni si el curso ya terminó** — un curso viejo cerrado en
`TreatApproval` le gana siempre a un `PlanApproval` real y vigente de un curso nuevo.

**Dos barreras agregadas** (pedidas explícitamente por el usuario):

1. **Cursos no-`Completed` solamente.** Nuevo helper `EsCursoActivo(Course)` en `AriaQuery.cs` y
   `MetodosParaWebScrap.cs`: excluye cursos con `CompletedDateTime` seteado o `ClinicalStatus ==
   "Completed"`. Aplicado en `patCourses`/`courses` (bulk y single-query) y en
   `PlanesDeCursosActivos()` (nuevo helper compartido en `MetodosParaWebScrap.cs`, usado por
   `PlanActivo`/`PlanesPlanApproval`/`PlanesTreatApproval`). **`CompletedDateTime` es la señal
   confiable** — no depende de acertarle al string exacto de `ClinicalStatus`, que tampoco se pudo
   verificar contra ARIA real (`Course.ClinicalStatus` es `string` libre, valor exacto no confirmado).

2. **Solo planes creados hace menos de 30 días** (mismo criterio que `PlanesPlanApproval`/
   `PlanesTreatApproval`, "como en QA Paciente Específico"). Trasladado a `SelectActivePlan()`
   (`AriaQuery.cs`) y `PlanActivo()` (`MetodosParaWebScrap.cs`, antes sin ningún filtro de
   antigüedad). Si **ningún** candidato es reciente (único plan real es viejo), se usa el pool
   completo sin filtrar — para no devolver "sin plan" cuando en verdad hay un plan viejo pero es el
   único que existe.

**Sin verificar contra ARIA real** (mismo motivo que el Bug 3: sin acceso a la base). Verificar en el
próximo refresh real que `1-114893-1` muestra Equipo 2, no Equipo 3.

## Bug 5: Equipo se corrigió, pero el plan (STATIC-I, MLC "Dose Dynamic") se clasificó como VMAT

Tras el fix del Bug 4, `1-114893-1` mostró correctamente Equipo 2 — pero la técnica se mostró como
VMAT en vez de IMRT estático. El usuario confirmó en ARIA: `Technique` = `STATIC` o `STATIC-I` (nunca
`ARC`), MLC = "Dose Dynamic". La rama `ARC` de `Modalidad()` (Bug 3) no debería ni ejecutarse para este
plan — la técnica nunca es `ARC`.

**Causas identificadas (2, compuestas):**

1. **`techId == "STATIC"` era comparación exacta** — no matcheaba `"STATIC-I"` (variante real
   observada en ARIA para IMRT step-and-shoot/multi-segmento). Al no matchear ni `ARC` ni `STATIC`,
   caía a `"Indefinido"`.
2. **Orden no-determinístico de `Radiations`.** La consulta bulk (`ctx.Radiations...ToList()`, sin
   `ORDER BY`) y las colecciones de navegación del camino vivo no garantizan orden estable — un plan
   con más de un haz puede devolver "el primero" distinto entre corridas, hacia un haz equivocado
   (posiblemente uno de verificación/setup, o de otra técnica). Esto explica por qué la clasificación
   cambió entre corridas sin cambiar el código de `STATIC`.

**Fix:**
- `techId.StartsWith("STATIC", OrdinalIgnoreCase)` en vez de igualdad exacta (`AriaQuery.cs`,
  `AriaAdapter.cs`) — cubre `STATIC` y `STATIC-I`.
- `OrderBy(r => r.RadiationSer)` explícito antes de `FirstOrDefault()`/`First()` en todos los puntos
  donde se toma "el primer haz" (`AriaQuery.cs`: query bulk; `AriaAdapter.cs` y
  `MetodosParaWebScrap.Equipo`: colecciones de navegación) — determinismo estable entre corridas.

**Sin verificar contra ARIA real.** Verificar en el próximo refresh real que `5toCostIzq_sk` muestra
IMRT (no VMAT, no Indefinido).

**Corrección del usuario tras el fix:** la causa #2 (orden no-determinístico) no aplica a este caso —
el usuario confirmó que todos los haces de este plan son `STATIC`/`STATIC-I` (nada de `ARC` mezclado).
Se mantiene el `OrderBy` igual como buena práctica general (no hace daño, da determinismo), pero **no
era la causa real** de este caso puntual. Ver Bug 6 para la causa real.

## Bug 6: técnica de SitraMed "VMAT" no es una técnica válida — falso positivo por `Classify()`

Tras el fix del Bug 5 (`STATIC-I`), una de las dos filas duplicadas de `1-114893-1` seguía mostrando
`TreatmentLabel` = "VMAT" mientras la otra mostraba "IMRT - estático" — mismo paciente, mismo plan
ARIA (`5toCostIzq_sk`, ahora ya corregido a IMRT). El usuario aclaró el modelo real: **SitraMed solo
tiene 7 técnicas válidas: `3DC`, `IMRT`, `TBI`, `BQT`, `TSET`, `RC`, `SBRT`** — "VMAT" nunca es una
técnica de SitraMed en sí misma; es ARIA quien la revela como refinamiento de `IMRT`/`RC`/`SBRT`.

**Causa raíz doble:**

1. **`TreatmentClassifier.BuildLabel()` agrupaba `IMRT` y `3DC` en la misma rama de refinamiento**
   (`tech is "IMRT" or "3DC"` → si ARIA decía VMAT, promovía a `"VMAT"` sin distinguir). Según el
   usuario, `3DC` **nunca** se promueve a VMAT/IMRT — ARIA solo le refina la energía (6X/10X/15X/18X/
   electrones). Solo `IMRT` se refina a `"IMRT - estático"` o `"VMAT"`.
2. **`TreatmentClassifier.Classify()` disparaba `"VMAT"` con el simple keyword `"arco"`** (sin
   contexto) — el paciente es "5to **Arco** Costal Izquierdo" (anatomía, costilla), no "técnica de
   arco". Cualquier texto SitraMed que mencione esa zona anatómica clasificaba falsamente como VMAT,
   una técnica que ni siquiera existe en el vocabulario real de SitraMed.

**Fix:**
- `BuildLabel()`: separadas las ramas `IMRT` y `3DC`. `3DC` solo refina energía (sin cambios de
  comportamiento ahí). `IMRT` refina a estático/VMAT como antes.
- Agregado soporte "arcos" para `RC`/`SBRT` (`"RC - arcos"`/`"SBRT - arcos"`) usando el mismo criterio
  ARC+MLC-no-VMAT que ya distingue `Modalidad()` para IMRT — requirió exponer un nuevo valor de
  modalidad `"ArcoConformado"` (antes colapsaba a `"3DC"` genérico, indistinguible) en `Modalidad()`
  (`AriaQuery.cs`) y `ResolveIrradiationModality()` (`AriaAdapter.cs`).
- `Classify()`: quitado el trigger genérico `"arco"` de la detección de VMAT — queda solo el
  match literal de la palabra `"VMAT"` en el texto.

Verificado con un proyecto descartable: `BuildLabel`/`Classify` con los casos IMRT+IMRT,
IMRT+VMAT, 3DC+VMAT (no promociona), 3DC+energía, RC/SBRT+ArcoConformado/VMAT, TBI, y
`Classify("...arco costal...")` ya no da VMAT.

**Sin verificar contra ARIA real** (mismo motivo de siempre). Verificar en el próximo refresh real que
ambas filas duplicadas de `1-114893-1` muestran "IMRT - estático" de forma consistente.
