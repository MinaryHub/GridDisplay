using System.Reflection;
using GridDisplay.Interop;

namespace GridDisplay.Services;

/// <summary>
/// Who this build is. Three questions answered in one place, because all three answers come from
/// the same source: which version is running, whether the process has MSIX package identity, and
/// where that package came from. Package identity is what decides how <see cref="UpdateService"/>
/// can behave — only a packaged process can stage its own replacement, an unpackaged one can
/// merely point the user at the installer — and the *signature* of that package decides whether it
/// is allowed to: a package installed from Microsoft Store must be updated by the Store.
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// Four-part version. When packaged this is the *package* version, not the assembly's: the
    /// package version is what the update feed advertises and what Windows compares during
    /// deployment, and the two only agree while packaging/build-msix.ps1 stamps them from the
    /// same &lt;Version&gt;. Comparing against the assembly instead would make a hand-built
    /// package silently look up to date.
    /// </summary>
    public static Version Current { get; }

    /// <summary>True when launched from an installed MSIX package.</summary>
    public static bool IsPackaged { get; }

    /// <summary>
    /// True when the package was signed by Microsoft Store, i.e. it was installed from the Store.
    /// Store Policy 10.2 reserves updating such a package for the Store itself, so this flag turns
    /// the app's own update machinery off rather than merely hiding it — a Store submission that
    /// can still stage its own replacement fails certification.
    /// </summary>
    public static bool IsStoreInstall { get; }

    /// <summary>Package family name, empty when unpackaged. Surfaced in the UI because it is the
    /// handle an admin needs for Get-AppxPackage / Remove-AppxPackage.</summary>
    public static string PackageFamilyName { get; }

    static AppVersion()
    {
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        IsPackaged = SafeHasPackageIdentity();
        Current = assemblyVersion;
        PackageFamilyName = string.Empty;
        IsStoreInstall = false;

        if (!IsPackaged) return;
        try
        {
            var package = Windows.ApplicationModel.Package.Current;
            var v = package.Id.Version;
            Current = new Version(v.Major, v.Minor, v.Build, v.Revision);
            PackageFamilyName = package.Id.FamilyName;
            IsStoreInstall = package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.Store;
        }
        catch
        {
            // Identity said "packaged" but the projection failed. Keep the assembly version:
            // a wrong version string must not stop the wall from starting.
        }
    }

    /// <summary>Three-part text for the UI; released packages always carry revision 0.</summary>
    public static string Display => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    public static string FullDisplay => IsStoreInstall
        ? $"{Display} (Microsoft Store)"
        : IsPackaged ? $"{Display} (MSIX)" : $"{Display} (MSIX 아님)";

    public static string InstallKindText => IsStoreInstall
        ? "Microsoft Store에서 설치됨 — 업데이트는 Store가 관리합니다"
        : IsPackaged
            ? "MSIX 패키지로 설치됨"
            : "MSIX 패키지가 아닌 실행 파일 — 자동 업데이트를 사용할 수 없습니다";

    private static bool SafeHasPackageIdentity()
    {
        try { return NativeMethods.HasPackageIdentity(); }
        catch { return false; }
    }
}
