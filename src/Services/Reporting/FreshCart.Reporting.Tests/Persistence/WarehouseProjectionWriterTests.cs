using Dapper;
using FluentAssertions;
using FreshCart.BuildingBlocks.Messaging.IntegrationEvents;
using FreshCart.Reporting.Infrastructure.Persistence.Warehouse;
using MySqlConnector;

namespace FreshCart.Reporting.Tests.Persistence;

[Collection(WarehouseIntegrationCollection.Name)]
public sealed class WarehouseProjectionWriterTests(WarehouseIntegrationFixture fixture)
{
    private readonly WarehouseProjectionWriter writer = new(fixture.ConnectionFactory);

    [Fact]
    public async Task ApplyingARefundIsExactlyOnceSoARedeliveryDoesNotDoubleDeduct()
    {
        var orderId = Guid.NewGuid();
        await SeedSalesFactAsync(orderId, netRevenue: 100m);

        var refund = new OrderRefundedIntegrationEvent
        {
            OrderId = orderId,
            RefundAmount = 30m,
            CurrencyCode = "USD",
            Reason = "Damaged item",
        };

        var firstDelivery = await writer.ApplyOrderRefundedAsync(refund, CancellationToken.None);
        var redelivery = await writer.ApplyOrderRefundedAsync(refund, CancellationToken.None);

        firstDelivery.Should().BeTrue();
        redelivery.Should().BeFalse();

        var fact = await ReadSalesFactAsync(orderId);
        fact.RefundTotal.Should().Be(30m);
        fact.NetRevenue.Should().Be(70m);
    }

    [Fact]
    public async Task ApplyingAConfirmedOrderIsExactlyOnceSoARedeliveryDoesNotDoubleCountLifetimeValue()
    {
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var confirmed = CreateConfirmedEvent(orderId, customerId);

        var firstDelivery = await writer.ApplyOrderConfirmedAsync(confirmed, CancellationToken.None);
        var redelivery = await writer.ApplyOrderConfirmedAsync(confirmed, CancellationToken.None);

        firstDelivery.Should().BeTrue();
        redelivery.Should().BeFalse();

        var customerValue = await ReadCustomerLifetimeValueAsync(customerId);
        customerValue.OrderCount.Should().Be(1);
        customerValue.LifetimeValue.Should().Be(59.30m);

        var lineCount = await CountSalesLineFactsAsync(orderId);
        lineCount.Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentDeliveriesOfTheSameConfirmedEventApplyItExactlyOnce()
    {
        const int ConcurrentConsumers = 12;
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var confirmed = CreateConfirmedEvent(orderId, customerId);

        var applyTasks = Enumerable
            .Range(0, ConcurrentConsumers)
            .Select(_ => Task.Run(() => writer.ApplyOrderConfirmedAsync(confirmed, CancellationToken.None)));
        var outcomes = await Task.WhenAll(applyTasks);

        outcomes.Count(applied => applied).Should().Be(1);

        var customerValue = await ReadCustomerLifetimeValueAsync(customerId);
        customerValue.OrderCount.Should().Be(1);
    }

    private static OrderConfirmedIntegrationEvent CreateConfirmedEvent(Guid orderId, Guid customerId) => new()
    {
        OrderId = orderId,
        CustomerId = customerId,
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

    private async Task SeedSalesFactAsync(Guid orderId, decimal netRevenue)
    {
        const string seedSql = """
            INSERT INTO sales_facts
                (order_id, customer_id, occurred_on_utc, gross_revenue, discount_total,
                 refund_total, tax_total, shipping_total, net_revenue, payment_method)
            VALUES
                (@OrderId, @CustomerId, UTC_TIMESTAMP(6), @NetRevenue, 0, 0, 0, 0, @NetRevenue, 'Card')
            """;

        var connection = new MySqlConnection(fixture.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await connection.ExecuteAsync(seedSql, new { OrderId = orderId, CustomerId = Guid.NewGuid(), NetRevenue = netRevenue }).ConfigureAwait(false);
        }
    }

    private async Task<SalesFactRow> ReadSalesFactAsync(Guid orderId)
    {
        var connection = new MySqlConnection(fixture.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            return await connection.QuerySingleAsync<SalesFactRow>(
                "SELECT refund_total AS RefundTotal, net_revenue AS NetRevenue FROM sales_facts WHERE order_id = @OrderId",
                new { OrderId = orderId }).ConfigureAwait(false);
        }
    }

    private async Task<CustomerLifetimeValueRow> ReadCustomerLifetimeValueAsync(Guid customerId)
    {
        var connection = new MySqlConnection(fixture.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            return await connection.QuerySingleAsync<CustomerLifetimeValueRow>(
                "SELECT order_count AS OrderCount, lifetime_value AS LifetimeValue FROM customer_lifetime_value WHERE customer_id = @CustomerId",
                new { CustomerId = customerId }).ConfigureAwait(false);
        }
    }

    private async Task<int> CountSalesLineFactsAsync(Guid orderId)
    {
        var connection = new MySqlConnection(fixture.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            return await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sales_line_facts WHERE order_id = @OrderId",
                new { OrderId = orderId }).ConfigureAwait(false);
        }
    }

    private sealed record SalesFactRow(decimal RefundTotal, decimal NetRevenue);

    private sealed record CustomerLifetimeValueRow(int OrderCount, decimal LifetimeValue);
}
