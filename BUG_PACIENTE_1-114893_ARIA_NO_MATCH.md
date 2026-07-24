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
