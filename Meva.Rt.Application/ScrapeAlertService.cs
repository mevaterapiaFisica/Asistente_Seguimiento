using System.Text;

namespace Meva.Rt.Application;

public interface IAlertMailSender
{
    bool IsConfigured { get; }
    Task SendAsync(string subject, string body, CancellationToken cancellationToken);
}

/// <summary>Último mail de alerta enviado (persistido en alert_mail_state.json) para limitar a uno por día.</summary>
public sealed class ScrapeAlertState
{
    public string? LastSentDate { get; set; }
    public string? LastStatus { get; set; }
    public DateTime SentAtUtc { get; set; }
}

/// <summary>
/// Avisa por mail cuando el scrape de seguimiento queda "degraded" o "broken" (ver BootstrapService.ApplyFollowUpHealth).
/// Máximo UN mail por día calendario (hora local): mientras siga roto, mañana llega otro recordatorio.
/// Un fallo al enviar nunca rompe el refresh; el estado solo se marca como enviado si el envío salió bien.
/// </summary>
public sealed class ScrapeAlertService(ISnapshotStore snapshotStore, IAlertMailSender sender)
{
    private const string StateName = "alert_mail_state";

    public async Task NotifyAsync(ScrapeHealth health, CancellationToken cancellationToken)
    {
        if (health.Status == "ok") return;

        try
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var state = await snapshotStore.TryLoadAsync<ScrapeAlertState>(StateName, cancellationToken);
            if (state?.LastSentDate == today)
            {
                Console.Error.WriteLine("[Alert] Scrape de seguimiento no está ok pero ya se envió el mail de hoy — no se reenvía.");
                return;
            }

            if (!sender.IsConfigured)
            {
                Console.Error.WriteLine("[Alert] Scrape de seguimiento no está ok y NO se pudo enviar mail: faltan credenciales (MEVA_TBI_MAIL_USER/MEVA_TBI_MAIL_APP_PASSWORD o MEVA_ALERT_MAIL_*).");
                return;
            }

            var (subject, body) = BuildMessage(health);
            await sender.SendAsync(subject, body, cancellationToken);
            await snapshotStore.SaveAsync(StateName,
                new ScrapeAlertState { LastSentDate = today, LastStatus = health.Status, SentAtUtc = DateTime.UtcNow },
                cancellationToken);
            Console.Error.WriteLine($"[Alert] Mail de alerta enviado ({health.Status}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[Alert] Falló el envío del mail de alerta (se reintenta en el próximo refresh): {ex.Message}");
        }
    }

    public static (string Subject, string Body) BuildMessage(ScrapeHealth h)
    {
        var broken = h.Status == "broken";
        var subject = broken
            ? $"[MevaRT] Scrape de seguimiento ROTO ({h.FailedCombos}/{h.TotalCombos} búsquedas con error)"
            : $"[MevaRT] Scrape de seguimiento INCOMPLETO ({h.FailedCombos}/{h.TotalCombos} búsquedas con error)";

        var sb = new StringBuilder();
        sb.AppendLine(h.Summary);
        sb.AppendLine();
        sb.AppendLine($"Estado: {(broken ? "ROTO (se descartó el scrape)" : "INCOMPLETO (datos parciales)")}");
        sb.AppendLine($"Último scrape correcto: {(h.LastOkUtc.HasValue ? h.LastOkUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "desconocido")}");
        sb.AppendLine($"Pacientes en este scrape: {h.Patients} (antes: {h.PreviousPatients})");
        sb.AppendLine($"Pacientes conservados del snapshot anterior (marcados como viejos): {h.StalePatients}");

        var errors = h.Issues.Where(i => i.Kind == "error").ToList();
        if (errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Errores (agrupados por mensaje):");
            foreach (var g in errors.GroupBy(e => e.Message).OrderByDescending(g => g.Count()).Take(10))
            {
                var combos = string.Join(", ", g.Take(8).Select(e => $"{e.Center}/{e.Stage}"));
                sb.AppendLine($" - {g.Key} ({g.Count()}): {combos}{(g.Count() > 8 ? ", ..." : "")}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Detalle en el banner del dashboard y en MEVA_DATA_DIR\\logs\\meva_AAAAMMDD.log.");
        sb.AppendLine("Este aviso se envía como máximo una vez por día mientras el problema persista.");
        return (subject, sb.ToString());
    }
}
