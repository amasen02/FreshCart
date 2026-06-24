using FreshCart.BuildingBlocks.CQRS;
using FreshCart.BuildingBlocks.Exceptions;
using FreshCart.Identity.Application.Common.Abstractions;
using FreshCart.Identity.Domain.AuditEvents;
using FreshCart.Identity.Domain.Roles;
using FreshCart.Identity.Domain.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace FreshCart.Identity.Application.Authentication.Commands.SignUp;

public sealed class SignUpCommandHandler(
    UserManager<ApplicationUser> userManager,
    IAccessTokenIssuer accessTokenIssuer,
    IRefreshTokenService refreshTokenService,
    IIdentityAuditLog auditLog,
    ICurrentRequestContext currentRequest,
    ILogger<SignUpCommandHandler> logger)
    : ICommandHandler<SignUpCommand, SignUpResult>
{
    public async Task<SignUpResult> Handle(SignUpCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (await userManager.FindByEmailAsync(command.Email).ConfigureAwait(false) is not null)
        {
            // Generic message keeps us from leaking which addresses are registered.
            throw new ConflictException("An account with the supplied email already exists.");
        }

        var newUser = new ApplicationUser
        {
            UserName = command.Email,
            Email = command.Email,
            DisplayName = command.DisplayName.Trim(),
            MarketingConsent = command.MarketingConsent,
            EmailConfirmed = false,
        };

        var createResult = await userManager.CreateAsync(newUser, command.Password).ConfigureAwait(false);
        if (!createResult.Succeeded)
        {
            var firstError = createResult.Errors.FirstOrDefault();
            throw new BadRequestException(
                "Sign-up failed.",
                firstError?.Description ?? "The account could not be created.");
        }

        var addRoleResult = await userManager.AddToRoleAsync(newUser, CanonicalRoles.Customer).ConfigureAwait(false);
        if (!addRoleResult.Succeeded)
        {
            logger.LogError(
                "Failed to assign role {Role} to user {UserId}: {Errors}",
                CanonicalRoles.Customer,
                newUser.Id,
                string.Join(", ", addRoleResult.Errors.Select(error => error.Description)));
            throw new InternalServerException("Account created but role assignment failed; contact support.");
        }

        await RecordSignUpAuditEventAsync(newUser, cancellationToken).ConfigureAwait(false);

        // Surfaced on the result so the API can seed the session cookie with the same roles, otherwise a
        // just-signed-up browser session carries no role and every RequireRole endpoint returns 403.
        var assignedRoles = (await userManager.GetRolesAsync(newUser).ConfigureAwait(false)).ToArray();

        if (!command.SignInImmediately)
        {
            return new SignUpResult(newUser.Id, newUser.Email!, newUser.DisplayName, assignedRoles, null, null, null);
        }

        return await IssueImmediateCredentialsAsync(newUser, assignedRoles, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SignUpResult> IssueImmediateCredentialsAsync(
        ApplicationUser newUser,
        IReadOnlyList<string> assignedRoles,
        CancellationToken cancellationToken)
    {
        var accessToken = accessTokenIssuer.Issue(newUser, assignedRoles);
        var refreshToken = await refreshTokenService
            .IssueAsync(newUser.Id, currentRequest.IpAddress, currentRequest.UserAgent, cancellationToken)
            .ConfigureAwait(false);

        return new SignUpResult(
            newUser.Id,
            newUser.Email!,
            newUser.DisplayName,
            assignedRoles,
            accessToken.AccessToken,
            refreshToken.PlaintextToken,
            accessToken.ExpiresOnUtc);
    }

    private Task RecordSignUpAuditEventAsync(ApplicationUser user, CancellationToken cancellationToken) =>
        auditLog.RecordAsync(
            new AuditEvent
            {
                EventType = AuditEventType.SignUpSucceeded,
                UserId = user.Id,
                Description = $"User {user.Email} signed up.",
                IpAddress = currentRequest.IpAddress,
                UserAgent = currentRequest.UserAgent,
                CorrelationId = currentRequest.CorrelationId,
            },
            cancellationToken);
}
