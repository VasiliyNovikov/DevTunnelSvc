using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DevTunnelSvc.Platform;

internal sealed partial class TrayMenu(TrayMenu.Popup popup, Action exit, Func<Process> launchUpdate, TrayMenu.Dialogs dialogs) : IDisposable
{
    internal const string UpdateWorker = "__tray-update";
    internal enum Command : uint { None, Update, About, Exit }
    internal delegate bool Append(nint menu, uint flags, nuint id, string? text);
    internal delegate uint Track(nint menu, uint flags, int x, int y, nint owner, nint parameters);
    internal sealed record Popup(Func<nint> Create, Append Add, Func<nint, Point> Anchor, Func<nint, bool> Foreground,
        Track TrackAt, TrayIcon.Post Post, Func<nint, bool> Destroy);
    private Process? updater;
    private bool showing;

    [SupportedOSPlatform("windows")]
    internal static TrayMenu Create(Action exit) => new(SystemPopup, exit, LaunchUpdater, SystemDialogs);

    [SupportedOSPlatform("windows")]
    internal static Popup SystemPopup => new(CreatePopupMenu, AppendMenuW, IconAnchor, SetForegroundWindow,
        TrackPopupMenuEx, TrayIcon.PostMessageW, DestroyMenu);

    internal void Show(nint owner)
    {
        if (showing) return;
        showing = true;
        try
        {
            var busy = updater is { HasExited: false };
            switch (Choose(owner, busy))
            {
                case Command.Update when !busy:
                    updater?.Dispose();
                    updater = null;
                    updater = launchUpdate();
                    break;
                case Command.About:
                    dialogs.Inform(owner, "About DevTunnelSvc", $"DevTunnelSvc {Program.Version}\n\nHosts and forwards Microsoft Dev Tunnels for your Windows user.");
                    break;
                case Command.Exit:
                    exit();
                    break;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            dialogs.ReportError(owner, ex.Message);
        }
        finally { showing = false; }
    }

    private Command Choose(nint owner, bool updating)
    {
        var menu = popup.Create();
        if (menu == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            if (!popup.Add(menu, updating ? 1u : 0u, (nuint)Command.Update, "&Update") ||
                !popup.Add(menu, 0, (nuint)Command.About, "&About") ||
                !popup.Add(menu, 0x800, 0, null) || // MF_SEPARATOR
                !popup.Add(menu, 0, (nuint)Command.Exit, "E&xit"))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var anchor = popup.Anchor(owner);
            _ = popup.Foreground(owner);
            // Return the selection (zero means dismissed) and let either mouse button select it.
            var selection = popup.TrackAt(menu, 0x0182, anchor.X, anchor.Y, owner, 0);
            _ = popup.Post(owner, 0, 0, 0); // WM_NULL lets subsequent tray menus dismiss normally.
            return (Command)selection;
        }
        finally { _ = popup.Destroy(menu); }
    }

    public void Dispose() => updater?.Dispose(); // The updater must survive the service it is replacing.

    [SupportedOSPlatform("windows")]
    private static Process LaunchUpdater()
    {
        Program.RequireSingleFileUpdate();
        return StartWorker(ServiceLifetime.ApplicationExecutable(), Environment.ProcessId, Process.Start);
    }

    internal static Process StartWorker(string executable, int servicePid, Func<ProcessStartInfo, Process?> start)
    {
        var info = new ProcessStartInfo(executable, [UpdateWorker, servicePid.ToString(CultureInfo.InvariantCulture)]) { CreateNoWindow = true };
        return start(info) ?? throw new InvalidOperationException("Could not start the update helper.");
    }

    [SupportedOSPlatform("windows")]
    private static Point IconAnchor(nint owner) => IconAnchor(owner, Shell_NotifyIconGetRect, GetCursorPos);

    internal delegate int IconBounds(ref IconIdentifier icon, out Rect rect);
    internal delegate bool Cursor(out Point point);
    internal static Point IconAnchor(nint owner, IconBounds bounds, Cursor cursor)
    {
        var icon = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = owner, Id = TrayIcon.IconId };
        if (bounds(ref icon, out var rect) == 0)
            return new Point { X = rect.Left + (rect.Right - rect.Left) / 2, Y = rect.Top };
        return cursor(out var point) ? point : throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [SupportedOSPlatform("windows")]
    internal static Dialogs SystemDialogs => new(MessageBoxW);

    internal sealed class Dialogs(Func<nint, string, string, uint, int> show)
    {
        internal bool Confirm(string text) => Open(0, "DevTunnelSvc Update", text, 0x134) == 6; // Warning, Yes/No, default No.
        internal void Inform(nint owner, string title, string text) => Open(owner, title, text, 0x40);
        internal void ReportError(nint owner, string text) => Open(owner, "DevTunnelSvc", text, 0x10);

        private int Open(nint owner, string title, string text, uint flags)
        {
            var result = show(owner, text, title, flags | 0x10000); // MB_SETFOREGROUND
            return result != 0 ? result : throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IconIdentifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }

    [LibraryImport("user32", SetLastError = true)] private static partial nint CreatePopupMenu();
    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [LibraryImport("user32")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyMenu(nint menu);
    [LibraryImport("user32")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out Point point);
    [LibraryImport("user32")] private static partial uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
    [LibraryImport("shell32")] private static partial int Shell_NotifyIconGetRect(ref IconIdentifier icon, out Rect rect);
    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int MessageBoxW(nint owner, string text, string caption, uint flags);
}
