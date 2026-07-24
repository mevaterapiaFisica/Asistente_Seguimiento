using System.Diagnostics;
using AriaQ;

namespace Meva.Rt.AriaRunner;

/// <summary>
/// Consultas a la base de datos de ARIA via AriaQ.dll (Entity Framework 6).
/// La connection string "variansystemEntities" se lee desde App.config (AriaRunner.exe.config).
/// </summary>
public sealed class AriaQuery
{
    private readonly RunnerLogger _log;

    public AriaQuery(RunnerLogger log)
    {
        _log = log;
    }

    private static Aria CreateContext() => new();

    /// <summary>
    /// Prueba la conexión haciendo una consulta mínima.
    /// </summary>
    public bool TestConnection()
    {
        try
        {
            using var ctx = CreateContext();
            var count = ctx.Patients.Count();
            _log.Info($"Conexión exitosa. Total de pacientes en ARIA: {count}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Error al conectar con ARIA", ex);
            return false;
        }
    }

    /// <summary>
    /// Consulta todos los pacientes en 5 queries bulk (WHERE PatientId IN (...)) y ensambla
    /// los resultados en memoria. Reduce de ~938 round-trips a ~6 queries totales.
    /// </summary>
    public List<PatientResult> QueryAllPatients(IReadOnlyList<string> patientIds)
    {
        var ids = patientIds
            .Select(id => (id ?? string.Empty).Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sw = Stopwatch.StartNew();
        _log.Info($"Modo bulk: {ids.Count} pacientes en {Math.Ceiling(ids.Count / 500.0)} lote(s)");

        try
        {
            using var ctx = CreateContext();

            // ── 1. Pacientes + médicos ────────────────────────────────────────────
            _log.Info("  [1/5] Cargando pacientes y médicos...");
            var patients = QueryInBatches(ids, chunk =>
                ctx.Patients
                   .Include("PatientDoctors.Doctor")
                   .Where(p => chunk.Contains(p.PatientId))
                   .ToList());
            _log.Info($"         {patients.Count} pacientes encontrados ({sw.Elapsed.TotalSeconds:F1}s)");

            var patientByPatientId = patients.ToDictionary(p => p.PatientId, StringComparer.OrdinalIgnoreCase);
            var patientSerList = patients.Select(p => p.PatientSer).ToList();

            // ── 2. Cursos ─────────────────────────────────────────────────────────
            _log.Info("  [2/5] Cargando cursos...");
            var courses = QueryInBatches(patientSerList, chunk =>
                ctx.Courses
                   .Where(c => chunk.Contains(c.PatientSer))
                   .ToList());
            _log.Info($"         {courses.Count} cursos encontrados ({sw.Elapsed.TotalSeconds:F1}s)");

            var coursesByPatientSer = courses.ToLookup(c => c.PatientSer);
            var courseSerList = courses.Select(c => c.CourseSer).ToList();

            // ── 3. Planes (excluye Rejected) con Prescription y RTPlans ──────────
            _log.Info("  [3/5] Cargando planes...");
            var planSetups = QueryInBatches(courseSerList, chunk =>
                ctx.PlanSetups
                   .Include("Prescription")
                   .Include("RTPlans")
                   .Where(ps => chunk.Contains(ps.CourseSer) && ps.Status != "Rejected")
                   .ToList());
            _log.Info($"         {planSetups.Count} planes encontrados ({sw.Elapsed.TotalSeconds:F1}s)");

            var plansByCourseSer = planSetups.ToLookup(ps => ps.CourseSer);
            var planSerList = planSetups.Select(ps => ps.PlanSetupSer).ToList();

            // ── 4a. Radiaciones sin ControlPoints (el Include de CPs genera JOIN cartesiano enorme) ──
            _log.Info("  [4/6] Cargando radiaciones...");
            var radiations = QueryInBatches(planSerList, chunk =>
                ctx.Radiations
                   .Include("RadiationDevice.Machine")
                   .Include("ExternalFieldCommon.EnergyMode")
                   .Include("ExternalFieldCommon.Technique")
                   .Where(r => chunk.Contains(r.PlanSetupSer))
                   .ToList());
            _log.Info($"         {radiations.Count} radiaciones encontradas ({sw.Elapsed.TotalSeconds:F1}s)");

            var radiationsByPlanSer = radiations.ToLookup(r => r.PlanSetupSer);
            var radiationSerList = radiations.Select(r => r.RadiationSer).ToList();

            // ── 4b. Conteo de ControlPoints por RadiationSer (GROUP BY, sin cargar los CPs) ──
            _log.Info("  [5/6] Contando control points...");
            var cpCounts = QueryInBatches(radiationSerList, chunk =>
                ctx.ExternalFieldCommons
                   .Where(ef => chunk.Contains(ef.RadiationSer))
                   .Select(ef => new { ef.RadiationSer, CpCount = ef.ControlPoints.Count() })
                   .ToList())
                .ToDictionary(x => x.RadiationSer, x => x.CpCount);
            _log.Info($"         {cpCounts.Count} entradas ({sw.Elapsed.TotalSeconds:F1}s)");

            // ── 4c. Tipo de MLC por RadiationSer (discrimina VMAT real de arco conformado/TBI con técnica ARC) ──
            _log.Info("  Cargando tipo de MLC...");
            var mlcTypes = QueryInBatches(radiationSerList, chunk =>
                ctx.ExternalFieldCommons
                   .Where(ef => chunk.Contains(ef.RadiationSer))
                   .SelectMany(ef => ef.MLCPlans.Select(m => new { ef.RadiationSer, m.MLCPlanType }))
                   .ToList())
                .GroupBy(x => x.RadiationSer)
                .ToDictionary(g => g.Key, g => g.Select(x => x.MLCPlanType).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)));
            _log.Info($"         {mlcTypes.Count} entradas ({sw.Elapsed.TotalSeconds:F1}s)");

            // ── 6. Ensamblar resultados en el orden original ───────────────────────
            _log.Info("  [6/6] Ensamblando resultados...");
            var results = new List<PatientResult>(ids.Count);

            foreach (var id in patientIds
                .Select(x => (x ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var result = new PatientResult { PatientId = id };

                if (!patientByPatientId.TryGetValue(id, out var patient))
                {
                    result.Found = false;
                    results.Add(result);
                    continue;
                }

                result.Found = true;
                result.FirstName = patient.FirstName?.Trim();
                result.LastName = patient.LastName?.Trim();
                result.DateOfBirth = patient.DateOfBirth?.ToString("yyyy-MM-dd");
                result.Sex = patient.Sex?.Trim();

                var oncologist = patient.PatientDoctors?
                    .Where(pd => pd.OncologistFlag == 1 || pd.PrimaryFlag == 1)
                    .Select(pd => pd.Doctor)
                    .FirstOrDefault(d => d != null);
                if (oncologist != null)
                    result.Oncologist = $"{oncologist.LastName?.Trim()}, {oncologist.FirstName?.Trim()}"
                        .Trim(',', ' ');

                var patCourses = coursesByPatientSer[patient.PatientSer]
                    .Where(c => c.CourseId != null
                        && !c.CourseId.Contains("qa", StringComparison.OrdinalIgnoreCase)
                        && !c.CourseId.Contains("fisica", StringComparison.OrdinalIgnoreCase)
                        && !c.CourseId.Contains("física", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var allPlans = new List<PlanResult>();
                foreach (var course in patCourses)
                {
                    foreach (var plan in plansByCourseSer[course.CourseSer])
                    {
                        var planRads = radiationsByPlanSer[plan.PlanSetupSer].ToList();
                        allPlans.Add(BuildPlanResult(course, plan, planRads, cpCounts, mlcTypes));
                    }
                }

                result.AllPlans = allPlans;
                result.ActivePlan = SelectActivePlan(allPlans);
                results.Add(result);
            }

            var found = results.Count(r => r.Found);
            var notFound = results.Count(r => !r.Found);
            _log.Info($"Bulk completado en {sw.Elapsed.TotalSeconds:F1}s — {found} encontrados, {notFound} no encontrados");
            return results;
        }
        catch (Exception ex)
        {
            _log.Error("Error en consulta bulk", ex);
            throw;
        }
    }

    /// <summary>
    /// Busca un único paciente. Útil para pruebas y consultas individuales.
    /// </summary>
    public PatientResult QueryPatient(string patientId)
    {
        var result = new PatientResult { PatientId = patientId };

        try
        {
            using var ctx = CreateContext();

            var patient = ctx.Patients
                .Include("Courses.PlanSetups.Radiations.RadiationDevice.Machine")
                .Include("Courses.PlanSetups.Radiations.ExternalFieldCommon.EnergyMode")
                .Include("Courses.PlanSetups.Radiations.ExternalFieldCommon.Technique")
                .Include("Courses.PlanSetups.Radiations.ExternalFieldCommon.ControlPoints")
                .Include("Courses.PlanSetups.Prescription")
                .Include("Courses.PlanSetups.RTPlans")
                .Include("PatientDoctors.Doctor")
                .FirstOrDefault(p => p.PatientId == patientId);

            if (patient == null)
            {
                result.Found = false;
                _log.Warn($"  [{patientId}] No encontrado en ARIA");
                return result;
            }

            result.Found = true;
            result.FirstName = patient.FirstName?.Trim();
            result.LastName = patient.LastName?.Trim();
            result.DateOfBirth = patient.DateOfBirth?.ToString("yyyy-MM-dd");
            result.Sex = patient.Sex?.Trim();

            var oncologist = patient.PatientDoctors?
                .Where(pd => pd.OncologistFlag == 1 || pd.PrimaryFlag == 1)
                .Select(pd => pd.Doctor)
                .FirstOrDefault(d => d != null);
            if (oncologist != null)
                result.Oncologist = $"{oncologist.LastName?.Trim()}, {oncologist.FirstName?.Trim()}"
                    .Trim(',', ' ');

            var courses = patient.Courses?
                .Where(c => c.CourseId != null
                    && !c.CourseId.Contains("qa", StringComparison.OrdinalIgnoreCase)
                    && !c.CourseId.Contains("fisica", StringComparison.OrdinalIgnoreCase)
                    && !c.CourseId.Contains("física", StringComparison.OrdinalIgnoreCase))
                .ToList() ?? [];

            var allPlans = new List<PlanResult>();
            foreach (var course in courses)
            {
                var planSetups = course.PlanSetups?
                    .Where(p => p.Status != "Rejected")
                    .ToList() ?? [];

                foreach (var plan in planSetups)
                    allPlans.Add(BuildPlanResult(course, plan, plan.Radiations));
            }

            result.AllPlans = allPlans;
            result.ActivePlan = SelectActivePlan(allPlans);

            _log.Info($"  [{patientId}] {result.LastName}, {result.FirstName} | " +
                      $"Plan: {result.ActivePlan?.PlanId ?? "ninguno"} ({result.ActivePlan?.Status ?? "-"}) | " +
                      $"Equipo: {result.ActivePlan?.MachineAriaId ?? "-"} | " +
                      $"Haz: {result.ActivePlan?.BeamType ?? "-"} | " +
                      $"Fx: {result.ActivePlan?.NumberOfFractions?.ToString() ?? "null"}");
        }
        catch (Exception ex)
        {
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
            _log.Error($"  [{patientId}] Error al consultar", ex);
        }

        return result;
    }

    // Ejecuta query en lotes de batchSize para no superar el límite de 2100 parámetros de SQL Server.
    private static List<TResult> QueryInBatches<TKey, TResult>(
        IReadOnlyList<TKey> keys,
        Func<List<TKey>, List<TResult>> query,
        int batchSize = 500)
    {
        var all = new List<TResult>();
        for (var i = 0; i < keys.Count; i += batchSize)
        {
            var batch = keys.Skip(i).Take(batchSize).ToList();
            all.AddRange(query(batch));
        }
        return all;
    }

    // radiations: para QueryAllPatients viene del lookup; para QueryPatient viene de plan.Radiations.
    // cpCounts: conteo de ControlPoints por RadiationSer (solo en bulk); null = usar navigation property.
    // mlcTypes: MLCPlanType por RadiationSer (solo en bulk); null = usar navigation property.
    private static PlanResult BuildPlanResult(Course course, PlanSetup plan, ICollection<Radiation>? radiations,
        Dictionary<long, int>? cpCounts = null, Dictionary<long, string?>? mlcTypes = null)
    {
        var statusDateStr = plan.StatusDate == default ? null : plan.StatusDate.ToString("yyyy-MM-dd");
        var creationDateStr = plan.CreationDate == default ? null : plan.CreationDate.ToString("yyyy-MM-dd");

        var pr = new PlanResult
        {
            CourseId = course.CourseId?.Trim(),
            PlanId = plan.PlanSetupId?.Trim(),
            PlanName = plan.PlanSetupName?.Trim(),
            Status = plan.Status?.Trim(),
            StatusDate = statusDateStr,
            CreationDate = creationDateStr,
            TreatmentTechnique = plan.TreatmentTechnique?.Trim(),
        };

        if (plan.Prescription != null)
        {
            pr.NumberOfFractions = plan.Prescription.NumberOfFractions;
            pr.PrescriptionSite = plan.Prescription.Site?.Trim();
            pr.PrescriptionTechnique = plan.Prescription.Technique?.Trim();
        }

        if (pr.NumberOfFractions == null)
            pr.NumberOfFractions = plan.RTPlans?.OrderByDescending(r => r.CreationDate).FirstOrDefault()?.NoFractions;

        var firstRadiation = radiations?.FirstOrDefault();
        if (firstRadiation?.RadiationDevice?.Machine != null)
        {
            pr.MachineAriaId = firstRadiation.RadiationDevice.Machine.MachineId?.Trim();
            pr.MachineName = firstRadiation.RadiationDevice.Machine.MachineName?.Trim();
        }

        if (firstRadiation != null)
        {
            var em = firstRadiation.ExternalFieldCommon?.EnergyMode;
            var techniqueLabel = firstRadiation.TechniqueLabel?.Trim() ?? string.Empty;
            pr.BeamType = DetermineBeamType(em?.RadiationType?.Trim(), em?.Energy, techniqueLabel);
            pr.IrradiationModality = Modalidad(firstRadiation, cpCounts, mlcTypes);
            pr.ExactBeamEnergy = DetermineExactBeamEnergy(radiations);
        }

        return pr;
    }

    // Energy en ARIA está en keV: 6000 = 6 MV (6X), 10000 = 10 MV (Alta Energia), etc.
    // RadiationType en EnergyMode: "X" = fotones, "E" = electrones (no "PHOTON"/"ELECTRON").
    private const int HighEnergyThresholdKev = 10_000; // 10 MV

    private static string? DetermineBeamType(string? emRadiationType, int? emEnergy, string techniqueLabel)
    {
        if (string.Equals(emRadiationType, "E", StringComparison.OrdinalIgnoreCase))
            return "Electrones";

        if (techniqueLabel.Contains("SRS", StringComparison.OrdinalIgnoreCase)
            || techniqueLabel.Contains("STEREO", StringComparison.OrdinalIgnoreCase))
            return "SRS";

        if (string.Equals(emRadiationType, "X", StringComparison.OrdinalIgnoreCase))
            return emEnergy.HasValue && emEnergy >= HighEnergyThresholdKev ? "AltaE" : "6X";

        if (emEnergy.HasValue)
            return emEnergy >= HighEnergyThresholdKev ? "AltaE" : "6X";

        return null;
    }

    private static string Modalidad(Radiation firstRadiation, Dictionary<long, int>? cpCounts = null, Dictionary<long, string?>? mlcTypes = null)
    {
        if (firstRadiation.ExternalFieldCommon?.Technique == null)
            return "Indefinido";

        var techId = firstRadiation.ExternalFieldCommon.Technique.TechniqueId;

        if (techId == "ARC")
        {
            // Técnica ARC no es sinónimo de VMAT: arco conformado (MLC static/dynamic arc) y algunos
            // TBI también usan ARC pero con MLCPlanType distinto (o sin MLC). Solo VMAT real si el
            // MLCPlanType lo indica explícitamente.
            var mlcType = mlcTypes != null
                ? (mlcTypes.TryGetValue(firstRadiation.RadiationSer, out var t) ? t : null)
                : firstRadiation.ExternalFieldCommon.MLCPlans?.Select(m => m.MLCPlanType).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            return !string.IsNullOrWhiteSpace(mlcType) && mlcType.Contains("VMAT", StringComparison.OrdinalIgnoreCase)
                ? "VMAT"
                : "3DC";
        }

        if (techId == "STATIC")
        {
            // cpCounts viene del bulk query (GROUP BY); ControlPoints viene del single query.
            var count = cpCounts != null
                ? (cpCounts.TryGetValue(firstRadiation.RadiationSer, out var n) ? n : 0)
                : (firstRadiation.ExternalFieldCommon.ControlPoints?.Count ?? 0);
            return count > 40 ? "IMRT" : "3DC";
        }

        return "Indefinido";
    }

    private static string DetermineExactBeamEnergy(ICollection<Radiation>? radiations)
    {
        if (radiations == null || radiations.Count == 0) return "Indefinido";

        if (radiations.Any(r =>
            string.Equals(r.ExternalFieldCommon?.EnergyMode?.RadiationType?.Trim(), "E",
                StringComparison.OrdinalIgnoreCase)))
            return "Electrones";

        var maxKev = radiations
            .Select(r => r.ExternalFieldCommon?.EnergyMode?.Energy)
            .Where(e => e.HasValue)
            .Select(e => e!.Value)
            .DefaultIfEmpty(0)
            .Max();

        if (maxKev <= 0) return "Indefinido";
        if (maxKev < 7000) return "6X";
        if (Math.Abs(maxKev - 10000) <= 500) return "10X";
        if (Math.Abs(maxKev - 15000) <= 500) return "15X";
        if (Math.Abs(maxKev - 18000) <= 500) return "18X";
        return "Indefinido";
    }

    private static PlanResult? SelectActivePlan(List<PlanResult> plans)
    {
        if (plans.Count == 0) return null;

        return plans.FirstOrDefault(p => p.Status == "TreatApproval")
            ?? plans.FirstOrDefault(p => p.Status == "PlanApproval")
            ?? plans.Where(p => p.Status == "Unapproved")
                    .OrderByDescending(p => p.CreationDate)
                    .FirstOrDefault();
    }
}
