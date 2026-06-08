using System.Text.Json;
using Meva.Rt.Application;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.Storage;

// IMPORTANTE: estos archivos solo deben contener transiciones detectadas
// en tiempo real por BootstrapService. No importar datos históricos de
// estado puntual — los promedios resultantes no serían comparables con
// las transiciones futuras.
public sealed class StageTransitionStore : IStageTransitionStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public StageTransitionStore(string baseDirectory)
    {
        _filePath = Path.Combine(baseDirectory, "stage_transitions.json");
    }

    public async Task AppendAsync(IEnumerable<StageTransitionEvent> events)
    {
        var existing = await LoadAsync();
        var all = existing.Concat(events).ToList();
        await SaveAsync(all);
    }

    public async Task PruneAsync(int retentionDays = 90)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.Today).AddDays(-retentionDays);
        var existing = await LoadAsync();
        var pruned = existing.Where(e => e.StageEndDate >= cutoff).ToList();
        if (pruned.Count == existing.Count) return;
        await SaveAsync(pruned);
    }

    public async Task<IReadOnlyList<StageTransitionEvent>> LoadAsync()
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<StageTransitionEvent>>(stream, _jsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task SaveAsync(List<StageTransitionEvent> events)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var tmpPath = _filePath + ".tmp";
        await using (var stream = File.Create(tmpPath))
            await JsonSerializer.SerializeAsync(stream, events, _jsonOptions);
        File.Move(tmpPath, _filePath, overwrite: true);
    }
}
