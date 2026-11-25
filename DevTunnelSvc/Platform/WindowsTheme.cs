using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace DevTunnelSvc.Platform;

internal sealed partial class WindowsTheme(bool supportsMenuTheme, Func<bool> highContrast, Func<bool> darkApps,
    Action<int> setMenuMode, Action<string> warn)
{
    private bool canThemeMenus = supportsMenuTheme;

    [SupportedOSPlatform("windows")]
    internal static WindowsTheme SystemTheme { get; } = FromSettings(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362),
        SystemParametersInfoW, Registry.GetValue, SetMenuMode, Console.Error.WriteLine);

    internal static WindowsTheme FromSettings(bool supportsMenus, ReadHighContrast readContrast,
        Func<string, string, object?, object?> readRegistry, Action<int> setMode, Action<string> warn) =>
        new(supportsMenus, () => HighContrastEnabled(readContrast), () => readRegistry(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is 0, setMode, warn);

    internal bool UseDarkPalette()
    {
        try { return !highContrast() && darkApps(); }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or SecurityException or IOException)
        {
            warn($"Cannot read Windows appearance settings; using system colors: {ex.Message}");
            return false;
        }
    }

    internal void PrepareMenu()
    {
        if (!canThemeMenus) return;
        try { setMenuMode(UseDarkPalette() ? 1 : 0); } // AllowDark, never ForceDark.
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            canThemeMenus = false;
            warn($"Windows menu theming is unavailable; using the native menu: {ex.Message}");
        }
    }

    internal static void ApplyTitleBar(int build, bool dark, Func<uint, int, int> set, Action<string> warn)
    {
        if (build < 17763) return;
        // DWMWA_USE_IMMERSIVE_DARK_MODE was attribute 19 before Windows 10 2004.
        var hr = set(build >= 19041 ? 20u : 19u, dark ? 1 : 0);
        if (hr < 0) warn($"Cannot theme the About title bar (HRESULT 0x{hr:X8}).");
    }

    private static void SetMenuMode(int mode)
    {
        RefreshImmersiveColorPolicyState();
        _ = SetPreferredAppMode(mode);
        FlushMenuThemes();
    }

    internal delegate bool ReadHighContrast(uint action, uint size, ref HighContrast data, uint flags);

    private static bool HighContrastEnabled(ReadHighContrast read)
    {
        var setting = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        if (!read(0x0042, setting.Size, ref setting, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (setting.Flags & 1) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HighContrast { public uint Size, Flags; public nint Scheme; }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SystemParametersInfoW(uint action, uint size, ref HighContrast data, uint flags);
    // Ordinal 135 changed signature in Windows 10 1903. Never bind it on earlier builds.
    [LibraryImport("uxtheme.dll", EntryPoint = "#104"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void RefreshImmersiveColorPolicyState();
    [LibraryImport("uxtheme.dll", EntryPoint = "#135"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetPreferredAppMode(int mode);
    [LibraryImport("uxtheme.dll", EntryPoint = "#136"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void FlushMenuThemes();
}
