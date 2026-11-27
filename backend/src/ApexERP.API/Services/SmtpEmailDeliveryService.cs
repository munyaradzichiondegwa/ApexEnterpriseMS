using System.Net;
using System.Net.Mail;

namespace ApexERP.API.Services;

public interface IEmailDeliveryService
{
    Task<bool> SendAsync(string recipient, string subject, string body, CancellationToken ct = default);
}

public sealed class SmtpEmailDeliveryService : IEmailDeliveryService
{
    private readonly IConfiguration _configuration;
    public SmtpEmailDeliveryService(IConfiguration configuration) => _configuration = configuration;

    public async Task<bool> SendAsync(string recipient, string subject, string body, CancellationToken ct = default)
    {
        var host = _configuration["Smtp:Host"];
        var from = _configuration["Smtp:From"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from)) return false;
        var port = _configuration.GetValue<int?>("Smtp:Port") ?? 587;
        using var message = new MailMessage(new MailAddress(from), new MailAddress(recipient))
        {
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        using var client = new SmtpClient(host, port)
        {
            EnableSsl = _configuration.GetValue<bool?>("Smtp:EnableSsl") ?? true,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 15000
        };
        var username = _configuration["Smtp:Username"];
        var password = _configuration["Smtp:Password"];
        if (!string.IsNullOrWhiteSpace(username))
            client.Credentials = new NetworkCredential(username, password ?? string.Empty);
        await client.SendMailAsync(message, ct);
        return true;
    }
}
