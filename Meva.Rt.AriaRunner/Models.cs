using Newtonsoft.Json;

namespace Meva.Rt.AriaRunner;

public sealed class RunnerInput
{
    [JsonProperty("patientIds")]
    public List<string> PatientIds { get; set; } = [];

    [JsonProperty("search")]
    public SearchCriteria? Search { get; set; }
}

public sealed class StructureClause
{
    [JsonProperty("mode")]
    public string Mode { get; set; } = "include"; // "include" | "exclude"

    [JsonProperty("text")]
    public string Text { get; set; } = string.Empty;
}

public sealed class SearchCriteria
{
    [JsonProperty("apellido")]
    public string? Apellido { get; set; }

    [JsonProperty("hc")]
    public string? Hc { get; set; }

    [JsonProperty("curso")]
    public string? Curso { get; set; }

    [JsonProperty("plan")]
    public string? Plan { get; set; }

    [JsonProperty("machineAriaId")]
    public string? MachineAriaId { get; set; }

    [JsonProperty("fechaDesde")]
    public DateTime? FechaDesde { get; set; }

    [JsonProperty("fechaHasta")]
    public DateTime? FechaHasta { get; set; }

    [JsonProperty("estadoAprobacion")]
    public string? EstadoAprobacion { get; set; } // Unapproved | PlanApproval | TreatApproval

    [JsonProperty("numeroFracciones")]
    public int? NumeroFracciones { get; set; }

    [JsonProperty("dosisPorFraccion")]
    public int? DosisPorFraccion { get; set; } // cGy (RTPlan.PrescribedDose, redondeado)

    [JsonProperty("dosisTotal")]
    public int? DosisTotal { get; set; } // cGy, calculado: PrescribedDose(cGy) * NoFractions

    [JsonProperty("irradiationModality")]
    public string? IrradiationModality { get; set; } // VMAT | ArcoConformado | IMRT | 3DC | Indefinido

    [JsonProperty("beamType")]
    public string? BeamType { get; set; } // Electrones | SRS | AltaE | 6X

    [JsonProperty("estructuras")]
    public List<StructureClause> Estructuras { get; set; } = [];
}

public sealed class PlanSearchResultRow
{
    [JsonProperty("patientId")]
    public string PatientId { get; set; } = string.Empty;

    [JsonProperty("lastName")]
    public string? LastName { get; set; }

    [JsonProperty("firstName")]
    public string? FirstName { get; set; }

    [JsonProperty("courseId")]
    public string? CourseId { get; set; }

    [JsonProperty("planId")]
    public string? PlanId { get; set; }

    [JsonProperty("planName")]
    public string? PlanName { get; set; }

    [JsonProperty("status")]
    public string? Status { get; set; }

    [JsonProperty("creationDate")]
    public string? CreationDate { get; set; }

    [JsonProperty("machineAriaId")]
    public string? MachineAriaId { get; set; }

    [JsonProperty("numberOfFractions")]
    public int? NumberOfFractions { get; set; }

    [JsonProperty("prescribedDosePerFraction")]
    public int? PrescribedDosePerFraction { get; set; } // cGy

    [JsonProperty("totalDose")]
    public int? TotalDose { get; set; } // cGy

    [JsonProperty("irradiationModality")]
    public string? IrradiationModality { get; set; }

    [JsonProperty("beamType")]
    public string? BeamType { get; set; }
}

public sealed class PlanSearchOutput
{
    [JsonProperty("results")]
    public List<PlanSearchResultRow> Results { get; set; } = [];
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

    [JsonProperty("beamType")]
    public string? BeamType { get; set; }

    [JsonProperty("irradiationModality")]
    public string? IrradiationModality { get; set; }

    [JsonProperty("exactBeamEnergy")]
    public string? ExactBeamEnergy { get; set; }
}
