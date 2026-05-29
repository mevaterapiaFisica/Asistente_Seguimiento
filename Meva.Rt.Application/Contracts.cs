using Meva.Rt.Core;

namespace Meva.Rt.Application;

public interface IRtSystemConfigurationProvider
{
    RtSystemConfiguration Configuration { get; }
}

public interface IAgendaExtractor
{
    Task<IReadOnlyList<MachineAppointmentSnapshot>> ExtractAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MachineAppointmentSnapshot>> ExtractForDateAsync(DateOnly date, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<DateOnly, IReadOnlyList<MachineAppointmentSnapshot>>> ExtractForDatesAsync(IEnumerable<DateOnly> dates, CancellationToken cancellationToken);
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

public sealed class DashboardBootstrapData
{
    public DateTime GeneratedAtUtc { get; set; }
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

    public BootstrapService(
        IAgendaExtractor agendaExtractor,
        IFollowUpExtractor followUpExtractor,
        IAriaPlanResolver ariaPlanResolver,
        ISnapshotStore snapshotStore,
        IRtSystemConfigurationProvider configurationProvider,
        IPatientHcResolver hcResolver)
    {
        _agendaExtractor = agendaExtractor;
        _followUpExtractor = followUpExtractor;
        _ariaPlanResolver = ariaPlanResolver;
        _snapshotStore = snapshotStore;
        _configurationProvider = configurationProvider;
        _hcResolver = hcResolver;
    }

    public async Task<DashboardBootstrapData> BuildAsync(CancellationToken cancellationToken, bool skipAria = false)
    {
        var agenda = await _agendaExtractor.ExtractAsync(cancellationToken);
        var followUp = await _followUpExtractor.ExtractAsync(cancellationToken);

        var longWaitThreshold = _configurationProvider.Configuration.LongWaitThresholdDays;
        foreach (var patient in followUp)
            patient.IsLongWait = patient.DaysInStage > longWaitThreshold;

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
                .Concat(agendaByHc.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            aria = (await _ariaPlanResolver.ResolveAsync(allPatientIds, cancellationToken)).ToList();
            var ariaByPatient = aria.ToDictionary(x => x.PatientId, StringComparer.OrdinalIgnoreCase);
            foreach (var patient in followUp)
            {
                if (!ariaByPatient.TryGetValue(patient.PatientId, out var plan)) continue;
                if (!string.IsNullOrWhiteSpace(plan.PlannedMachineDisplayName))
                    patient.PlannedMachineDisplayName = plan.PlannedMachineDisplayName;
                if (!string.IsNullOrWhiteSpace(plan.BeamType))
                    patient.BeamType = plan.BeamType;
            }

            foreach (var (hc, items) in agendaByHc)
            {
                if (!ariaByPatient.TryGetValue(hc, out var plan) || string.IsNullOrWhiteSpace(plan.BeamType)) continue;
                foreach (var item in items)
                    item.BeamType = plan.BeamType;
            }
        }

        var summary = followUp
            .GroupBy(x => new { x.CenterName, x.StageGroupName, x.ExpectedDaysInStage })
            .Select(group =>
            {
                var countable = group.Where(x => !x.IsLongWait).ToList();
                return new StageSummaryItem
                {
                    CenterName = group.Key.CenterName,
                    StageGroupName = group.Key.StageGroupName,
                    PatientCount = group.Count(),
                    AverageDaysInStage = countable.Count > 0 ? countable.Average(x => x.DaysInStage) : 0,
                    ExpectedDays = group.Key.ExpectedDaysInStage
                };
            })
            .OrderBy(x => x.CenterName)
            .ThenBy(x => x.StageGroupName)
            .ToList();

        var data = new DashboardBootstrapData
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Stages = _configurationProvider.Configuration.Stages.OrderBy(x => x.SortOrder).ToList(),
            StageSummary = summary,
            AgendaItems = agenda.ToList(),
            FollowUpPatients = followUp.ToList(),
            AriaPlans = aria
        };

        await _snapshotStore.SaveAsync("dashboard_bootstrap", data, cancellationToken);
        return data;
    }
}
