using FreshCart.BuildingBlocks.Messaging.IntegrationEvents;

namespace FreshCart.Reporting.Application.Common.Abstractions;

/// <summary>
/// Applies a consumed integration event onto the materialised warehouse tables the dashboards read.
/// Each apply is exactly-once: the projection and the idempotency record commit in a single transaction,
/// so a redelivered event is a no-op. Returns <c>true</c> when this call applied the event and
/// <c>false</c> when it had already been processed.
/// </summary>
public interface IProjectionWriter
{
    Task<bool> ApplyOrderConfirmedAsync(OrderConfirmedIntegrationEvent integrationEvent, CancellationToken cancellationToken);

    Task<bool> ApplyOrderRefundedAsync(OrderRefundedIntegrationEvent integrationEvent, CancellationToken cancellationToken);
}
