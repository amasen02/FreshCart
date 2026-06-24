using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Carter;
using FreshCart.BuildingBlocks.Exceptions;
using FreshCart.Identity.Api.Configuration;
using FreshCart.Identity.Application.Authentication.Commands.RefreshAccessToken;
using FreshCart.Identity.Application.Authentication.Commands.SignIn;
using FreshCart.Identity.Application.Authentication.Commands.SignOut;
using FreshCart.Identity.Application.Authentication.Commands.SignUp;
using Mapster;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FreshCart.Identity.Api.Endpoints.Authentication;

/// <summary>
/// Carter module exposing sign-up / sign-in / sign-out / refresh endpoints. Each endpoint dispatches
/// to a MediatR command; the cookie scheme is set up by this layer because writing
/// <see cref="HttpContext"/> belongs at the HTTP boundary, not in handlers.
/// </summary>
public sealed class AuthenticationEndpoints : ICarterModule
{
    private static readonly TimeSpan SessionAbsoluteLifetime = TimeSpan.FromHours(8);

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var authenticationGroup = app.MapGroup("/auth").WithTags("Authentication");

        authenticationGroup.MapPost("/sign-up", SignUpAsync)
            .WithSummary("Create a new customer account.")
            .Produces<SignUpResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        authenticationGroup.MapPost("/sign-in", SignInAsync)
            .WithSummary("Authenticate with email and password.")
            .Produces<SignInResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        authenticationGroup.MapPost("/sign-out", SignOutAsync)
            .RequireAuthorization()
            .WithSummary("Invalidate the current session and revoke refresh tokens.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        authenticationGroup.MapPost("/refresh", RefreshAsync)
            .WithSummary("Exchange a refresh token for a new access token (JWT mode only).")
            .Produces<RefreshResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        authenticationGroup.MapGet("/anti-forgery-token", IssueAntiforgeryTokenAsync)
            .RequireAuthorization()
            .WithSummary("Refresh the XSRF cookie used by the Angular SPA.")
            .Produces(StatusCodes.Status204NoContent);
    }

    private static async Task<IResult> SignUpAsync(
        SignUpRequest signUpRequest,
        ISender mediator,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        var command = signUpRequest.Adapt<SignUpCommand>();
        var commandResult = await mediator.Send(command, cancellationToken).ConfigureAwait(false);

        if (signUpRequest.SignInImmediately && signUpRequest.UseCookie)
        {
            await SignInWithCookieAsync(
                    httpContext,
                    commandResult.UserId,
                    commandResult.Email,
                    commandResult.DisplayName,
                    commandResult.Roles)
                .ConfigureAwait(false);
            AntiforgeryConfiguration.IssueAntiforgeryCookie(httpContext, antiforgery);
        }

        var response = commandResult.Adapt<SignUpResponse>();
        return Results.Created($"/users/{commandResult.UserId}", response);
    }

    private static async Task<IResult> SignInAsync(
        SignInRequest signInRequest,
        ISender mediator,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        var command = signInRequest.Adapt<SignInCommand>();
        var commandResult = await mediator.Send(command, cancellationToken).ConfigureAwait(false);

        if (signInRequest.UseCookie)
        {
            await SignInWithCookieAsync(
                    httpContext,
                    commandResult.Profile.UserId,
                    commandResult.Profile.Email,
                    commandResult.Profile.DisplayName,
                    commandResult.Profile.Roles)
                .ConfigureAwait(false);

            AntiforgeryConfiguration.IssueAntiforgeryCookie(httpContext, antiforgery);
        }

        var response = commandResult.Adapt<SignInResponse>();
        return Results.Ok(response);
    }

    private static async Task<IResult> SignOutAsync(
        ISender mediator,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        await AntiforgeryConfiguration.ValidateBrowserRequestAsync(httpContext, antiforgery).ConfigureAwait(false);

        var subject = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.User.FindFirstValue("sub")
            ?? throw new ForbiddenException("Cannot identify the current user.");

        if (!Guid.TryParse(subject, out var currentUserId))
        {
            throw new ForbiddenException("Authenticated subject is not a valid user identifier.");
        }

        await mediator.Send(new SignOutCommand(currentUserId), cancellationToken).ConfigureAwait(false);

        await httpContext
            .SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
            .ConfigureAwait(false);

        httpContext.Response.Cookies.Delete(AntiforgeryConfiguration.ClientReadableCookieName);

        return Results.NoContent();
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest refreshRequest,
        ISender mediator,
        CancellationToken cancellationToken)
    {
        var command = new RefreshAccessTokenCommand(refreshRequest.RefreshToken);
        var commandResult = await mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Results.Ok(commandResult.Adapt<RefreshResponse>());
    }

    private static Task<IResult> IssueAntiforgeryTokenAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery)
    {
        AntiforgeryConfiguration.IssueAntiforgeryCookie(httpContext, antiforgery);
        return Task.FromResult(Results.NoContent());
    }

    private static Task SignInWithCookieAsync(
        HttpContext httpContext,
        Guid userId,
        string email,
        string displayName,
        IReadOnlyCollection<string>? roles = null)
    {
        var authenticatedOnUtc = DateTimeOffset.UtcNow;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("sub", userId.ToString()),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Name, displayName),

            // The gateway keys its downstream-token cache on this instant so a re-authenticated
            // session cannot replay a token minted for the prior session. It rides on the principal
            // because the gateway only ever sees the decrypted claims, never the ticket properties.
            new(JwtRegisteredClaimNames.AuthTime, authenticatedOnUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        };

        if (roles is not null)
        {
            foreach (var roleName in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, roleName));
            }
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var authenticationProperties = new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = true,
            IssuedUtc = authenticatedOnUtc,
            ExpiresUtc = authenticatedOnUtc.Add(SessionAbsoluteLifetime),
        };

        return httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            authenticationProperties);
    }
}
