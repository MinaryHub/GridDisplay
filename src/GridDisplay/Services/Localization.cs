using System.Globalization;
using System.Text.Json;

namespace GridDisplay.Services;

/// <summary>
/// UI language: Korean on a Korean Windows, English everywhere else, unless
/// <see cref="Models.AppState.Language"/> says otherwise.
///
/// Both texts sit side by side at the call site — <c>L.T("새로 고침", "Reload")</c> — rather than
/// in resource files keyed by name. Every dialog here is built in code, so a caption is easiest to
/// read, and to keep translated, right where the control is made; and a string cannot ship with
/// its other half missing, because the compiler will not take one argument.
///
/// The language is fixed for the life of the process. Captions are read once when each control
/// is built, so switching mid-run would leave half the UI in the old language; the menu's
/// language choice therefore takes effect on the next start.
/// </summary>
public static class L
{
    /// <summary>True when the UI speaks Korean.</summary>
    public static bool Korean { get; private set; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";

    /// <summary>The caption for the current language.</summary>
    public static string T(string ko, string en) => Korean ? ko : en;

    /// <summary>"ko", "en", or anything else (normally empty) for "follow Windows".</summary>
    public static void Use(string? setting)
    {
        Korean = setting?.Trim().ToLowerInvariant() switch
        {
            "ko" => true,
            "en" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko",
        };
    }

    /// <summary>
    /// Read only the language out of settings.json. Program.Main needs it before anything builds
    /// a control — MainForm's own fields included — which is earlier than SetStore loads.
    /// A missing or unreadable file means "follow Windows", the same as the default.
    /// </summary>
    public static void UseSavedSetting(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (doc.RootElement.TryGetProperty("Language", out var value) && value.ValueKind == JsonValueKind.String)
                Use(value.GetString());
        }
        catch
        {
            // A corrupt file is SetStore's to deal with; the language just stays automatic.
        }
    }
}
