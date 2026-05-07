using Newtonsoft.Json;

namespace Meva.Rt.AriaRunner;

public sealed class RunnerInput
{
    [JsonProperty("patientIds")]
    public List<string> PatientIds { get; set; } = [];
}

public sealed class RunnerOutput
{
    [JsonProperty("generatedAt")]
    public string GeneratedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonProperty("totalRequested")]
    public int TotalRequested { get; set; }

    [JsonProperty("totalFound")]
    public int TotalFound { get; set; }

    [JsonProperty("totalNotFound")]
    public int TotalNotFound { get; set; }

    [JsonProperty("totalErrors")]
    public int TotalErrors { get; set; }

    [JsonProperty("patients")]
    public List<PatientResult> Patients { get; set; } = [];
}

public sealed class PatientResult
{
    [JsonProperty("patientId")]
    public string PatientId { get; set; } = string.Empty;

    [JsonProperty("found")]
    public bool Found { get; set; }

    [JsonProperty("error")]
    public string? Error { get; set; }

    [JsonProperty("firstName")]
    public string? FirstName { get; set; }

    [JsonProperty("lastName")]
    public string? LastName { get; set; }

    [JsonProperty("dateOfBirth")]
    public string? DateOfBirth { get; set; }

    [JsonProperty("sex")]
    public string? Sex { get; set; }

    [JsonProperty("oncologist")]
    public string? Oncologist { get; set; }

    [JsonProperty("activePlan")]
    public PlanResult? ActivePlan { get; set; }

    [JsonProperty("allPlans")]
    public List<PlanResult> AllPlans { get; set; } = [];
}

public sealed class PlanResult
{
    [JsonProperty("courseId")]
    public string? CourseId { get; set; }

    [JsonProperty("planId")]
    public string? PlanId { get; set; }

    [JsonProperty("planName")]
    public string? PlanName { get; set; }

    [JsonProperty("status")]
    public string? Status { get; set; }

    [JsonProperty("statusDate")]
    public string? StatusDate { get; set; }

    [JsonProperty("creationDate")]
    public string? CreationDate { get; set; }

    [JsonProperty("treatmentTechnique")]
    public string? TreatmentTechnique { get; set; }

    [JsonProperty("numberOfFractions")]
    public int? NumberOfFractions { get; set; }

    [JsonProperty("prescriptionSite")]
    public string? PrescriptionSite { get; set; }

    [JsonProperty("prescriptionTechnique")]
    public string? PrescriptionTechnique { get; set; }

    [JsonProperty("machineAriaId")]
    public string? MachineAriaId { get; set; }

    [JsonProperty("machineName")]
    public string? MachineName { get; set; }
}
