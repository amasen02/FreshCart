using FreshCart.Pricing.Grpc.Protos;
using FreshCart.ServiceDefaults;
using Grpc.Net.Client;
using Microsoft.Extensions.Hosting;

namespace FreshCart.Basket.Api.Pricing;

/// <summary>
/// Owns the single gRPC channel to the Pricing service. Grpc.Net.ClientFactory is not part of the
/// pinned package set, so this small factory fills the same role: one shared, lazily created
/// channel (channels multiplex requests and are safe to share) and cheap client instances on top.
/// </summary>
public sealed class PricingGrpcChannelFactory : IDisposable
{
    public const string AddressConfigurationKey = "Services:Pricing:Address";

    private readonly Lazy<GrpcChannel> lazyChannel;

    public PricingGrpcChannelFactory(IConfiguration configuration, IHostEnvironment hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        var pricingAddress = configuration[AddressConfigurationKey]
            ?? throw new InvalidOperationException($"Configuration value \"{AddressConfigurationKey}\" is required.");

        var channelOptions = new GrpcChannelOptions();
        if (hostEnvironment.IsDevelopment())
        {
            // Pricing presents the ASP.NET Core dev certificate over HTTPS locally; accept it on this
            // channel so the price call does not fail the SSL handshake. Production validates fully.
            channelOptions.HttpHandler = new SocketsHttpHandler
            {
                SslOptions =
                {
                    RemoteCertificateValidationCallback =
                        DevelopmentCertificateValidation.AcceptAspNetCoreDevelopmentCertificate,
                },
            };
        }

        lazyChannel = new Lazy<GrpcChannel>(
            () => GrpcChannel.ForAddress(pricingAddress, channelOptions),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public PricingService.PricingServiceClient CreateClient() => new(lazyChannel.Value);

    public void Dispose()
    {
        if (lazyChannel.IsValueCreated)
        {
            lazyChannel.Value.Dispose();
        }
    }
}
