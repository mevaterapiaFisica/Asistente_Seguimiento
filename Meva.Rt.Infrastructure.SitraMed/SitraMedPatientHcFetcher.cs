using Meva.Rt.Application;

namespace Meva.Rt.Infrastructure.SitraMed;

public sealed class SitraMedPatientHcFetcher : IPatientHcResolver
{
    private readonly PlaywrightSitraMedClient _client;

    public SitraMedPatientHcFetcher(PlaywrightSitraMedClient client)
    {
        _client = client;
    }

    public Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        IEnumerable<string> sitraMedGuids,
        CancellationToken cancellationToken)
    {
        return _client.FetchHcForGuidsAsync(sitraMedGuids, cancellationToken);
    }
}
