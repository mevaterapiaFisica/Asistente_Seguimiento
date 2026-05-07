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
                .Include("Courses.PlanSetups.Prescription")
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
                    allPlans.Add(BuildPlanResult(course, plan));
            }

            result.AllPlans = allPlans;
            result.ActivePlan = SelectActivePlan(allPlans);

            _log.Info($"  [{patientId}] {result.LastName}, {result.FirstName} | " +
                      $"Plan: {result.ActivePlan?.PlanId ?? "ninguno"} ({result.ActivePlan?.Status ?? "-"}) | " +
                      $"Equipo: {result.ActivePlan?.MachineName ?? "-"}");
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

        var firstRadiation = plan.Radiations?.FirstOrDefault();
        if (firstRadiation?.RadiationDevice?.Machine != null)
        {
            pr.MachineAriaId = firstRadiation.RadiationDevice.Machine.MachineId?.Trim();
            pr.MachineName = firstRadiation.RadiationDevice.Machine.MachineName?.Trim();
        }

        return pr;
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
