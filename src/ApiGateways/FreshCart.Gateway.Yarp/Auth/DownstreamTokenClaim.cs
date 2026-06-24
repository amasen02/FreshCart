using System.Security.Claims;

namespace FreshCart.Gateway.Yarp.Auth;

/// <summary>
/// Claim type names placed on the downstream bearer token. They mirror what the Identity service
/// writes onto the session cookie so a downstream service reads identical claims whether the original
/// caller used a cookie (browser, via the gateway) or a bearer token (programmatic client).
/// </summary>
public static class DownstreamTokenClaim
{
    public const string Subject = "sub";

    public const string Email = "email";

    public const string DisplayName = "display_name";

    // Roles must use the same claim type the Identity JWT issuer emits (ClaimTypes.Role), because the
    // services authorize with RequireRole, whose default RoleClaimType is ClaimTypes.Role and which —
    // unlike the subject/email readers — does not fall back to the short "role" name. Emitting the
    // short name here previously made every RequireRole check fail (HTTP 403) for browser callers.
    public const string Role = ClaimTypes.Role;
}
