using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FreshCart.Pricing.Grpc.Protos;
using Grpc.Core;
using Xunit;

namespace FreshCart.Pricing.Tests.Integration;

/// <summary>
/// Boundary tests that boot the Pricing host and exercise the gRPC calculator end-to-end over a real
/// channel, plus the REST authorization edge — the integration coverage PR-01 was missing. The
/// development coupon seed (WELCOME10: 10% off, minimum order 20) is the reference data under test.
/// </summary>
public sealed class PricingHostIntegrationTests(PricingApiFactory factory) : IClassFixture<PricingApiFactory>
{
    private const string SeededPercentageCoupon = "WELCOME10";

    [Fact]
    public async Task PriceBasketReturnsSubtotalTaxAndGrandTotalWithNoDiscount()
    {
        var client = factory.CreateGrpcClient();

        var response = await client.PriceBasketAsync(BuildTwoLineBasket(couponCode: null));

        Money(response.Subtotal).Should().Be(36.50m);
        Money(response.DiscountTotal).Should().Be(0m);
        Money(response.TaxTotal).Should().Be(2.92m, "the default tax rate is 8%");
        Money(response.GrandTotal).Should().Be(39.42m);
        response.AppliedCoupon.Should().BeEmpty();
        response.Lines.Should().HaveCount(2);
    }

    [Fact]
    public async Task PriceBasketAppliesASeededPercentageCoupon()
    {
        var client = factory.CreateGrpcClient();

        var response = await client.PriceBasketAsync(BuildTwoLineBasket(SeededPercentageCoupon));

        response.AppliedCoupon.Should().Be(SeededPercentageCoupon);
        Money(response.Subtotal).Should().Be(36.50m);
        Money(response.DiscountTotal).Should().Be(3.65m, "WELCOME10 takes 10% off the 36.50 subtotal");
        Money(response.TaxTotal).Should().Be(2.63m, "tax is charged on the post-coupon base of 32.85");
        Money(response.GrandTotal).Should().Be(35.48m);
    }

    [Fact]
    public async Task PriceBasketRejectsAnEmptyBasketWithInvalidArgument()
    {
        var client = factory.CreateGrpcClient();
        var emptyBasket = new PriceBasketRequest
        {
            CustomerId = Guid.NewGuid().ToString(),
            CurrencyCode = "USD",
        };

        var priceEmptyBasket = async () => await client.PriceBasketAsync(emptyBasket);

        (await priceEmptyBasket.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ValidateCouponReturnsTheTypeAndValueForASeededCoupon()
    {
        var client = factory.CreateGrpcClient();

        var response = await client.ValidateCouponAsync(new ValidateCouponRequest
        {
            CouponCode = SeededPercentageCoupon,
            CustomerId = Guid.NewGuid().ToString(),
            OrderSubtotal = "50.00",
        });

        response.IsValid.Should().BeTrue();
        response.DiscountType.Should().Be("Percentage");
        Money(response.DiscountValue).Should().Be(10m);
    }

    [Fact]
    public async Task ValidateCouponReturnsInvalidForAnUnknownCode()
    {
        var client = factory.CreateGrpcClient();

        var response = await client.ValidateCouponAsync(new ValidateCouponRequest
        {
            CouponCode = "NO-SUCH-COUPON",
            CustomerId = Guid.NewGuid().ToString(),
            OrderSubtotal = "50.00",
        });

        response.IsValid.Should().BeFalse();
        response.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateCouponRejectsAnAnonymousCallerWith401()
    {
        var httpClient = factory.CreateClient();

        var response = await httpClient.PostAsJsonAsync("/pricing/coupons", new
        {
            code = "ANON",
            discountType = "Percentage",
            discountValue = 5m,
            validFromUtc = DateTimeOffset.UtcNow,
            validToUtc = DateTimeOffset.UtcNow.AddDays(1),
            isActive = true,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static PriceBasketRequest BuildTwoLineBasket(string? couponCode)
    {
        var request = new PriceBasketRequest
        {
            CustomerId = Guid.NewGuid().ToString(),
            CurrencyCode = "USD",
            CouponCode = couponCode ?? string.Empty,
        };
        request.Lines.Add(new PriceBasketLine
        {
            ProductId = Guid.NewGuid().ToString(),
            ProductSku = "FC-A",
            UnitPrice = "10.00",
            Quantity = 2,
        });
        request.Lines.Add(new PriceBasketLine
        {
            ProductId = Guid.NewGuid().ToString(),
            ProductSku = "FC-B",
            UnitPrice = "5.50",
            Quantity = 3,
        });

        return request;
    }

    private static decimal Money(string wireValue) => decimal.Parse(wireValue, CultureInfo.InvariantCulture);
}
