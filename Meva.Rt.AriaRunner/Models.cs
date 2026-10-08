using Newtonsoft.Json;

namespace Meva.Rt.AriaRunner;

public sealed class RunnerInput
{
    [JsonProperty("patientIds")]
    public List<string> PatientIds { get; set; } = [];

    [JsonProperty("search")]
    public SearchCriteria? Search { get; set; }

    [JsonProperty("imagingFrequency")]
    public bool ImagingFrequency { get; set; }

    /// <summary>Exporta corrimientos (Vrt/Lng/Lat) del matcheo online de placas para PatientIds.</summary>
    [JsonProperty("couchShifts")]
    public bool CouchShifts { get; set; }
}

public sealed record CouchPos(
    [property: JsonProperty("vrt")] double? Vrt,
    [property: JsonProperty("lng")] double? Lng,
    [property: JsonProperty("lat")] double? Lat);

public sealed class CouchShiftRow
{
    [JsonProperty("patientId")] public string PatientId { get; set; } = string.Empty;
    [JsonProperty("fraction")] public int? Fraction { get; set; }
    [JsonProperty("acquisitionDateTime")] public string AcquisitionDateTime { get; set; } = string.Empty;
    [JsonProperty("imagePair")] public string ImagePair { get; set; } = string.Empty; // orden de adquisición, ej. "Lat Der - Post"
    [JsonProperty("vrtCm")] public double VrtCm { get; set; }
    [JsonProperty("lngCm")] public double LngCm { get; set; }
    [JsonProperty("latCm")] public double LatCm { get; set; }
    [JsonProperty("imageCouch")] public CouchPos ImageCouch { get; set; } = new(null, null, null); // camilla al adquirir (valores crudos ARIA)
    [JsonProperty("couchAfter")] public CouchPos? CouchAfter { get; set; } // camilla del siguiente set del día, o del tratamiento
    [JsonProperty("applied")] public bool? Applied { get; set; } // la camilla cambió tras estas placas; null = sin datos
    [JsonProperty("spatialRegistrationSer")] public int SpatialRegistrationSer { get; set; }
    [JsonProperty("versions")] public int Versions { get; set; } // re-matcheos del mismo set (se exporta el último)
}

public sealed class CouchShiftsOutput
{
    [JsonProperty("rows")]
    public List<CouchShiftRow> Rows { get; set; } = [];
}

public sealed class ImagingFrequencyRow
{
    [JsonProperty("patientId")]
    public string PatientId { get; set; } = string.Empty;

    [JsonProperty("lastName")]
    public string? LastName { get; set; }

    [JsonProperty("firstName")]
    public string? FirstName { get; set; }

    [JsonProperty("machineAriaId")]
    public string? MachineAriaId { get; set; } // máquina planificada del plan

    [JsonProperty("irradiationModality")]
    public string? IrradiationModality { get; set; } // IMRT | 3DC

    [JsonProperty("beamType")]
    public string? BeamType { get; set; } // Electrones | SRS | AltaE | 6X — Electrones suele no llevar imagen

    [JsonProperty("isFinished")]
    public bool IsFinished { get; set; } // ya entregó todas las fracciones prescriptas

    [JsonProperty("lastValidImageDate")]
    public string? LastValidImageDate { get; set; } // yyyy-MM-dd, null = nunca

    [JsonProperty("lastValidImageType")]
    public string? LastValidImageType { get; set; } // MV | kV/CBCT

    [JsonProperty("businessDaysSinceLastImage")]
    public int? BusinessDaysSinceLastImage { get; set; } // null = nunca tuvo imagen válida; si
    // IsFinished, cuenta hasta la última aplicación real, no hasta hoy

    [JsonProperty("applicationsSinceLastImage")]
    public int ApplicationsSinceLastImage { get; set; } // días de tratamiento real entregados
    // después de la última imagen válida (o todos, si nunca tuvo imagen) — distingue "paciente
    // sigue en tratamiento sin placa" (muchas aplicaciones) de "suspendió y no volvió" (pocas/0)

    [JsonProperty("semaforo")]
    public string? Semaforo { get; set; } // Verde | Amarillo | Rojo | null si IsFinished (no aplica)
}

public sealed class ImagingFrequencyOutput
{
    [JsonProperty("rows")]
    public List<ImagingFrequencyRow> Rows { get; set; } = [];
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
