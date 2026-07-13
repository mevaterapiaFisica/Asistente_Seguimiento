using Meva.Rt.Application;

namespace Meva.Rt.Infrastructure.SitraMed;

public sealed class SitraMedPatientPhoneFetcher : IPatientPhoneResolver
{
    private readonly PlaywrightSitraMedClient _client;

    public SitraMedPatientPhoneFetcher(PlaywrightSitraMedClient client)
    {
        _client = client;
    }

    public Task<IReadOnlyDictionary<string, List<string>>> ResolveAsync(
        IEnumerable<string> sitraMedGuids,
        CancellationToken cancellationToken)
    {
        return _client.FetchPhonesForGuidsAsync(sitraMedGuids, cancellationToken);
    }
}
