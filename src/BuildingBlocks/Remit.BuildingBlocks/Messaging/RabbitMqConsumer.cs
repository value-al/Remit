using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Remit.BuildingBlocks.Messaging;

/// <summary>A message as a consumer sees it. <see cref="Id"/> is the outbox id — the idempotency key downstream.</summary>
public sealed record IncomingMessage(Guid Id, string Type, string Payload, string? CorrelationId, DateTimeOffset OccurredAt);

public interface IMessageHandler
{
    /// <summary>The routing patterns this handler wants bound to its queue, e.g. <c>funding.deposit.settled.v1</c> or <c>funding.#</c>.</summary>
    IReadOnlyList<string> Bindings { get; }

    /// <summary>
    /// Handle the message. Return normally to ack. Throw to nack: the message is requeued once,
    /// then dead-lettered on the second failure, so a poison message cannot block the queue.
    /// Handlers must be idempotent on <see cref="IncomingMessage.Id"/> (ADR-0003).
    /// </summary>
    Task HandleAsync(IncomingMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// One durable queue per service, bound to the handler's patterns on the shared topic exchange.
/// Manual acks, prefetch 16, a dead-letter exchange for messages that fail twice, and a
/// consumer span that is the child of the producer's span (ADR-0007).
/// </summary>
public sealed class RabbitMqConsumer(
    IOptions<RabbitMqOptions> options,
    IMessageHandler handler,
    IHostEnvironment environment,
    ILogger<RabbitMqConsumer> logger) : BackgroundService
{
    public static readonly ActivitySource Activity = new("Remit.Messaging");

    private readonly RabbitMqOptions _options = options.Value;
    private IConnection? _connection;
    private IChannel? _channel;

    public string QueueName { get; } = $"{environment.ApplicationName.ToLowerInvariant().Replace(".", "-")}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory { Uri = new Uri(_options.Uri), AutomaticRecoveryEnabled = true };
        _connection = await factory.CreateConnectionAsync(stoppingToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

        var deadLetterExchange = $"{_options.Exchange}.dead";
        await _channel.ExchangeDeclareAsync(_options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: stoppingToken);
        await _channel.ExchangeDeclareAsync(deadLetterExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: stoppingToken);
        await _channel.QueueDeclareAsync($"{QueueName}.dead", durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
        await _channel.QueueBindAsync($"{QueueName}.dead", deadLetterExchange, "#", cancellationToken: stoppingToken);

        await _channel.QueueDeclareAsync(
            QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = deadLetterExchange },
            cancellationToken: stoppingToken);

        foreach (var binding in handler.Bindings)
        {
            await _channel.QueueBindAsync(QueueName, _options.Exchange, binding, cancellationToken: stoppingToken);
        }

        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 16, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, ea) => await OnReceivedAsync(ea, stoppingToken);
        await _channel.BasicConsumeAsync(QueueName, autoAck: false, consumer, stoppingToken);

        logger.LogInformation("Consuming {Queue} bound to {Bindings}.", QueueName, string.Join(", ", handler.Bindings));
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task OnReceivedAsync(BasicDeliverEventArgs ea, CancellationToken cancellationToken)
    {
        var properties = ea.BasicProperties;
        var parent = TraceContext.Extract(properties.Headers);
        using var span = Activity.StartActivity($"process {properties.Type}", ActivityKind.Consumer, parent);
        span?.SetTag("messaging.system", "rabbitmq");
        span?.SetTag("messaging.destination.name", QueueName);
        span?.SetTag("messaging.message.id", properties.MessageId);

        if (!Guid.TryParse(properties.MessageId, out var id) || string.IsNullOrEmpty(properties.Type))
        {
            logger.LogWarning("Discarding message without id/type on {Queue}.", QueueName);
            await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken);
            return;
        }

        var message = new IncomingMessage(
            id,
            properties.Type,
            Encoding.UTF8.GetString(ea.Body.Span),
            properties.CorrelationId,
            properties.Timestamp.UnixTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(properties.Timestamp.UnixTime) : DateTimeOffset.UtcNow);

        try
        {
            await handler.HandleAsync(message, cancellationToken);
            await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // First failure: requeue once. Second: dead-letter, so a poison message cannot block the queue.
            var alreadyRedelivered = ea.Redelivered;
            span?.SetStatus(ActivityStatusCode.Error, e.Message);
            logger.LogError(e, "Handling {Type} {MessageId} failed (redelivered: {Redelivered}).", message.Type, message.Id, alreadyRedelivered);
            await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: !alreadyRedelivered, cancellationToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // Disposing the connection closes its channels. Disposing the channel afterwards races the
        // client's recovery bookkeeping and throws ObjectDisposedException, so it is not done here.
        // Shutdown must never fail because the broker went away first.
        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or RabbitMQ.Client.Exceptions.AlreadyClosedException)
        {
            logger.LogDebug(e, "Broker connection was already gone at shutdown.");
        }
    }
}
