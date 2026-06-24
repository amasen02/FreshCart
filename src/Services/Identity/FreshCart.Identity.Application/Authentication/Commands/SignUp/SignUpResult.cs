namespace FreshCart.Identity.Application.Authentication.Commands.SignUp;

/// <summary>
/// Result returned after a successful sign-up. <see cref="AccessToken"/> and
/// <see cref="RefreshToken"/> are populated only when <see cref="SignUpCommand.SignInImmediately"/>
/// is true AND the caller requested JWT-mode credentials (mobile / service-to-service).
/// </summary>
public sealed record SignUpResult(
    Guid UserId,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    string? AccessToken,
    string? RefreshToken,
    DateTimeOffset? AccessTokenExpiresOnUtc);
