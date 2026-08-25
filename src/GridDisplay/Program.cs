using GridDisplay.Forms;
using GridDisplay.Services;

namespace GridDisplay;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Before anything reads a path: StateStore and UpdateInstaller resolve their roots
        // through AppPaths, and several copies of the app share a machine.
        AppPaths.Initialize(args);

        // A staged build relaunches itself with this switch to replace the installed files.
        // It runs headless and must not bring the wall up — see UpdateInstaller.
        if (args.Length > 0 && args[0] == UpdateInstaller.ApplySwitch)
        {
            UpdateInstaller.RunApply(args);
            return;
        }

        Application.Run(new MainForm());
    }
}
