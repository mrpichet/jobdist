// JobDist operator console (Blazor WebAssembly + DevExpress Blazor 26.1).
using JobDist.UI;
using JobDist.UI.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Step 1: DevExpress Blazor components.
builder.Services.AddDevExpressBlazor();

// Step 2: same-origin API client; the API key lives only in this browser tab's session storage.
builder.Services.AddScoped<ApiKeyStore>();
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<JobDistApi>();

await builder.Build().RunAsync();
