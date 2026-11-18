using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Meva.Rt.Application;
using MimeKit;

namespace Meva.Rt.Infrastructure.Mail;

public sealed class AlertMailOptions
{
    public string User { get; set; } = string.Empty;
    public string AppPassword { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 465;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(AppPassword) && !string.IsNullOrWhiteSpace(To);
}

/// <summary>Envía mails de alerta por SMTP (Gmail con app password por defecto).</summary>
public sealed class AlertMailClient(AlertMailOptions options) : IAlertMailSender
{
    public bool IsConfigured => options.IsConfigured;

    public async Task SendAsync(string subject, string body, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(options.User));
        message.To.Add(MailboxAddress.Parse(options.To));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        // Timeout corto: un SMTP colgado no debe frenar el refresh del dashboard.
        using var client = new SmtpClient { Timeout = 20_000 };
        await client.ConnectAsync(options.SmtpHost, options.SmtpPort, SecureSocketOptions.Auto, cancellationToken);
        if (client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
            await client.AuthenticateAsync(options.User, options.AppPassword, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
