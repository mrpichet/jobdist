using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using JobDist.Contracts;
using JobDist.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

namespace JobDist.Infrastructure.Alerts;

/// <summary>
/// Connection to MessageHub (github.com/mrpichet/messagehub), section <c>Alerts:MessageHub</c>.
/// Recipients are no longer configured here: they are managed on the alert recipient screen (table <c>alertrecipients</c>).
/// </summary>
public sealed class MessageHubAlertOptions
{
    public const string SectionName = "Alerts:MessageHub";

    /// <summary>Send through MessageHub; false = alerts are recorded as Skipped (development).</summary>
    public bool Enabled { get; set; }

    /// <summary>MessageHub base URL, e.g. <c>https://messagehub.internal:8080</c>. Must be https unless <see cref="AllowInsecureHttp"/>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>MessageHub API key (role User). Supply via environment <c>Alerts__MessageHub__ApiKey</c> or a secret store, never commit.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Ask MessageHub to add its hybrid ML-DSA + ECDSA digital signature to the message.</summary>
    public bool Sign { get; set; } = true;

    /// <summary>Allow http:// (only inside a private container network).</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>HTTP timeout of one send.</summary>
    [Range(1, 120)] public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// <see cref="IAlertDeliveryClient"/> over MessageHub's REST API: <c>POST /api/messages</c> with header <c>X-Api-Key</c> and body
/// <c>{ channel, recipient, subject, body, sign }</c>. The URL comes from configuration only (no SSRF) and the API key is never logged.
/// The send client has a timeout and a circuit breaker but no retry (POST is not idempotent at MessageHub; the outbox retries later).
/// </summary>
public sealed class MessageHubDeliveryClient(IHttpClientFactory httpFactory, IOptionsMonitor<MessageHubAlertOptions> options, TimeProvider time,
    ILogger<MessageHubDeliveryClient> logger) : IAlertDeliveryClient
{
    /// <summary>HttpClient for sends (timeout + circuit breaker).</summary>
    public const string SendClientName = "messagehub-send";

    /// <summary>HttpClient for reads (timeout + retry + circuit breaker).</summary>
    public const string ReadClientName = "messagehub-read";

    private static readonly TimeSpan ChannelCacheTtl = TimeSpan.FromMinutes(10);
    private volatile Tuple<DateTimeOffset, IReadOnlyList<string>>? _channels;

    /// <inheritdoc />
    public bool IsConfigured
    {
        get
        {
            var o = options.CurrentValue;
            return o.Enabled && !string.IsNullOrWhiteSpace(o.BaseUrl) && !string.IsNullOrWhiteSpace(o.ApiKey);
        }
    }

    /// <inheritdoc />
    public async Task<AlertDeliveryResult> SendAsync(string channel, string address, string subject, string body, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!IsConfigured)
        {
            return new(AlertSendStatus.Skipped, false, null, "MessageHub is not configured.");
        }
        if (Endpoint(o, "api/messages") is not { } endpoint)
        {
            return new(AlertSendStatus.Failed, false, null, "Alerts:MessageHub:BaseUrl is invalid or not https.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new SendRequest(channel, address, Truncate(subject, 300), Truncate(body, 100_000), o.Sign))
        };
        request.Headers.Add("X-Api-Key", o.ApiKey);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
            using var response = await httpFactory.CreateClient(SendClientName).SendAsync(request, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<DeliveryResult>(cts.Token).ConfigureAwait(false);
                return result?.Status switch
                {
                    "Sent" or "Queued" or null => new(AlertSendStatus.Sent, false, result?.CorrelationId, null),
                    "Rejected" => new(AlertSendStatus.Failed, false, result.CorrelationId, Truncate(result.Error ?? "Rejected by MessageHub.", 500)),
                    _ => new(AlertSendStatus.Failed, true, result.CorrelationId, Truncate(result.Error ?? "Delivery failed at MessageHub.", 500))
                };
            }

            // 400/401/403/404/422: our request or configuration is wrong - retrying will not help.
            var status = (int)response.StatusCode;
            var retryable = status >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;
            logger.LogWarning("MessageHub rejected an alert: HTTP {Status}.", status);
            return new(AlertSendStatus.Failed, retryable, null, $"MessageHub HTTP {status}.");
        }
        catch (BrokenCircuitException)
        {
            return new(AlertSendStatus.Failed, true, null, "MessageHub circuit breaker is open.", CircuitOpen: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or Polly.Timeout.TimeoutRejectedException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("MessageHub unreachable: {Message}", ex.Message);
            return new(AlertSendStatus.Failed, true, null, "MessageHub unreachable or timed out.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetChannelsAsync(CancellationToken ct)
    {
        var cached = _channels;
        var now = time.GetUtcNow();
        if (cached is not null && now - cached.Item1 < ChannelCacheTtl)
        {
            return cached.Item2;
        }
        var o = options.CurrentValue;
        if (!IsConfigured || Endpoint(o, "api/messages/channels") is not { } endpoint)
        {
            return [];
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Add("X-Api-Key", o.ApiKey);
            using var response = await httpFactory.CreateClient(ReadClientName).SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var list = await response.Content.ReadFromJsonAsync<List<string>>(ct).ConfigureAwait(false) ?? [];
            _channels = Tuple.Create(now, (IReadOnlyList<string>)list);
            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BrokenCircuitException or Polly.Timeout.TimeoutRejectedException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not read MessageHub channels: {Message}", ex.Message);
            return cached?.Item2 ?? [];
        }
    }

    private Uri? Endpoint(MessageHubAlertOptions o, string path)
    {
        if (!Uri.TryCreate(o.BaseUrl?.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || !Uri.TryCreate(baseUri, path, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps && !(o.AllowInsecureHttp && endpoint.Scheme == Uri.UriSchemeHttp)))
        {
            logger.LogError("Alerts:MessageHub:BaseUrl is invalid or not https.");
            return null;
        }
        return endpoint;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>Body of MessageHub <c>POST /api/messages</c> (channel is the enum name).</summary>
    private sealed record SendRequest(string Channel, string Recipient, string Subject, string Body, bool Sign);

    /// <summary>MessageHub <c>DeliveryResultDTO</c> (only the fields used here).</summary>
    private sealed record DeliveryResult(string? CorrelationId, string? Status, string? Error);
}
