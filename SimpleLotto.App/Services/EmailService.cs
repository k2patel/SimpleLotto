using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleLotto.App.Services;

public sealed class EmailService
{
    private const int MaximumRecipients = 25;

    public static bool TryParseRecipients(
        string raw,
        out List<string> recipients,
        out string error)
    {
        recipients = new List<string>();
        error = string.Empty;
        var candidates = (raw ?? string.Empty).Split(
            new[] { ',', ';', '\r', '\n' },
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (candidates.Length == 0)
        {
            error = "Enter at least one recipient email address.";
            return false;
        }

        if (candidates.Length > MaximumRecipients)
        {
            error = $"Enter no more than {MaximumRecipients} recipients.";
            return false;
        }

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            try
            {
                var address = new MailAddress(candidate);
                if (!string.Equals(address.Address, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Use a plain email address without a display name: {candidate}";
                    return false;
                }

                if (unique.Add(address.Address))
                    recipients.Add(address.Address);
            }
            catch (FormatException)
            {
                error = $"Invalid recipient email address: {candidate}";
                return false;
            }
        }

        return recipients.Count > 0;
    }

    public async Task<EmailSendResult> SendAsync(
        EmailConfiguration configuration,
        string subject,
        string body,
        IReadOnlyList<string>? attachmentPaths = null)
    {
        if (string.IsNullOrWhiteSpace(configuration.Host))
            return EmailSendResult.Failed("SMTP server is missing.");
        if (configuration.Port is < 1 or > 65535)
            return EmailSendResult.Failed("SMTP port must be from 1 through 65535.");
        if (string.IsNullOrWhiteSpace(configuration.User))
            return EmailSendResult.Failed("Gmail address is missing.");
        if (string.IsNullOrWhiteSpace(configuration.Password))
            return EmailSendResult.Failed("Gmail app password is missing.");
        if (configuration.Recipients.Count == 0)
            return EmailSendResult.Failed("No recipients are configured.");

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(configuration.User),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };
            foreach (var recipient in configuration.Recipients)
                message.To.Add(new MailAddress(recipient));

            foreach (var path in attachmentPaths ?? Array.Empty<string>())
            {
                if (!File.Exists(path))
                    return EmailSendResult.Failed($"Selected attachment was not found: {Path.GetFileName(path)}");
                message.Attachments.Add(new Attachment(path, MimeTypeFor(path)));
            }

            using var client = new SmtpClient(configuration.Host, configuration.Port)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(configuration.User, configuration.Password),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 60_000
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await client.SendMailAsync(message, timeout.Token);
            return EmailSendResult.Succeeded(
                $"SMTP accepted the message for {configuration.Recipients.Count} recipient{(configuration.Recipients.Count == 1 ? string.Empty : "s")} via {configuration.Host}:{configuration.Port}.");
        }
        catch (OperationCanceledException)
        {
            return EmailSendResult.Failed("SMTP send timed out after 60 seconds.");
        }
        catch (SmtpException ex)
        {
            return EmailSendResult.Failed($"SMTP {ex.StatusCode}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return EmailSendResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string MimeTypeFor(string path) =>
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)
            ? "application/pdf"
            : "text/csv";
}

public sealed record EmailConfiguration(
    string Host,
    int Port,
    string User,
    string Password,
    IReadOnlyList<string> Recipients);

public sealed record EmailSendResult(bool IsSuccess, string Message)
{
    public static EmailSendResult Succeeded(string message) => new(true, message);

    public static EmailSendResult Failed(string message) => new(false, message);
}
