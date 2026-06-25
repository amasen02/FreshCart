using FreshCart.BuildingBlocks.Messaging.IntegrationEvents;
using FreshCart.Reporting.Application.Common.Abstractions;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace FreshCart.Reporting.Application.Projections.Consumers;

/// <summary>
/// Projects the <see cref="OrderConfirmedIntegrationEvent"/> emitted by the Ordering service into
/// the read warehouse: daily sales snapshot, product leaderboard counters and customer lifetime value.
/// </summary>
public sealed partial class OrderConfirmedProjectionConsumer(
    IProjectionWriter projectionWriter,
    ILogger<OrderConfirmedProjectionConsumer> logger)
    : IConsumer<OrderConfirmedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<OrderConfirmedIntegrationEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var applied = await projectionWriter
            .ApplyOrderConfirmedAsync(context.Message, context.CancellationToken)
            .ConfigureAwait(false);

        if (!applied)
        {
            LogSkippingAlreadyProcessedEvent(context.Message.EventId);
            return;
        }

        LogProjectedOrderConfirmed(
            context.Message.OrderId,
            context.Message.GrandTotal,
            context.Message.CurrencyCode);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Skipping previously processed OrderConfirmedIntegrationEvent {EventId}")]
    private partial void LogSkippingAlreadyProcessedEvent(Guid eventId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Projected OrderConfirmed for order {OrderId} totalling {GrandTotal} {CurrencyCode}")]
    private partial void LogProjectedOrderConfirmed(Guid orderId, decimal grandTotal, string currencyCode);
}
