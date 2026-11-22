using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using JobDist.Core.Domain;
using JobDist.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Infrastructure.Alerts;

/// <summary>
/// Settings for sending admin alerts through MessageHub (github.com/mrpichet/messagehub), section <c>Alerts:MessageHub</c>.
/// </summary>
public sealed class MessageHubAlertOptions
{
    public const string SectionName = "Alerts:MessageHub";

    /// <summary>Send through MessageHub; false = alerts are only logged.</summary>
    public bool Enabled { get; set; }

    /// <summary>MessageHub base URL, e.g. <c>https://messagehub.internal:8080</c>. Must be https unless <see cref="AllowInsecureHttp"/>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>MessageHub API key (role User). Supply via secret store / environment, never commit.</summary>
    public string? ApiKey { get; set; }

    /// <summary>MessageHub channel: Email, TeamsEmail, TeamsChat, TeamsChannel, Sms, Telegram, Line, Discord, ...</summary>
    [Required] public string Channel { get; set; } = "Email";

    /// <summary>Admin addresses for the channel (e-mail addresses, chat ids or <c>webhook:name</c> for Teams channels).</summary>
    public List<string> Recipients { get; set; } = [];

    /// <summary>Ask MessageHub to add its hybrid ML-DSA + ECDSA digital signature to the alert.</summary>
    public bool Sign { get; set; } = true;

    /// <summary>Allow http:// (only inside a private container network).</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>HTTP timeout.</summary>
    [Range(1, 120)] public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// <see cref="IAlertSender"/> that calls MessageHub's REST API: <c>POST /api/messages</c> with header <c>X-Api-Key</c> and body
/// <c>{ channel, recipient, subject, body, sign }</c> - one call per admin recipient. The URL comes from configuration only (no SSRF);
/// the API key is never logged. POST is not retried automatically (avoids duplicate alerts); the alert service retries on the next scan.
/// </summary>
public sealed class MessageHubAlertSender(HttpClient http, IOptionsMonitor<MessageHubAlertOptions> options, ILogger<MessageHubAlertSender> logger) : IAlertSender
{
    /// <summary>Name of the configured HttpClient.</summary>
    public const string HttpClientName = "messagehub";

    /// <inheritdoc />
    public async Task<bool> SendAsync(AdminAlertDTO alert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var o = options.CurrentValue;

        // Step 1: disabled / not configured → log only (development).
        if (!o.Enabled || string.IsNullOrWhiteSpace(o.BaseUrl) || o.Recipients.Count == 0)
        {
            logger.LogWarning("ADMIN ALERT (MessageHub not configured): {Title}\n{Body}", alert.Title, alert.Body);
            return true;
        }

        // Step 2: validate the endpoint (https only unless explicitly allowed).
        if (!Uri.TryCreate(new Uri(o.BaseUrl.TrimEnd('/') + "/"), "api/messages", out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps && !(o.AllowInsecureHttp && endpoint.Scheme == Uri.UriSchemeHttp)))
        {
            logger.LogError("Alerts:MessageHub:BaseUrl is invalid or not https.");
            return false;
        }

        // Step 3: one message per recipient; success when at least one is accepted.
        var anyOk = false;
        foreach (var recipient in o.Recipients.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new MessageHubSendRequest(o.Channel, recipient, Truncate(alert.Title, 300), Truncate(alert.Body, 100_000), o.Sign))
            };
            request.Headers.Add("X-Api-Key", o.ApiKey);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
                using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    anyOk = true;
                }
                else
                {
                    logger.LogError("MessageHub rejected the alert for one recipient: HTTP {Status}.", (int)response.StatusCode);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogError("MessageHub unreachable: {Message}", ex.Message);
            }
        }
        return anyOk;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>Request body of MessageHub <c>POST /api/messages</c> (enum channel serialised as its name).</summary>
    private sealed record MessageHubSendRequest(string Channel, string Recipient, string Subject, string Body, bool Sign);
}
