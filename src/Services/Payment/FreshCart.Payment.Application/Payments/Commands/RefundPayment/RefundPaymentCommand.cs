using FreshCart.BuildingBlocks.CQRS;

namespace FreshCart.Payment.Application.Payments.Commands.RefundPayment;

public sealed record RefundPaymentCommand(
    Guid PaymentId,
    decimal Amount,
    string Reason,
    string IdempotencyKey) : ICommand<RefundPaymentResult>;
