using System.Text.Json;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.Storage;

public sealed class TbiMailStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public TbiMailStore(string baseDirectory)
    {
        _filePath = Path.Combine(baseDirectory, "tbi_mail_info.json");
    }

    public async Task<IReadOnlyList<TbiMailInfo>> LoadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<TbiMailInfo>>(stream, _jsonOptions, ct) ?? [];
        }
        catch { return []; }
    }

    // Devuelve false si es el mismo mail ya procesado (mismo Message-ID) — así el caller sabe
    // si debe (re)tocar también el turno reservado asociado, o dejarlo como el admin lo dejó.
    public async Task<bool> UpsertFromMailAsync(TbiMailInfo info, CancellationToken ct)
    {
        var all = (await LoadAllAsync(ct)).ToList();
        var idx = all.FindIndex(i => i.PatientId == info.PatientId);
        if (idx >= 0)
        {
            if (!string.IsNullOrEmpty(info.MessageId) && all[idx].MessageId == info.MessageId) return false;
            info.Confirmed = false;
            all[idx] = info;
        }
        else
        {
            info.Confirmed = false;
            all.Add(info);
        }
        await SaveAsync(all, ct);
        return true;
    }

    public async Task<TbiMailInfo> UpdateAsync(string patientId, string patientName, DateOnly? tomographyDate, int? totalApplications, CancellationToken ct)
    {
        var all = (await LoadAllAsync(ct)).ToList();
        var item = all.FirstOrDefault(i => i.PatientId == patientId);
        if (item is null)
        {
            item = new TbiMailInfo { PatientId = patientId, PatientName = patientName, ReceivedAtUtc = DateTime.UtcNow };
            all.Add(item);
        }
        item.TomographyDate = tomographyDate;
        item.TotalApplications = totalApplications;
        item.Confirmed = true;
        await SaveAsync(all, ct);
        return item;
    }

    private async Task SaveAsync(List<TbiMailInfo> items, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var tmpPath = _filePath + ".tmp";
        await using (var stream = File.Create(tmpPath))
            await JsonSerializer.SerializeAsync(stream, items, _jsonOptions, ct);
        File.Move(tmpPath, _filePath, overwrite: true);
    }
}
