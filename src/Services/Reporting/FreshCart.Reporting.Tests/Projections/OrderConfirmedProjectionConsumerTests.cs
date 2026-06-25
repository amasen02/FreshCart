using FreshCart.BuildingBlocks.Messaging.IntegrationEvents;
using FreshCart.Reporting.Application.Common.Abstractions;
using FreshCart.Reporting.Application.Projections.Consumers;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FreshCart.Reporting.Tests.Projections;

public sealed class OrderConfirmedProjectionConsumerTests
{
    private readonly IProjectionWriter projectionWriter = Substitute.For<IProjectionWriter>();
    private readonly OrderConfirmedProjectionConsumer consumer;

    public OrderConfirmedProjectionConsumerTests()
    {
        consumer = new OrderConfirmedProjectionConsumer(
            projectionWriter,
            NullLogger<OrderConfirmedProjectionConsumer>.Instance);
    }

    [Fact]
    public async Task AppliesProjectionOnFirstDelivery()
    {
        var integrationEvent = CreateOrderConfirmedEvent();
        using var cancellationSource = new CancellationTokenSource();
        var consumeContext = CreateConsumeContext(integrationEvent, cancellationSource.Token);

        projectionWriter
            .ApplyOrderConfirmedAsync(integrationEvent, cancellationSource.Token)
            .Returns(true);

        await consumer.Consume(consumeContext);

        await projectionWriter.Received(1).ApplyOrderConfirmedAsync(integrationEvent, cancellationSource.Token);
    }

    [Fact]
    public async Task TreatsAnAlreadyProcessedEventAsANoOpSoRedeliveriesDoNotDoubleCount()
    {
        var integrationEvent = CreateOrderConfirmedEvent();
        var consumeContext = CreateConsumeContext(integrationEvent, CancellationToken.None);

        projectionWriter
            .ApplyOrderConfirmedAsync(integrationEvent, Arg.Any<CancellationToken>())
            .Returns(false);

        await consumer.Consume(consumeContext);

        await projectionWriter.Received(1).ApplyOrderConfirmedAsync(integrationEvent, Arg.Any<CancellationToken>());
    }

    private static ConsumeContext<OrderConfirmedIntegrationEvent> CreateConsumeContext(
        OrderConfirmedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        var consumeContext = Substitute.For<ConsumeContext<OrderConfirmedIntegrationEvent>>();
        consumeContext.Message.Returns(integrationEvent);
        consumeContext.CancellationToken.Returns(cancellationToken);
        return consumeContext;
    }

    private static OrderConfirmedIntegrationEvent CreateOrderConfirmedEvent() => new()
    {
        OrderId = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        GrandTotal = 64.30m,
        DiscountTotal = 5.00m,
        TaxTotal = 4.30m,
        ShippingTotal = 6.00m,
        CurrencyCode = "USD",
        PaymentMethod = "Card",
        Lines =
        [
            new OrderConfirmedLine("SKU-APPLES-1KG", "Royal Gala Apples 1kg", "Produce", 2, 4.50m),
            new OrderConfirmedLine("SKU-MILK-2L", "Full Cream Milk 2L", "Dairy", 1, 3.80m),
        ],
    };
}
