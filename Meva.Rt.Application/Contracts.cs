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

public interface IFollowUpExtractor
{
    Task<IReadOnlyList<ProcessPatientSnapshot>> ExtractAsync(CancellationToken cancellationToken);
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
}

public sealed class BootstrapService
{
    private readonly IAgendaExtractor _agendaExtractor;
    private readonly IFollowUpExtractor _followUpExtractor;
    private readonly IAriaPlanResolver _ariaPlanResolver;
    private readonly ISnapshotStore _snapshotStore;
    private readonly IRtSystemConfigurationProvider _configurationProvider;
    private readonly IPatientHcResolver _hcResolver;
    private readonly IStageTransitionStore _transitionStore;
    private readonly IWeeklyStatsStore _weeklyStatsStore;
    private readonly BusinessDayCalculator _bdCalc;
    private readonly IPatientProcessEventStore _eventStore;

    public BootstrapService(
        IAgendaExtractor agendaExtractor,
        IFollowUpExtractor followUpExtractor,
        IAriaPlanResolver ariaPlanResolver,
        ISnapshotStore snapshotStore,
        IRtSystemConfigurationProvider configurationProvider,
        IPatientHcResolver hcResolver,
        IStageTransitionStore transitionStore,
        IWeeklyStatsStore weeklyStatsStore,
        BusinessDayCalculator bdCalc,
        IPatientProcessEventStore eventStore)
    {
        _agendaExtractor = agendaExtractor;
        _followUpExtractor = followUpExtractor;
        _ariaPlanResolver = ariaPlanResolver;
        _snapshotStore = snapshotStore;
        _configurationProvider = configurationProvider;
        _hcResolver = hcResolver;
        _transitionStore = transitionStore;
        _weeklyStatsStore = weeklyStatsStore;
        _bdCalc = bdCalc;
        _eventStore = eventStore;
    }

    public async Task<DashboardBootstrapData> BuildAsync(CancellationToken cancellationToken, bool skipAria = false)
    {
        // Agenda y seguimiento usan browsers independientes → pueden correr en paralelo.
        var agendaTask = _agendaExtractor.ExtractAsync(cancellationToken);
        var followUpTask = _followUpExtractor.ExtractAsync(cancellationToken);
        await Task.WhenAll(agendaTask, followUpTask);
        var agenda = agendaTask.Result;
        var followUp = followUpTask.Result;

        var longWaitThreshold = _configurationProvider.Configuration.LongWaitThresholdDays;
        foreach (var patient in followUp)
        {
            patient.IsLongWait = patient.DaysInStage > longWaitThreshold;
            patient.IsDelayed = patient.DaysInStage > patient.ExpectedDaysInStage && !patient.IsLongWait;
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

            var allPatientIds = followUpIds
                .Concat(agendaByHc.Keys.Where(hc => !followUpIds.Contains(hc)))
                .ToList();

            aria = (await _ariaPlanResolver.ResolveAsync(allPatientIds, cancellationToken)).ToList();
            ariaByPatient = aria.ToDictionary(x => x.PatientId, StringComparer.OrdinalIgnoreCase);
            foreach (var patient in followUp)
            {
                if (!ariaByPatient.TryGetValue(patient.PatientId, out var plan)) continue;
                if (!string.IsNullOrWhiteSpace(plan.PlannedMachineDisplayName))
                    patient.PlannedMachineDisplayName = plan.PlannedMachineDisplayName;
                if (!string.IsNullOrWhiteSpace(plan.BeamType))
                    patient.BeamType = plan.BeamType;
                patient.NumberOfFractions = plan.NumberOfFractions;
                if (!string.IsNullOrWhiteSpace(plan.IrradiationModality))
                    patient.IrradiationModality = plan.IrradiationModality;
                if (!string.IsNullOrWhiteSpace(plan.ExactBeamEnergy))
                    patient.ExactBeamEnergy = plan.ExactBeamEnergy;
            }

            foreach (var (hc, items) in agendaByHc)
            {
                if (!ariaByPatient.TryGetValue(hc, out var plan)) continue;
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

        var summary = followUp
            .GroupBy(x => new { x.CenterName, x.StageCode })
            .Select(group =>
            {
                var stageDef = stageByCode.GetValueOrDefault(group.Key.StageCode);
                var countable = group.Where(x => !x.IsLongWait).ToList();
                return new StageSummaryItem
                {
                    CenterName = group.Key.CenterName,
                    StageCode = group.Key.StageCode,
                    StageGroupName = stageDef?.GroupName ?? group.First().StageGroupName,
                    PatientCount = group.Count(),
                    AverageDaysInStage = countable.Count > 0 ? countable.Average(x => x.DaysInStage) : 0,
                    ExpectedDays = stageDef?.ExpectedDays ?? 0,
                    DelayedCount = group.Count(x => x.IsDelayed),
                    LongWaitCount = group.Count(x => x.IsLongWait)
                };
            })
            .OrderBy(x => x.CenterName)
            .ThenBy(x => x.StageGroupName)
            .ToList();

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

            var today = DateOnly.FromDateTime(DateTime.Today);
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
}
