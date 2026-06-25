using FluentValidation.TestHelper;
using FreshCart.Payment.Application.Payments.Commands.RefundPayment;
using Xunit;

namespace FreshCart.Payment.Tests.Application;

public sealed class RefundPaymentCommandValidatorTests
{
    private readonly RefundPaymentCommandValidator _validator = new();

    [Fact]
    public void WellFormedCommandPassesEveryRule()
    {
        var validationResult = _validator.TestValidate(ValidCommand());

        validationResult.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptyPaymentIdIsRejected()
    {
        var validationResult = _validator.TestValidate(ValidCommand() with { PaymentId = Guid.Empty });

        validationResult.ShouldHaveValidationErrorFor(command => command.PaymentId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveAmountIsRejected(decimal invalidAmount)
    {
        var validationResult = _validator.TestValidate(ValidCommand() with { Amount = invalidAmount });

        validationResult.ShouldHaveValidationErrorFor(command => command.Amount);
    }

    [Fact]
    public void AmountWithMoreThanTwoDecimalPlacesIsRejected()
    {
        var validationResult = _validator.TestValidate(ValidCommand() with { Amount = 5.005m });

        validationResult.ShouldHaveValidationErrorFor(command => command.Amount);
    }

    [Fact]
    public void EmptyReasonIsRejected()
    {
        var validationResult = _validator.TestValidate(ValidCommand() with { Reason = string.Empty });

        validationResult.ShouldHaveValidationErrorFor(command => command.Reason);
    }

    [Fact]
    public void OverlongReasonIsRejected()
    {
        var validationResult = _validator.TestValidate(ValidCommand() with { Reason = new string('r', 201) });

        validationResult.ShouldHaveValidationErrorFor(command => command.Reason);
    }

    [Fact]
    public void EmptyIdempotencyKeyIsRejected()
    {
        var validationResult = _validator.TestValidate(ValidCommand() with { IdempotencyKey = string.Empty });

        validationResult.ShouldHaveValidationErrorFor(command => command.IdempotencyKey);
    }

    private static RefundPaymentCommand ValidCommand() => new(
        PaymentId: Guid.Parse("88888888-8888-8888-8888-888888888888"),
        Amount: 25.00m,
        Reason: "Customer returned the goods.",
        IdempotencyKey: "88888888-8888-8888-8888-888888888888");
}
