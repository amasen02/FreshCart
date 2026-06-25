using System.Net.Http.Json;
using FreshCart.BuildingBlocks.Exceptions;
using FreshCart.Ordering.Application.Abstractions;

namespace FreshCart.Ordering.Infrastructure.Payment;

/// <summary>
/// Adapter from the <see cref="IPaymentClient"/> port to the Payment REST API. The order id is sent
/// as the idempotency key so a retried capture never double-charges the customer. A declined card is
/// a business result; a malformed or missing response body is a contract fault and throws so the
/// saga's retry policy can react.
/// </summary>
public sealed class HttpPaymentClient(HttpClient httpClient) : IPaymentClient
{
    public const string IdempotencyKeyHeaderName = "Idempotency-Key";

    private const string CapturePath = "/payments";

    // The Payment service reports the outcome as its PaymentStatus string; these are the success values.
    private const string CapturedStatus = "Captured";
    private static readonly string[] RefundedStatuses = ["Refunded", "PartiallyRefunded"];

    public async Task<PaymentCaptureResult> CapturePaymentAsync(
        PaymentCaptureRequest captureRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(captureRequest);

        var payload = new PaymentCaptureRequestPayload(
            captureRequest.OrderId,
            captureRequest.CustomerId,
            captureRequest.Amount,
            captureRequest.CurrencyCode,
            captureRequest.PaymentMethod);

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, CapturePath)
        {
            Content = JsonContent.Create(payload),
        };
        requestMessage.Headers.Add(IdempotencyKeyHeaderName, captureRequest.OrderId.ToString());

        using var responseMessage = await httpClient
            .SendAsync(requestMessage, cancellationToken)
            .ConfigureAwait(false);

        responseMessage.EnsureSuccessStatusCode();

        var response = await responseMessage.Content
            .ReadFromJsonAsync<PaymentCaptureResponsePayload>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InternalServerException(
                $"Payment service returned an empty body when capturing order {captureRequest.OrderId}.");

        var captureSucceeded = string.Equals(response.Status, CapturedStatus, StringComparison.OrdinalIgnoreCase);
        return new PaymentCaptureResult(captureSucceeded, response.PaymentId, response.FailureReason);
    }

    public async Task<PaymentRefundResult> RefundPaymentAsync(
        PaymentRefundRequest refundRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refundRequest);

        var refundPath = $"/payments/{refundRequest.PaymentId}/refunds";

        var payload = new PaymentRefundRequestPayload(refundRequest.Amount, refundRequest.Reason);

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, refundPath)
        {
            Content = JsonContent.Create(payload),
        };
        // One order is refunded in full exactly once, so the payment id is a stable idempotency key:
        // a retried refund (e.g. after the order's own save failed) reuses it and Payment replays the
        // recorded outcome instead of refunding the customer a second time.
        requestMessage.Headers.Add(IdempotencyKeyHeaderName, refundRequest.PaymentId.ToString());

        using var responseMessage = await httpClient
            .SendAsync(requestMessage, cancellationToken)
            .ConfigureAwait(false);

        responseMessage.EnsureSuccessStatusCode();

        var response = await responseMessage.Content
            .ReadFromJsonAsync<PaymentRefundResponsePayload>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InternalServerException(
                $"Payment service returned an empty body when refunding payment {refundRequest.PaymentId}.");

        var refundSucceeded = RefundedStatuses.Contains(response.Status, StringComparer.OrdinalIgnoreCase);
        return new PaymentRefundResult(refundSucceeded, response.RefundedAmount, FailureReason: null);
    }
}
