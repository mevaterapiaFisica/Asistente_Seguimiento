namespace Meva.Rt.Core;

public sealed class RtCenter
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool AriaEnabled { get; set; } = true;
}

public sealed class RtMachine
{
    public string CenterName { get; set; } = string.Empty;
    public string SitraName { get; set; } = string.Empty;
    public string AriaName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class ProcessStageDefinition
{
    public string Code { get; set; } = string.Empty;
    public string SitraMicroStatus { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public int ExpectedDays { get; set; }
    public int SortOrder { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class ProcessPatientSnapshot
{
    public string PatientId { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string CenterName { get; set; } = string.Empty;
    public string StageCode { get; set; } = string.Empty;
    public string StageDisplayName { get; set; } = string.Empty;
    public string StageGroupName { get; set; } = string.Empty;
    public DateOnly? StageStartDate { get; set; }
    public int DaysInStage { get; set; }
    public int ExpectedDaysInStage { get; set; }
    public bool IsDelayed { get; set; }
    public bool IsLongWait { get; set; }
    public string? PlannedMachineDisplayName { get; set; }
    public string? BeamType { get; set; }
    public string SourceCenterName { get; set; } = string.Empty;
    public string? SitraMedGuid { get; set; }
    public string? AssignedPhysicist { get; set; }
    public string TreatmentType { get; set; } = string.Empty;
    public string? TreatmentTechnique { get; set; }
    public int? NumberOfFractions { get; set; }
    public string? IrradiationModality { get; set; }
    public string? ExactBeamEnergy { get; set; }
    public string? TreatmentLabel { get; set; }
    public int? Priority { get; set; }
}

public sealed class MachineAppointmentSnapshot
{
    public string CenterName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public DateOnly AgendaDate { get; set; }
    public string StartTime { get; set; } = string.Empty;
    public string EndTime { get; set; } = string.Empty;
    public string Treatment { get; set; } = string.Empty;
    public string? SitraMedGuid { get; set; }
    public string? BeamType { get; set; }
    public string? IrradiationModality { get; set; }
    public string? TreatmentLabel { get; set; }
    public int? Priority { get; set; }
}

public sealed class AriaPlanSnapshot
{
    public string PatientId { get; set; } = string.Empty;
    public string? PlannedMachineDisplayName { get; set; }
    public string? PlannedMachineAriaId { get; set; }
    public string? PlanStatus { get; set; }
    public string? BeamType { get; set; }
    public int? NumberOfFractions { get; set; }
    public string? IrradiationModality { get; set; }
    public string? ExactBeamEnergy { get; set; }
}


public sealed class StageSummaryItem
{
    public string CenterName { get; set; } = string.Empty;
    public string StageCode { get; set; } = string.Empty;
    public string StageGroupName { get; set; } = string.Empty;
    public int PatientCount { get; set; }
    public double AverageDaysInStage { get; set; }
    public int ExpectedDays { get; set; }
    public int DelayedCount { get; set; }
    public int LongWaitCount { get; set; }
}

public sealed class MachineCapacitySetting
{
    public string CenterName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public decimal WorkingHours { get; set; }
    public int StandardSlotMinutes { get; set; }
    public decimal ReservedSpecialHours { get; set; }
}

public sealed class RtTomograph
{
    public string CenterName  { get; set; } = string.Empty;
    public string SitraName   { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class RtMachineCapabilities
{
    public string MachineName { get; set; } = string.Empty;
    public bool CanDoElectrons { get; set; }
    public bool CanDoVMAT { get; set; }
    public bool CanDoSBRT { get; set; }
    public bool CanDoRC { get; set; }
    public bool CanDoTBI { get; set; }
    public bool CanDoTSET { get; set; }
    public bool CanDoIGRT { get; set; }
    public List<string> HighEnergyBeams { get; set; } = new();
}

public sealed class RtSystemConfiguration
{
    public List<RtCenter> Centers { get; set; } = new();
    public List<RtMachine> Machines { get; set; } = new();
    public List<RtTomograph> Tomographs { get; set; } = new();
    public List<ProcessStageDefinition> Stages { get; set; } = new();
    public List<MachineCapacitySetting> MachineCapacities { get; set; } = new();
    public List<MachineCapacitySetting> TomographCapacities { get; set; } = new();
    public List<RtMachineCapabilities> MachineCapabilities { get; set; } = new();
    public int LongWaitThresholdDays { get; set; } = 40;
    public int UpcomingScrapeDays { get; set; } = 15;
}

public sealed class SitraMedRuntimeOptions
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool Headless { get; set; } = true;
    public bool UseLocalExamplesFallback { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;
    public bool SaveAgendaHtmlCapture { get; set; }
    public string? AgendaHtmlCaptureDirectory { get; set; }
    public bool EnableDiagnostics { get; set; }
    public string? DiagnosticsDirectory { get; set; }
}

public sealed class AriaRuntimeOptions
{
    /// <summary>Ruta al archivo mapEquiposAriaSitra.txt (Aria machine id → nombre Sitra).</summary>
    public string MapFilePath { get; set; } = string.Empty;

    /// <summary>
    /// JSON opcional con planes mock para desarrollo o overrides.
    /// Formato: array de { "patientId", "plannedMachineDisplayName", "plannedMachineAriaId", "planStatus" }.
    /// </summary>
    public string? MockPlansJsonPath { get; set; }
}

public sealed class WeeklyStageStats
{
    public DateOnly WeekStart { get; set; }
    public string CenterName { get; set; } = string.Empty;
    public string StageCode { get; set; } = string.Empty;
    public string TreatmentTechnique { get; set; } = string.Empty;
    public int Count { get; set; }
    public double SumDays { get; set; }
    public double SumDaysSquared { get; set; }
}

public sealed class StageTransitionEvent
{
    public string PatientId { get; set; } = string.Empty;
    public string CenterName { get; set; } = string.Empty;
    public string StageCode { get; set; } = string.Empty;
    public string TreatmentTechnique { get; set; } = string.Empty;
    public string? PlannedMachineDisplayName { get; set; }
    public DateOnly StageStartDate { get; set; }
    public DateOnly StageEndDate { get; set; }
    public int DaysInStage { get; set; }
    public int ExpectedDays { get; set; }
    public bool WasDelayed { get; set; }
}

public sealed class HomeSnapshotOptions
{
    /// <summary>
    /// every_request: siempre ejecuta scraping al cargar /api/home (comportamiento anterior).
    /// snapshot_first: usa dashboard_bootstrap.json si existe y es válido; si no, scraping.
    /// snapshot_only: solo lectura del snapshot; si falta devuelve error.
    /// </summary>
    public string RefreshMode { get; set; } = "snapshot_first";
}
