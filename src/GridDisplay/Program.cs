using GridDisplay.Forms;
using GridDisplay.Services;

namespace GridDisplay;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Which set this copy should open. A wall that runs several copies gives each shortcut
        // its own set; without it the copy reopens whatever it had last.
        RequestedSet = ReadSetArgument(args);

        // A staged build relaunches itself with this switch to replace the installed files.
        // It runs headless and must not bring the wall up — see UpdateInstaller.
        if (args.Length > 0 && args[0] == UpdateInstaller.ApplySwitch)
        {
            UpdateInstaller.RunApply(args);
            return;
        }

        Application.Run(new MainForm());
    }

    /// <summary>Set named with <c>--set "이름"</c>, or empty.</summary>
    internal static string RequestedSet { get; private set; } = string.Empty;

    /// <summary>
    /// Everything after <c>--set</c> is the name, joined back with spaces. Set names contain
    /// spaces ("기업 관제"), and a shortcut written without quotes arrives here already split —
    /// taking only the next argument would silently open, or create, a set called "기업".
    /// </summary>
    private static string ReadSetArgument(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], "--set", StringComparison.OrdinalIgnoreCase))
                return string.Join(' ', args.Skip(i + 1)).Trim();
        return string.Empty;
    }
}
