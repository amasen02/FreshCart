using FluentValidation;

namespace FreshCart.Payment.Application.Payments.Commands.RefundPayment;

public sealed class RefundPaymentCommandValidator : AbstractValidator<RefundPaymentCommand>
{
    private const int MoneyPrecision = 18;
    private const int MoneyScale = 2;
    private const int MaxReasonLength = 200;
    private const int MaxIdempotencyKeyLength = 128;

    public RefundPaymentCommandValidator()
    {
        RuleFor(command => command.PaymentId).NotEmpty();

        RuleFor(command => command.IdempotencyKey)
            .NotEmpty()
            .MaximumLength(MaxIdempotencyKeyLength);

        RuleFor(command => command.Amount)
            .GreaterThan(0)
            .PrecisionScale(MoneyPrecision, MoneyScale, ignoreTrailingZeros: true);

        RuleFor(command => command.Reason)
            .NotEmpty()
            .MaximumLength(MaxReasonLength);
    }
}
