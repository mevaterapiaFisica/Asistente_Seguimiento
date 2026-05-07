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

public interface ISnapshotStore
{
    Task SaveAsync<T>(string snapshotName, T data, CancellationToken cancellationToken);
    Task<T?> TryLoadAsync<T>(string snapshotName, CancellationToken cancellationToken);
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

    public BootstrapService(
        IAgendaExtractor agendaExtractor,
        IFollowUpExtractor followUpExtractor,
        IAriaPlanResolver ariaPlanResolver,
        ISnapshotStore snapshotStore,
        IRtSystemConfigurationProvider configurationProvider)
    {
        _agendaExtractor = agendaExtractor;
        _followUpExtractor = followUpExtractor;
        _ariaPlanResolver = ariaPlanResolver;
        _snapshotStore = snapshotStore;
        _configurationProvider = configurationProvider;
    }

    public async Task<DashboardBootstrapData> BuildAsync(CancellationToken cancellationToken, bool skipAria = false)
    {
        var agenda = await _agendaExtractor.ExtractAsync(cancellationToken);
        var followUp = await _followUpExtractor.ExtractAsync(cancellationToken);

        List<AriaPlanSnapshot> aria = [];
        if (!skipAria)
        {
            var patientIds = followUp
                .Select(x => x.PatientId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            aria = (await _ariaPlanResolver.ResolveAsync(patientIds, cancellationToken)).ToList();
            var ariaByPatient = aria.ToDictionary(x => x.PatientId, StringComparer.OrdinalIgnoreCase);
            foreach (var patient in followUp)
            {
                if (ariaByPatient.TryGetValue(patient.PatientId, out var plan)
                    && !string.IsNullOrWhiteSpace(plan.PlannedMachineDisplayName))
                {
                    patient.PlannedMachineDisplayName = plan.PlannedMachineDisplayName;
                }
            }
        }

        var summary = followUp
            .GroupBy(x => new { x.CenterName, x.StageGroupName, x.ExpectedDaysInStage })
            .Select(group => new StageSummaryItem
            {
                CenterName = group.Key.CenterName,
                StageGroupName = group.Key.StageGroupName,
                PatientCount = group.Count(),
                AverageDaysInStage = group.Any() ? group.Average(x => x.DaysInStage) : 0,
                ExpectedDays = group.Key.ExpectedDaysInStage
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
