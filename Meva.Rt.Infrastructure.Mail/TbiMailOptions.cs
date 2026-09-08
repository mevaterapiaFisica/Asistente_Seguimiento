namespace Meva.Rt.Infrastructure.Mail;

public sealed class TbiMailOptions
{
    public string User { get; set; } = string.Empty;
    public string AppPassword { get; set; } = string.Empty;
    public string Host { get; set; } = "imap.gmail.com";
    public int Port { get; set; } = 993;
    public string Folder { get; set; } = "INBOX";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(AppPassword);
}
