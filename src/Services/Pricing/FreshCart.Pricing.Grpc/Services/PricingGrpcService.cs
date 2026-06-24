using FreshCart.Pricing.Grpc.Protos;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;

namespace FreshCart.Pricing.Grpc.Services;

/// <summary>
/// gRPC facade over plain service classes. Pricing is a stateless calculator on admin-managed
/// reference data, so this service deliberately uses no CQRS, no MediatR and no integration
/// events: that ceremony would add indirection to the hottest synchronous path in the system
/// without buying anything.
/// </summary>
// Anonymous on purpose: this gRPC calculator is internal (never routed through the public gateway),
// exposes no PII and performs no mutation, and is called both from authenticated request handlers and
// from the price-changed message consumer — contexts that carry no single common credential. A blanket
// [Authorize] here required a bearer the Basket caller never sent, which 401'd the integrated price call.
[AllowAnonymous]
public sealed class PricingGrpcService(
    BasketPriceCalculator basketPriceCalculator,
    CouponValidator couponValidator) : PricingService.PricingServiceBase
{
    public override async Task<PriceBasketResponse> PriceBasket(PriceBasketRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var pricingRequest = MapPricingRequest(request);
        var priceResult = await basketPriceCalculator
            .CalculateAsync(pricingRequest, context.CancellationToken)
            .ConfigureAwait(false);

        var response = new PriceBasketResponse
        {
            Subtotal = MoneyWireFormat.ToWire(priceResult.Subtotal),
            DiscountTotal = MoneyWireFormat.ToWire(priceResult.DiscountTotal),
            TaxTotal = MoneyWireFormat.ToWire(priceResult.TaxTotal),
            GrandTotal = MoneyWireFormat.ToWire(priceResult.GrandTotal),
            AppliedCoupon = priceResult.AppliedCouponCode ?? string.Empty,
        };

        foreach (var pricedLine in priceResult.Lines)
        {
            response.Lines.Add(new PricedLine
            {
                ProductId = pricedLine.ProductId.ToString(),
                UnitPrice = MoneyWireFormat.ToWire(pricedLine.UnitPrice),
                DiscountedUnitPrice = MoneyWireFormat.ToWire(pricedLine.DiscountedUnitPrice),
                LineTotal = MoneyWireFormat.ToWire(pricedLine.LineTotal),
            });
        }

        return response;
    }

    public override async Task<ValidateCouponResponse> ValidateCoupon(ValidateCouponRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        _ = ParseRequiredGuid(request.CustomerId, "customer_id");

        if (string.IsNullOrWhiteSpace(request.CouponCode))
        {
            throw InvalidArgument("Field 'coupon_code' must not be empty.");
        }

        var orderSubtotal = MoneyWireFormat.Parse(request.OrderSubtotal, "order_subtotal");

        var couponValidation = await couponValidator
            .ValidateAsync(request.CouponCode, orderSubtotal, context.CancellationToken)
            .ConfigureAwait(false);

        return couponValidation.IsValid
            ? new ValidateCouponResponse
            {
                IsValid = true,
                DiscountValue = MoneyWireFormat.ToWire(couponValidation.DiscountValue),
                DiscountType = couponValidation.DiscountType.ToString(),
            }
            : new ValidateCouponResponse
            {
                IsValid = false,
                ErrorMessage = couponValidation.ErrorMessage ?? string.Empty,
            };
    }

    private static BasketPricingRequest MapPricingRequest(PriceBasketRequest request)
    {
        var customerId = ParseRequiredGuid(request.CustomerId, "customer_id");

        if (string.IsNullOrWhiteSpace(request.CurrencyCode))
        {
            throw InvalidArgument("Field 'currency_code' must not be empty.");
        }

        if (request.Lines.Count == 0)
        {
            throw InvalidArgument("Field 'lines' must contain at least one line.");
        }

        var basketLines = new List<BasketPriceLine>(request.Lines.Count);
        foreach (var requestLine in request.Lines)
        {
            if (string.IsNullOrWhiteSpace(requestLine.ProductSku))
            {
                throw InvalidArgument("Field 'product_sku' must not be empty.");
            }

            if (requestLine.Quantity <= 0)
            {
                throw InvalidArgument("Field 'quantity' must be greater than zero.");
            }

            var unitPrice = MoneyWireFormat.Parse(requestLine.UnitPrice, "unit_price");
            if (unitPrice < 0m)
            {
                throw InvalidArgument("Field 'unit_price' must not be negative.");
            }

            basketLines.Add(new BasketPriceLine(
                ParseRequiredGuid(requestLine.ProductId, "product_id"),
                requestLine.ProductSku,
                unitPrice,
                requestLine.Quantity));
        }

        var couponCode = string.IsNullOrWhiteSpace(request.CouponCode) ? null : request.CouponCode;

        return new BasketPricingRequest(customerId, couponCode, request.CurrencyCode, basketLines);
    }

    private static Guid ParseRequiredGuid(string value, string fieldName)
    {
        if (!Guid.TryParse(value, out var parsedGuid) || parsedGuid == Guid.Empty)
        {
            throw InvalidArgument($"Field '{fieldName}' must be a non-empty GUID.");
        }

        return parsedGuid;
    }

    private static RpcException InvalidArgument(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));
}
