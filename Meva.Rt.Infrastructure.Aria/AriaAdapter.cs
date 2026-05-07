using System.Text.Json;
using AriaQ;
using Meva.Rt.Application;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.Aria;

public sealed class AriaPlanResolver : IAriaPlanResolver
{
    private static readonly JsonSerializerOptions MockJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IRtSystemConfigurationProvider _configurationProvider;
    private readonly AriaRuntimeOptions _options;
    private readonly IAriaPatientRootProvider _rootProvider;

    public AriaPlanResolver(
        IRtSystemConfigurationProvider configurationProvider,
        AriaRuntimeOptions options,
        IAriaPatientRootProvider rootProvider)
    {
        _configurationProvider = configurationProvider;
        _options = options;
        _rootProvider = rootProvider;
    }

    public Task<IReadOnlyList<AriaPlanSnapshot>> ResolveAsync(IEnumerable<string> patientIds, CancellationToken cancellationToken)
    {
        var ids = patientIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byId = ids.ToDictionary(
            id => id,
            id => new AriaPlanSnapshot { PatientId = id },
            StringComparer.OrdinalIgnoreCase);

        ApplyMockPlans(byId);

        var mapPath = _options.MapFilePath;
        var root = _rootProvider.TryGetPatientRoot();
        if (root != null && !string.IsNullOrWhiteSpace(mapPath) && File.Exists(mapPath))
        {
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var patient = MetodosParaWebScrap.BuscarPaciente(id, root);
                if (patient == null)
                {
                    continue;
                }

                var plan = MetodosParaWebScrap.PlanActivo(patient);
                var row = byId[id];
                row.PlanStatus ??= plan?.Status;

                var rad = plan?.Radiations.FirstOrDefault();
                if (rad?.RadiationDevice.Machine.MachineId is { } ariaMachineId)
                {
                    row.PlannedMachineAriaId ??= ariaMachineId;
                }

                var sitraEquip = MetodosParaWebScrap.Equipo(patient, mapPath);
                var display = ResolveMachineDisplay(sitraEquip);
                if (!string.IsNullOrWhiteSpace(display))
                {
                    row.PlannedMachineDisplayName ??= display;
                }
            }
        }

        IReadOnlyList<AriaPlanSnapshot> ordered = ids.Select(id => byId[id]).ToList();
        return Task.FromResult(ordered);
    }

    private void ApplyMockPlans(Dictionary<string, AriaPlanSnapshot> byId)
    {
        var path = _options.MockPlansJsonPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        List<AriaPlanSnapshot>? mocks;
        try
        {
            mocks = JsonSerializer.Deserialize<List<AriaPlanSnapshot>>(File.ReadAllText(path), MockJsonOptions);
        }
        catch
        {
            return;
        }

        if (mocks == null)
        {
            return;
        }

        foreach (var mock in mocks)
        {
            if (string.IsNullOrWhiteSpace(mock.PatientId))
            {
                continue;
            }

            var key = mock.PatientId.Trim();
            if (!byId.TryGetValue(key, out var row))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(mock.PlannedMachineDisplayName))
            {
                row.PlannedMachineDisplayName = mock.PlannedMachineDisplayName;
            }

            if (!string.IsNullOrWhiteSpace(mock.PlannedMachineAriaId))
            {
                row.PlannedMachineAriaId = mock.PlannedMachineAriaId;
            }

            if (!string.IsNullOrWhiteSpace(mock.PlanStatus))
            {
                row.PlanStatus = mock.PlanStatus;
            }
        }
    }

    private string? ResolveMachineDisplay(string? sitraEquipName)
    {
        if (string.IsNullOrWhiteSpace(sitraEquipName))
        {
            return null;
        }

        var trimmed = sitraEquipName.Trim();
        var machine = _configurationProvider.Configuration.Machines.FirstOrDefault(m =>
            string.Equals(m.SitraName, trimmed, StringComparison.OrdinalIgnoreCase));

        return machine?.DisplayName;
    }
}
