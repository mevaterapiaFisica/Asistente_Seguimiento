namespace Meva.Rt.Web;

internal sealed class AriaRunnerOutput
{
    public int TotalRequested { get; set; }
    public int TotalFound { get; set; }
    public List<AriaRunnerPatient> Patients { get; set; } = new();
}

internal sealed class AriaRunnerPatient
{
    public string PatientId { get; set; } = string.Empty;
    public bool Found { get; set; }
    public AriaRunnerPlan? ActivePlan { get; set; }
}

internal sealed class AriaRunnerPlan
{
    public string? MachineAriaId { get; set; }
    public string? MachineName { get; set; }
    public string? Status { get; set; }
    public string? BeamType { get; set; }
    public int? NumberOfFractions { get; set; }
    public string? IrradiationModality { get; set; }
    public string? ExactBeamEnergy { get; set; }
}
