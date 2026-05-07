using System.IO;
using System.Linq;
using System.Collections;
using AriaQ;

namespace Meva.Rt.Infrastructure.Aria;

public static class MetodosParaWebScrap
{
    public static Patient? BuscarPaciente(string id, object aria)
    {
        var patientsValue = aria.GetType().GetProperty("Patients")?.GetValue(aria);
        if (patientsValue is not IEnumerable patients)
        {
            return null;
        }

        foreach (var item in patients)
        {
            if (item is Patient patient && string.Equals(patient.PatientId, id, StringComparison.OrdinalIgnoreCase))
            {
                return patient;
            }
        }

        return null;
    }

    public static PlanSetup? PlanActivo(Patient paciente)
    {
        var planSetups = paciente.Courses
            .Where(curso => !curso.CourseId.ToLower().Contains("qa") && !curso.CourseId.ToLower().Contains("fisica"))
            .SelectMany(curso => curso.PlanSetups.Where(p => p.Status != "Rejected"))
            .ToList();

        if (planSetups.Any(p => p.Status == "TreatApproval"))
        {
            return planSetups.FirstOrDefault(p => p.Status == "TreatApproval");
        }

        if (planSetups.Any(p => p.Status == "PlanApproval"))
        {
            return planSetups.FirstOrDefault(p => p.Status == "PlanApproval");
        }

        if (planSetups.Any(p => p.Status == "Unapproved"))
        {
            return planSetups.Where(p => p.Status == "Unapproved").OrderBy(p => p.CreationDate).Last();
        }

        return null;
    }

    public static string? Equipo(Patient patient, string mapFilePath)
    {
        var plan = PlanActivo(patient);
        if (plan == null || !plan.Radiations.Any())
        {
            return null;
        }

        var idAria = plan.Radiations.First().RadiationDevice.Machine.MachineId;
        return EquipoAriaASitra(idAria, mapFilePath);
    }

    public static string? EquipoAriaASitra(string idAria, string mapFilePath)
    {
        foreach (var line in File.ReadAllLines(mapFilePath))
        {
            var parts = line.Split(',');
            if (parts.Length < 2)
            {
                continue;
            }

            if (string.Equals(parts[0], idAria, StringComparison.OrdinalIgnoreCase))
            {
                return parts[1];
            }
        }

        return null;
    }
}
