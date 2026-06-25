using System.Globalization;
using FreshCart.BuildingBlocks.Exceptions;
using FreshCart.Payment.Domain.Events;

namespace FreshCart.Payment.Domain;

/// <summary>
/// Event-sourced payment aggregate. State is never set directly; every change is expressed as an
/// <see cref="IPaymentEvent"/> applied through <see cref="Apply"/>, so replaying the stored stream
/// reproduces the exact state and the stream itself is the compliance audit trail.
/// Invariants: capture only after authorization, refund only after capture, cumulative refunds
/// never exceed the captured amount, and a decline is terminal.
/// </summary>
public sealed class PaymentAggregate
{
    private const int InitialVersion = 1;

    private readonly List<IPaymentEvent> _uncommittedEvents = [];

    private readonly HashSet<string> _appliedRefundKeys = new(StringComparer.Ordinal);

    private PaymentAggregate()
    {
    }

    public Guid PaymentId { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid CustomerId { get; private set; }

    public decimal Amount { get; private set; }

    public decimal RefundedAmount { get; private set; }

    public string CurrencyCode { get; private set; } = string.Empty;

    public string Method { get; private set; } = string.Empty;

    public PaymentStatus Status { get; private set; }

    public string? ProviderReference { get; private set; }

    public string? DeclineReason { get; private set; }

    public DateTimeOffset InitiatedOnUtc { get; private set; }

    public DateTimeOffset LastChangedOnUtc { get; private set; }

    public int Version { get; private set; }

    public static PaymentAggregate Initiate(
        Guid paymentId,
        Guid orderId,
        Guid customerId,
        decimal amount,
        string currencyCode,
        string method,
        DateTimeOffset occurredOnUtc)
    {
        if (paymentId == Guid.Empty || orderId == Guid.Empty || customerId == Guid.Empty)
        {
            throw new DomainException("A payment requires a payment, order and customer identifier.");
        }

        if (amount <= 0)
        {
            throw new DomainException("A payment amount must be positive.");
        }

        if (string.IsNullOrWhiteSpace(currencyCode) || string.IsNullOrWhiteSpace(method))
        {
            throw new DomainException("A payment requires a currency code and a payment method.");
        }

        var payment = new PaymentAggregate();
        payment.RaiseEvent(new PaymentInitiated(
            paymentId,
            InitialVersion,
            occurredOnUtc,
            orderId,
            customerId,
            amount,
            currencyCode,
            method));

        return payment;
    }

    public static PaymentAggregate ReplayFrom(IReadOnlyList<IPaymentEvent> eventStream)
    {
        ArgumentNullException.ThrowIfNull(eventStream);

        if (eventStream.Count == 0)
        {
            throw new DomainException("A payment cannot be replayed from an empty event stream.");
        }

        var payment = new PaymentAggregate();
        foreach (var paymentEvent in eventStream)
        {
            payment.Apply(paymentEvent);
        }

        return payment;
    }

    public void Authorize(string providerReference, DateTimeOffset occurredOnUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerReference);

        if (Status != PaymentStatus.Initiated)
        {
            throw new DomainException(
                $"Only an initiated payment can be authorized; the payment is {Status}.");
        }

        RaiseEvent(new PaymentAuthorized(PaymentId, Version + 1, occurredOnUtc, providerReference));
    }

    public void Capture(DateTimeOffset occurredOnUtc)
    {
        if (Status != PaymentStatus.Authorized)
        {
            throw new DomainException(
                $"Only an authorized payment can be captured; the payment is {Status}.");
        }

        RaiseEvent(new PaymentCaptured(PaymentId, Version + 1, occurredOnUtc));
    }

    public void Decline(string reason, DateTimeOffset occurredOnUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status is not (PaymentStatus.Initiated or PaymentStatus.Authorized))
        {
            throw new DomainException(
                $"Only an initiated or authorized payment can be declined; the payment is {Status}.");
        }

        RaiseEvent(new PaymentDeclined(PaymentId, Version + 1, occurredOnUtc, reason));
    }

    public bool HasRefundWithKey(string idempotencyKey) =>
        !string.IsNullOrEmpty(idempotencyKey) && _appliedRefundKeys.Contains(idempotencyKey);

    public void Refund(decimal amount, string reason, string idempotencyKey, DateTimeOffset occurredOnUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        if (Status is not (PaymentStatus.Captured or PaymentStatus.PartiallyRefunded))
        {
            throw new DomainException(
                $"Only a captured payment can be refunded; the payment is {Status}.");
        }

        if (amount <= 0)
        {
            throw new DomainException("A refund amount must be positive.");
        }

        if (RefundedAmount + amount > Amount)
        {
            throw new DomainException(string.Create(
                CultureInfo.InvariantCulture,
                $"Refunding {amount} would bring cumulative refunds to {RefundedAmount + amount}, exceeding the captured amount of {Amount}."));
        }

        RaiseEvent(new PaymentRefunded(PaymentId, Version + 1, occurredOnUtc, amount, reason, idempotencyKey));
    }

    public IReadOnlyList<IPaymentEvent> DequeueUncommittedEvents()
    {
        var dequeuedEvents = _uncommittedEvents.ToArray();
        _uncommittedEvents.Clear();
        return dequeuedEvents;
    }

    private void RaiseEvent(IPaymentEvent paymentEvent)
    {
        Apply(paymentEvent);
        _uncommittedEvents.Add(paymentEvent);
    }

    private void Apply(IPaymentEvent paymentEvent)
    {
        if (paymentEvent.Version != Version + 1)
        {
            throw new DomainException(string.Create(
                CultureInfo.InvariantCulture,
                $"Event version {paymentEvent.Version} does not follow the current version {Version} of payment {paymentEvent.PaymentId}."));
        }

        switch (paymentEvent)
        {
            case PaymentInitiated initiated:
                PaymentId = initiated.PaymentId;
                OrderId = initiated.OrderId;
                CustomerId = initiated.CustomerId;
                Amount = initiated.Amount;
                CurrencyCode = initiated.CurrencyCode;
                Method = initiated.Method;
                Status = PaymentStatus.Initiated;
                InitiatedOnUtc = initiated.OccurredOnUtc;
                break;

            case PaymentAuthorized authorized:
                ProviderReference = authorized.ProviderReference;
                Status = PaymentStatus.Authorized;
                break;

            case PaymentCaptured:
                Status = PaymentStatus.Captured;
                break;

            case PaymentDeclined declined:
                DeclineReason = declined.Reason;
                Status = PaymentStatus.Declined;
                break;

            case PaymentRefunded refunded:
                RefundedAmount += refunded.Amount;
                Status = RefundedAmount >= Amount
                    ? PaymentStatus.Refunded
                    : PaymentStatus.PartiallyRefunded;
                if (!string.IsNullOrEmpty(refunded.IdempotencyKey))
                {
                    _appliedRefundKeys.Add(refunded.IdempotencyKey);
                }

                break;

            default:
                throw new DomainException($"Unsupported payment event type {paymentEvent.GetType().Name}.");
        }

        Version = paymentEvent.Version;
        LastChangedOnUtc = paymentEvent.OccurredOnUtc;
    }
}
