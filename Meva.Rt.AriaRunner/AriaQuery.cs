using System.Diagnostics;
using AriaQ;
using Meva.Rt.Application;

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
            // Orden explícito por RadiationSer: sin ORDER BY, SQL Server no garantiza el orden de
            // retorno, y "primer haz" (FirstOrDefault más abajo) sería no-determinístico entre corridas.
            var radiations = QueryInBatches(planSerList, chunk =>
                ctx.Radiations
                   .Include("RadiationDevice.Machine")
                   .Include("ExternalFieldCommon.EnergyMode")
                   .Include("ExternalFieldCommon.Technique")
                   .Where(r => chunk.Contains(r.PlanSetupSer))
                   .OrderBy(r => r.RadiationSer)
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

            // ── 4c. DoseRate por RadiationSer (discrimina VMAT real de arco conformado con técnica ARC:
            // MLCPlanType es "DynMLCPlan" en ambos casos, no sirve — DoseRate=1000 (haz SRS) es arco
            // conformado, DoseRate normal (600) es VMAT real; confirmado contra ARIA real, sesión 2026-08-03) ──
            _log.Info("  Cargando dose rate...");
            var doseRates = QueryInBatches(radiationSerList, chunk =>
                ctx.ExternalFieldCommons
                   .Where(ef => chunk.Contains(ef.RadiationSer))
                   .Select(ef => new { ef.RadiationSer, ef.ExternalField.DoseRate })
                   .ToList())
                .ToDictionary(x => x.RadiationSer, x => x.DoseRate);
            _log.Info($"         {doseRates.Count} entradas ({sw.Elapsed.TotalSeconds:F1}s)");

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
                        && !c.CourseId.Contains("física", StringComparison.OrdinalIgnoreCase)
                        && EsCursoActivo(c))
                    .ToList();

                var allPlans = new List<PlanResult>();
                foreach (var course in patCourses)
                {
                    foreach (var plan in plansByCourseSer[course.CourseSer])
                    {
                        var planRads = radiationsByPlanSer[plan.PlanSetupSer].ToList();
                        allPlans.Add(BuildPlanResult(course, plan, planRads, cpCounts, doseRates));
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
    /// Búsqueda de planes por criterios (equivalente al tabBuscar de PlanHelper, corregido).
    /// </summary>
    public PlanSearchOutput SearchPlans(SearchCriteria c)
    {
        using var ctx = CreateContext();

        IQueryable<PlanSetup> q = ctx.PlanSetups
            .Include("Course.Patient")
            .Include("RTPlans")
            .Include("StructureSet.Structures")
            .Include("Radiations.RadiationDevice.Machine")
            .Include("Radiations.ExternalFieldCommon.EnergyMode")
            .Include("Radiations.ExternalFieldCommon.Technique")
            .Where(p => p.Status != "Rejected"
                && p.Course.CourseId != null
                && !p.Course.CourseId.Contains("qa")
                && !p.Course.CourseId.Contains("fisica")
                && !p.Course.CourseId.Contains("física"));

        if (!string.IsNullOrWhiteSpace(c.Apellido))
            q = q.Where(p => p.Course.Patient.LastName.Contains(c.Apellido));
        if (!string.IsNullOrWhiteSpace(c.Hc))
            q = q.Where(p => p.Course.Patient.PatientId.Contains(c.Hc));
        if (!string.IsNullOrWhiteSpace(c.Curso))
            q = q.Where(p => p.Course.CourseId.Contains(c.Curso)); // legacy tenía un "!" acá (bug: excluía en vez de incluir)
        if (!string.IsNullOrWhiteSpace(c.Plan))
            q = q.Where(p => p.PlanSetupId.Contains(c.Plan));
        if (!string.IsNullOrWhiteSpace(c.MachineAriaId))
            q = q.Where(p => p.Radiations.Any(r => r.RadiationDevice.Machine.MachineId == c.MachineAriaId));
        if (c.FechaDesde.HasValue)
            q = q.Where(p => p.CreationDate >= c.FechaDesde.Value);
        if (c.FechaHasta.HasValue)
            q = q.Where(p => p.CreationDate <= c.FechaHasta.Value);
        if (!string.IsNullOrWhiteSpace(c.EstadoAprobacion))
            q = q.Where(p => p.Status == c.EstadoAprobacion);
        if (c.NumeroFracciones.HasValue)
            q = q.Where(p => p.RTPlans.Any(r => r.NoFractions == c.NumeroFracciones.Value));
        // Dosis (cGy, entero) se filtra en memoria más abajo: PrescribedDose es Gy en ARIA, y
        // redondear a cGy en el propio SQL no es expresable de forma confiable en LINQ-to-Entities.

        foreach (var clause in c.Estructuras.Where(cl => !string.IsNullOrWhiteSpace(cl.Text)))
        {
            // ToLower() en ambos lados: la columna StructureId no es case-insensitive por default
            // (a diferencia de apellido/curso/plan, que sí lo son por la collation de la DB) — mismo
            // problema que ya tenía PlanHelper con este filtro.
            // NO filtrar por FirstSlice != null acá: se probó como proxy de "estructura sin contornos"
            // pero dio falsos negativos contra ARIA real (excluía estructuras con contornos reales,
            // ej. "Bladder"/"Bladder!PTV" de un paciente real) — no es un indicador confiable.
            var text = clause.Text.ToLower();
            q = string.Equals(clause.Mode, "exclude", StringComparison.OrdinalIgnoreCase)
                ? q.Where(p => !p.StructureSet.Structures.Any(s => s.StructureId.ToLower().Contains(text)))
                : q.Where(p => p.StructureSet.Structures.Any(s => s.StructureId.ToLower().Contains(text)));
        }

        // Sin límite: se necesita ver el grupo completo, no un top-N. OJO — no usar .Take() acá:
        // con varios .Include() de colecciones (RTPlans, StructureSet.Structures, Radiations) en la
        // misma query, EF6 arma un JOIN cartesiano y aplica el Take sobre las filas del join, no
        // sobre las entidades ya armadas — cortaba la búsqueda muy por debajo del límite real
        // (~20 resultados con cap=2000). Se materializa toda la query.
        var planSetups = q.ToList();

        // Modalidad/tipo de haz/dosis total: sin equivalente SQL, se calculan en memoria
        // (reusa los mismos clasificadores privados que BuildPlanResult).
        var rows = planSetups.Select(BuildSearchRow).ToList();

        if (!string.IsNullOrWhiteSpace(c.IrradiationModality))
            rows = rows.Where(r => string.Equals(r.IrradiationModality, c.IrradiationModality, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(c.BeamType))
            rows = rows.Where(r => string.Equals(r.BeamType, c.BeamType, StringComparison.OrdinalIgnoreCase)).ToList();
        if (c.DosisPorFraccion.HasValue)
            rows = rows.Where(r => r.PrescribedDosePerFraction == c.DosisPorFraccion.Value).ToList();
        if (c.DosisTotal.HasValue)
            rows = rows.Where(r => r.TotalDose == c.DosisTotal.Value).ToList();

        _log.Info($"Búsqueda de planes: {rows.Count} resultado(s)");
        return new PlanSearchOutput { Results = rows };
    }

    private static PlanSearchResultRow BuildSearchRow(PlanSetup plan)
    {
        // Excluye campos de setup (kV/CBCT), mismo criterio que BuildPlanResult: si caen primero
        // contaminan técnica/CPs y la modalidad sale mal clasificada.
        var ordered = plan.Radiations?.OrderBy(r => r.RadiationSer).ToList();
        var firstRadiation = ordered?.FirstOrDefault(r => r.ExternalFieldCommon?.SetupFieldFlag != 1)
            ?? ordered?.FirstOrDefault();
        var rtPlan = plan.RTPlans?.OrderByDescending(r => r.CreationDate).FirstOrDefault();

        var row = new PlanSearchResultRow
        {
            PatientId = plan.Course?.Patient?.PatientId?.Trim() ?? string.Empty,
            LastName = plan.Course?.Patient?.LastName?.Trim(),
            FirstName = plan.Course?.Patient?.FirstName?.Trim(),
            CourseId = plan.Course?.CourseId?.Trim(),
            PlanId = plan.PlanSetupId?.Trim(),
            PlanName = plan.PlanSetupName?.Trim(),
            Status = plan.Status?.Trim(),
            CreationDate = plan.CreationDate == default ? null : plan.CreationDate.ToString("yyyy-MM-dd"),
            MachineAriaId = firstRadiation?.RadiationDevice?.Machine?.MachineId?.Trim(),
            NumberOfFractions = rtPlan?.NoFractions,
        };

        // PrescribedDose de ARIA está en Gy; se muestra en cGy. Redondear a cGy ANTES de
        // multiplicar por fracciones (no después) evita el arrastre de error de punto flotante
        // que daba totales como 4999 en vez de 5000.
        if (rtPlan?.PrescribedDose != null)
        {
            var doseCgy = (int)Math.Round(rtPlan.PrescribedDose.Value * 100, MidpointRounding.AwayFromZero);
            row.PrescribedDosePerFraction = doseCgy;
            if (rtPlan.NoFractions != null)
                row.TotalDose = doseCgy * rtPlan.NoFractions.Value;
        }

        if (firstRadiation != null)
        {
            var em = firstRadiation.ExternalFieldCommon?.EnergyMode;
            var techniqueLabel = firstRadiation.TechniqueLabel?.Trim() ?? string.Empty;
            row.BeamType = DetermineBeamType(em?.RadiationType?.Trim(), em?.Energy, techniqueLabel);
            row.IrradiationModality = Modalidad(firstRadiation);
        }

        return row;
    }

    // Fase 2 (corrida de prueba) — MEVA-Central Equipo 1/3/4, únicos equipos de MEVA-Central
    // que cargan imágenes en ARIA. AriaName confirmado en AppConfiguration.cs.
    private static readonly string[] ImagingFrequencyMachines = ["Equipo1", "Equipo3", "D-2300CD"];
    // VMAT cuenta como IMRT para este monitoreo (mismo requisito semanal) — el clasificador
    // Modalidad() los distingue técnicamente, pero clínicamente van en la misma bolsa acá.
    private static readonly string[] ImagingFrequencyModalities = ["IMRT", "3DC", "VMAT"];

    /// <summary>
    /// Reporte de cumplimiento de imágenes semanales (IMRT/3D, 1 imagen cada 5 días hábiles ±2)
    /// para pacientes en tratamiento en MEVA-Central Equipo 1/3/4. Solo cuenta imágenes de
    /// fracciones ya entregadas (TreatmentRecord/RadiationHstry, Session completados) tomadas en
    /// la MISMA máquina planificada del plan — una imagen tomada un día que el paciente se trató
    /// en un equipo sustituto no cuenta para el cumplimiento.
    /// </summary>
    public List<ImagingFrequencyRow> GetImagingFrequencyReport()
    {
        using var ctx = CreateContext();

        // Candidatos = tuvo al menos un TreatmentRecord (cualquier tipo, no solo imagen) en alguno
        // de los 3 equipos en los últimos ACTIVE_WINDOW_DAYS. Se probó primero filtrando por
        // Course.CompletedDateTime/ClinicalStatus (igual que en QueryAllPatients) — no sirvió: ese
        // campo casi no se usa en esta instalación (3951→3768 planes, incluía pacientes desde 2016).
        // Arrancar por actividad reciente, en vez de por historial completo de Status=TreatApproval,
        // además acota la consulta desde el vamos (TreatmentRecord por fecha+equipo es mucho más
        // chico que "todo paciente jamás tratado en estos 3 equipos"). Ventana ancha (60 días, no
        // solo "sigue en tratamiento") a propósito: también sirve para estadística de pacientes
        // recién dados de alta — el filtro real de "¿sigue en tratamiento?" es IsFinished más
        // abajo (fracciones entregadas vs. prescriptas), no esta ventana.
        const int ActiveWindowDays = 60;
        var cutoff = DateTime.Today.AddDays(-ActiveWindowDays);

        var activeRtPlanSers = ctx.TreatmentRecords
            .Where(t => t.TreatmentRecordDateTime != null && t.TreatmentRecordDateTime >= cutoff
                && t.RTPlanSer != null
                && t.RadiationDevice.Machine != null
                && ImagingFrequencyMachines.Contains(t.RadiationDevice.Machine.MachineId))
            .Select(t => t.RTPlanSer!.Value)
            .Distinct()
            .ToList();

        // Paso 1: candidatos livianos (sin historial de sesiones todavía) — Radiations y RTPlans
        // son colecciones chicas por plan, el cartesiano entre ellas es inofensivo.
        var candidatePlans = ctx.PlanSetups
            .Include("Course.Patient")
            .Include("RTPlans")
            .Include("Radiations.RadiationDevice.Machine")
            .Include("Radiations.ExternalFieldCommon.Technique")
            .Include("Radiations.ExternalFieldCommon.EnergyMode")
            .Where(p => p.Status == "TreatApproval"
                && p.RTPlans.Any(rt => activeRtPlanSers.Contains(rt.RTPlanSer)))
            .ToList();

        var selected = new List<(PlanSetup Plan, RTPlan RtPlan, string PlannedMachineId, string Modality, string? BeamType)>();
        foreach (var plan in candidatePlans)
        {
            var ordered = plan.Radiations?.OrderBy(r => r.RadiationSer).ToList();
            var firstRadiation = ordered?.FirstOrDefault(r => r.ExternalFieldCommon?.SetupFieldFlag != 1)
                ?? ordered?.FirstOrDefault();
            if (firstRadiation == null) continue;

            var modality = Modalidad(firstRadiation);
            if (!ImagingFrequencyModalities.Contains(modality)) continue;

            var plannedMachineId = firstRadiation.RadiationDevice?.Machine?.MachineId?.Trim();
            // activeRtPlanSers filtra por máquina REAL de entrega (pudo ser un día sustituto) —
            // acá exigimos que la máquina PLANIFICADA también sea una de las 3 en alcance, si no
            // se cuela un paciente planificado en otro equipo que un día se trató acá de yapa.
            if (string.IsNullOrEmpty(plannedMachineId) || !ImagingFrequencyMachines.Contains(plannedMachineId))
                continue;
            var rtPlan = plan.RTPlans?.OrderByDescending(r => r.CreationDate).FirstOrDefault();
            if (rtPlan == null) continue;

            // Electrones suele no llevar imagen de verificación (campo chico, sin IGRT) — se tagea
            // para que la pestaña lo pueda marcar como "no monitoreado", igual que ExacTrac.
            var em = firstRadiation.ExternalFieldCommon?.EnergyMode;
            var beamType = DetermineBeamType(em?.RadiationType?.Trim(), em?.Energy, firstRadiation.TechniqueLabel?.Trim() ?? "");

            selected.Add((plan, rtPlan, plannedMachineId, modality, beamType));
        }

        // Paso 2-5: historial de tratamiento/sesiones en queries separadas y batcheadas (NO todas
        // en un solo Include multi-colección — eso fue lo que provocó un timeout SQL: el join
        // cartesiano entre historial de RadiationHstry y de SessionProcedure/ActivitySession para
        // TODOS los TreatApproval de 3 equipos era inmanejable). Join en memoria con ToLookup.
        var rtPlanSerList = selected.Select(s => s.RtPlan.RTPlanSer).Distinct().ToList();

        var treatmentRecords = QueryInBatches(rtPlanSerList, chunk =>
            ctx.TreatmentRecords
               .Include("RadiationHstries")
               .Include("RadiationDevice.Machine")
               .Where(t => t.RTPlanSer != null && chunk.Contains(t.RTPlanSer.Value))
               .ToList());
        var trByRtPlanSer = treatmentRecords.ToLookup(t => t.RTPlanSer!.Value);

        // kV/CBCT: se probaron dos caminos que resultaron NO confiables contra ARIA real —
        // SessionProcedure.Status="COMPLETE" se marca en TODAS las sesiones programadas (15/15)
        // apuntando siempre al mismo SeriesSer de PLANIFICACIÓN (Modality=PLAN, 0 imágenes), no a
        // una imagen real tomada ese día. El dato real está en la tabla `Image`:
        //  - kV: ImageType="ImagePI" con Image.CreationDate real (confirmado exacto contra el
        //    gráfico de tratamiento de ARIA).
        //  - CBCT: ARIA no tiene un ImageType propio — un CBCT se reconstruye como otra Series tipo
        //    "CT" (ImageType="ImageCT", ~80 slices). Image.CreationDate ahí es SOLO el timestamp de
        //    importación masiva (cientos de imágenes comparten el mismo instante) — la fecha real de
        //    cada CBCT es Series.CreationDate (confirmado: una Series de ~81 imágenes por cada día
        //    de tratamiento real, sesión 2026-10-06). La única Series "CT" que no cae en un día de
        //    tratamiento es la CT de planificación original, y el cruce contra TreatmentRecord de
        //    abajo ya la descarta sola.
        // `Image`/`Series` no tienen máquina propia confiable: se cruza la fecha contra
        // `TreatmentRecord` (ya cargado arriba para MV) — de paso resuelve "no cuenta en equipo
        // sustituto", sin necesitar el join frágil Session→ActivitySession→Attendee.
        var patientSerList = selected.Select(s => s.Plan.Course!.Patient!.PatientSer).Distinct().ToList();
        var portalImages = QueryInBatches(patientSerList, chunk =>
            ctx.Images
               .Include("Series")
               .Where(i => i.PatientSer != null && chunk.Contains(i.PatientSer.Value)
                   && (i.ImageType == "ImagePI" || i.Series.SeriesModality == "CT")
                   // PredictedPortal: imagen sintética predicha por el plan (dosis portal calculada),
                   // no una placa real tomada al paciente — falso positivo confirmado (Garay, sesión
                   // 2026-10-06: su gráfico de ARIA no tiene ninguna imagen real, pero esta aparecía).
                   && i.Series.AcquisitionType != "PredictedPortal")
               .ToList());
        var imagesByPatientSer = portalImages.ToLookup(i => i.PatientSer!.Value);

        // El cruce fecha/máquina/fracción de las imágenes usa el historial del RTPlan "elegido"
        // ÚNICAMENTE (no todo el curso). Se probó ampliarlo a nivel curso completo (para cubrir
        // replanes) y a nivel paciente (para el mismo motivo) — ambos resultaron demasiado amplios:
        // un curso puede tener varios RTPlans con el mismo NoFractions por reoptimizaciones donde
        // el RTPlan viejo quedó con TreatmentRecords de prueba/abandonados (caso real: Do Santos,
        // sesión 2026-10-06 — seguía tomando una entrega del día 9/9 bajo un RTPlan anterior ya
        // reemplazado). El RTPlan elegido (el de creación más reciente) es la fuente de verdad.
        var bdCalc = BusinessDayCalculator.FromFile(Environment.GetEnvironmentVariable("MEVA_FERIADOS_PATH"));
        var today = DateOnly.FromDateTime(DateTime.Today);
        var rows = new List<ImagingFrequencyRow>();

        const int StaleWindowBusinessDays = 20;

        foreach (var (plan, rtPlan, plannedMachineId, modality, beamType) in selected)
        {
            var patientSer = plan.Course!.Patient!.PatientSer;
            var planTreatmentRecords = trByRtPlanSer[rtPlan.RTPlanSer].ToList();

            var (_, lastTreatmentDate, deliveredFractions) = GetTreatmentProgress(planTreatmentRecords);

            // Sin ninguna aplicación hace más de 20 días hábiles: se asume que no vuelve, o si
            // vuelve reinicia — se saca de la lista entera (ni activo ni "finalizado" útil para
            // estadística, es un paciente abandonado/pausado).
            if (lastTreatmentDate == null) continue;
            var daysSinceLastTreatment = bdCalc.CountBusinessDays(DateOnly.FromDateTime(lastTreatmentDate.Value), today);
            if (daysSinceLastTreatment > StaleWindowBusinessDays) continue;

            var (lastValidDate, lastValidType) = FindLastValidImage(
                planTreatmentRecords, plannedMachineId, imagesByPatientSer[patientSer]);

            // Todo paciente (salvo electrones/ExacTrac) debe arrancar con placa. Si ya entregó
            // fracciones y nunca tuvo una imagen válida, no es un caso a monitorear acá — es
            // electrones sin IGRT o se hace por otro medio (ExacTrac): se saca de la lista en vez
            // de mostrarlo siempre en rojo sin sentido (casos reales: Farias, Garay).
            if (lastValidDate == null && deliveredFractions > 0) continue;

            // "Terminó el tratamiento" = ya entregó todas las fracciones prescriptas (cualquier
            // equipo, no solo el planificado — un día sustituto también entrega la fracción).
            // Para estos, el semáforo no aplica (ya no hay nada que monitorear) y los "días desde
            // la última imagen" se cuentan desde su ÚLTIMA APLICACIÓN real, no desde hoy — si no,
            // un paciente dado de alta hace un mes siempre marcaría rojo sin que eso signifique nada.
            var isFinished = rtPlan.NoFractions.HasValue && deliveredFractions >= rtPlan.NoFractions.Value;
            var referenceDate = isFinished ? DateOnly.FromDateTime(lastTreatmentDate.Value) : today;

            int? businessDays = lastValidDate.HasValue
                ? bdCalc.CountBusinessDays(DateOnly.FromDateTime(lastValidDate.Value), referenceDate)
                : null;
            var semaforo = isFinished ? null
                : businessDays is null or > 7 ? "Rojo" : businessDays <= 5 ? "Verde" : "Amarillo";

            // Distingue "sigue en tratamiento sin placa" (muchas aplicaciones desde la última
            // imagen) de "suspendió y no volvió" (0 aplicaciones desde esa imagen, aunque los días
            // hábiles transcurridos sean muchos) — caso real: Rodriguez Alberto.
            var applicationsSinceLastImage = CountApplicationsSinceLastImage(
                planTreatmentRecords, plannedMachineId, lastValidDate);

            rows.Add(new ImagingFrequencyRow
            {
                PatientId = plan.Course?.Patient?.PatientId?.Trim() ?? string.Empty,
                LastName = plan.Course?.Patient?.LastName?.Trim(),
                FirstName = plan.Course?.Patient?.FirstName?.Trim(),
                MachineAriaId = plannedMachineId,
                IrradiationModality = modality,
                BeamType = beamType,
                IsFinished = isFinished,
                LastValidImageDate = lastValidDate?.ToString("yyyy-MM-dd"),
                LastValidImageType = lastValidType,
                BusinessDaysSinceLastImage = businessDays,
                ApplicationsSinceLastImage = applicationsSinceLastImage,
                Semaforo = semaforo,
            });
        }

        _log.Info($"Reporte de frecuencia de imágenes: {rows.Count} plan(es)");
        return rows;
    }

    // Busca la imagen válida más reciente (MV vía TreatmentRecord/RadiationHstry; kV/CBCT vía
    // `Image`/`Series`) cuya máquina real de entrega coincide con la máquina planificada del plan
    // — descarta imágenes tomadas en un equipo sustituto. Sin excepciones: se probó aceptar
    // portal dosimetry (cursos "_QA") sin cruce de máquina, pero esas imágenes NO son verificación
    // de posición del paciente — son QA del plan/máquina y no deben contar para este monitoreo
    // (confirmado: el mismo tipo de imagen "_QA" era la causa del falso positivo de Garay). El
    // cruce día/máquina/fracción usa SOLO el historial del RTPlan elegido (ver comentario arriba
    // sobre por qué no ampliarlo a curso completo ni a paciente).
    private static (DateTime? Date, string? Type) FindLastValidImage(
        List<TreatmentRecord> treatmentRecords, string plannedMachineId, IEnumerable<Image> patientPortalImages)
    {
        DateTime? lastDate = null;
        string? lastType = null;

        foreach (var tr in treatmentRecords)
        {
            if (tr.RadiationDevice?.Machine?.MachineId?.Trim() != plannedMachineId) continue;
            foreach (var rh in tr.RadiationHstries ?? [])
            {
                if (rh.TreatmentDeliveryType is not ("OPEN_PORTFILM" or "TRMT_PORTFILM")) continue;
                if (lastDate == null || rh.TreatmentStartTime > lastDate)
                {
                    lastDate = rh.TreatmentStartTime;
                    lastType = "MV";
                }
            }
        }

        foreach (var img in patientPortalImages)
        {
            // CBCT (Series.SeriesModality=="CT"): Image.CreationDate es timestamp de importación
            // masiva (cientos de imágenes comparten el mismo instante) — la fecha real está en
            // Series.CreationDate. kV (ImageType=="ImagePI"): Image.CreationDate ya es la real.
            var isCbct = img.Series?.SeriesModality == "CT";
            var effectiveDate = isCbct ? (img.Series?.CreationDate ?? img.CreationDate) : img.CreationDate;
            var day = effectiveDate.Date;

            // Además de día+equipo, se exige cercanía horaria (±4h) entre la imagen y el
            // TreatmentRecord — una sesión QA de la mañana puede caer el mismo día calendario
            // que el tratamiento real de la tarde (caso real: Do Santos, imagen 9:07-9:10 AM vs.
            // TreatmentRecord real 3:58 PM el mismo 9/9 — 7hs de diferencia, sesiones distintas).
            var dayMatches = treatmentRecords.Where(tr =>
                tr.TreatmentRecordDateTime?.Date == day
                && tr.RadiationDevice?.Machine?.MachineId?.Trim() == plannedMachineId
                && Math.Abs((tr.TreatmentRecordDateTime!.Value - effectiveDate).TotalHours) <= 4);

            // Para kV (FractionNumber poblado): no alcanza con el día+equipo — un portal dosimetry
            // de QA (1 sola fracción, FractionNumber siempre "1") puede coincidir casualmente con un
            // día real de tratamiento. Se exige que ESE día se haya entregado realmente esa misma
            // fracción (caso real: Do Santos, FractionNumber=1 repetido en 3 días distintos — una
            // fracción real "1" solo puede pasar una vez; confirmó que eran las 4 sesiones QA del
            // curso, no el curso real de 30 fracciones).
            var valid = img.FractionNumber == null
                ? dayMatches.Any()
                : dayMatches.Any(tr => tr.RadiationHstries != null && tr.RadiationHstries.Any(rh =>
                    rh.TreatmentDeliveryType == "TREATMENT" && rh.FractionNumber == img.FractionNumber.Value));
            if (!valid) continue;

            if (lastDate == null || effectiveDate > lastDate)
            {
                lastDate = effectiveDate;
                lastType = isCbct ? "CBCT" : "kV";
            }
        }

        return (lastDate, lastType);
    }

    // Primera y última fecha de entrega real (cualquier equipo — una fracción entregada en un
    // equipo sustituto sigue siendo una fracción entregada) y cuántas fracciones distintas se
    // entregaron, para decidir si el paciente ya terminó el tratamiento (fracciones entregadas
    // >= prescriptas) y para aceptar imágenes previas a la primera fracción.
    private static (DateTime? FirstTreatmentDate, DateTime? LastTreatmentDate, int DeliveredFractions) GetTreatmentProgress(
        List<TreatmentRecord> treatmentRecords)
    {
        DateTime? firstDate = null, lastDate = null;
        var fractions = new HashSet<int>();
        foreach (var tr in treatmentRecords)
        foreach (var rh in tr.RadiationHstries ?? [])
        {
            if (rh.TreatmentDeliveryType != "TREATMENT") continue;
            fractions.Add(rh.FractionNumber);
            if (firstDate == null || rh.TreatmentStartTime < firstDate) firstDate = rh.TreatmentStartTime;
            if (lastDate == null || rh.TreatmentStartTime > lastDate) lastDate = rh.TreatmentStartTime;
        }
        return (firstDate, lastDate, fractions.Count);
    }

    // Días de tratamiento real (en la máquina planificada) entregados DESPUÉS de la última imagen
    // válida — o todos, si nunca hubo imagen válida.
    private static int CountApplicationsSinceLastImage(
        List<TreatmentRecord> courseTreatmentRecords, string plannedMachineId, DateTime? lastValidDate)
    {
        var days = new HashSet<DateTime>();
        foreach (var tr in courseTreatmentRecords)
        {
            if (tr.RadiationDevice?.Machine?.MachineId?.Trim() != plannedMachineId) continue;
            if (tr.RadiationHstries == null || !tr.RadiationHstries.Any(rh => rh.TreatmentDeliveryType == "TREATMENT"))
                continue;
            var day = tr.TreatmentRecordDateTime?.Date;
            if (day == null) continue;
            if (lastValidDate.HasValue && day <= lastValidDate.Value.Date) continue;
            days.Add(day.Value);
        }
        return days.Count;
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
                    && !c.CourseId.Contains("física", StringComparison.OrdinalIgnoreCase)
                    && EsCursoActivo(c))
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
    // doseRates: DoseRate por RadiationSer (solo en bulk); null = usar navigation property.
    private static PlanResult BuildPlanResult(Course course, PlanSetup plan, ICollection<Radiation>? radiations,
        Dictionary<long, int>? cpCounts = null, Dictionary<long, int?>? doseRates = null)
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

        // Excluye campos de setup (kV/CBCT, SetupFieldFlag=1): tienen menor RadiationSer que los haces
        // de tratamiento reales y, si caen primero, contaminan técnica/CPs → modalidad mal clasificada
        // (ej. VMAT real leído como 3DC porque el "primer haz" era en realidad un campo de imagen).
        var ordered = radiations?.OrderBy(r => r.RadiationSer).ToList();
        var firstRadiation = ordered?.FirstOrDefault(r => r.ExternalFieldCommon?.SetupFieldFlag != 1)
            ?? ordered?.FirstOrDefault();
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
            pr.IrradiationModality = Modalidad(firstRadiation, cpCounts, doseRates);
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

    private static string Modalidad(Radiation firstRadiation, Dictionary<long, int>? cpCounts = null, Dictionary<long, int?>? doseRates = null)
    {
        if (firstRadiation.ExternalFieldCommon?.Technique == null)
            return "Indefinido";

        var techId = firstRadiation.ExternalFieldCommon.Technique.TechniqueId;

        if (techId == "ARC")
        {
            // Técnica ARC no es sinónimo de VMAT: arco conformado (MLC estático dentro del arco, típico
            // de SRS/SBRT) también usa ARC. MLCPlanType NO discrimina (da "DynMLCPlan" en ambos casos,
            // verificado contra ARIA real). El discriminador confiable es DoseRate: 1000 (haz SRS de alta
            // tasa) = arco conformado; el resto (600, dosis normal) = VMAT real (heurística del físico,
            // sesión 2026-08-03, pacientes 1-119097-0 vs 1-119477-0).
            var doseRate = doseRates != null
                ? (doseRates.TryGetValue(firstRadiation.RadiationSer, out var dr) ? dr : null)
                : firstRadiation.ExternalFieldCommon.ExternalField?.DoseRate;
            return doseRate == 1000 ? "ArcoConformado" : "VMAT";
        }

        if (techId != null && techId.StartsWith("STATIC", StringComparison.OrdinalIgnoreCase))
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

    // Curso "COMPLETED" (ClinicalStatus o CompletedDateTime seteado) no aporta plan activo/vigente —
    // solo cursos en curso. CompletedDateTime es la señal confiable (no depende del string exacto
    // de ClinicalStatus, que no se pudo verificar contra la base real).
    private static bool EsCursoActivo(Course curso) =>
        curso.CompletedDateTime == null
        && !string.Equals(curso.ClinicalStatus?.Trim(), "Completed", StringComparison.OrdinalIgnoreCase);

    // Un plan "vigente" fue creado hace menos de 1 mes. Si ninguno de los candidatos es reciente
    // (caso límite: único plan real es viejo), se usa el pool completo para no devolver null.
    private const int RecentPlanDays = 30;

    private static bool EsPlanReciente(PlanResult p) =>
        !DateTime.TryParse(p.CreationDate, out var creado) || (DateTime.Today - creado).Days < RecentPlanDays;

    private static PlanResult? SelectActivePlan(List<PlanResult> plans)
    {
        if (plans.Count == 0) return null;

        var recientes = plans.Where(EsPlanReciente).ToList();
        var pool = recientes.Count > 0 ? recientes : plans;

        return pool.FirstOrDefault(p => p.Status == "TreatApproval")
            ?? pool.FirstOrDefault(p => p.Status == "PlanApproval")
            ?? pool.Where(p => p.Status == "Unapproved")
                    .OrderByDescending(p => p.CreationDate)
                    .FirstOrDefault();
    }
}
