namespace FreshCart.Payment.Domain.Events;

public sealed record PaymentRefunded(
    Guid PaymentId,
    int Version,
    DateTimeOffset OccurredOnUtc,
    decimal Amount,
    string Reason,
    string IdempotencyKey) : IPaymentEvent;
