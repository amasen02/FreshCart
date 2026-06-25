using System.Globalization;
using FluentAssertions;
using FreshCart.BuildingBlocks.Exceptions;
using FreshCart.Payment.Domain.Events;
using FreshCart.Payment.Infrastructure.EventStore;
using FreshCart.Payment.Tests.Common;
using MongoDB.Driver;
using Xunit;

namespace FreshCart.Payment.Tests.Infrastructure;

[Collection(MongoEventStoreFixture.CollectionName)]
public sealed class MongoPaymentEventStoreTests : IDisposable
{
    private static readonly Guid OrderId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid CustomerId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly DateTimeOffset FirstEventInstant = new(2026, 6, 3, 8, 0, 0, TimeSpan.Zero);

    private const decimal Amount = 120.50m;
    private const string CurrencyCode = "GBP";
    private const string CardMethod = "card";
    private const string ProviderReference = "SIM-TEST-REFERENCE";
    private const string RefundReason = "Order arrived after the promised delivery slot.";

    private readonly MongoClient _mongoClient;
    private readonly MongoPaymentEventStore _eventStore;

    public MongoPaymentEventStoreTests(MongoEventStoreFixture mongoFixture)
    {
        ArgumentNullException.ThrowIfNull(mongoFixture);

        _mongoClient = new MongoClient(mongoFixture.ConnectionString);

        // Every test class instance gets its own database so xunit's parallel test runs can never
        // observe each other's streams or index state.
        var isolatedDatabaseName = $"paymentevents_{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}";
        _eventStore = new MongoPaymentEventStore(_mongoClient.GetDatabase(isolatedDatabaseName));
    }

    public void Dispose() => _mongoClient.Dispose();

    [Fact]
    public async Task AppendedStreamLoadsBackInOrderWithIdenticalEventData()
    {
        await _eventStore.EnsureIndexesAsync(CancellationToken.None);
        var paymentId = Guid.NewGuid();
        IPaymentEvent[] writtenStream =
        [
            InitiatedEvent(paymentId),
            new PaymentAuthorized(paymentId, 2, FirstEventInstant.AddSeconds(1), ProviderReference),
            new PaymentCaptured(paymentId, 3, FirstEventInstant.AddSeconds(2)),
            new PaymentRefunded(paymentId, 4, FirstEventInstant.AddMinutes(5), 20.00m, RefundReason, paymentId.ToString()),
        ];

        await _eventStore.AppendAsync(paymentId, expectedVersion: 0, writtenStream, CancellationToken.None);
        var loadedStream = await _eventStore.LoadStreamAsync(paymentId, CancellationToken.None);

        loadedStream.Should().Equal(writtenStream);
    }

    [Fact]
    public async Task LoadingAStreamThatWasNeverWrittenReturnsAnEmptyList()
    {
        await _eventStore.EnsureIndexesAsync(CancellationToken.None);

        var loadedStream = await _eventStore.LoadStreamAsync(Guid.NewGuid(), CancellationToken.None);

        loadedStream.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentAppendOfTheSameVersionThrowsConflict()
    {
        await _eventStore.EnsureIndexesAsync(CancellationToken.None);
        var paymentId = Guid.NewGuid();
        await _eventStore.AppendAsync(paymentId, expectedVersion: 0, [InitiatedEvent(paymentId)], CancellationToken.None);

        var appendSameVersionAgain = () => _eventStore.AppendAsync(
            paymentId,
            expectedVersion: 0,
            [InitiatedEvent(paymentId)],
            CancellationToken.None);

        (await appendSameVersionAgain.Should().ThrowAsync<ConflictException>())
            .Which.Message.Should().Contain("modified concurrently");
    }

    [Fact]
    public async Task StaleWriterLosingTheRaceOnALaterVersionGetsAConflict()
    {
        await _eventStore.EnsureIndexesAsync(CancellationToken.None);
        var paymentId = Guid.NewGuid();
        await _eventStore.AppendAsync(paymentId, expectedVersion: 0, [InitiatedEvent(paymentId)], CancellationToken.None);
        var winningAuthorization = new PaymentAuthorized(paymentId, 2, FirstEventInstant.AddSeconds(1), ProviderReference);
        await _eventStore.AppendAsync(paymentId, expectedVersion: 1, [winningAuthorization], CancellationToken.None);

        var staleDecline = () => _eventStore.AppendAsync(
            paymentId,
            expectedVersion: 1,
            [new PaymentDeclined(paymentId, 2, FirstEventInstant.AddSeconds(1), "Stale writer decline.")],
            CancellationToken.None);

        await staleDecline.Should().ThrowAsync<ConflictException>();

        var survivingStream = await _eventStore.LoadStreamAsync(paymentId, CancellationToken.None);
        survivingStream.Should().HaveCount(2);
        survivingStream[1].Should().Be(winningAuthorization);
    }

    [Fact]
    public Task AppendingAnEmptyBatchIsRejected()
    {
        var appendNothing = () => _eventStore.AppendAsync(
            Guid.NewGuid(), expectedVersion: 0, [], CancellationToken.None);

        return appendNothing.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AppendingEventsThatDoNotContinueTheExpectedVersionIsRejected()
    {
        var paymentId = Guid.NewGuid();
        var eventSkippingAVersion = new PaymentAuthorized(paymentId, 3, FirstEventInstant, ProviderReference);

        var appendWithGap = () => _eventStore.AppendAsync(
            paymentId, expectedVersion: 1, [eventSkippingAVersion], CancellationToken.None);

        (await appendWithGap.Should().ThrowAsync<ArgumentException>())
            .Which.Message.Should().Contain("does not continue stream");
    }

    [Fact]
    public Task AppendingAnEventBelongingToAnotherStreamIsRejected()
    {
        var paymentId = Guid.NewGuid();
        var foreignEvent = InitiatedEvent(Guid.NewGuid());

        var appendForeignEvent = () => _eventStore.AppendAsync(
            paymentId, expectedVersion: 0, [foreignEvent], CancellationToken.None);

        return appendForeignEvent.Should().ThrowAsync<ArgumentException>();
    }

    private static PaymentInitiated InitiatedEvent(Guid paymentId) => new(
        paymentId, 1, FirstEventInstant, OrderId, CustomerId, Amount, CurrencyCode, CardMethod);
}
