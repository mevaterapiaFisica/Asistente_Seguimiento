using System.Text.Json;
using Meva.Rt.Application;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.Storage;

public sealed class TurnReservationStore
{
    private readonly string _filePath;
    private readonly BusinessDayCalculator _bdCalc;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public TurnReservationStore(string baseDirectory, BusinessDayCalculator bdCalc)
    {
        _filePath = Path.Combine(baseDirectory, "turn_reservations.json");
        _bdCalc = bdCalc;
    }

    public async Task<PatientTurnReservation?> GetByPatientIdAsync(string patientId, CancellationToken ct)
    {
        var all = await LoadAllActiveAsync(ct);
        return all.FirstOrDefault(r => r.PatientId == patientId);
    }

    public async Task<IReadOnlyList<PatientTurnReservation>> LoadAllActiveAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<PatientTurnReservation>>(stream, _jsonOptions, ct) ?? [];
        }
        catch { return []; }
    }

    public async Task SaveOrUpdateAsync(PatientTurnReservation reservation, CancellationToken ct)
    {
        var all = (await LoadAllActiveAsync(ct)).ToList();
        var idx = all.FindIndex(r => r.PatientId == reservation.PatientId);
        if (idx >= 0)
            all[idx] = reservation;
        else
            all.Add(reservation);
        await SaveAsync(all, ct);
    }

    public async Task DeleteByIdAsync(string reservationId, CancellationToken ct)
    {
        var all = (await LoadAllActiveAsync(ct)).ToList();
        all.RemoveAll(r => r.ReservationId == reservationId);
        await SaveAsync(all, ct);
    }

    public async Task PruneExpiredAsync(int businessDaysAfter, CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return;
        var all = (await LoadAllActiveAsync(ct)).ToList();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var cutoff = _bdCalc.SubtractBusinessDays(today, businessDaysAfter);
        var pruned = all.Where(r => r.ReservedDate >= cutoff).ToList();
        if (pruned.Count != all.Count)
            await SaveAsync(pruned, ct);
    }

    private async Task SaveAsync(List<PatientTurnReservation> reservations, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var tmpPath = _filePath + ".tmp";
        await using (var stream = File.Create(tmpPath))
            await JsonSerializer.SerializeAsync(stream, reservations, _jsonOptions, ct);
        File.Move(tmpPath, _filePath, overwrite: true);
    }
}
