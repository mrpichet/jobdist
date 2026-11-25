using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using JobDist.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace JobDist.Infrastructure.Messaging;

/// <summary>RabbitMQ settings for <c>Messaging:Transport = RabbitMq</c> (section <c>Messaging:RabbitMq</c>).</summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "Messaging:RabbitMq";

    /// <summary>Broker host name.</summary>
    [Required] public string HostName { get; set; } = "localhost";

    /// <summary>AMQP port (5672, or 5671 with TLS).</summary>
    [Range(1, 65_535)] public int Port { get; set; } = 5672;

    /// <summary>Virtual host.</summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>User name.</summary>
    public string UserName { get; set; } = "guest";

    /// <summary>Password. Supply via environment <c>Messaging__RabbitMq__Password</c> or a secret store, never commit.</summary>
    public string? Password { get; set; }

    /// <summary>Use TLS (AMQPS).</summary>
    public bool UseTls { get; set; }

    /// <summary>Durable direct exchange for alert messages.</summary>
    public string Exchange { get; set; } = "jobdist.alerts";

    /// <summary>Durable quorum queue consumed by every server instance (competing consumers).</summary>
    public string Queue { get; set; } = "jobdist.alerts";

    /// <summary>Dead-letter exchange / queue for messages that cannot be processed (poison messages).</summary>
    public string DeadLetterExchange { get; set; } = "jobdist.alerts.dlx";

    /// <summary>Unacknowledged messages per consumer.</summary>
    [Range(1, 1_000)] public ushort PrefetchCount { get; set; } = 20;

    /// <summary>A published row is re-published when the consumer has not finished it within this time (lost message).</summary>
    [Range(30, 86_400)] public int RelayLockSeconds { get; set; } = 300;

    /// <summary>Deliveries of one message before it goes to the dead-letter queue (quorum queue delivery limit).</summary>
    [Range(1, 100)] public int DeliveryLimit { get; set; } = 5;
}

/// <summary>Shared RabbitMQ connection and topology (exchange, quorum queue with delivery limit, dead-letter queue).</summary>
public sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnection> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    /// <summary>Opens (once) a connection with automatic recovery and declares the topology.</summary>
    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is { IsOpen: true } again)
            {
                return again;
            }
            var o = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = o.HostName, Port = o.Port, VirtualHost = o.VirtualHost, UserName = o.UserName, Password = o.Password ?? string.Empty,
                AutomaticRecoveryEnabled = true, TopologyRecoveryEnabled = true, NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
                ClientProvidedName = "jobdist-server"
            };
            if (o.UseTls)
            {
                factory.Ssl = new SslOption { Enabled = true, ServerName = o.HostName };
            }
            _connection = await factory.CreateConnectionAsync(ct).ConfigureAwait(false);

            // Topology is idempotent: every instance declares the same objects.
            await using var channel = await _connection.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);
            await channel.ExchangeDeclareAsync(o.Exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct).ConfigureAwait(false);
            await channel.ExchangeDeclareAsync(o.DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct).ConfigureAwait(false);
            await channel.QueueDeclareAsync(o.Queue + ".dead", durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: ct).ConfigureAwait(false);
            await channel.QueueBindAsync(o.Queue + ".dead", o.DeadLetterExchange, string.Empty, cancellationToken: ct).ConfigureAwait(false);
            await channel.QueueDeclareAsync(o.Queue, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = o.DeliveryLimit,
                ["x-dead-letter-exchange"] = o.DeadLetterExchange
            }, cancellationToken: ct).ConfigureAwait(false);
            await channel.QueueBindAsync(o.Queue, o.Exchange, o.Queue, cancellationToken: ct).ConfigureAwait(false);
            logger.LogInformation("Connected to RabbitMQ {Host}:{Port}, queue {Queue}.", o.HostName, o.Port, o.Queue);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        _gate.Dispose();
    }
}

/// <summary>
/// Outbox relay (transactional outbox pattern): reserves due outbox rows and publishes one persistent message per row with
/// publisher confirms. Message id = <c>{eventKey}:{recipientId}</c>, body = outbox id. Runs on every instance (rows are
/// reserved with row locks, so each is published by one instance). A row stays Pending until the consumer finishes it.
/// </summary>
public sealed class RabbitMqOutboxRelayWorker(AlertOutboxProcessor processor, RabbitMqConnection connection, IOptions<RabbitMqOptions> rabbit,
    IOptions<Core.Options.AlertOptions> alertOptions, ILogger<RabbitMqOutboxRelayWorker> logger) : Workers.PeriodicWorker(logger)
{
    private IChannel? _channel;

    /// <inheritdoc />
    protected override async Task<TimeSpan> RunCycleAsync(CancellationToken ct)
    {
        var o = rabbit.Value;
        if (_channel is not { IsOpen: true })
        {
            var conn = await connection.GetAsync(ct).ConfigureAwait(false);
            _channel = await conn.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct).ConfigureAwait(false);
        }

        var rows = await processor.ReserveForRelayAsync(TimeSpan.FromSeconds(o.RelayLockSeconds), ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var props = new BasicProperties
            {
                Persistent = true,
                MessageId = $"{row.EventKey}:{row.RecipientId}",
                ContentType = "text/plain",
                Type = "jobdist.alert.v1"
            };
            // With confirmation tracking the call completes when the broker confirmed (throws when nacked / returned).
            await _channel.BasicPublishAsync(o.Exchange, o.Queue, mandatory: true, basicProperties: props,
                body: Encoding.UTF8.GetBytes(row.OutboxId.ToString(CultureInfo.InvariantCulture)), cancellationToken: ct).ConfigureAwait(false);
        }
        return rows.Count >= alertOptions.Value.OutboxBatchSize ? TimeSpan.Zero : TimeSpan.FromSeconds(alertOptions.Value.OutboxPollSeconds);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Idempotent consumer: claims the outbox row by id and delivers it to MessageHub. A duplicate message, or one for a row
/// already handled, is acknowledged without sending. Retryable delivery errors are kept in the outbox (back-off) and
/// re-published by the relay when due; a message that keeps crashing the consumer is dead-lettered by the quorum queue.
/// </summary>
public sealed class RabbitMqAlertConsumerWorker(AlertOutboxProcessor processor, RabbitMqConnection connection, IOptions<RabbitMqOptions> rabbit,
    ILogger<RabbitMqAlertConsumerWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var errors = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var conn = await connection.GetAsync(stoppingToken).ConfigureAwait(false);
                await using var channel = await conn.CreateChannelAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                await channel.BasicQosAsync(0, rabbit.Value.PrefetchCount, false, stoppingToken).ConfigureAwait(false);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, ea) =>
                {
                    try
                    {
                        if (long.TryParse(Encoding.UTF8.GetString(ea.Body.Span), NumberStyles.None, CultureInfo.InvariantCulture, out var outboxId))
                        {
                            await processor.ProcessOneAsync(outboxId, stoppingToken).ConfigureAwait(false);
                        }
                        else
                        {
                            logger.LogWarning("Ignoring malformed alert message {MessageId}.", ea.BasicProperties.MessageId);
                        }
                        await channel.BasicAckAsync(ea.DeliveryTag, false, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Requeue; the quorum queue's delivery limit dead-letters a message that keeps failing.
                        logger.LogError(ex, "Alert message {MessageId} failed; requeued.", ea.BasicProperties.MessageId);
                        await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: true, stoppingToken).ConfigureAwait(false);
                    }
                };
                await channel.BasicConsumeAsync(rabbit.Value.Queue, autoAck: false, consumer, stoppingToken).ConfigureAwait(false);
                errors = 0;
                while (channel.IsOpen && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                errors++;
                logger.LogError(ex, "RabbitMQ consumer failed ({Errors} in a row); reconnecting.", errors);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(errors, 6)))), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
