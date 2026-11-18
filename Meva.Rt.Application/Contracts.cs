using Meva.Rt.Core;

namespace Meva.Rt.Application;

public interface IRtSystemConfigurationProvider
{
    RtSystemConfiguration Configuration { get; }
}

public interface IAgendaExtractor
{
    Task<IReadOnlyList<MachineAppointmentSnapshot>> ExtractAsync(CancellationToken cancellationToken);
    Task<AgendaExtractionResult> ExtractForDateAsync(DateOnly date, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<DateOnly, IReadOnlyList<MachineAppointmentSnapshot>>> ExtractForDatesAsync(IEnumerable<DateOnly> dates, CancellationToken cancellationToken);
}

public sealed class AgendaExtractionResult
{
    public IReadOnlyList<MachineAppointmentSnapshot> Slots { get; init; } = [];
    public IReadOnlyList<string> ScrapingErrors { get; init; } = [];
}

/// <summary>Una combinación centro×etapa con problema. Kind: "error" (falló el scrape) o "empty" (0 pacientes, tenía antes).</summary>
public sealed class ScrapeIssue
{
    public string Center { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public string Kind { get; set; } = "error";
    public string Message { get; set; } = string.Empty;
    public int PreviousCount { get; set; }
}

public sealed class FollowUpExtractionResult
{
    public IReadOnlyList<ProcessPatientSnapshot> Patients { get; init; } = [];
    /// <summary>Combinaciones centro×etapa intentadas (0 si se usó el fallback local).</summary>
    public int TotalCombos { get; init; }
    /// <summary>Combinaciones que fallaron (timeout, estructura cambiada, canario, etc.).</summary>
    public IReadOnlyList<ScrapeIssue> Errors { get; init; } = [];
    /// <summary>Combinaciones que terminaron bien pero sin pacientes.</summary>
    public IReadOnlyList<ScrapeIssue> EmptyCombos { get; init; } = [];
}

/// <summary>Estado del último scrape de seguimiento; se persiste dentro de dashboard_bootstrap y llega al front.</summary>
public sealed class ScrapeHealth
{
    /// <summary>"ok" | "degraded" (faltan combinaciones, se conservaron las anteriores) | "broken" (se descartó el scrape).</summary>
    public string Status { get; set; } = "ok";
    public DateTime CheckedAtUtc { get; set; }
    public DateTime? LastOkUtc { get; set; }
    public int TotalCombos { get; set; }
    public int FailedCombos { get; set; }
    public int Patients { get; set; }
    public int PreviousPatients { get; set; }
    public bool KeptPreviousSnapshot { get; set; }
    public int StalePatients { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<ScrapeIssue> Issues { get; set; } = new();
}

public interface IFollowUpExtractor
{
    Task<FollowUpExtractionResult> ExtractAsync(CancellationToken cancellationToken);
}

public interface IAriaPlanResolver
{
    Task<IReadOnlyList<AriaPlanSnapshot>> ResolveAsync(IEnumerable<string> patientIds, CancellationToken cancellationToken);
}

public interface ITomographAgendaExtractor
{
    Task<IReadOnlyList<MachineAppointmentSnapshot>> ExtractForDateAsync(DateOnly date, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<DateOnly, IReadOnlyList<MachineAppointmentSnapshot>>> ExtractForDatesAsync(IEnumerable<DateOnly> dates, CancellationToken cancellationToken);
}

public interface ISnapshotStore
{
    Task SaveAsync<T>(string snapshotName, T data, CancellationToken cancellationToken);
    Task<T?> TryLoadAsync<T>(string snapshotName, CancellationToken cancellationToken);
}

public interface IPatientHcResolver
{
    Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        IEnumerable<string> sitraMedGuids,
        CancellationToken cancellationToken);
}

public interface IPatientPhoneResolver
{
    Task<IReadOnlyDictionary<string, List<string>>> ResolveAsync(
        IEnumerable<string> sitraMedGuids,
        CancellationToken cancellationToken);
}

public interface IAttendedPatientsExtractor
{
    Task<IReadOnlySet<string>> ExtractAttendedGuidsAsync(
        string centerName,
        string machineSitraName,
        DateOnly date,
        CancellationToken cancellationToken);
}

public interface IStageTransitionStore
{
    Task AppendAsync(IEnumerable<StageTransitionEvent> events);
    Task PruneAsync(int retentionDays = 90);
    Task<IReadOnlyList<StageTransitionEvent>> LoadAsync();
}

public interface IWeeklyStatsStore
{
    Task AccumulateAsync(IEnumerable<StageTransitionEvent> newEvents);
    Task<IReadOnlyList<WeeklyStageStats>> LoadAsync();
}

public interface IPatientProcessEventStore
{
    Task AppendAsync(IEnumerable<PatientProcessEvent> events, CancellationToken ct);
    Task<IReadOnlyList<PatientProcessEvent>> LoadAsync(CancellationToken ct);
    Task<IReadOnlyList<PatientProcessEvent>> LoadRecentAsync(int days, CancellationToken ct);
}

public sealed class DashboardBootstrapData
{
    public DateTime GeneratedAtUtc { get; set; }
    public List<RtCenter> Centers { get; set; } = new();
    public List<ProcessStageDefinition> Stages { get; set; } = new();
    public List<StageSummaryItem> StageSummary { get; set; } = new();
    public List<MachineAppointmentSnapshot> AgendaItems { get; set; } = new();
    public List<ProcessPatientSnapshot> FollowUpPatients { get; set; } = new();
    public List<AriaPlanSnapshot> AriaPlans { get; set; } = new();
    public ScrapeHealth? FollowUpHealth { get; set; }
}

public sealed class BootstrapService
{
    private readonly IAgendaExtractor _agendaExtractor;
    private readonly IFollowUpExtractor _followUpExtractor;
    private readonly IAriaPlanResolver _ariaPlanResolver;
    private readonly ISnapshotStore _snapshotStore;
    private readonly IRtSystemConfigurationProvider _configurationProvider;
    private readonly IPatientHcResolver _hcResolver;
    private readonly IPatientPhoneResolver _phoneResolver;
    private readonly IStageTransitionStore _transitionStore;
    private readonly IWeeklyStatsStore _weeklyStatsStore;
    private readonly BusinessDayCalculator _bdCalc;
    private readonly IPatientProcessEventStore _eventStore;
    private readonly ScrapeAlertService _alertService;

    public BootstrapService(
        IAgendaExtractor agendaExtractor,
        IFollowUpExtractor followUpExtractor,
        IAriaPlanResolver ariaPlanResolver,
        ISnapshotStore snapshotStore,
        IRtSystemConfigurationProvider configurationProvider,
        IPatientHcResolver hcResolver,
        IPatientPhoneResolver phoneResolver,
        IStageTransitionStore transitionStore,
        IWeeklyStatsStore weeklyStatsStore,
        BusinessDayCalculator bdCalc,
        IPatientProcessEventStore eventStore,
        ScrapeAlertService alertService)
    {
        _agendaExtractor = agendaExtractor;
        _followUpExtractor = followUpExtractor;
        _ariaPlanResolver = ariaPlanResolver;
        _snapshotStore = snapshotStore;
        _configurationProvider = configurationProvider;
        _hcResolver = hcResolver;
        _phoneResolver = phoneResolver;
        _transitionStore = transitionStore;
        _weeklyStatsStore = weeklyStatsStore;
        _bdCalc = bdCalc;
        _eventStore = eventStore;
        _alertService = alertService;
    }

    public async Task<DashboardBootstrapData> BuildAsync(CancellationToken cancellationToken, bool skipAria = false)
    {
        // Agenda y seguimiento usan browsers independientes → pueden correr en paralelo.
        var agendaTask = _agendaExtractor.ExtractAsync(cancellationToken);
        var followUpTask = _followUpExtractor.ExtractAsync(cancellationToken);
        await Task.WhenAll(agendaTask, followUpTask);
        var agenda = agendaTask.Result;
        var followUpExtraction = followUpTask.Result;
        var followUp = followUpExtraction.Patients;

        var longWaitThreshold = _configurationProvider.Configuration.LongWaitThresholdDays;
        var today = DateOnly.FromDateTime(DateTime.Today);
        foreach (var patient in followUp)
        {
            patient.IsLongWait = patient.DaysInStage > longWaitThreshold;
            patient.IsDelayed = patient.DaysInStage > patient.ExpectedDaysInStage && !patient.IsLongWait;

            if (patient.StageCode == "F4"
                && patient.PostponedUntil.HasValue
                && patient.PostponedUntil.Value >= today)
            {
                patient.IsLongWait = true;
                patient.IsDelayed = false;
            }

            if (patient.StageCode == "F6A" && patient.TreatmentTechnique == "TBI")
            {
                patient.IsLongWait = true;
                patient.IsDelayed = false;
            }
        }

        // Build GUID→HC map: load previous scrape's cache, drop any GUID→GUID entries (unresolved),
        // then enrich and prune.
        var rawGuidHcMap = await _snapshotStore.TryLoadAsync<Dictionary<string, string>>("guid_hc_map", cancellationToken)
                           ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var guidHcMap = rawGuidHcMap
            .Where(kv => !string.Equals(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        // Solo guardar en el mapa si el PatientId es un HC real (no el GUID en sí mismo).
        // Cuando el scraper de seguimiento no puede leer la HC muestra el GUID como ID —
        // evitar guardarlo para que esos pacientes también pasen por FetchHcForGuidsAsync.
        foreach (var p in followUp)
        {
            if (!string.IsNullOrWhiteSpace(p.SitraMedGuid) && !string.IsNullOrWhiteSpace(p.PatientId)
                && !string.Equals(p.PatientId, p.SitraMedGuid, StringComparison.OrdinalIgnoreCase))
                guidHcMap[p.SitraMedGuid] = p.PatientId;
        }

        // GUIDs de agenda cuya HC no está resuelta todavía (ni desde seguimiento ni desde el mapa).
        // Los pacientes de seguimiento con GUID como PatientId (etapas F1/F2 sin HC asignado)
        // se excluyen: no tienen HC todavía y no se debe intentar resolverlos.
        var uncachedAgendaGuids = agenda
            .Where(a => !string.IsNullOrWhiteSpace(a.SitraMedGuid) && !guidHcMap.ContainsKey(a.SitraMedGuid!))
            .Select(a => a.SitraMedGuid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (uncachedAgendaGuids.Count > 0)
        {
            var resolved = await _hcResolver.ResolveAsync(uncachedAgendaGuids, cancellationToken);
            foreach (var (guid, hc) in resolved)
                guidHcMap[guid] = hc;
        }

        // Prune to only active patients — evicts finished patients automatically
        var activeGuids = followUp.Select(p => p.SitraMedGuid)
            .Concat(agenda.Select(a => a.SitraMedGuid))
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        var prunedMap = guidHcMap
            .Where(kv => activeGuids.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        await _snapshotStore.SaveAsync("guid_hc_map", prunedMap, cancellationToken);

        // Teléfonos: solo se buscan para pacientes en conducta expectante (evita una navegación
        // Playwright por cada paciente activo). No se cachean listas vacías — a diferencia de la HC,
        // un teléfono puede cargarse después en SitraMed, así que un miss se reintenta cada refresh.
        var expectantGuids = followUp
            .Where(p => p.ExpectantStartDate.HasValue && !string.IsNullOrWhiteSpace(p.SitraMedGuid))
            .Select(p => p.SitraMedGuid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var phoneMap = await _snapshotStore.TryLoadAsync<Dictionary<string, List<string>>>("guid_phone_map", cancellationToken)
                       ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var uncachedExpectantGuids = expectantGuids.Where(g => !phoneMap.ContainsKey(g)).ToList();
        if (uncachedExpectantGuids.Count > 0)
        {
            var resolvedPhones = await _phoneResolver.ResolveAsync(uncachedExpectantGuids, cancellationToken);
            foreach (var (guid, phones) in resolvedPhones)
                phoneMap[guid] = phones;
        }

        var prunedPhoneMap = phoneMap
            .Where(kv => expectantGuids.Contains(kv.Key, StringComparer.OrdinalIgnoreCase) && kv.Value.Count > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        await _snapshotStore.SaveAsync("guid_phone_map", prunedPhoneMap, cancellationToken);

        foreach (var p in followUp)
            if (!string.IsNullOrWhiteSpace(p.SitraMedGuid) && prunedPhoneMap.TryGetValue(p.SitraMedGuid, out var patientPhones))
                p.PatientPhones = patientPhones;

        // HC → agenda items (one HC may have multiple slots)
        var agendaByHc = new Dictionary<string, List<MachineAppointmentSnapshot>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in agenda)
        {
            if (string.IsNullOrWhiteSpace(item.SitraMedGuid)) continue;
            if (!prunedMap.TryGetValue(item.SitraMedGuid, out var hc)) continue;
            if (!agendaByHc.TryGetValue(hc, out var lst)) agendaByHc[hc] = lst = [];
            lst.Add(item);
        }

        List<AriaPlanSnapshot> aria = [];
        var ariaByPatient = new Dictionary<string, AriaPlanSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (!skipAria)
        {
            var ariaEnabledCenters = _configurationProvider.Configuration.Centers
                .Where(c => c.AriaEnabled)
                .Select(c => c.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var followUpIds = followUp
                .Where(x => ariaEnabledCenters.Contains(x.CenterName))
                .Select(x => x.PatientId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var baseIds = followUpIds
                .Concat(agendaByHc.Keys.Where(hc => !followUpIds.Contains(hc)))
                .ToList();
            // SitraMed sufija el HC por seguimiento/curso concurrente (ej. "1-114893-1"); ARIA
            // guarda al paciente bajo un único PatientId (típicamente sufijo "-0"). Consultamos
            // también la variante "-0" para no perder el match cuando difieren.
            var allPatientIds = baseIds
                .Concat(baseIds.Select(NormalizeAriaBaseId).Where(id => !baseIds.Contains(id)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            aria = (await _ariaPlanResolver.ResolveAsync(allPatientIds, cancellationToken)).ToList();
            ariaByPatient = aria.ToDictionary(x => x.PatientId, StringComparer.OrdinalIgnoreCase);
            foreach (var patient in followUp)
            {
                if (!TryFindAriaPlan(ariaByPatient, patient.PatientId, out var plan)) continue;
                if (!string.IsNullOrWhiteSpace(plan.PlannedMachineDisplayName))
                    patient.PlannedMachineDisplayName = plan.PlannedMachineDisplayName;
                if (!string.IsNullOrWhiteSpace(plan.BeamType))
                    patient.BeamType = plan.BeamType;
                patient.NumberOfFractions = plan.NumberOfFractions;
                if (!string.IsNullOrWhiteSpace(plan.IrradiationModality))
                    patient.IrradiationModality = plan.IrradiationModality;
                if (!string.IsNullOrWhiteSpace(plan.ExactBeamEnergy))
                    patient.ExactBeamEnergy = plan.ExactBeamEnergy;
                if (plan.Plans.Count > 0)
                    patient.Plans = plan.Plans;
            }

            foreach (var (hc, items) in agendaByHc)
            {
                if (!TryFindAriaPlan(ariaByPatient, hc, out var plan)) continue;
                foreach (var item in items)
                {
                    if (!string.IsNullOrWhiteSpace(plan.BeamType)) item.BeamType = plan.BeamType;
                    if (!string.IsNullOrWhiteSpace(plan.IrradiationModality)) item.IrradiationModality = plan.IrradiationModality;
                }
            }
        }

        // Calcular TreatmentLabel para todos los pacientes (con la info ARIA disponible hasta ahora)
        foreach (var patient in followUp)
        {
            patient.TreatmentLabel = TreatmentClassifier.BuildLabel(
                patient.TreatmentTechnique,
                patient.IrradiationModality,
                patient.ExactBeamEnergy,
                patient.BeamType);
        }

        // Propagar TreatmentLabel a los turnos de agenda
        var followUpByHc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in followUp)
            if (!string.IsNullOrWhiteSpace(p.PatientId) && !string.IsNullOrWhiteSpace(p.TreatmentLabel))
                followUpByHc[p.PatientId!] = p.TreatmentLabel!;
        foreach (var (hc, items) in agendaByHc)
        {
            if (followUpByHc.TryGetValue(hc, out var label))
            {
                foreach (var item in items)
                    item.TreatmentLabel = label;  // paciente en seguimiento
            }
            else if (ariaByPatient.TryGetValue(hc, out var plan))
            {
                // Paciente agenda-pura: calcular label desde datos ARIA + texto de agenda
                foreach (var item in items)
                {
                    var tech = TreatmentClassifier.Classify(item.Treatment);
                    item.TreatmentLabel = TreatmentClassifier.BuildLabel(tech, plan.IrradiationModality, plan.ExactBeamEnergy, plan.BeamType);
                }
            }
        }

        var stageByCode = _configurationProvider.Configuration.Stages
            .ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);

        var summary = ComputeStageSummary(followUp, stageByCode);

        var data = new DashboardBootstrapData
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Centers = _configurationProvider.Configuration.Centers.ToList(),
            Stages = _configurationProvider.Configuration.Stages.OrderBy(x => x.SortOrder).ToList(),
            StageSummary = summary,
            AgendaItems = agenda.ToList(),
            FollowUpPatients = followUp.ToList(),
            AriaPlans = aria
        };

        var previousBootstrap = await _snapshotStore.TryLoadAsync<DashboardBootstrapData>("dashboard_bootstrap", cancellationToken);

        // Guard de salud del scrape de seguimiento: el scraper puede fallar sin tirar excepción
        // (timeouts por combinación, cambios en SitraMed, red saturada). Ver ApplyFollowUpHealth.
        var followUpHealth = ApplyFollowUpHealth(data, followUpExtraction, previousBootstrap, stageByCode);
        await _alertService.NotifyAsync(followUpHealth, cancellationToken);  // 1 mail/día, nunca tira excepción

        if (previousBootstrap is not null)
        {
            // Preservar TomographyDate y ResponsibleDoctor del snapshot previo si el refresh no los encontró.
            var previousTomoDate = previousBootstrap.FollowUpPatients
                .Where(p => p.TomographyDate.HasValue && !string.IsNullOrEmpty(p.PatientId))
                .GroupBy(p => p.PatientId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().TomographyDate!.Value, StringComparer.OrdinalIgnoreCase);
            var previousDoctor = previousBootstrap.FollowUpPatients
                .Where(p => !string.IsNullOrEmpty(p.ResponsibleDoctor) && !string.IsNullOrEmpty(p.PatientId))
                .GroupBy(p => p.PatientId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().ResponsibleDoctor!, StringComparer.OrdinalIgnoreCase);
            foreach (var patient in data.FollowUpPatients)
            {
                if (patient.TomographyDate == null
                    && !string.IsNullOrEmpty(patient.PatientId)
                    && previousTomoDate.TryGetValue(patient.PatientId, out var prev))
                    patient.TomographyDate = prev;
                if (patient.ResponsibleDoctor == null
                    && !string.IsNullOrEmpty(patient.PatientId)
                    && previousDoctor.TryGetValue(patient.PatientId, out var prevDoc))
                    patient.ResponsibleDoctor = prevDoc;
            }

            var previousByPatient = new Dictionary<string, ProcessPatientSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in previousBootstrap.FollowUpPatients)
                if (!string.IsNullOrEmpty(p.PatientId))
                    previousByPatient[p.PatientId] = p;

            var transitions = new List<StageTransitionEvent>();
            foreach (var current in data.FollowUpPatients)
            {
                if (string.IsNullOrEmpty(current.PatientId)) continue;
                if (!previousByPatient.TryGetValue(current.PatientId, out var previous)) continue;
                if (string.Equals(previous.StageCode, current.StageCode, StringComparison.OrdinalIgnoreCase)) continue;
                if (previous.IsLongWait) continue;

                var startDate = previous.StageStartDate ?? today;
                transitions.Add(new StageTransitionEvent
                {
                    PatientId = previous.PatientId,
                    CenterName = previous.CenterName,
                    StageCode = previous.StageCode,
                    TreatmentTechnique = previous.TreatmentTechnique ?? string.Empty,
                    PlannedMachineDisplayName = previous.PlannedMachineDisplayName,
                    StageStartDate = startDate,
                    StageEndDate = today,
                    DaysInStage = _bdCalc.CountBusinessDays(startDate, today),
                    ExpectedDays = previous.ExpectedDaysInStage,
                    WasDelayed = previous.IsDelayed
                });
            }

            if (transitions.Count > 0)
            {
                await _transitionStore.AppendAsync(transitions);
                await _weeklyStatsStore.AccumulateAsync(transitions);
            }
            await _transitionStore.PruneAsync();

            // ── Detección de eventos de proceso ───────────────────────────────
            var stages = _configurationProvider.Configuration.Stages;
            var stageByCodeEvents = stages.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
            var currentById = data.FollowUpPatients
                .Where(p => !string.IsNullOrEmpty(p.PatientId))
                .GroupBy(p => p.PatientId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var newEvents = new List<PatientProcessEvent>();
            var detectedAt = DateTime.UtcNow;

            // Cambio de técnica
            foreach (var current in data.FollowUpPatients)
            {
                if (string.IsNullOrEmpty(current.PatientId)) continue;
                if (!previousByPatient.TryGetValue(current.PatientId, out var prev)) continue;
                if (string.IsNullOrEmpty(prev.TreatmentTechnique)) continue;
                if (string.Equals(prev.TreatmentTechnique, current.TreatmentTechnique, StringComparison.OrdinalIgnoreCase)) continue;

                newEvents.Add(new PatientProcessEvent
                {
                    PatientId     = current.PatientId,
                    PatientName   = current.PatientName,
                    CenterName    = current.CenterName,
                    EventType     = PatientProcessEventType.TechniqueChanged,
                    DetectedAtUtc = detectedAt,
                    PreviousValue = prev.TreatmentTechnique,
                    NewValue      = current.TreatmentTechnique,
                    Notes         = $"Etapa al momento del cambio: {current.StageCode}"
                });
            }

            // Retroceso de etapa
            foreach (var current in data.FollowUpPatients)
            {
                if (string.IsNullOrEmpty(current.PatientId)) continue;
                if (!previousByPatient.TryGetValue(current.PatientId, out var prev)) continue;
                if (string.IsNullOrEmpty(prev.StageCode) || string.IsNullOrEmpty(current.StageCode)) continue;
                if (!stageByCodeEvents.TryGetValue(prev.StageCode, out var prevStageDef)) continue;
                if (!stageByCodeEvents.TryGetValue(current.StageCode, out var currStageDef)) continue;
                if (currStageDef.SortOrder >= prevStageDef.SortOrder) continue;

                newEvents.Add(new PatientProcessEvent
                {
                    PatientId     = current.PatientId,
                    PatientName   = current.PatientName,
                    CenterName    = current.CenterName,
                    EventType     = PatientProcessEventType.StageRegressed,
                    DetectedAtUtc = detectedAt,
                    PreviousValue = $"{prev.StageCode} ({prev.StageDisplayName})",
                    NewValue      = $"{current.StageCode} ({current.StageDisplayName})",
                    Notes         = $"Días en etapa anterior: {prev.DaysInStage}"
                });
            }


            if (newEvents.Count > 0)
                await _eventStore.AppendAsync(newEvents, cancellationToken);
        }

        await _snapshotStore.SaveAsync("dashboard_bootstrap", data, cancellationToken);
        return data;
    }

    // SitraMed sufija el HC por seguimiento/curso concurrente (ej. "1-114893-1"); ARIA guarda
    // al paciente bajo un único PatientId (típicamente sufijo "-0"). Normaliza a esa forma.
    public static string NormalizeAriaBaseId(string hc)
    {
        var lastDash = hc.LastIndexOf('-');
        if (lastDash < 0) return hc;
        var suffix = hc[(lastDash + 1)..];
        return int.TryParse(suffix, out _) ? hc[..(lastDash + 1)] + "0" : hc;
    }

    // AriaAdapter (camino vivo) siempre crea una entrada "vacía" por cada id consultado, exista o
    // no en ARIA — un match exacto vacío no debe tapar datos reales bajo el HC normalizado "-0".
    private static bool HasAriaData(AriaPlanSnapshot p) =>
        p.Plans.Count > 0
        || !string.IsNullOrWhiteSpace(p.PlanStatus)
        || !string.IsNullOrWhiteSpace(p.PlannedMachineDisplayName)
        || !string.IsNullOrWhiteSpace(p.IrradiationModality);

    public static bool TryFindAriaPlan(
        Dictionary<string, AriaPlanSnapshot> ariaByPatient, string patientId, out AriaPlanSnapshot plan)
    {
        var exactFound = ariaByPatient.TryGetValue(patientId, out plan!);
        if (exactFound && HasAriaData(plan)) return true;

        if (ariaByPatient.TryGetValue(NormalizeAriaBaseId(patientId), out var normalizedPlan) && HasAriaData(normalizedPlan))
        {
            plan = normalizedPlan;
            return true;
        }

        return exactFound;
    }

    // Umbrales del guard de salud (documentados en SITRAMED_SCRAPING.md, "Salud del scrape").
    internal const double BrokenFailedRatio = 0.5;   // >= 50% de combinaciones con error => scrape roto
    internal const double BrokenDropRatio = 0.4;     // total cae >= 40% vs snapshot previo => scrape roto
    internal const int MinPreviousForDrop = 20;      // con menos pacientes previos la caída % no es significativa

    /// <summary>
    /// Evalúa el scrape de seguimiento y protege el dashboard:
    /// - "broken": se descarta el scrape y se conserva todo el snapshot previo (marcado IsStale).
    /// - "degraded": se conservan solo los pacientes previos de las combinaciones que fallaron (IsStale).
    /// - "ok": sin cambios.
    /// Las combinaciones vacías (0 pacientes, tenían antes) solo se informan: pueden ser legítimas
    /// (los pacientes avanzaron de etapa) y arrastrarlos duplicaría pacientes.
    /// El resultado queda en data.FollowUpHealth.
    /// </summary>
    public static ScrapeHealth ApplyFollowUpHealth(
        DashboardBootstrapData data,
        FollowUpExtractionResult extraction,
        DashboardBootstrapData? previous,
        Dictionary<string, ProcessStageDefinition> stageByCode)
    {
        static string Key(string center, string stage) => $"{center.Trim()}|{stage.Trim()}";

        var now = DateTime.UtcNow;
        var previousPatients = previous?.FollowUpPatients ?? [];
        var previousCount = previousPatients.Count;
        var currentCount = data.FollowUpPatients.Count;
        var previousByCombo = previousPatients.ToLookup(p => Key(p.CenterName, p.StageCode), StringComparer.OrdinalIgnoreCase);

        var issues = new List<ScrapeIssue>();
        foreach (var e in extraction.Errors)
        {
            e.PreviousCount = previousByCombo[Key(e.Center, e.Stage)].Count();
            issues.Add(e);
        }
        foreach (var e in extraction.EmptyCombos)
        {
            var n = previousByCombo[Key(e.Center, e.Stage)].Count();
            if (n == 0) continue;
            issues.Add(new ScrapeIssue
            {
                Center = e.Center, Stage = e.Stage, Kind = "empty", PreviousCount = n,
                Message = $"0 pacientes (antes {n})"
            });
        }

        var failedRatio = extraction.TotalCombos > 0 ? (double)extraction.Errors.Count / extraction.TotalCombos : 0;
        var dropRatio = previousCount >= MinPreviousForDrop ? 1.0 - (double)currentCount / previousCount : 0;
        var broken = (currentCount == 0 && previousCount > 0)
                     || failedRatio >= BrokenFailedRatio
                     || dropRatio >= BrokenDropRatio;

        var keptPrevious = false;
        if (broken && previous is not null)
        {
            foreach (var p in previousPatients) p.IsStale = true;
            data.FollowUpPatients = previousPatients;
            data.StageSummary = previous.StageSummary;
            keptPrevious = true;
        }
        else if (!broken && extraction.Errors.Count > 0)
        {
            var currentIds = data.FollowUpPatients
                .Where(p => !string.IsNullOrEmpty(p.PatientId))
                .Select(p => p.PatientId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var carried = new List<ProcessPatientSnapshot>();
            foreach (var e in extraction.Errors)
                foreach (var p in previousByCombo[Key(e.Center, e.Stage)])
                {
                    if (!string.IsNullOrEmpty(p.PatientId) && currentIds.Contains(p.PatientId)) continue;
                    p.IsStale = true;
                    carried.Add(p);
                }
            if (carried.Count > 0)
            {
                data.FollowUpPatients.AddRange(carried);
                data.StageSummary = ComputeStageSummary(data.FollowUpPatients, stageByCode);
            }
        }

        var status = broken ? "broken" : extraction.Errors.Count > 0 ? "degraded" : "ok";
        var health = new ScrapeHealth
        {
            Status = status,
            CheckedAtUtc = now,
            LastOkUtc = status == "ok" ? now : previous?.FollowUpHealth?.LastOkUtc ?? previous?.GeneratedAtUtc,
            TotalCombos = extraction.TotalCombos,
            FailedCombos = extraction.Errors.Count,
            Patients = currentCount,
            PreviousPatients = previousCount,
            KeptPreviousSnapshot = keptPrevious,
            StalePatients = data.FollowUpPatients.Count(p => p.IsStale),
            Issues = issues
        };
        health.Summary = status switch
        {
            "broken" => $"Scrape de seguimiento descartado (errores {health.FailedCombos}/{health.TotalCombos}, pacientes {currentCount} vs {previousCount} antes)"
                        + (keptPrevious ? "; se conservan los datos anteriores." : "."),
            "degraded" => $"Seguimiento incompleto: fallaron {health.FailedCombos} de {health.TotalCombos} búsquedas; se conservan {health.StalePatients} pacientes del snapshot anterior.",
            _ => string.Empty
        };

        if (status != "ok")
        {
            Console.Error.WriteLine($"[BuildAsync] {health.Summary}");
            var errorIssues = issues.Where(i => i.Kind == "error").ToList();
            foreach (var i in errorIssues.Take(10))
                Console.Error.WriteLine($"[BuildAsync]   {i.Center}/{i.Stage}: {i.Message}");
            if (errorIssues.Count > 10)
                Console.Error.WriteLine($"[BuildAsync]   ... y {errorIssues.Count - 10} errores más (ver dashboard_bootstrap.json → followUpHealth.issues)");
        }

        data.FollowUpHealth = health;
        return health;
    }

    public static List<StageSummaryItem> ComputeStageSummary(
        IEnumerable<ProcessPatientSnapshot> patients,
        Dictionary<string, ProcessStageDefinition> stageByCode)
    {
        return patients
            .GroupBy(x => new { x.CenterName, x.StageCode })
            .Select(group =>
            {
                var stageDef = stageByCode.GetValueOrDefault(group.Key.StageCode);
                var countable = group.Where(x => !x.IsLongWait).ToList();
                return new StageSummaryItem
                {
                    CenterName        = group.Key.CenterName,
                    StageCode         = group.Key.StageCode,
                    StageGroupName    = stageDef?.GroupName ?? group.First().StageGroupName,
                    PatientCount      = countable.Count,
                    AverageDaysInStage = countable.Count > 0 ? countable.Average(x => x.DaysInStage) : 0,
                    ExpectedDays      = stageDef?.ExpectedDays ?? 0,
                    DelayedCount      = group.Count(x => x.IsDelayed),
                    LongWaitCount     = group.Count(x => x.IsLongWait)
                };
            })
            .OrderBy(x => x.CenterName)
            .ThenBy(x => x.StageGroupName)
            .ToList();
    }
}
