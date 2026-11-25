using System.Net;
using JobDist.Web.Localization;
using JobDist.Web.Services;

namespace JobDist.Web.Components;

/// <summary>Result of loading one record for a page.</summary>
public sealed record Loaded<T>(T? Value, bool NotFound, string? Error) where T : class
{
    /// <summary>Calls the API and maps 404 / API errors / network errors.</summary>
    public static async Task<Loaded<T>> FromAsync(Func<Task<T>> call)
    {
        try
        {
            return new Loaded<T>(await call(), false, null);
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return new Loaded<T>(null, true, null);
        }
        catch (ApiException ex)
        {
            return new Loaded<T>(null, false, ex.Message);
        }
        catch (HttpRequestException)
        {
            return new Loaded<T>(null, false, AppText.Get("Common.NetworkError"));
        }
    }
}
