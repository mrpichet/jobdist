using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace JobDist.Server.Security;

/// <summary>One configured API key. Only the SHA-256 hash is stored, never the key itself.</summary>
public sealed class ApiKeyEntry
{
    /// <summary>Client name (becomes the user name claim and the submitter of jobs).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Hex SHA-256 of the key: <c>printf '%s' 'key' | sha256sum</c>.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>Roles: Admin, Submitter, Viewer, Worker, AiAgent.</summary>
    public string[] Roles { get; set; } = [];
}

/// <summary>Security settings (section <c>Security</c>).</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>API keys.</summary>
    public List<ApiKeyEntry> ApiKeys { get; set; } = [];

    /// <summary>Allowed CORS origins (empty = CORS disabled; the bundled UI is same-origin).</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Redirect HTTP to HTTPS (enable when Kestrel terminates TLS itself).</summary>
    public bool HttpsRedirection { get; set; }
}

/// <summary>Role names.</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Submitter = "Submitter";
    public const string Viewer = "Viewer";
    public const string Worker = "Worker";
    public const string AiAgent = "AiAgent";
}

/// <summary>Authorization policy names.</summary>
public static class Policies
{
    /// <summary>Read job / worker status (Viewer, Submitter, Admin).</summary>
    public const string Read = "Read";
    /// <summary>Submit and manage jobs (Submitter, Admin).</summary>
    public const string Submit = "Submit";
    /// <summary>Cluster administration (Admin).</summary>
    public const string Admin = "Admin";
    /// <summary>Worker protocol (Worker) - additionally requires the per-worker token.</summary>
    public const string Worker = "Worker";
    /// <summary>MCP endpoint (AiAgent, Admin).</summary>
    public const string AiAgent = "AiAgent";
}

/// <summary>
/// Authenticates requests with an <c>X-Api-Key</c> header using ASP.NET Core's built-in authentication framework.
/// Keys are compared by SHA-256 hash in constant time over all entries (no timing side channel on the matching index).
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<SecurityOptions> security)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    /// <summary>Validates the API key header.</summary>
    /// <returns>Success with role claims, NoResult without header, Fail for an unknown key.</returns>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Step 1: no header → anonymous (endpoints are deny-by-default through the fallback policy).
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || string.IsNullOrWhiteSpace(values.ToString()))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var presented = values.ToString();
        if (presented.Length > 256)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        // Step 2: hash once, compare against every key in constant time.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        ApiKeyEntry? match = null;
        foreach (var entry in security.CurrentValue.ApiKeys)
        {
            if (entry.KeyHash.Length != 64)
            {
                continue;
            }
            byte[] expected;
            try
            {
                expected = Convert.FromHexString(entry.KeyHash);
            }
            catch (FormatException)
            {
                continue;
            }
            if (CryptographicOperations.FixedTimeEquals(hash, expected))
            {
                match = entry;
            }
        }
        if (match is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        // Step 3: principal with role claims.
        var claims = new List<Claim> { new(ClaimTypes.Name, match.Name), new(ClaimTypes.NameIdentifier, match.Name) };
        claims.AddRange(match.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
    }
}
