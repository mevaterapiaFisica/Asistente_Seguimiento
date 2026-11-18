using System.Text.Json;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.Storage;

public sealed class QaEquiposMensualesStore
{
    private static readonly string[] DefaultFisicos =
        ["Flor", "Ro", "Pablo", "Joaquin", "Gonzalo", "Charly", "Luis", "Mariel", "Nico", "Sofi", "Noe", "Carla", "Jime"];

    private readonly string _filePath;
    private readonly string _fisicosPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public QaEquiposMensualesStore(string baseDirectory)
    {
        _filePath = Path.Combine(baseDirectory, "qa_equipos_mensuales.json");
        _fisicosPath = Path.Combine(baseDirectory, "fisicos.json");
    }

    /// <summary>Filas de los meses pedidos; siembra las que falten (una por equipo) y poda meses fuera de [n-2, n+1].</summary>
    public async Task<List<QaEquipoMensualItem>> GetMonthsAsync(
        IReadOnlyCollection<string> meses, IReadOnlyList<(string Centro, string Equipo)> equipos, DateTime today, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            var keep = Enumerable.Range(-2, 4).Select(i => today.AddMonths(i).ToString("yyyy-MM")).ToHashSet();
            var changed = all.RemoveAll(x => !keep.Contains(x.Mes)) > 0;
            foreach (var mes in meses.Where(keep.Contains))
                foreach (var (centro, equipo) in equipos)
                    if (!all.Any(x => x.Mes == mes && x.Centro == centro && x.Equipo == equipo))
                    {
                        all.Add(new QaEquipoMensualItem { Id = $"QEM_{Guid.NewGuid():N}", Mes = mes, Centro = centro, Equipo = equipo });
                        changed = true;
                    }
            if (changed) await SaveAsync(all, ct);
            return all.Where(x => meses.Contains(x.Mes)).ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task<QaEquipoMensualItem?> UpdateAsync(string id, QaEquipoMensualItem req, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            var cur = all.FirstOrDefault(x => x.Id == id);
            if (cur is null) return null;
            // Mes/Centro/Equipo no se editan
            cur.Estado = req.Estado;
            cur.Responsables = req.Responsables ?? [];
            cur.FechaPropuesta = req.FechaPropuesta;
            cur.HayPreventivo = req.HayPreventivo;
            cur.FechaPreventivo = req.HayPreventivo ? req.FechaPreventivo : null;
            cur.Comentarios = req.Comentarios ?? string.Empty;
            await SaveAsync(all, ct);
            return cur;
        }
        finally { _lock.Release(); }
    }

    /// <summary>fisicos.json: se siembra la 1ª vez; se edita a mano (Centros vacío = todos).</summary>
    public async Task<List<FisicoItem>> LoadFisicosAsync(CancellationToken ct)
    {
        var seed = DefaultFisicos.Select(n => new FisicoItem { Nombre = n }).ToList();
        try
        {
            if (File.Exists(_fisicosPath))
            {
                await using var s = File.OpenRead(_fisicosPath);
                return await JsonSerializer.DeserializeAsync<List<FisicoItem>>(s, _jsonOptions, ct) ?? [];
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_fisicosPath)!);
            await File.WriteAllTextAsync(_fisicosPath, JsonSerializer.Serialize(seed, _jsonOptions), ct);
        }
        catch { }
        return seed;
    }

    private async Task<List<QaEquipoMensualItem>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<QaEquipoMensualItem>>(stream, _jsonOptions, ct) ?? [];
        }
        catch { return []; }
    }

    private async Task SaveAsync(List<QaEquipoMensualItem> items, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var tmpPath = _filePath + ".tmp";
        await using (var stream = File.Create(tmpPath))
            await JsonSerializer.SerializeAsync(stream, items, _jsonOptions, ct);
        File.Move(tmpPath, _filePath, overwrite: true);
    }
}
