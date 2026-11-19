using System.Text.Json;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.Storage;

public sealed class QaFueraToleranciaStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public QaFueraToleranciaStore(string baseDirectory)
    {
        _filePath = Path.Combine(baseDirectory, "qa_fuera_tolerancia.json");
    }

    public async Task<List<QaFueraToleranciaItem>> LoadAllAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { return await LoadAsync(ct); }
        finally { _lock.Release(); }
    }

    public async Task<QaFueraToleranciaItem> CreateAsync(QaFueraToleranciaItem item, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            item.Id = $"QFT_{Guid.NewGuid():N}";
            item.CreatedAtUtc = DateTime.UtcNow;
            all.Add(item);
            await SaveAsync(all, ct);
            return item;
        }
        finally { _lock.Release(); }
    }

    public async Task<QaFueraToleranciaItem?> UpdateAsync(string id, QaFueraToleranciaItem req, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            var cur = all.FirstOrDefault(x => x.Id == id);
            if (cur is null) return null;
            cur.Centro = req.Centro ?? string.Empty;
            cur.Equipo = req.Equipo ?? string.Empty;
            cur.Control = req.Control ?? string.Empty;
            cur.FechaDeteccion = req.FechaDeteccion;
            cur.Responsables = req.Responsables ?? [];
            cur.Status = req.Status;
            cur.Comentarios = req.Comentarios ?? string.Empty;
            await SaveAsync(all, ct);
            return cur;
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            if (all.RemoveAll(x => x.Id == id) > 0) await SaveAsync(all, ct);
        }
        finally { _lock.Release(); }
    }

    private async Task<List<QaFueraToleranciaItem>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<QaFueraToleranciaItem>>(stream, _jsonOptions, ct) ?? [];
        }
        catch { return []; }
    }

    private async Task SaveAsync(List<QaFueraToleranciaItem> items, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var tmpPath = _filePath + ".tmp";
        await using (var stream = File.Create(tmpPath))
            await JsonSerializer.SerializeAsync(stream, items, _jsonOptions, ct);
        File.Move(tmpPath, _filePath, overwrite: true);
    }
}
