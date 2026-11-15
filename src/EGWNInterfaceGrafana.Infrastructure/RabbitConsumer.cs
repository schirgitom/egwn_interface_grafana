using EGWNInterfaceGrafana.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EGWNInterfaceGrafana.Infrastructure;

public sealed class RabbitStatus
{
    public volatile bool Connected;
}

public sealed class RabbitConsumer(
    IServiceScopeFactory scopes, IOptions<RabbitOptions> options,
    RabbitStatus status, ILogger<RabbitConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogWarning("RabbitMQ consumption is disabled.");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var factory = new ConnectionFactory
                {
                    HostName = settings.HostName, Port = settings.Port, VirtualHost = settings.VirtualHost,
                    UserName = settings.UserName, Password = settings.Password,
                    AutomaticRecoveryEnabled = false,
                    Ssl = new SslOption { Enabled = settings.UseTls, ServerName = settings.HostName }
                };
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), stoppingToken);
                await channel.ExchangeDeclareAsync(settings.Exchange, ExchangeType.Topic, durable: true, cancellationToken: stoppingToken);
                await channel.QueueDeclareAsync(settings.Queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
                await channel.QueueBindAsync(settings.Queue, settings.Exchange, settings.RoutingKey, cancellationToken: stoppingToken);
                await channel.QueueDeclareAsync(settings.Queue + ".dead", durable: true, exclusive: false,
                    autoDelete: false, cancellationToken: stoppingToken);
                await channel.BasicQosAsync(0, 1, false, stoppingToken);

                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, _) => { status.Connected = false; disconnected.TrySetResult(); return Task.CompletedTask; };
                connection.ConnectionShutdownAsync += (_, _) => { status.Connected = false; disconnected.TrySetResult(); return Task.CompletedTask; };
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        await HandleAsync(channel, delivery, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                    catch (Exception exception)
                    {
                        // Callback failures must tear down the channel so unacknowledged messages are redelivered.
                        logger.LogError("RabbitMQ callback failed ({ErrorType}); reconnecting", exception.GetType().Name);
                        status.Connected = false;
                        disconnected.TrySetResult();
                    }
                };
                await channel.BasicConsumeAsync(settings.Queue, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
                status.Connected = true;
                logger.LogInformation("Consuming queue {Queue}", settings.Queue);
                await disconnected.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("RabbitMQ connection failed ({ErrorType}); reconnecting", exception.GetType().Name);
            }
            finally { status.Connected = false; }
            try { await Task.Delay(TimeSpan.FromSeconds(settings.RetrySeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var settings = options.Value;
        for (var attempt = 1; attempt <= settings.MaxAttempts; attempt++)
        {
            try
            {
                if (delivery.Body.Length > settings.MaxMessageBytes)
                    throw new InvalidEventException("Message exceeds the configured size limit.");
                var message = EventPolicy.Parse(delivery.Body.Span);
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<EventProcessor>().ProcessAsync(message, ct);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Event processing failed, attempt {Attempt}/{MaxAttempts}, error {ErrorType}, HTTP {HttpStatus}, SQL {SqlState}, reason {Reason}",
                    attempt, settings.MaxAttempts, exception.GetType().Name,
                    (exception as HttpRequestException)?.StatusCode,
                    (exception as PostgresException ?? exception.InnerException as PostgresException)?.SqlState,
                    (exception as InvalidEventException)?.Message);
                if (exception is InvalidEventException || attempt == settings.MaxAttempts)
                {
                    var properties = new BasicProperties
                    {
                        Persistent = true, ContentType = "application/json",
                        MessageId = delivery.BasicProperties.MessageId,
                        Headers = new Dictionary<string, object?> { ["egwn-error-type"] = exception.GetType().Name }
                    };
                    // Confirm the dead-letter copy before acknowledging the original.
                    await channel.BasicPublishAsync("", settings.Queue + ".dead", mandatory: true,
                        basicProperties: properties, body: delivery.Body, cancellationToken: ct);
                    await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
                    logger.LogError("Message moved to dead-letter queue {Queue}", settings.Queue + ".dead");
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, settings.RetrySeconds * Math.Pow(2, attempt - 1))), ct);
            }
        }
    }
}
