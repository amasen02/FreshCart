using FluentAssertions;
using FreshCart.BuildingBlocks.Exceptions;
using FreshCart.Payment.Domain;
using FreshCart.Payment.Domain.Events;
using Xunit;

namespace FreshCart.Payment.Tests.Domain;

public sealed class PaymentAggregateTests
{
    private static readonly Guid PaymentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CustomerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset InitiationInstant = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private const decimal CapturedAmount = 100.00m;
    private const string CurrencyCode = "USD";
    private const string CardMethod = "card";
    private const string ProviderReference = "SIM-TEST-REFERENCE";
    private const string DeclineReason = "The card was declined by the issuing bank.";
    private const string RefundReason = "Customer returned the goods.";
    private const string RefundKey = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void InitiateProducesAnInitiatedPaymentAtVersionOne()
    {
        var payment = InitiatePayment();

        payment.PaymentId.Should().Be(PaymentId);
        payment.OrderId.Should().Be(OrderId);
        payment.CustomerId.Should().Be(CustomerId);
        payment.Amount.Should().Be(CapturedAmount);
        payment.RefundedAmount.Should().Be(0m);
        payment.CurrencyCode.Should().Be(CurrencyCode);
        payment.Method.Should().Be(CardMethod);
        payment.Status.Should().Be(PaymentStatus.Initiated);
        payment.ProviderReference.Should().BeNull();
        payment.DeclineReason.Should().BeNull();
        payment.InitiatedOnUtc.Should().Be(InitiationInstant);
        payment.LastChangedOnUtc.Should().Be(InitiationInstant);
        payment.Version.Should().Be(1);
    }

    [Fact]
    public void InitiateQueuesASinglePaymentInitiatedEvent()
    {
        var payment = InitiatePayment();

        var uncommittedEvents = payment.DequeueUncommittedEvents();

        var initiated = uncommittedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<PaymentInitiated>().Subject;
        initiated.Version.Should().Be(1);
        initiated.OrderId.Should().Be(OrderId);
        initiated.Amount.Should().Be(CapturedAmount);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void InitiateRejectsEmptyIdentifiers(bool emptyPaymentId, bool emptyOrderId, bool emptyCustomerId)
    {
        var initiateWithEmptyIdentifier = () => PaymentAggregate.Initiate(
            emptyPaymentId ? Guid.Empty : PaymentId,
            emptyOrderId ? Guid.Empty : OrderId,
            emptyCustomerId ? Guid.Empty : CustomerId,
            CapturedAmount,
            CurrencyCode,
            CardMethod,
            InitiationInstant);

        initiateWithEmptyIdentifier.Should().Throw<DomainException>()
            .WithMessage("*payment, order and customer identifier*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    public void InitiateRejectsNonPositiveAmounts(decimal invalidAmount)
    {
        var initiateWithInvalidAmount = () => PaymentAggregate.Initiate(
            PaymentId, OrderId, CustomerId, invalidAmount, CurrencyCode, CardMethod, InitiationInstant);

        initiateWithInvalidAmount.Should().Throw<DomainException>()
            .WithMessage("*amount must be positive*");
    }

    [Theory]
    [InlineData("", CardMethod)]
    [InlineData("   ", CardMethod)]
    [InlineData(CurrencyCode, "")]
    [InlineData(CurrencyCode, "   ")]
    public void InitiateRejectsBlankCurrencyOrMethod(string currencyCode, string method)
    {
        var initiateWithBlankField = () => PaymentAggregate.Initiate(
            PaymentId, OrderId, CustomerId, CapturedAmount, currencyCode, method, InitiationInstant);

        initiateWithBlankField.Should().Throw<DomainException>()
            .WithMessage("*currency code and a payment method*");
    }

    [Fact]
    public void AuthorizeMovesAnInitiatedPaymentToAuthorizedAndRecordsTheProviderReference()
    {
        var payment = InitiatePayment();

        payment.Authorize(ProviderReference, InitiationInstant.AddSeconds(1));

        payment.Status.Should().Be(PaymentStatus.Authorized);
        payment.ProviderReference.Should().Be(ProviderReference);
        payment.Version.Should().Be(2);
        payment.LastChangedOnUtc.Should().Be(InitiationInstant.AddSeconds(1));
    }

    [Fact]
    public void AuthorizeIsRejectedOnceThePaymentLeftTheInitiatedState()
    {
        var payment = CapturedPayment();

        var authorizeCapturedPayment = () => payment.Authorize(ProviderReference, InitiationInstant.AddSeconds(3));

        authorizeCapturedPayment.Should().Throw<DomainException>()
            .WithMessage("*Only an initiated payment can be authorized*Captured*");
    }

    [Fact]
    public void CaptureMovesAnAuthorizedPaymentToCaptured()
    {
        var payment = AuthorizedPayment();

        payment.Capture(InitiationInstant.AddSeconds(2));

        payment.Status.Should().Be(PaymentStatus.Captured);
        payment.Version.Should().Be(3);
    }

    [Fact]
    public void CaptureWithoutPriorAuthorizationIsRejected()
    {
        var payment = InitiatePayment();

        var captureUnauthorizedPayment = () => payment.Capture(InitiationInstant.AddSeconds(1));

        captureUnauthorizedPayment.Should().Throw<DomainException>()
            .WithMessage("*Only an authorized payment can be captured*Initiated*");
    }

    [Fact]
    public void CaptureOfADeclinedPaymentIsRejected()
    {
        var payment = DeclinedPayment();

        var captureDeclinedPayment = () => payment.Capture(InitiationInstant.AddSeconds(2));

        captureDeclinedPayment.Should().Throw<DomainException>()
            .WithMessage("*Only an authorized payment can be captured*Declined*");
    }

    [Fact]
    public void DeclineFromInitiatedRecordsTheReasonAndEndsTheStream()
    {
        var payment = InitiatePayment();

        payment.Decline(DeclineReason, InitiationInstant.AddSeconds(1));

        payment.Status.Should().Be(PaymentStatus.Declined);
        payment.DeclineReason.Should().Be(DeclineReason);
        payment.Version.Should().Be(2);
    }

    [Fact]
    public void DeclineFromAuthorizedIsAllowedBecauseCaptureCanStillFail()
    {
        var payment = AuthorizedPayment();

        payment.Decline(DeclineReason, InitiationInstant.AddSeconds(2));

        payment.Status.Should().Be(PaymentStatus.Declined);
        payment.Version.Should().Be(3);
    }

    [Fact]
    public void DeclineIsTerminalSoDecliningAgainIsRejected()
    {
        var payment = DeclinedPayment();

        var declineAgain = () => payment.Decline(DeclineReason, InitiationInstant.AddSeconds(2));

        declineAgain.Should().Throw<DomainException>()
            .WithMessage("*Only an initiated or authorized payment can be declined*Declined*");
    }

    [Fact]
    public void DeclineOfACapturedPaymentIsRejected()
    {
        var payment = CapturedPayment();

        var declineCapturedPayment = () => payment.Decline(DeclineReason, InitiationInstant.AddSeconds(3));

        declineCapturedPayment.Should().Throw<DomainException>()
            .WithMessage("*Only an initiated or authorized payment can be declined*Captured*");
    }

    [Fact]
    public void PartialRefundMovesACapturedPaymentToPartiallyRefunded()
    {
        var payment = CapturedPayment();

        payment.Refund(30.00m, RefundReason, RefundKey, InitiationInstant.AddMinutes(1));

        payment.Status.Should().Be(PaymentStatus.PartiallyRefunded);
        payment.RefundedAmount.Should().Be(30.00m);
        payment.Version.Should().Be(4);
    }

    [Fact]
    public void TracksAppliedRefundIdempotencyKeysSoARetryCanBeRecognised()
    {
        var payment = CapturedPayment();
        payment.Refund(30.00m, RefundReason, RefundKey, InitiationInstant.AddMinutes(1));

        payment.HasRefundWithKey(RefundKey).Should().BeTrue();
        payment.HasRefundWithKey("a-different-key").Should().BeFalse();
    }

    [Fact]
    public void FullRefundMovesACapturedPaymentToRefunded()
    {
        var payment = CapturedPayment();

        payment.Refund(CapturedAmount, RefundReason, RefundKey, InitiationInstant.AddMinutes(1));

        payment.Status.Should().Be(PaymentStatus.Refunded);
        payment.RefundedAmount.Should().Be(CapturedAmount);
    }

    [Fact]
    public void SecondRefundCompletingTheAmountMovesAPartiallyRefundedPaymentToRefunded()
    {
        var payment = CapturedPayment();
        payment.Refund(60.00m, RefundReason, RefundKey, InitiationInstant.AddMinutes(1));

        payment.Refund(40.00m, RefundReason, RefundKey, InitiationInstant.AddMinutes(2));

        payment.Status.Should().Be(PaymentStatus.Refunded);
        payment.RefundedAmount.Should().Be(CapturedAmount);
        payment.Version.Should().Be(5);
    }

    [Fact]
    public void RefundExceedingTheCapturedAmountIsRejected()
    {
        var payment = CapturedPayment();

        var refundTooMuch = () => payment.Refund(100.01m, RefundReason, RefundKey, InitiationInstant.AddMinutes(1));

        refundTooMuch.Should().Throw<DomainException>()
            .WithMessage("*exceeding the captured amount*");
    }

    [Fact]
    public void CumulativeRefundsExceedingTheCapturedAmountAreRejected()
    {
        var payment = CapturedPayment();
        payment.Refund(70.00m, RefundReason, RefundKey, InitiationInstant.AddMinutes(1));

        var refundBeyondRemainder = () => payment.Refund(30.01m, RefundReason, RefundKey, InitiationInstant.AddMinutes(2));

        refundBeyondRemainder.Should().Throw<DomainException>()
            .WithMessage("*exceeding the captured amount*");
        payment.RefundedAmount.Should().Be(70.00m);
        payment.Status.Should().Be(PaymentStatus.PartiallyRefunded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void RefundRejectsNonPositiveAmounts(decimal invalidAmount)
    {
        var payment = CapturedPayment();

        var refundInvalidAmount = () => payment.Refund(invalidAmount, RefundReason, RefundKey, InitiationInstant.AddMinutes(1));

        refundInvalidAmount.Should().Throw<DomainException>()
            .WithMessage("*refund amount must be positive*");
    }

    [Fact]
    public void RefundBeforeCaptureIsRejected()
    {
        var payment = AuthorizedPayment();

        var refundUncapturedPayment = () => payment.Refund(10.00m, RefundReason, RefundKey, InitiationInstant.AddMinutes(1));

        refundUncapturedPayment.Should().Throw<DomainException>()
            .WithMessage("*Only a captured payment can be refunded*Authorized*");
    }

    [Fact]
    public void RefundOfADeclinedPaymentIsRejected()
    {
        var payment = DeclinedPayment();

        var refundDeclinedPayment = () => payment.Refund(10.00m, RefundReason, RefundKey, InitiationInstant.AddMinutes(1));

        refundDeclinedPayment.Should().Throw<DomainException>()
            .WithMessage("*Only a captured payment can be refunded*Declined*");
    }

    [Fact]
    public void ReplayFromTheStoredEventStreamYieldsIdenticalState()
    {
        var livePayment = CapturedPayment();
        livePayment.Refund(25.00m, RefundReason, RefundKey, InitiationInstant.AddMinutes(1));
        var eventStream = livePayment.DequeueUncommittedEvents();

        var replayedPayment = PaymentAggregate.ReplayFrom(eventStream);

        replayedPayment.Should().BeEquivalentTo(livePayment);
        replayedPayment.DequeueUncommittedEvents().Should().BeEmpty();
    }

    [Fact]
    public void ReplayFromAnEmptyStreamIsRejected()
    {
        var replayNothing = () => PaymentAggregate.ReplayFrom([]);

        replayNothing.Should().Throw<DomainException>()
            .WithMessage("*cannot be replayed from an empty event stream*");
    }

    [Fact]
    public void ReplayWithAVersionGapIsRejected()
    {
        var initiated = new PaymentInitiated(
            PaymentId, 1, InitiationInstant, OrderId, CustomerId, CapturedAmount, CurrencyCode, CardMethod);
        var authorizedSkippingAVersion = new PaymentAuthorized(
            PaymentId, 3, InitiationInstant.AddSeconds(1), ProviderReference);

        var replayWithGap = () => PaymentAggregate.ReplayFrom([initiated, authorizedSkippingAVersion]);

        replayWithGap.Should().Throw<DomainException>()
            .WithMessage("*does not follow the current version*");
    }

    [Fact]
    public void DequeueUncommittedEventsEmptiesTheQueueAndPreservesOrder()
    {
        var payment = CapturedPayment();

        var firstDequeue = payment.DequeueUncommittedEvents();
        var secondDequeue = payment.DequeueUncommittedEvents();

        firstDequeue.Select(paymentEvent => paymentEvent.GetType()).Should().ContainInOrder(
            typeof(PaymentInitiated), typeof(PaymentAuthorized), typeof(PaymentCaptured));
        firstDequeue.Select(paymentEvent => paymentEvent.Version).Should().ContainInOrder(1, 2, 3);
        secondDequeue.Should().BeEmpty();
    }

    private static PaymentAggregate InitiatePayment() => PaymentAggregate.Initiate(
        PaymentId, OrderId, CustomerId, CapturedAmount, CurrencyCode, CardMethod, InitiationInstant);

    private static PaymentAggregate AuthorizedPayment()
    {
        var payment = InitiatePayment();
        payment.Authorize(ProviderReference, InitiationInstant.AddSeconds(1));
        return payment;
    }

    private static PaymentAggregate CapturedPayment()
    {
        var payment = AuthorizedPayment();
        payment.Capture(InitiationInstant.AddSeconds(2));
        return payment;
    }

    private static PaymentAggregate DeclinedPayment()
    {
        var payment = InitiatePayment();
        payment.Decline(DeclineReason, InitiationInstant.AddSeconds(1));
        return payment;
    }
}
