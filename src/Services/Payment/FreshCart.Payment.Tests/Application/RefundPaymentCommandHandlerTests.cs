using FluentAssertions;
using FreshCart.BuildingBlocks.Exceptions;
using FreshCart.Payment.Application.Abstractions;
using FreshCart.Payment.Application.Payments.Commands.RefundPayment;
using FreshCart.Payment.Application.Payments.Models;
using FreshCart.Payment.Domain;
using FreshCart.Payment.Domain.Events;
using FreshCart.Payment.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FreshCart.Payment.Tests.Application;

public sealed class RefundPaymentCommandHandlerTests
{
    private static readonly Guid PaymentId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid OrderId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid CustomerId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset RefundInstant = new(2026, 6, 2, 9, 0, 0, TimeSpan.Zero);

    private const decimal CapturedAmount = 80.00m;
    private const string CurrencyCode = "EUR";
    private const string CardMethod = "card";
    private const string ProviderReference = "SIM-TEST-REFERENCE";
    private const string RefundReason = "Damaged item reported by the customer.";
    private const string RefundIdempotencyKey = "88888888-8888-8888-8888-888888888888";
    private const string ProviderRejectionReason = "The settlement window for this transaction has closed.";

    private readonly IPaymentEventStore _paymentEventStore = Substitute.For<IPaymentEventStore>();
    private readonly IPaymentReadModelWriter _paymentReadModelWriter = Substitute.For<IPaymentReadModelWriter>();
    private readonly IPaymentProvider _paymentProvider = Substitute.For<IPaymentProvider>();

    private readonly List<IReadOnlyList<IPaymentEvent>> _appendedBatches = [];
    private readonly List<PaymentReadModel> _projectedModels = [];

    private readonly RefundPaymentCommandHandler _commandHandler;

    public RefundPaymentCommandHandlerTests()
    {
        _paymentEventStore
            .AppendAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Do<IReadOnlyList<IPaymentEvent>>(_appendedBatches.Add),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _paymentReadModelWriter
            .UpsertAsync(Arg.Do<PaymentReadModel>(_projectedModels.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _commandHandler = new RefundPaymentCommandHandler(
            _paymentEventStore,
            _paymentReadModelWriter,
            _paymentProvider,
            new FixedTimeProvider(RefundInstant),
            NullLogger<RefundPaymentCommandHandler>.Instance);
    }

    [Fact]
    public async Task PartialRefundAppendsPaymentRefundedAndProjectsPartiallyRefunded()
    {
        StreamContainsCapturedPayment();
        ProviderApprovesRefund(30.00m);

        var refundResult = await _commandHandler.Handle(
            new RefundPaymentCommand(PaymentId, 30.00m, RefundReason, RefundIdempotencyKey), CancellationToken.None);

        refundResult.PaymentId.Should().Be(PaymentId);
        refundResult.OrderId.Should().Be(OrderId);
        refundResult.Status.Should().Be(PaymentStatus.PartiallyRefunded);
        refundResult.RefundedAmount.Should().Be(30.00m);

        var refunded = _appendedBatches.Should().ContainSingle()
            .Which.Should().ContainSingle()
            .Which.Should().BeOfType<PaymentRefunded>().Subject;
        refunded.Version.Should().Be(4);
        refunded.Amount.Should().Be(30.00m);
        refunded.Reason.Should().Be(RefundReason);

        var projectedModel = _projectedModels.Should().ContainSingle().Subject;
        projectedModel.Status.Should().Be(PaymentStatus.PartiallyRefunded);
        projectedModel.RefundedAmount.Should().Be(30.00m);
    }

    [Fact]
    public async Task FullRefundProjectsTheRefundedStatus()
    {
        StreamContainsCapturedPayment();
        ProviderApprovesRefund(CapturedAmount);

        var refundResult = await _commandHandler.Handle(
            new RefundPaymentCommand(PaymentId, CapturedAmount, RefundReason, RefundIdempotencyKey), CancellationToken.None);

        refundResult.Status.Should().Be(PaymentStatus.Refunded);
        refundResult.RefundedAmount.Should().Be(CapturedAmount);
    }

    [Fact]
    public async Task RefundAppendsAgainstTheVersionLoadedFromTheStream()
    {
        StreamContainsCapturedPayment();
        ProviderApprovesRefund(10.00m);
        var observedExpectedVersions = new List<int>();
        _paymentEventStore
            .AppendAsync(
                Arg.Any<Guid>(),
                Arg.Do<int>(observedExpectedVersions.Add),
                Arg.Any<IReadOnlyList<IPaymentEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _commandHandler.Handle(
            new RefundPaymentCommand(PaymentId, 10.00m, RefundReason, RefundIdempotencyKey), CancellationToken.None);

        observedExpectedVersions.Should().ContainSingle().Which.Should().Be(3);
    }

    [Fact]
    public Task RefundOfAnUnknownPaymentThrowsNotFound()
    {
        _paymentEventStore
            .LoadStreamAsync(PaymentId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IPaymentEvent>());

        var refundUnknownPayment = () => _commandHandler.Handle(
            new RefundPaymentCommand(PaymentId, 10.00m, RefundReason, RefundIdempotencyKey), CancellationToken.None);

        return refundUnknownPayment.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task ProviderRejectionLeavesTheStreamAndReadModelUntouched()
    {
        StreamContainsCapturedPayment();
        _paymentProvider
            .RefundAsync(ProviderReference, 10.00m, CurrencyCode, Arg.Any<CancellationToken>())
            .Returns(ProviderRefundResult.Declined(ProviderRejectionReason));

        var refundRejectedByProvider = () => _commandHandler.Handle(
            new RefundPaymentCommand(PaymentId, 10.00m, RefundReason, RefundIdempotencyKey), CancellationToken.None);

        (await refundRejectedByProvider.Should().ThrowAsync<BadRequestException>())
            .Which.Detail.Should().Be(ProviderRejectionReason);
        _appendedBatches.Should().BeEmpty();
        _projectedModels.Should().BeEmpty();
    }

    [Fact]
    public async Task RefundExceedingTheCapturedAmountFailsBeforeReachingTheProvider()
    {
        StreamContainsCapturedPayment();

        var refundTooMuch = () => _commandHandler.Handle(
            new RefundPaymentCommand(PaymentId, CapturedAmount + 0.01m, RefundReason, RefundIdempotencyKey), CancellationToken.None);

        await refundTooMuch.Should().ThrowAsync<DomainException>();
        await _paymentProvider.DidNotReceiveWithAnyArgs().RefundAsync(default!, default, default!, default);
        _appendedBatches.Should().BeEmpty();
    }

    [Fact]
    public async Task RetriedRefundWithTheSameKeyReplaysTheRecordedOutcomeWithoutRefundingAgain()
    {
        _paymentEventStore
            .LoadStreamAsync(PaymentId, Arg.Any<CancellationToken>())
            .Returns(new IPaymentEvent[]
            {
                new PaymentInitiated(
                    PaymentId, 1, RefundInstant.AddMinutes(-5), OrderId, CustomerId, CapturedAmount, CurrencyCode, CardMethod),
                new PaymentAuthorized(PaymentId, 2, RefundInstant.AddMinutes(-4), ProviderReference),
                new PaymentCaptured(PaymentId, 3, RefundInstant.AddMinutes(-3)),
                new PaymentRefunded(PaymentId, 4, RefundInstant.AddMinutes(-2), 30.00m, RefundReason, RefundIdempotencyKey),
            });

        var refundResult = await _commandHandler.Handle(
            new RefundPaymentCommand(PaymentId, 30.00m, RefundReason, RefundIdempotencyKey), CancellationToken.None);

        refundResult.Status.Should().Be(PaymentStatus.PartiallyRefunded);
        refundResult.RefundedAmount.Should().Be(30.00m);
        await _paymentProvider.DidNotReceiveWithAnyArgs().RefundAsync(default!, default, default!, default);
        _appendedBatches.Should().BeEmpty();
        _projectedModels.Should().BeEmpty();
    }

    private void StreamContainsCapturedPayment() =>
        _paymentEventStore
            .LoadStreamAsync(PaymentId, Arg.Any<CancellationToken>())
            .Returns(new IPaymentEvent[]
            {
                new PaymentInitiated(
                    PaymentId, 1, RefundInstant.AddMinutes(-5), OrderId, CustomerId, CapturedAmount, CurrencyCode, CardMethod),
                new PaymentAuthorized(PaymentId, 2, RefundInstant.AddMinutes(-4), ProviderReference),
                new PaymentCaptured(PaymentId, 3, RefundInstant.AddMinutes(-3)),
            });

    private void ProviderApprovesRefund(decimal amount) =>
        _paymentProvider
            .RefundAsync(ProviderReference, amount, CurrencyCode, Arg.Any<CancellationToken>())
            .Returns(ProviderRefundResult.Approved());
}
