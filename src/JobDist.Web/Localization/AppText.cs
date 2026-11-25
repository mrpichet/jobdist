using System.Globalization;
using System.Resources;

[assembly: NeutralResourcesLanguage("th")]

namespace JobDist.Web.Localization;

/// <summary>
/// Texts of the JobDist console (AppText.resx = Thai, AppText.en.resx = English). Used through <c>IStringLocalizer&lt;AppText&gt;</c>
/// (injected as <c>L</c> in .razor); webcommon template texts use <c>T</c> (UiText).
/// </summary>
public sealed class AppText
{
    private static readonly ResourceManager Resources = new("JobDist.Web.Localization.AppText", typeof(AppText).Assembly);

    /// <summary>Text in the current UI culture (the language only changes at start-up, so static reads are safe).</summary>
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>Formatted text.</summary>
    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
