using FreshCart.Pricing.Grpc.Persistence;
using FreshCart.Pricing.Grpc.Protos;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FreshCart.Pricing.Tests.Integration;

/// <summary>
/// Boots the Pricing host in-process for gRPC and REST boundary tests. The development environment
/// supplies the JWT and connection-string configuration the host reads at startup; the file-backed SQLite
/// store is swapped for an isolated in-memory connection held open for the host's lifetime, so the hosted
/// initializer still creates the schema and seeds the development coupons but no test touches a real file.
/// </summary>
public sealed class PricingApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection sqliteConnection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        sqliteConnection.Open();

        builder.ConfigureServices(services =>
        {
            var optionsDescriptor = services.SingleOrDefault(
                descriptor => descriptor.ServiceType == typeof(DbContextOptions<PricingDbContext>));
            if (optionsDescriptor is not null)
            {
                services.Remove(optionsDescriptor);
            }

            services.AddDbContext<PricingDbContext>(options => options.UseSqlite(sqliteConnection));
        });
    }

    public PricingService.PricingServiceClient CreateGrpcClient()
    {
        // gRPC over the in-process TestServer: the handler rewrites the response version to match the
        // request so Grpc.Net.Client accepts the HTTP/2 reply the TestServer does not stamp itself.
        var httpClient = CreateDefaultClient(new ResponseVersionHandler());
        var channel = GrpcChannel.ForAddress(
            httpClient.BaseAddress!,
            new GrpcChannelOptions { HttpClient = httpClient });

        return new PricingService.PricingServiceClient(channel);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            sqliteConnection.Dispose();
        }
    }

    private sealed class ResponseVersionHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.Version = request.Version;
            return response;
        }
    }
}
