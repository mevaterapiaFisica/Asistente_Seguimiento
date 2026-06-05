# Idea: AriaRunner — consulta bulk para reducir round-trips

## Diagnóstico (2026-06-05)

Se probó paralelización con 1/3/5/10 workers. El bottleneck no es CPU sino el **número de round-trips a ARIAMEVADB-SVR**:

| Workers | Tiempo (200 pac) |
|---------|-----------------|
| 1 | 413s (6.9 min) |
| 3 | 354s (5.9 min) |
| 5 | 357s (6.0 min) |
| 10 | ~similares |

Más workers no escala → SQL Server saturado. Se revirtió a 1 worker secuencial.

Run real 938 pacientes: **27 min secuencial** (código actual).

## La idea

En lugar de 938 queries individuales, usar una query bulk con `WHERE PatientId IN (...)`.

### Opción A — EF6 Include en bulk (simple pero riesgosa)

```csharp
var ids = input.PatientIds.ToHashSet();
using var ctx = CreateContext();
var patients = ctx.Patients
    .Where(p => ids.Contains(p.PatientId))
    .Include("Courses.PlanSetups.Radiations.RadiationDevice.Machine")
    .Include("Courses.PlanSetups.Radiations.ExternalFieldCommon.EnergyMode")
    .Include("Courses.PlanSetups.Radiations.ExternalFieldCommon.Technique")
    .Include("Courses.PlanSetups.Radiations.ExternalFieldCommon.ControlPoints")
    .Include("Courses.PlanSetups.Prescription")
    .Include("Courses.PlanSetups.RTPlans")
    .Include("PatientDoctors.Doctor")
    .ToList();
```

**Riesgo:** EF6 genera JOINs cartesianos sobre 938 pacientes × cursos × planes × haces × CPs → resultado set potencialmente enorme. Puede ser más lento que el secuencial.

### Opción B — queries separadas por tabla y ensamblado en memoria (recomendada)

```csharp
var ids = input.PatientIds.ToHashSet();
using var ctx = CreateContext();

// 1 query por tabla → 5-6 queries totales en lugar de 938
var patients     = ctx.Patients.Where(p => ids.Contains(p.PatientId)).ToList();
var patientSers  = patients.Select(p => p.PatientSer).ToHashSet();

var courses      = ctx.Courses.Where(c => patientSers.Contains(c.PatientSer)).ToList();
var courseSers   = courses.Select(c => c.CourseSer).ToHashSet();

var planSetups   = ctx.PlanSetups
                      .Include("Prescription").Include("RTPlans")
                      .Where(ps => courseSers.Contains(ps.CourseSer) && ps.Status != "Rejected")
                      .ToList();
var planSers     = planSetups.Select(ps => ps.PlanSetupSer).ToHashSet();

var radiations   = ctx.Radiations
                      .Include("RadiationDevice.Machine")
                      .Include("ExternalFieldCommon.EnergyMode")
                      .Include("ExternalFieldCommon.Technique")
                      .Include("ExternalFieldCommon.ControlPoints")
                      .Where(r => planSers.Contains(r.PlanSetupSer))
                      .ToList();

var patientDocs  = ctx.PatientDoctors.Include("Doctor")
                      .Where(pd => patientSers.Contains(pd.PatientSer))
                      .ToList();

// Ensamblar en memoria usando Lookup/Dictionary
var coursesByPatient    = courses.ToLookup(c => c.PatientSer);
var plansByCourseSer    = planSetups.ToLookup(ps => ps.CourseSer);
var radiationsByPlanSer = radiations.ToLookup(r => r.PlanSetupSer);
var docsByPatient       = patientDocs.ToLookup(pd => pd.PatientSer);

foreach (var patient in patients)
{
    // construir PatientResult como hoy pero desde las colecciones en memoria
}
```

## Estimación de ganancia

- Latencia por round-trip a ARIAMEVADB-SVR: ~30ms (LAN)
- Costo actual: 938 × 30ms = ~28s solo en latencia
- Con bulk: 6 × 30ms = 0.18s en latencia
- Speedup potencial: **5-10×** → de 27 min a 3-6 min

## Lo que hay que cambiar

- `AriaQuery.cs`: reescribir `QueryPatient` → nuevo método `QueryAllPatients(List<string> ids)`
- `Program.cs`: llamar el nuevo método en lugar del loop
- Validar que los datos ensamblados sean idénticos a los de hoy (comparar con baseline)

## Contexto adicional

- EF6 DbContext `Aria` está en `AriaQ.dll` (Varian, no modificable)
- Tablas relevantes: `Patients`, `Courses`, `PlanSetups`, `Radiations`, `RadiationDevices`, `Machines`, `ExternalFieldCommons`, `EnergyModes`, `Techniques`, `ControlPoints`, `Prescriptions`, `RTPlans`, `PatientDoctors`, `Doctors`
- El código de construcción de `PlanResult` en `AriaQuery.BuildPlanResult()` puede reutilizarse casi sin cambios
