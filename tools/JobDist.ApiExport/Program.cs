// =============================================================================================
// JobDist API exporter. Reflects over the real assemblies (so the export can never drift from the code) and
// combines them with the XML documentation comments:
//   1. REST endpoints  : controllers → verb, route, auth policy/roles, parameters, body and response types.
//   2. MCP / AI tools  : the same AIFunction objects the server exposes (name, description, JSON schema).
//   3. Contracts       : public records/enums of JobDist.Contracts (request / response shapes).
//   4. SDK interfaces  : IJobDistClient (submit side) and IJobHandler / JobDistWorkerOptions (worker side).
// Output: <outDir>/API_EXPORT.md (for humans and AI assistants) and <outDir>/api-export.json.
// =============================================================================================
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using JobDist.Client;
using JobDist.Contracts;
using JobDist.Mcp;
using JobDist.Worker.Sdk;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

var outDir = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "docs");
Directory.CreateDirectory(outDir);
var docs = new XmlDocs();

// Step 1: REST endpoints from the controllers of JobDist.Server.
var serverAssembly = typeof(JobDist.Server.Controllers.JobsController).Assembly;
var endpoints = new List<Endpoint>();
foreach (var controller in serverAssembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract).OrderBy(t => t.Name))
{
    // Versioned route (api/v{version:apiVersion}/...) shown as api/v1/...; the unversioned api/... alias is the same endpoint.
    var routes = controller.GetCustomAttributes<RouteAttribute>().Select(r => r.Template).ToList();
    var baseRoute = (routes.FirstOrDefault(r => r.Contains("{version:apiVersion}", StringComparison.Ordinal)) ?? routes.FirstOrDefault() ?? string.Empty)
        .Replace("v{version:apiVersion}", "v1", StringComparison.Ordinal);
    var classAuth = Auth(controller);
    foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    {
        foreach (var http in method.GetCustomAttributes<HttpMethodAttribute>())
        {
            var route = "/" + string.Join('/', new[] { baseRoute, http.Template }.Where(s => !string.IsNullOrEmpty(s)));
            var parameters = method.GetParameters()
                .Where(p => p.ParameterType != typeof(CancellationToken))
                .Select(p => new Param(p.Name!, TypeName(p.ParameterType), Source(p, route), docs.Param(method, p.Name!)))
                .ToList();
            endpoints.Add(new Endpoint(
                string.Join(',', http.HttpMethods), route, controller.Name.Replace("Controller", string.Empty),
                Auth(method) ?? classAuth ?? "authenticated", docs.Summary(method), parameters, ResponseType(method)));
        }
    }
}
endpoints.Add(new Endpoint("POST", "/mcp", "Mcp", "policy:AiAgent", "MCP Streamable HTTP endpoint (stateless JSON-RPC). Tools listed below.", [], "JSON-RPC"));
endpoints.Add(new Endpoint("GET", "/health", "Health", "anonymous", "Readiness probe (database reachable).", [], "text"));
endpoints.Add(new Endpoint("GET", "/alive", "Health", "anonymous", "Liveness probe.", [], "text"));

// Step 2: MCP / AI tools (same AIFunction definitions as the server).
var tools = JobDistAiFunctions.Create(includeSideEffects: true)
    .Select(f => new Tool(f.Name, f.Description, f.JsonSchema.GetRawText(),
        f.Name is "submit_job" or "cancel_job" or "change_job_priority"))
    .ToList();

// Step 3: contracts and SDK types.
var contractTypes = typeof(SubmitJobRequest).Assembly.GetExportedTypes()
    .Where(t => t.Namespace == "JobDist.Contracts" && (t.IsEnum || t.IsClass) && !t.IsAbstract)
    .OrderBy(t => t.IsEnum).ThenBy(t => t.Name)
    .Select(t => Describe(t, docs)).ToList();
var sdkTypes = new[] { typeof(IJobDistClient), typeof(JobDistClientOptions), typeof(IJobHandler), typeof(JobContext), typeof(JobResult), typeof(JobDistWorkerOptions) }
    .Select(t => Describe(t, docs)).ToList();

// Step 4: write JSON.
var json = new JsonObject
{
    ["name"] = "JobDist",
    ["description"] = "Distributed job scheduler (.NET 10): REST API + MCP server over the same business logic.",
    ["authentication"] = "Header X-Api-Key (roles: Admin, Submitter, Viewer, Worker, AiAgent) or Authorization: Bearer <AuthServer token> with jobdist.* permissions. Worker calls also need X-Worker-Token from registration.",
    ["versioning"] = "URL segment api/v1/...; the unversioned api/... alias equals v1. Header api-version or query ?api-version also accepted.",
    ["endpoints"] = JsonSerializer.SerializeToNode(endpoints),
    ["mcpTools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
    {
        ["name"] = t.Name, ["description"] = t.Description, ["sideEffect"] = t.SideEffect, ["inputSchema"] = JsonNode.Parse(t.Schema)
    }).ToArray()),
    ["contracts"] = JsonSerializer.SerializeToNode(contractTypes),
    ["sdk"] = JsonSerializer.SerializeToNode(sdkTypes),
};
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
await File.WriteAllTextAsync(Path.Combine(outDir, "api-export.json"), json.ToJsonString(jsonOptions));

// Step 5: write Markdown.
var md = new StringBuilder();
md.AppendLine("# JobDist API export");
md.AppendLine();
md.AppendLine("> Generated by `tools/JobDist.ApiExport` from the compiled assemblies and XML doc comments. Do not edit by hand:");
md.AppendLine("> run `dotnet run --project tools/JobDist.ApiExport -- docs` after changing the API. Machine-readable copy: `api-export.json`.");
md.AppendLine();
md.AppendLine("JobDist is a distributed job scheduler. The same business logic is served as a **REST API** (`/api/*`) and as an **MCP server** (`/mcp`, tools built with Microsoft.Extensions.AI).");
md.AppendLine();
md.AppendLine("## Authentication");
md.AppendLine();
md.AppendLine("- Machines (workers, SDK, MCP, scripts): header `X-Api-Key: <key>`. Keys map to roles **Admin, Submitter, Viewer, Worker, AiAgent** (only SHA-256 hashes are stored server-side).");
md.AppendLine("- People (web console): `Authorization: Bearer <access token>` from AuthServer (Application `jobdist`); access needs the `jobdist.*` permission shown in the Auth column, or the AuthServer admin role.");
md.AppendLine("- Auth column \"permission `p` / API key S\": a Bearer token passes with permission `p`, an API key passes with a role of set S (Read = Viewer/Submitter/Admin, Submit = Submitter/Admin, Admin, WorkerOrAdmin, ConsoleRead = Admin in Production else Viewer/Submitter/Admin, ConsoleWrite = Admin, None = no API key).");
md.AppendLine("- Versioning: routes are `api/v1/...`; the unversioned `api/...` alias is the same v1 endpoint (header `api-version` or `?api-version=1.0` also accepted).");
md.AppendLine("- POST endpoints marked idempotent accept `Idempotency-Key` (1-100 chars): a retry with the same body replays the first response (header `Idempotent-Replayed: true`), a different body gets 422.");
md.AppendLine("- Worker calls (`/api/workers/{workerId}/...`): also `X-Worker-Token: <token>` returned once by `POST /api/workers/register`.");
md.AppendLine("- Enums are serialized as strings (e.g. `\"Pending\"`). Times are ISO-8601 UTC. Errors are RFC 7807 problem details; 429 with `error: queue_full` and `Retry-After` when a queue threshold is exceeded.");
md.AppendLine();
md.AppendLine("## REST endpoints");
md.AppendLine();
foreach (var group in endpoints.GroupBy(e => e.Group))
{
    md.AppendLine($"### {group.Key}");
    md.AppendLine();
    md.AppendLine("| Verb | Route | Auth | Body / params | Returns | Description |");
    md.AppendLine("|---|---|---|---|---|---|");
    foreach (var e in group)
    {
        var ps = string.Join("<br>", e.Parameters.Select(p => $"`{p.Name}` ({p.Source}): {p.Type}"));
        md.AppendLine($"| {e.Verb} | `{e.Route}` | {e.Auth} | {Cell(ps)} | {Cell(e.Returns)} | {Cell(e.Summary)} |");
    }
    md.AppendLine();
}
md.AppendLine("## MCP / AI tools");
md.AppendLine();
md.AppendLine("Endpoint `POST /mcp` (Streamable HTTP, stateless), API key with role **AiAgent**; or the stdio host `src/JobDist.McpServer`.");
md.AppendLine("Side-effect tools are only listed when the server sets `Mcp:AllowSideEffects=true`.");
md.AppendLine();
foreach (var t in tools)
{
    md.AppendLine($"### `{t.Name}`{(t.SideEffect ? " (side effect)" : string.Empty)}");
    md.AppendLine();
    md.AppendLine(t.Description);
    md.AppendLine();
    var schema = JsonNode.Parse(t.Schema)!;
    if (schema["properties"] is JsonObject props && props.Count > 0)
    {
        var required = (schema["required"] as JsonArray)?.Select(r => r!.GetValue<string>()).ToHashSet() ?? [];
        md.AppendLine("| Parameter | Type | Required | Description |");
        md.AppendLine("|---|---|---|---|");
        foreach (var (name, node) in props)
        {
            var type = node?["type"]?.ToJsonString().Trim('"') ?? "object";
            md.AppendLine($"| `{name}` | {Cell(type)} | {(required.Contains(name) ? "yes" : "no")} | {Cell(node?["description"]?.GetValue<string>() ?? string.Empty)} |");
        }
        md.AppendLine();
    }
}
md.AppendLine("## Contracts (JobDist.Contracts)");
md.AppendLine();
WriteTypes(md, contractTypes);
md.AppendLine("## SDKs");
md.AppendLine();
md.AppendLine("- **Submit jobs** from another project: reference `JobDist.Client`, `services.AddJobDistClient(configuration.GetSection(\"JobDistClient\"))`, inject `IJobDistClient`.");
md.AppendLine("- **Implement a worker**: reference `JobDist.Worker.Sdk`, implement `IJobHandler`, `services.AddJobDistWorker(configuration).AddJobHandler<MyHandler>()` (section `JobDistWorker`).");
md.AppendLine();
WriteTypes(md, sdkTypes);
await File.WriteAllTextAsync(Path.Combine(outDir, "API_EXPORT.md"), md.ToString());
Console.WriteLine($"Exported {endpoints.Count} endpoints, {tools.Count} MCP tools, {contractTypes.Count} contracts to {outDir}");
return;

// ------------------------------------------------------------------------------------------------ helpers

static string? Auth(MemberInfo member)
{
    if (member.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
    {
        return "anonymous";
    }
    var a = member.GetCustomAttributes<AuthorizeAttribute>().FirstOrDefault();
    if (a is JobDist.Server.Security.JobDistAuthorizeAttribute j)
    {
        return $"permission `{j.Permission.Replace("|", " or ", StringComparison.Ordinal)}` / API key {j.RoleSet}";
    }
    return a is null ? null : a.Policy is not null ? $"policy:{a.Policy}" : a.Roles is not null ? $"roles:{a.Roles}" : "authenticated";
}

static string Source(ParameterInfo p, string route)
{
    if (p.GetCustomAttribute<FromBodyAttribute>() is not null)
    {
        return "body";
    }
    if (p.GetCustomAttribute<FromQueryAttribute>() is not null)
    {
        return "query";
    }
    return route.Contains('{' + p.Name!, StringComparison.Ordinal) ? "route" : "query";
}

static string ResponseType(MethodInfo m)
{
    var t = m.ReturnType;
    if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
    {
        t = t.GetGenericArguments()[0];
    }
    if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ActionResult<>))
    {
        return TypeName(t.GetGenericArguments()[0]);
    }
    return t == typeof(IActionResult) || t == typeof(Task) ? "status / JSON" : TypeName(t);
}

static string TypeName(Type t)
{
    if (Nullable.GetUnderlyingType(t) is { } inner)
    {
        return TypeName(inner) + "?";
    }
    if (!t.IsGenericType)
    {
        return t.Name switch { "String" => "string", "Int32" => "int", "Int64" => "long", "Boolean" => "bool", "Double" => "double", "Object" => "object", _ => t.Name };
    }
    var name = t.Name[..t.Name.IndexOf('`')];
    return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(TypeName))}>";
}

static TypeDoc Describe(Type t, XmlDocs docs)
{
    if (t.IsEnum)
    {
        return new TypeDoc(t.Name, "enum", docs.Summary(t), Enum.GetNames(t).Select(n => new MemberDoc(n, Convert.ToInt32(Enum.Parse(t, n)).ToString(System.Globalization.CultureInfo.InvariantCulture), docs.Field(t, n))).ToList());
    }
    var members = new List<MemberDoc>();
    if (t.IsInterface)
    {
        members.AddRange(t.GetMethods().Where(m => !m.IsSpecialName).Select(m =>
            new MemberDoc($"{m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{TypeName(p.ParameterType)} {p.Name}"))})", TypeName(m.ReturnType), docs.Summary(m))));
        members.AddRange(t.GetProperties().Select(p => new MemberDoc(p.Name, TypeName(p.PropertyType), docs.Property(p))));
    }
    else
    {
        var ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != "EqualityContract"))
        {
            var doc = docs.Property(p);
            if (string.IsNullOrEmpty(doc) && ctor is not null)
            {
                doc = docs.Param(t, p.Name);
            }
            members.Add(new MemberDoc(p.Name, TypeName(p.PropertyType), doc));
        }
    }
    return new TypeDoc(t.Name, t.IsInterface ? "interface" : t.IsValueType ? "struct" : t.GetMethod("<Clone>$") is not null ? "record" : "class", docs.Summary(t), members);
}

static void WriteTypes(StringBuilder md, IEnumerable<TypeDoc> types)
{
    foreach (var t in types)
    {
        md.AppendLine($"### `{t.Name}` ({t.Kind})");
        md.AppendLine();
        if (!string.IsNullOrEmpty(t.Summary))
        {
            md.AppendLine(t.Summary);
            md.AppendLine();
        }
        md.AppendLine(t.Kind == "enum" ? "| Name | Value | Description |" : "| Member | Type | Description |");
        md.AppendLine("|---|---|---|");
        foreach (var m in t.Members)
        {
            md.AppendLine($"| `{Cell(m.Name)}` | {Cell(m.Type)} | {Cell(m.Description)} |");
        }
        md.AppendLine();
    }
}

static string Cell(string? s) => (s ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

// ------------------------------------------------------------------------------------------------ models

internal sealed record Param(string Name, string Type, string Source, string Description);

internal sealed record Endpoint(string Verb, string Route, string Group, string Auth, string Summary, List<Param> Parameters, string Returns);

internal sealed record Tool(string Name, string Description, string Schema, bool SideEffect);

internal sealed record MemberDoc(string Name, string Type, string Description);

internal sealed record TypeDoc(string Name, string Kind, string Summary, List<MemberDoc> Members);

/// <summary>Reads the XML documentation files that sit next to the referenced assemblies.</summary>
internal sealed class XmlDocs
{
    private readonly Dictionary<string, XElement> _members = new(StringComparer.Ordinal);

    public XmlDocs()
    {
        // Step 1: load every *.xml doc file in the output directory.
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "JobDist.*.xml"))
        {
            foreach (var m in XDocument.Load(file).Descendants("member"))
            {
                _members[(string)m.Attribute("name")!] = m;
            }
        }
    }

    public string Summary(Type t) => Text(Find("T:" + Id(t))?.Element("summary"));

    public string Summary(MethodInfo m) => Text(FindMethod(m)?.Element("summary"));

    public string Property(PropertyInfo p) => Text(Find($"P:{Id(p.DeclaringType!)}.{p.Name}")?.Element("summary"));

    public string Field(Type t, string name) => Text(Find($"F:{Id(t)}.{name}")?.Element("summary"));

    public string Param(MethodInfo m, string name) => Text(FindMethod(m)?.Elements("param").FirstOrDefault(e => (string?)e.Attribute("name") == name));

    public string Param(Type t, string name) => Text(Find("T:" + Id(t))?.Elements("param").FirstOrDefault(e => (string?)e.Attribute("name") == name));

    private XElement? FindMethod(MethodInfo m)
    {
        var prefix = $"M:{Id(m.DeclaringType!)}.{m.Name}";
        return _members.FirstOrDefault(kv => kv.Key == prefix || kv.Key.StartsWith(prefix + "(", StringComparison.Ordinal)).Value;
    }

    private XElement? Find(string id) => _members.GetValueOrDefault(id);

    private static string Id(Type t) => (t.FullName ?? t.Name).Replace('+', '.');

    /// <summary>Flattens an XML doc element (see/paramref/c) into plain text.</summary>
    private static string Text(XElement? e)
    {
        if (e is null)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        foreach (var node in e.Nodes())
        {
            sb.Append(node switch
            {
                XText text => text.Value,
                XElement { Name.LocalName: "see" or "seealso" } see => ((string?)see.Attribute("cref") ?? (string?)see.Attribute("langword") ?? see.Value).Split('.', ':').Last(),
                XElement { Name.LocalName: "paramref" or "typeparamref" } r => (string?)r.Attribute("name"),
                XElement { Name.LocalName: "c" or "code" } c => "`" + c.Value + "`",
                XElement other => Text(other),
                _ => string.Empty
            });
        }
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
