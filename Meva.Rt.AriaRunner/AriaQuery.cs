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
    /// Busca un paciente por PatientId y extrae toda la información relevante.
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

            // Oncólogo: OncologistFlag y PrimaryFlag son int (1 = sí)
            var oncologist = patient.PatientDoctors?
                .Where(pd => pd.OncologistFlag == 1 || pd.PrimaryFlag == 1)
                .Select(pd => pd.Doctor)
                .FirstOrDefault(d => d != null);

            if (oncologist != null)
            {
                result.Oncologist = $"{oncologist.LastName?.Trim()}, {oncologist.FirstName?.Trim()}"
                    .Trim(',', ' ');
            }

            // Todos los planes (excluyendo cursos de QA/Física)
            var courses = patient.Courses?
                .Where(c => c.CourseId != null
                    && c.CourseId.IndexOf("qa", StringComparison.OrdinalIgnoreCase) < 0
                    && c.CourseId.IndexOf("fisica", StringComparison.OrdinalIgnoreCase) < 0
                    && c.CourseId.IndexOf("física", StringComparison.OrdinalIgnoreCase) < 0)
                .ToList() ?? [];

            var allPlans = new List<PlanResult>();
            foreach (var course in courses)
            {
                var planSetups = course.PlanSetups?
                    .Where(p => p.Status != "Rejected")
                    .ToList() ?? [];

                foreach (var plan in planSetups)
                    allPlans.Add(BuildPlanResult(course, plan));
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

    private static PlanResult BuildPlanResult(Course course, PlanSetup plan)
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

        // Fallback: RTPlan (DICOM export) always has NoFractions when plan is complete
        if (pr.NumberOfFractions == null)
            pr.NumberOfFractions = plan.RTPlans?.OrderByDescending(r => r.CreationDate).FirstOrDefault()?.NoFractions;

        var firstRadiation = plan.Radiations?.FirstOrDefault();
        if (firstRadiation?.RadiationDevice?.Machine != null)
        {
            pr.MachineAriaId = firstRadiation.RadiationDevice.Machine.MachineId?.Trim();
            pr.MachineName = firstRadiation.RadiationDevice.Machine.MachineName?.Trim();
        }

        // EnergyMode de ExternalFieldCommon tiene RadiationType y Energy reales.
        // Radiation.RadiationType == "BeamLinac" para todos los haces de linac — no útil para clasificar.
        // BeamType e IrradiationModality se derivan del primer campo (técnica y tecnología).
        // ExactBeamEnergy usa el máximo sobre todos los campos: si al menos uno es 10X/15X/18X
        // se etiqueta con esa energía aunque otros campos sean 6X.
        if (firstRadiation != null)
        {
            var em = firstRadiation.ExternalFieldCommon?.EnergyMode;
            var techniqueLabel = firstRadiation.TechniqueLabel?.Trim() ?? string.Empty;
            pr.BeamType = DetermineBeamType(em?.RadiationType?.Trim(), em?.Energy, techniqueLabel);
            pr.IrradiationModality = Modalidad(plan);
            pr.ExactBeamEnergy = DetermineExactBeamEnergy(plan.Radiations);
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

        if (techniqueLabel.IndexOf("SRS", StringComparison.OrdinalIgnoreCase) >= 0
            || techniqueLabel.IndexOf("STEREO", StringComparison.OrdinalIgnoreCase) >= 0)
            return "SRS";

        if (string.Equals(emRadiationType, "X", StringComparison.OrdinalIgnoreCase))
            return emEnergy.HasValue && emEnergy >= HighEnergyThresholdKev ? "AltaE" : "6X";

        if (emEnergy.HasValue)
            return emEnergy >= HighEnergyThresholdKev ? "AltaE" : "6X";

        return null;
    }

    private static string Modalidad(PlanSetup plan)
    {
        var firstRad = plan.Radiations?.FirstOrDefault();
        if (firstRad?.ExternalFieldCommon?.Technique == null)
            return "Indefinido";

        var techId = firstRad.ExternalFieldCommon.Technique.TechniqueId;

        if (techId == "ARC")
            return "VMAT";

        if (techId == "STATIC")
            return (firstRad.ExternalFieldCommon.ControlPoints?.Count ?? 0) > 40 ? "IMRT" : "3DC";

        return "Indefinido";
    }

    private static string DetermineExactBeamEnergy(ICollection<Radiation>? radiations)
    {
        if (radiations == null || radiations.Count == 0) return "Indefinido";

        // Electrones: si algún campo es electrones
        if (radiations.Any(r =>
            string.Equals(r.ExternalFieldCommon?.EnergyMode?.RadiationType?.Trim(), "E",
                StringComparison.OrdinalIgnoreCase)))
            return "Electrones";

        // Máxima energía fotónica sobre todos los campos
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
