using System.Globalization;

namespace GameWeb.Localization;

/// <summary>
/// The language a page was opened in. Room updates arrive from server timers and the computer
/// player's background task, which carry the culture of whoever created the room; a page applies
/// its own culture before re-rendering so each viewer keeps their language.
/// </summary>
public readonly record struct ViewerCulture(CultureInfo Culture, CultureInfo UICulture)
{
    public static ViewerCulture Capture() => new(CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);

    public void Apply()
    {
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = UICulture;
    }
}
