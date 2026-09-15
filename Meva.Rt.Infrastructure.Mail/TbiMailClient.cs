using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Meva.Rt.Core;
using MimeKit;

namespace Meva.Rt.Infrastructure.Mail;

public sealed class TbiMailClient(TbiMailOptions options)
{
    public async Task<IReadOnlyList<TbiMailFetchResult>> FetchNewAsync(CancellationToken ct)
    {
        if (!options.IsConfigured) return [];

        using var client = new ImapClient();
        await client.ConnectAsync(options.Host, options.Port, MailKit.Security.SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(options.User, options.AppPassword, ct);

        var folder = await client.GetFolderAsync(options.Folder, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        // Ventana de 4 meses en vez de NotSeen: evita depender del flag \Seen (que se puede
        // "ensuciar" si alguien lee la casilla a mano) — el dedupe real es por Message-ID en
        // TbiMailStore. Ancha a propósito: sirve también de "entrenamiento" del parser sobre
        // mails viejos (procesa todos los mails "TBI", no sólo los de pacientes activos hoy) —
        // ver reporte de no-parseados más abajo.
        var since = DateTime.UtcNow.AddMonths(-4);
        var uids = await folder.SearchAsync(SearchQuery.DeliveredAfter(since).And(SearchQuery.SubjectContains("TBI")), ct);

        var results = new List<TbiMailFetchResult>();
        var unparsed = new List<string>();
        foreach (var uid in uids)
        {
            var message = await folder.GetMessageAsync(uid, ct);
            var subject = message.Subject ?? string.Empty;
            var body = message.TextBody ?? message.HtmlBody ?? string.Empty;
            var info = TbiMailParser.Parse(subject, body, message.Date);
            if (info is null)
            {
                unparsed.Add($"[ASUNTO NO RECONOCIDO]\nAsunto: {subject}\nRecibido: {message.Date}\n\n{body}\n{new string('-', 60)}\n");
                continue;
            }

            info.MessageId = message.MessageId ?? $"{options.Folder}:{uid}";
            info.SenderEmail = message.From.Mailboxes.FirstOrDefault()?.Address;
            results.Add(info);

            var missing = new List<string>();
            if (info.TomographyDate is null) missing.Add("Fecha Tomo");
            if (info.TreatmentStartDate is null || info.MachineDisplayName is null) missing.Add("Fecha Inicio/Equipo");
            if (info.TotalApplications is null) missing.Add("Aplicaciones");
            if (missing.Count > 0)
            {
                unparsed.Add($"[FALTA: {string.Join(", ", missing)}]\nAsunto: {subject}\nRecibido: {message.Date}\n\n{body}\n{new string('-', 60)}\n");
            }
        }

        await client.DisconnectAsync(true, ct);

        if (!string.IsNullOrEmpty(options.DiagnosticsPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.DiagnosticsPath)!);
            await File.WriteAllTextAsync(options.DiagnosticsPath, string.Join("\n", unparsed), ct);
        }

        return results;
    }
}
