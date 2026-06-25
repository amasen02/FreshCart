using Carter;
using FreshCart.BuildingBlocks.Exceptions;
using FreshCart.BuildingBlocks.Pagination;
using FreshCart.BuildingBlocks.Security;
using FreshCart.Payment.Application.Payments.Commands.CapturePayment;
using FreshCart.Payment.Application.Payments.Commands.RefundPayment;
using FreshCart.Payment.Application.Payments.Queries.GetPaymentByOrderId;
using FreshCart.Payment.Application.Payments.Queries.GetPayments;
using MediatR;

namespace FreshCart.Payment.Api.Endpoints;

public sealed class PaymentEndpoints : ICarterModule
{
    private const string IdempotencyKeyHeaderName = "Idempotency-Key";

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var paymentsGroup = app.MapGroup("/payments").WithTags("Payments");

        paymentsGroup.MapPost("/", CapturePaymentAsync)
            .RequireAuthorization(ServiceAuthenticationDefaults.ServiceCallerPolicy)
            .WithSummary("Authorize and capture a payment for an order. Service-to-service only (the Ordering saga). Idempotent per order; a declined card returns 200 with a Declined status.")
            .Produces<PaymentResultDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);

        paymentsGroup.MapPost("/{paymentId:guid}/refunds", RefundPaymentAsync)
            .RequireAuthorization(ServiceAuthenticationDefaults.ServiceCallerPolicy)
            .WithSummary("Refund a captured payment, fully or partially. Service-to-service only (the Ordering refund flow); the human-administrator check is enforced at the Ordering edge. Idempotent per Idempotency-Key.")
            .Produces<RefundResultDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        paymentsGroup.MapGet("/order/{orderId:guid}", GetPaymentByOrderIdAsync)
            .RequireAuthorization(AuthorizationPolicies.Administrator)
            .WithSummary("Payment recorded for an order.")
            .Produces<PaymentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        paymentsGroup.MapGet("/", GetPaymentsPageAsync)
            .RequireAuthorization(AuthorizationPolicies.Administrator)
            .WithSummary("All payments, paginated and ordered by creation time descending.")
            .Produces<PaginatedResult<PaymentResponse>>(StatusCodes.Status200OK);
    }

    private static async Task<IResult> CapturePaymentAsync(
        CapturePaymentRequest captureRequest,
        HttpContext httpContext,
        ISender mediator,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = httpContext.Request.Headers[IdempotencyKeyHeaderName].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new BadRequestException($"The {IdempotencyKeyHeaderName} header is required.");
        }

        var command = new CapturePaymentCommand(
            captureRequest.OrderId,
            captureRequest.CustomerId,
            captureRequest.Amount,
            captureRequest.CurrencyCode,
            captureRequest.Method,
            idempotencyKey);

        var commandResult = await mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Results.Ok(PaymentResultDto.FromCommandResult(commandResult));
    }

    private static async Task<IResult> RefundPaymentAsync(
        Guid paymentId,
        RefundPaymentRequest refundRequest,
        HttpContext httpContext,
        ISender mediator,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = httpContext.Request.Headers[IdempotencyKeyHeaderName].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new BadRequestException($"The {IdempotencyKeyHeaderName} header is required.");
        }

        var command = new RefundPaymentCommand(paymentId, refundRequest.Amount, refundRequest.Reason, idempotencyKey);
        var commandResult = await mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Results.Ok(RefundResultDto.FromCommandResult(commandResult));
    }

    private static async Task<IResult> GetPaymentByOrderIdAsync(
        Guid orderId,
        ISender mediator,
        CancellationToken cancellationToken)
    {
        var payment = await mediator
            .Send(new GetPaymentByOrderIdQuery(orderId), cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(PaymentResponse.FromReadModel(payment));
    }

    private static async Task<IResult> GetPaymentsPageAsync(
        [AsParameters] PaginationRequest paginationRequest,
        ISender mediator,
        CancellationToken cancellationToken)
    {
        var paymentsPage = await mediator
            .Send(new GetPaymentsQuery(paginationRequest), cancellationToken)
            .ConfigureAwait(false);

        var responsePage = new PaginatedResult<PaymentResponse>(
            paymentsPage.PageNumber,
            paymentsPage.PageSize,
            paymentsPage.TotalItemCount,
            paymentsPage.Items.Select(PaymentResponse.FromReadModel).ToList());

        return Results.Ok(responsePage);
    }
}
