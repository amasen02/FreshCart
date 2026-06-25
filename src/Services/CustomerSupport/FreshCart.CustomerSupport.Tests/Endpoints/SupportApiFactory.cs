using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FreshCart.CustomerSupport.Api.Persistence;
using FreshCart.CustomerSupport.Tests.Support;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace FreshCart.CustomerSupport.Tests.Endpoints;

/// <summary>
/// Hosts the CustomerSupport API in-process for HTTP-boundary authorization tests. The read endpoints
/// touch only the two repositories, so the in-memory stubs replace the Mongo stores and the hosted
/// persistence initialiser is dropped: the host boots without a live Mongo or Redis (the Redis
/// multiplexer and SignalR backplane resolve lazily and are never exercised by these tests). The JWT
/// validation parameters are pinned directly so a token minted with the matching key always validates,
/// independent of how the configuration layers resolve under the test host.
/// </summary>
public sealed class SupportApiFactory : WebApplicationFactory<Program>
{
    private const string Issuer = "https://freshcart.test/identity";
    private const string Audience = "https://freshcart.test";
    private const string SigningKey = "customer-support-endpoint-integration-test-signing-key";

    public InMemoryChatSessionRepository Sessions { get; } = new();

    public InMemoryChatMessageRepository Messages { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["Jwt:SigningKey"] = SigningKey,
                ["ConnectionStrings:supportchatdb"] = "mongodb://localhost:27017/freshcart_support_endpoint_tests",
                ["ConnectionStrings:cache"] = "localhost:6379",
            }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IChatSessionRepository>();
            services.AddSingleton<IChatSessionRepository>(Sessions);
            services.RemoveAll<IChatMessageRepository>();
            services.AddSingleton<IChatMessageRepository>(Messages);

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, bearerOptions =>
            {
                bearerOptions.TokenValidationParameters.ValidIssuer = Issuer;
                bearerOptions.TokenValidationParameters.ValidAudience = Audience;
                bearerOptions.TokenValidationParameters.IssuerSigningKey =
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
            });
        });
    }

    public static string CreateAccessToken(Guid userId, string role)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, role),
        };

        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: TimeProvider.System.GetUtcNow().AddHours(1).UtcDateTime,
            signingCredentials: signingCredentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
