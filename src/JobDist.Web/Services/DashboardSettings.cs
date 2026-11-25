using Microsoft.JSInterop;

namespace JobDist.Web.Services;

/// <summary>Dashboard auto-refresh interval chosen by the user (Q8: off / 10 / 30 / 60 s), kept in this browser's localStorage.</summary>
public sealed class DashboardSettings(IJSRuntime js)
{
    private const string Key = "jobdist.dashboard.refreshSeconds";

    /// <summary>Allowed values (0 = off).</summary>
    public static readonly int[] Choices = [0, 10, 30, 60];

    /// <summary>Reads the saved interval (0 when missing or storage is blocked).</summary>
    public async Task<int> GetAsync()
    {
        try
        {
            var raw = await js.InvokeAsync<string?>("localStorage.getItem", Key);
            return int.TryParse(raw, out var s) && Choices.Contains(s) ? s : 0;
        }
        catch (JSException)
        {
            return 0;
        }
    }

    /// <summary>Saves the interval.</summary>
    public async Task SetAsync(int seconds)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", Key, Choices.Contains(seconds) ? seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0");
        }
        catch (JSException)
        {
            // Storage blocked (private window): keep the value for this page only.
        }
    }
}
