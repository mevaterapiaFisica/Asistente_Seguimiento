using Meva.Rt.Application;
using Meva.Rt.Core;

namespace Meva.Rt.Web;

public static class HomeResponseMapper
{
    public static HomeResponse Map(DashboardBootstrapData data, IRtSystemConfigurationProvider configurationProvider)
    {
        foreach (var p in data.FollowUpPatients)
            if (p.AssignedPhysicist != null)
                p.AssignedPhysicist = FormatPhysicistName(p.AssignedPhysicist);

        var configurationModel = configurationProvider.Configuration;
        var equipments = configurationModel.MachineCapacities
            .Select(capacity => new EquipmentSummaryItem
            {
                CenterName = capacity.CenterName,
                DisplayName = capacity.MachineName,
                WorkingHours = capacity.WorkingHours,
                StandardSlotMinutes = capacity.StandardSlotMinutes,
                ReservedSpecialHours = capacity.ReservedSpecialHours,
                AgendaPatients = data.AgendaItems.Count(x =>
                    x.CenterName == capacity.CenterName && x.MachineName == capacity.MachineName),
                PlannedPatients = data.FollowUpPatients.Count(x => x.PlannedMachineDisplayName == capacity.MachineName)
            })
            .ToList();

        return new HomeResponse
        {
            GeneratedAtUtc = data.GeneratedAtUtc,
            Stages = data.Stages,
            StageSummary = data.StageSummary,
            Patients = data.FollowUpPatients,
            Agenda = data.AgendaItems,
            Equipments = equipments,
            Configuration = new ConfigurationViewModel
            {
                Stages = configurationModel.Stages.OrderBy(x => x.SortOrder).ToList(),
                Machines = configurationModel.Machines.OrderBy(x => x.CenterName).ThenBy(x => x.DisplayName).ToList(),
                MachineCapacities = configurationModel.MachineCapacities.OrderBy(x => x.CenterName).ThenBy(x => x.MachineName).ToList(),
                TomographCapacities = configurationModel.TomographCapacities.OrderBy(x => x.CenterName).ThenBy(x => x.MachineName).ToList()
            }
        };
    }

    private static string FormatPhysicistName(string name)
    {
        var idx = name.IndexOf(", ", StringComparison.Ordinal);
        if (idx < 0) return name;
        var lastName = name[..idx].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name[..idx];
        var rawFirst = name[(idx + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var firstName = rawFirst.Length == 0 ? rawFirst
            : char.ToUpper(rawFirst[0]) + rawFirst[1..].ToLower();
        return $"{lastName}, {firstName}";
    }
}

public sealed class HomeResponse
{
    public string SolutionName { get; set; } = "Meva.Rt";
    public DateTime GeneratedAtUtc { get; set; }
    public List<ProcessStageDefinition> Stages { get; set; } = new();
    public List<StageSummaryItem> StageSummary { get; set; } = new();
    public List<ProcessPatientSnapshot> Patients { get; set; } = new();
    public List<MachineAppointmentSnapshot> Agenda { get; set; } = new();
    public List<EquipmentSummaryItem> Equipments { get; set; } = new();
    public ConfigurationViewModel Configuration { get; set; } = new();
}

public sealed class EquipmentSummaryItem
{
    public string CenterName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public decimal WorkingHours { get; set; }
    public int StandardSlotMinutes { get; set; }
    public decimal ReservedSpecialHours { get; set; }
    public int AgendaPatients { get; set; }
    public int PlannedPatients { get; set; }
}

public sealed class ConfigurationViewModel
{
    public List<ProcessStageDefinition> Stages { get; set; } = new();
    public List<RtMachine> Machines { get; set; } = new();
    public List<MachineCapacitySetting> MachineCapacities { get; set; } = new();
    public List<MachineCapacitySetting> TomographCapacities { get; set; } = new();
}

public sealed class AgendaSlotDto
{
    public string CenterName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string AgendaDate { get; set; } = string.Empty;
    public string StartTime { get; set; } = string.Empty;
    public string EndTime { get; set; } = string.Empty;
    public string Treatment { get; set; } = string.Empty;
    public bool IsEstimated { get; set; }
    public string? EstimatedFromStage { get; set; }
    public string? EstimatedPatientId { get; set; }

    public AgendaSlotDto() { }

    public AgendaSlotDto(MachineAppointmentSnapshot src)
    {
        CenterName = src.CenterName;
        MachineName = src.MachineName;
        PatientName = src.PatientName;
        AgendaDate = src.AgendaDate.ToString("yyyy-MM-dd");
        StartTime = src.StartTime;
        EndTime = src.EndTime;
        Treatment = src.Treatment;
    }
}
