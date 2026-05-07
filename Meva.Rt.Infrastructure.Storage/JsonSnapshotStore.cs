using System.Text.Json;
using Meva.Rt.Application;

namespace Meva.Rt.Infrastructure.Storage;

public sealed class JsonSnapshotStore : ISnapshotStore
{
    private readonly string _baseDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public JsonSnapshotStore(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
    }

    public async Task SaveAsync<T>(string snapshotName, T data, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_baseDirectory);
        var path = Path.Combine(_baseDirectory, $"{snapshotName}.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, data, _jsonOptions, cancellationToken);
    }

    public async Task<T?> TryLoadAsync<T>(string snapshotName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_baseDirectory, $"{snapshotName}.json");
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = File.OpenRead(path);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken);
        }
        catch
        {
            return default;
        }
    }
}
