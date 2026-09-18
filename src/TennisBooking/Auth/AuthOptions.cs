namespace TennisBooking.Auth;

/// <summary>
/// Generic OpenID Connect settings. Provider-agnostic: point <see cref="Authority"/>
/// at Pocket ID today and at any other OIDC issuer tomorrow without code changes.
/// All values can also come from environment variables, e.g. Auth__Authority,
/// Auth__ClientId, Auth__ClientSecret (see README).
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Issuer base URL, e.g. https://id.example.com (Pocket ID instance).</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>OIDC client id registered at the provider.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OIDC client secret. Prefer the Auth__ClientSecret environment variable.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Requested scopes. Defaults to openid + profile + email.</summary>
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    /// <summary>Claim used as User.Identity.Name. Pocket ID provides "name".</summary>
    public string NameClaimType { get; set; } = "name";

    /// <summary>Set to false only for plain-http test issuers. Always true in production.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Public base URL of this app, e.g. https://tennis.example.com.
    /// Set when the app sits behind proxies that don't preserve the original host/scheme,
    /// so login/logout redirects always use this origin instead of the incoming request.
    /// Empty (default) means derive the redirect URI from the incoming request.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Absolute redirect URI for the given callback path, or null to derive from the request.</summary>
    public string? GetRedirectUri(string callbackPath) =>
        string.IsNullOrWhiteSpace(PublicBaseUrl) ? null : PublicBaseUrl.TrimEnd('/') + callbackPath;
}
