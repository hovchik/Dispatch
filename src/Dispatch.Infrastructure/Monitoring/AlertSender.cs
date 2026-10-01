using System.Net;
using System.Net.Mail;
using System.Text;
using Dispatch.Application.Monitoring;
using Dispatch.Domain;

namespace Dispatch.Infrastructure.Monitoring;

/// <summary>Sends monitor alerts over HTTP (generic webhook or Slack incoming webhook) and SMTP email.</summary>
public sealed class AlertSender : IAlertSender
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task SendAsync(AlertTarget target, AlertMessage message, CancellationToken ct)
    {
        switch (target.Channel)
        {
            case AlertChannel.Slack:
                await PostAsync(target.Target, AlertFormatter.SlackPayload(message), ct).ConfigureAwait(false);
                break;
            case AlertChannel.Webhook:
                await PostAsync(target.Target, AlertFormatter.WebhookPayload(message), ct).ConfigureAwait(false);
                break;
            case AlertChannel.Email:
                await SendEmailAsync(target, message, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task PostAsync(string url, string json, CancellationToken ct)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static async Task SendEmailAsync(AlertTarget target, AlertMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.SmtpHost))
            throw new InvalidOperationException("Email alert has no SMTP host configured.");
        using var mail = new MailMessage
        {
            From = new MailAddress(target.FromAddress.Length > 0 ? target.FromAddress : "dispatch@localhost"),
            Subject = message.Title,
            Body = message.Body
        };
        foreach (var recipient in target.Target.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            mail.To.Add(recipient);
        if (mail.To.Count == 0)
            throw new InvalidOperationException("Email alert has no recipients.");

        using var client = new SmtpClient(target.SmtpHost, target.SmtpPort)
        {
            EnableSsl = target.SmtpUseTls,
            Credentials = target.SmtpUser.Length > 0 ? new NetworkCredential(target.SmtpUser, target.SmtpPassword) : null
        };
        await client.SendMailAsync(mail, ct).ConfigureAwait(false);
    }
}
