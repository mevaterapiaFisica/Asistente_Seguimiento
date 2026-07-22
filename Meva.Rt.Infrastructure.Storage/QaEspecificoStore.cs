using System.Text.Json;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.Storage;

public sealed class QaEspecificoStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public QaEspecificoStore(string baseDirectory)
    {
        _filePath = Path.Combine(baseDirectory, "qa_especifico.json");
    }

    public async Task<IReadOnlyList<QaEspecificoItem>> LoadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<QaEspecificoItem>>(stream, _jsonOptions, ct) ?? [];
        }
        catch { return []; }
    }

    public async Task SaveOrUpdateAsync(QaEspecificoItem item, CancellationToken ct)
    {
        var all = (await LoadAllAsync(ct)).ToList();
        var idx = all.FindIndex(p => p.Id == item.Id);
        if (idx >= 0)
            all[idx] = item;
        else
            all.Add(item);
        await SaveAsync(all, ct);
    }

    public async Task DeleteByIdAsync(string id, CancellationToken ct)
    {
        var all = (await LoadAllAsync(ct)).ToList();
        all.RemoveAll(p => p.Id == id);
        await SaveAsync(all, ct);
    }

    private async Task SaveAsync(List<QaEspecificoItem> items, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var tmpPath = _filePath + ".tmp";
        await using (var stream = File.Create(tmpPath))
            await JsonSerializer.SerializeAsync(stream, items, _jsonOptions, ct);
        File.Move(tmpPath, _filePath, overwrite: true);
    }
}
