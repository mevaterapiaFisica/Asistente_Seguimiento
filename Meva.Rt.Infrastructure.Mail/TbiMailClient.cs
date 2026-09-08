using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.Mail;

public sealed class TbiMailClient(TbiMailOptions options)
{
    public async Task<IReadOnlyList<TbiMailInfo>> FetchNewAsync(CancellationToken ct)
    {
        if (!options.IsConfigured) return [];

        using var client = new ImapClient();
        await client.ConnectAsync(options.Host, options.Port, MailKit.Security.SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(options.User, options.AppPassword, ct);

        var folder = await client.GetFolderAsync(options.Folder, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        // Ventana de 3 días en vez de NotSeen: evita depender del flag \Seen (que se puede
        // "ensuciar" si alguien lee la casilla a mano) — el dedupe real es por Message-ID en TbiMailStore.
        var since = DateTime.UtcNow.AddDays(-3);
        var uids = await folder.SearchAsync(SearchQuery.DeliveredAfter(since).And(SearchQuery.SubjectContains("TBI")), ct);

        var results = new List<TbiMailInfo>();
        foreach (var uid in uids)
        {
            var message = await folder.GetMessageAsync(uid, ct);
            var info = TbiMailParser.Parse(message.Subject ?? string.Empty, message.TextBody ?? message.HtmlBody ?? string.Empty, message.Date);
            if (info != null)
            {
                info.MessageId = message.MessageId ?? $"{options.Folder}:{uid}";
                results.Add(info);
            }
        }

        await client.DisconnectAsync(true, ct);
        return results;
    }
}
