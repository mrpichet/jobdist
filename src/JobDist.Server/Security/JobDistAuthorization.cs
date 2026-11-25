using System.Security.Claims;
using Common.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace JobDist.Server.Security;

/// <summary>Permission ids of Application <c>jobdist</c> in AuthServer (format <c>jobdist.module.action</c>).</summary>
public static class Permissions
{
    public const string DashboardView = "jobdist.dashboard.view";
    public const string JobView = "jobdist.job.view";
    public const string JobCreate = "jobdist.job.create";
    public const string JobEdit = "jobdist.job.edit";
    public const string JobCancel = "jobdist.job.cancel";
    public const string JobExport = "jobdist.job.export";
    public const string WorkerView = "jobdist.worker.view";
    public const string WorkerDrain = "jobdist.worker.drain";
    public const string WorkerUnregister = "jobdist.worker.unregister";
    public const string InsightView = "jobdist.insight.view";
    public const string AlertSend = "jobdist.alert.send";
    public const string AlertView = "jobdist.alert.view";
    public const string AlertManage = "jobdist.alert.manage";
    public const string QueueManage = "jobdist.queue.manage";
    public const string ReportView = "jobdist.report.view";
    public const string AgentUse = "jobdist.agent.use";

    /// <summary>Every permission (seed list for AuthServer).</summary>
    public static readonly string[] All =
    [
        DashboardView, JobView, JobCreate, JobEdit, JobCancel, JobExport, WorkerView, WorkerDrain, WorkerUnregister, InsightView,
        AlertSend, AlertView, AlertManage, QueueManage, ReportView, AgentUse
    ];
}

/// <summary>Which API-key roles may call an endpoint (the API-key half of the "permission OR role" rule).</summary>
public static class RoleSets
{
    /// <summary>Viewer, Submitter, Admin.</summary>
    public const string Read = "Read";
    /// <summary>Submitter, Admin.</summary>
    public const string Submit = "Submit";
    /// <summary>Admin.</summary>
    public const string Admin = "Admin";
    /// <summary>Worker, Admin (unregister).</summary>
    public const string WorkerOrAdmin = "WorkerOrAdmin";
    /// <summary>New read endpoints: Admin in Production, Viewer/Submitter/Admin elsewhere (Q18).</summary>
    public const string ConsoleRead = "ConsoleRead";
    /// <summary>New write endpoints: Admin.</summary>
    public const string ConsoleWrite = "ConsoleWrite";
    /// <summary>No API key role (web console users only).</summary>
    public const string None = "None";

    /// <summary>Roles allowed for a set.</summary>
    public static string[] Resolve(string set, bool production) => set switch
    {
        Read => [Roles.Viewer, Roles.Submitter, Roles.Admin],
        Submit => [Roles.Submitter, Roles.Admin],
        Admin => [Roles.Admin],
        WorkerOrAdmin => [Roles.Worker, Roles.Admin],
        ConsoleRead => production ? [Roles.Admin] : [Roles.Viewer, Roles.Submitter, Roles.Admin],
        ConsoleWrite => [Roles.Admin],
        _ => []
    };
}

/// <summary>
/// Endpoint access rule: a Bearer token from AuthServer passes with the permission (or one of several separated by <c>|</c>),
/// an <c>X-Api-Key</c> passes with a role from the role set. <c>[JobDistAuthorize(Permissions.JobView, RoleSets.Read)]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class JobDistAuthorizeAttribute(string permission, string roleSet) : AuthorizeAttribute(JobDistPolicyProvider.Prefix + permission + ";" + roleSet)
{
    public string Permission { get; } = permission;
    public string RoleSet { get; } = roleSet;
}

/// <summary>Requirement behind <see cref="JobDistAuthorizeAttribute"/>.</summary>
public sealed class JobDistAccessRequirement(string[] permissions, string roleSet) : IAuthorizationRequirement
{
    public string[] Permissions { get; } = permissions;
    public string RoleSet { get; } = roleSet;
}

/// <summary>Checks each authenticated identity by its scheme: API key → roles, AuthServer → permissions (or the AuthServer admin role).</summary>
public sealed class JobDistAccessHandler(IHostEnvironment env, IOptions<AuthServerOptions> authServer) : AuthorizationHandler<JobDistAccessRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, JobDistAccessRequirement requirement)
    {
        foreach (var identity in context.User.Identities.Where(i => i.IsAuthenticated))
        {
            if (identity.AuthenticationType == ApiKeyAuthenticationHandler.SchemeName)
            {
                if (RoleSets.Resolve(requirement.RoleSet, env.IsProduction()).Any(r => identity.HasClaim(ClaimTypes.Role, r)))
                {
                    context.Succeed(requirement);
                    break;
                }
            }
            else if (identity.AuthenticationType == AuthServerAuthenticationHandler.SchemeName)
            {
                var admin = authServer.Value.AdminRole;
                if (identity.HasClaim(c => c.Type == ClaimTypes.Role && string.Equals(c.Value, admin, StringComparison.Ordinal))
                    || identity.Claims.Any(c => (c.Type == AuthServerTokenValidator.PermissionClaim || c.Type == AuthServerTokenValidator.ScopeClaim)
                                                && requirement.Permissions.Contains(c.Value, StringComparer.OrdinalIgnoreCase)))
                {
                    context.Succeed(requirement);
                    break;
                }
            }
        }
        return Task.CompletedTask;
    }
}

/// <summary>Builds <c>jd:</c> policies on demand; other names fall back to the configured policies.</summary>
public sealed class JobDistPolicyProvider(IOptions<AuthorizationOptions> options, AuthSchemes schemes) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "jd:";

    /// <inheritdoc />
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var parts = policyName[Prefix.Length..].Split(';');
            if (parts.Length == 2)
            {
                return new AuthorizationPolicyBuilder(schemes.Names)
                    .RequireAuthenticatedUser()
                    .AddRequirements(new JobDistAccessRequirement(parts[0].Split('|', StringSplitOptions.RemoveEmptyEntries), parts[1]))
                    .Build();
            }
        }
        return await base.GetPolicyAsync(policyName).ConfigureAwait(false);
    }
}

/// <summary>Authentication schemes accepted by console policies (API key, plus AuthServer when <c>AuthServer:Authority</c> is set).</summary>
/// <param name="Names">Scheme names.</param>
public sealed record AuthSchemes(string[] Names)
{
    /// <summary><c>true</c> when AuthServer Bearer tokens are accepted.</summary>
    public bool AuthServerEnabled => Names.Contains(AuthServerAuthenticationHandler.SchemeName);
}

/// <summary>Who is calling, for audit logs and as the job submitter.</summary>
public static class CallerExtensions
{
    /// <summary>User name (AuthServer <c>preferred_username</c>) or API key client name.</summary>
    public static string CallerName(this ClaimsPrincipal user) =>
        user.Identities.Where(i => i.IsAuthenticated).Select(i => i.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "anonymous";

    /// <summary>Stable id: AuthServer <c>sub</c> or API key name, prefixed by scheme so the two never collide.</summary>
    public static string CallerId(this ClaimsPrincipal user)
    {
        var id = user.Identities.FirstOrDefault(i => i.IsAuthenticated);
        return id is null ? "anonymous" : $"{id.AuthenticationType}:{id.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? id.Name}";
    }

    /// <summary>Permissions in the AuthServer token (empty for API keys).</summary>
    public static IReadOnlySet<string> Permissions(this ClaimsPrincipal user) =>
        user.Identities.Where(i => i.IsAuthenticated && i.AuthenticationType == AuthServerAuthenticationHandler.SchemeName)
            .SelectMany(i => i.Claims).Where(c => c.Type == AuthServerTokenValidator.PermissionClaim || c.Type == AuthServerTokenValidator.ScopeClaim)
            .Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
