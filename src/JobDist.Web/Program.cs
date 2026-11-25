using System.Net.Http.Json;
using JobDist.Contracts;
using JobDist.Web;
using JobDist.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Options;
using WebCommon.UI;
using WebCommon.UI.Auth;
using WebCommon.UI.Localization;
using WebCommon.UI.Navigation;

// =====================================================================
// JobDist web console: webcommon templates + AuthServer sign-in + JobDist REST API (same origin as JobDist.Server).
// =====================================================================
var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Step 1: root components.
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Step 2: show the AI assistant button only when the server has an AI provider configured (AgentChat on the server).
await ApplyAgentStatusAsync(builder);

// Step 3: webcommon services (theme, splash, toast, dialogs, export, AuthServer sign-in through the same-origin proxy).
builder.Services.AddWebCommonUI(builder.Configuration, builder.HostEnvironment.BaseAddress);
builder.Services.AddScoped<IAppMenuProvider, AppMenuProvider>();

// Step 4: JobDist API client with Bearer token (webcommon BearerTokenHandler: 401 → sign out).
builder.Services.AddScoped(sp =>
{
    var handler = sp.GetRequiredService<BearerTokenHandler>();
    handler.InnerHandler = new HttpClientHandler();
    return new JobDistApi(new HttpClient(handler) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress), Timeout = TimeSpan.FromSeconds(60) });
});
builder.Services.AddScoped<DashboardSettings>();

// Step 5: browser logs → POST /api/v1/client-logs → server → LogServer (Common.Logging).
builder.Services.Configure<ClientLoggingOptions>(builder.Configuration.GetSection(ClientLoggingOptions.SectionName));
builder.Services.AddSingleton<ClientLogQueue>();
builder.Services.AddSingleton<NavigationManagerAccessor>();
builder.Services.AddScoped<ClientLogShipper>();
builder.Services.AddSingleton<ILoggerProvider, ClientLogProvider>();

// Step 6: language (Thai / English) before the first render.
var host = builder.Build();
await LanguageService.ApplyUserCultureAsync(host.Services);
await host.RunAsync();

static async Task ApplyAgentStatusAsync(WebAssemblyHostBuilder builder)
{
    if (!builder.Configuration.GetValue("AgentChat:Enabled", true))
    {
        return;
    }
    try
    {
        using var http = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress), Timeout = TimeSpan.FromSeconds(5) };
        var status = await http.GetFromJsonAsync<AgentStatusResponse>("api/v1/agent/status");
        builder.Configuration["AgentChat:Enabled"] = (status?.Enabled == true).ToString();
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
    {
        builder.Configuration["AgentChat:Enabled"] = "false";
    }
}
