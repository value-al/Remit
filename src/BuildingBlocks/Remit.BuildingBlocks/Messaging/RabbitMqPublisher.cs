using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Remit.BuildingBlocks.Outbox;

namespace Remit.BuildingBlocks.Messaging;

public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";

    /// <summary>amqp://user:pass@host:5672/vhost</summary>
    public string Uri { get; set; } = "amqp://guest:guest@localhost:5672";

    /// <summary>Topic exchange all Remit services publish to; routing key is the message type.</summary>
    public string Exchange { get; set; } = "remit";
}

/// <summary>The transport behind an outbox relay. Must not return before the broker has accepted the message.</summary>
public interface IMessagePublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>For the in-memory configuration and for tests that only need the relay's bookkeeping.</summary>
public sealed class NullMessagePublisher : IMessagePublisher
{
    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Publishes to a durable topic exchange with publisher confirms enabled, so
/// <see cref="PublishAsync"/> completes only once the broker has taken responsibility for the
/// message. Combined with the outbox this gives at-least-once delivery end to end (ADR-0003).
/// The current trace context travels in the message headers (ADR-0007).
/// </summary>
public sealed class RabbitMqPublisher(IOptions<RabbitMqOptions> options) : IMessagePublisher, IAsyncDisposable
{
    public static readonly ActivitySource Activity = new("Remit.Messaging");

    private readonly RabbitMqOptions _options = options.Value;
    private readonly SemaphoreSlim _connect = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        using var span = Activity.StartActivity($"publish {message.Type}", ActivityKind.Producer);
        span?.SetTag("messaging.system", "rabbitmq");
        span?.SetTag("messaging.destination.name", _options.Exchange);
        span?.SetTag("messaging.message.id", message.Id.ToString());
        span?.SetTag("messaging.rabbitmq.routing_key", message.Type);

        var channel = await GetChannelAsync(cancellationToken);

        var headers = new Dictionary<string, object?>();
        TraceContext.Inject(span ?? System.Diagnostics.Activity.Current, headers);

        var properties = new BasicProperties
        {
            MessageId = message.Id.ToString(),
            Type = message.Type,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            CorrelationId = message.CorrelationId,
            Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
            Headers = headers,
        };

        await channel.BasicPublishAsync(
            exchange: _options.Exchange,
            routingKey: message.Type,
            mandatory: false,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(message.Payload),
            cancellationToken: cancellationToken);
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await _connect.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            if (_connection is not { IsOpen: true })
            {
                var factory = new ConnectionFactory { Uri = new Uri(_options.Uri), AutomaticRecoveryEnabled = true };
                _connection = await factory.CreateConnectionAsync(cancellationToken);
            }

            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                cancellationToken);
            await _channel.ExchangeDeclareAsync(_options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
            return _channel;
        }
        finally
        {
            _connect.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _connect.Dispose();
    }
}
