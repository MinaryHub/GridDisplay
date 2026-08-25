namespace GridDisplay.Services;

/// <summary>
/// Where this copy of the app keeps its files.
///
/// Every path used to be a fixed folder, which is fine for one wall and wrong for several: two
/// copies running side by side loaded the same state.json, then each saved its own grid over the
/// other's on the next navigation. <c>--profile &lt;name&gt;</c> gives a copy its own corner so
/// the three can coexist.
///
/// Resolved once at startup, before any form exists, because <see cref="StateStore"/> and
/// <see cref="UpdateInstaller"/> capture their roots in static initialisers.
/// </summary>
public static class AppPaths
{
    public const string ProfileSwitch = "--profile";

    /// <summary>Empty for the default profile, which keeps the original folder layout.</summary>
    public static string ProfileName { get; private set; } = string.Empty;

    /// <summary>%APPDATA%\GridDisplay — settings that follow the user between machines.</summary>
    public static string RoamingRoot { get; private set; } = Resolve(Environment.SpecialFolder.ApplicationData);

    /// <summary>%LOCALAPPDATA%\GridDisplay — caches, browser data, staged updates.</summary>
    public static string LocalRoot { get; private set; } = Resolve(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// Read <c>--profile</c> off the command line. Call once from Program.Main, before anything
    /// touches a path.
    /// </summary>
    public static void Initialize(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], ProfileSwitch, StringComparison.OrdinalIgnoreCase)) continue;

            var name = Sanitize(args[i + 1]);
            if (name.Length == 0) continue;

            ProfileName = name;
            RoamingRoot = Path.Combine(Resolve(Environment.SpecialFolder.ApplicationData), "profiles", name);
            LocalRoot = Path.Combine(Resolve(Environment.SpecialFolder.LocalApplicationData), "profiles", name);
            return;
        }
    }

    private static string Resolve(Environment.SpecialFolder folder) =>
        Path.Combine(Environment.GetFolderPath(folder), "GridDisplay");

    /// <summary>Profile names end up as folder names, so anything unusable becomes an underscore.</summary>
    private static string Sanitize(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(raw.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }
}
