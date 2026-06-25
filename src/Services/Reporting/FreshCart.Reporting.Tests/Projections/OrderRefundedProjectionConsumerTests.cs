using FreshCart.BuildingBlocks.Messaging.IntegrationEvents;
using FreshCart.Reporting.Application.Common.Abstractions;
using FreshCart.Reporting.Application.Projections.Consumers;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FreshCart.Reporting.Tests.Projections;

public sealed class OrderRefundedProjectionConsumerTests
{
    private readonly IProjectionWriter projectionWriter = Substitute.For<IProjectionWriter>();
    private readonly OrderRefundedProjectionConsumer consumer;

    public OrderRefundedProjectionConsumerTests()
    {
        consumer = new OrderRefundedProjectionConsumer(
            projectionWriter,
            NullLogger<OrderRefundedProjectionConsumer>.Instance);
    }

    [Fact]
    public async Task AppliesRefundProjectionOnFirstDelivery()
    {
        var integrationEvent = CreateOrderRefundedEvent();
        using var cancellationSource = new CancellationTokenSource();
        var consumeContext = CreateConsumeContext(integrationEvent, cancellationSource.Token);

        projectionWriter
            .ApplyOrderRefundedAsync(integrationEvent, cancellationSource.Token)
            .Returns(true);

        await consumer.Consume(consumeContext);

        await projectionWriter.Received(1).ApplyOrderRefundedAsync(integrationEvent, cancellationSource.Token);
    }

    [Fact]
    public async Task TreatsAnAlreadyProcessedEventAsANoOpSoRedeliveriesDoNotDoubleRefund()
    {
        var integrationEvent = CreateOrderRefundedEvent();
        var consumeContext = CreateConsumeContext(integrationEvent, CancellationToken.None);

        projectionWriter
            .ApplyOrderRefundedAsync(integrationEvent, Arg.Any<CancellationToken>())
            .Returns(false);

        await consumer.Consume(consumeContext);

        await projectionWriter.Received(1).ApplyOrderRefundedAsync(integrationEvent, Arg.Any<CancellationToken>());
    }

    private static ConsumeContext<OrderRefundedIntegrationEvent> CreateConsumeContext(
        OrderRefundedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        var consumeContext = Substitute.For<ConsumeContext<OrderRefundedIntegrationEvent>>();
        consumeContext.Message.Returns(integrationEvent);
        consumeContext.CancellationToken.Returns(cancellationToken);
        return consumeContext;
    }

    private static OrderRefundedIntegrationEvent CreateOrderRefundedEvent() => new()
    {
        OrderId = Guid.NewGuid(),
        RefundAmount = 12.75m,
        CurrencyCode = "USD",
        Reason = "Damaged item reported by customer",
    };
}
