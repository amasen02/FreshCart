using Carter;
using FreshCart.BuildingBlocks.Exceptions;
using FreshCart.Reporting.Api.Authentication;
using FreshCart.Reporting.Application.Common.Abstractions;
using FreshCart.Reporting.Application.Invoices.Commands.GenerateInvoice;
using FreshCart.Reporting.Application.Invoices.Queries.DownloadInvoice;
using FreshCart.Reporting.Domain.Invoices;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace FreshCart.Reporting.Api.Endpoints.Invoices;

public sealed class InvoiceEndpoints : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var invoicesGroup = app
            .MapGroup("/invoices")
            .RequireAuthorization()
            .WithTags("Invoices");

        invoicesGroup.MapPost("/", GenerateAsync)
            .RequireAuthorization(AuthorizationPolicies.BackOfficeUser)
            .WithSummary("Generate an invoice PDF for a confirmed order (idempotent).")
            .Produces<GenerateInvoiceResult>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // Invoices carry customer PII and are numbered gap-free (trivially enumerable), so both read
        // routes require the back-office role rather than just any authenticated session (closing the
        // BOLA the bare group-level RequireAuthorization left open).
        invoicesGroup.MapGet("/{invoiceNumber}", GetSignedUrlAsync)
            .RequireAuthorization(AuthorizationPolicies.BackOfficeUser)
            .WithSummary("Return a short-lived signed URL the caller can use to download the invoice PDF.")
            .Produces<DownloadInvoiceResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        invoicesGroup.MapGet("/{invoiceNumber}/content.pdf", StreamPdfAsync)
            .RequireAuthorization(AuthorizationPolicies.BackOfficeUser)
            .WithSummary("Stream the invoice PDF directly (used by the SPA when a SAS URL is not desired).")
            .Produces<FileStreamHttpResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GenerateAsync(
        GenerateInvoiceRequest generateRequest,
        ISender mediator,
        CancellationToken cancellationToken)
    {
        var command = new GenerateInvoiceCommand(
            OrderId: generateRequest.OrderId,
            Kind: generateRequest.Kind,
            CustomerEmail: generateRequest.CustomerEmail,
            CustomerDisplayName: generateRequest.CustomerDisplayName,
            CustomerId: generateRequest.CustomerId,
            BillingAddress: generateRequest.BillingAddress,
            ShippingAddress: generateRequest.ShippingAddress,
            Lines: generateRequest.Lines,
            DiscountTotal: generateRequest.DiscountTotal,
            TaxTotal: generateRequest.TaxTotal,
            ShippingTotal: generateRequest.ShippingTotal,
            CurrencyCode: generateRequest.CurrencyCode ?? "USD",
            OriginalInvoiceNumber: generateRequest.OriginalInvoiceNumber,
            Notes: generateRequest.Notes);

        var commandResult = await mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Results.Created($"/invoices/{commandResult.InvoiceNumber}", commandResult);
    }

    private static async Task<IResult> GetSignedUrlAsync(
        string invoiceNumber,
        ISender mediator,
        CancellationToken cancellationToken)
    {
        var queryResult = await mediator
            .Send(new DownloadInvoiceQuery(invoiceNumber), cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(queryResult);
    }

    private static async Task<IResult> StreamPdfAsync(
        string invoiceNumber,
        IDocumentStore documentStore,
        CancellationToken cancellationToken)
    {
        // Validate before the value reaches the blob path: TryParse only accepts the canonical
        // INV/CR/PF-YYYY-NNNNNN shape, which rejects path-traversal input, and the normalised
        // parsed.Value (never the raw route string) is what addresses the blob.
        if (!InvoiceNumber.TryParse(invoiceNumber, out var parsedInvoiceNumber))
        {
            throw new BadRequestException($"\"{invoiceNumber}\" is not a valid invoice number.");
        }

        var blobName = $"{parsedInvoiceNumber.Value}.pdf";
        var contentStream = await documentStore
            .OpenReadAsync("invoices", blobName, cancellationToken)
            .ConfigureAwait(false);

        return Results.Stream(contentStream, contentType: "application/pdf", fileDownloadName: blobName);
    }
}
