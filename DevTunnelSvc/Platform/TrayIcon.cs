using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DevTunnelSvc.Platform;

internal sealed unsafe partial class TrayIcon : IDisposable
{
    internal const uint IconId = 1;
    internal const uint CallbackMessage = 0x8001; // WM_APP + 1
    internal const uint ContextMenu = 0x007B;
    internal const uint CloseMessage = 0x0010;
    private readonly Thread thread;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object sync = new();
    private IWindow? active;

    [SupportedOSPlatform("windows")]
    public TrayIcon(Action<Exception?> stopped) : this(() => OpenWindowsWindow(TrayMenu.Create(() => PostQuitMessage(0))), stopped) { }

    internal TrayIcon(Func<IWindow> factory, Action<Exception?> stopped)
    {
        thread = new Thread(() => Pump(factory, stopped)) { IsBackground = true, Name = "DevTunnelSvc tray" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Task.GetAwaiter().GetResult();
    }

    private void Pump(Func<IWindow> factory, Action<Exception?> stopped)
    {
        try
        {
            using var owned = factory();
            owned.Show();
            lock (sync) active = owned;
            ready.SetResult();
            try { owned.Run(); }
            finally { lock (sync) active = null; }
        }
        catch (Exception ex)
        {
            if (!ready.TrySetException(ex)) stopped(ex);
            return;
        }
        stopped(null);
    }

    public void Dispose()
    {
        lock (sync) active?.Close();
        thread.Join();
    }

    internal interface IWindow : IDisposable
    {
        void Show();
        void Run();
        void Close();
    }

    internal delegate bool Notify(uint action, ref IconData data);
    internal delegate int Receive(out Message message, nint window, uint first, uint last);
    internal delegate nint Dispatch(ref Message message);
    internal delegate bool Post(nint window, uint message, nuint w, nint l);
    internal sealed record Shell(Notify NotifyIcon, Receive GetMessage, Dispatch DispatchMessage, Post CloseWindow,
        Action<nint> Quit, Func<nint, bool> Destroy, Func<nint, bool> DestroyIcon);

    [SupportedOSPlatform("windows")]
    internal static WindowsWindow OpenWindowsWindow(TrayMenu menu) => CreateOwnerWindow(RegisterWindowMessageW("TaskbarCreated"),
        LoadArtwork, () => CreateWindowExW(0, "STATIC", "DevTunnelSvc", 0, 0, 0, 0, 0, 0, 0, 0, 0),
        AttachProcedure, new(Shell_NotifyIconW, GetMessageW, DispatchMessageW, SendNotifyMessageW, Quit, DestroyWindow, DestroyIcon),
        menu);

    internal static WindowsWindow CreateOwnerWindow(uint taskbarCreated, Func<nint> loadIcon, Func<nint> createOwner,
        Action<nint, WindowsWindow> attach, Shell shell, TrayMenu menu)
    {
        if (taskbarCreated == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var icon = loadIcon();
        var hwnd = createOwner();
        if (hwnd == 0)
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            _ = shell.DestroyIcon(icon);
            throw error;
        }
        var window = new WindowsWindow(hwnd, icon, taskbarCreated, shell, menu);
        try { attach(hwnd, window); return window; }
        catch { window.Dispose(); throw; }
    }

    private static void AttachProcedure(nint hwnd, WindowsWindow window)
    {
        var reference = GCHandle.Alloc(window);
        try
        {
            if (!SetWindowSubclass(hwnd, &Procedure, 1, (nuint)GCHandle.ToIntPtr(reference)))
                throw new InvalidOperationException("Could not attach the notification-area window procedure.");
        }
        catch { reference.Free(); throw; }
    }

    [SupportedOSPlatform("windows")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Use the bundled executable's icon when the managed module has no file path.")]
    private static nint LoadArtwork()
        => ExtractArtwork(typeof(TrayIcon).Assembly.Location, ServiceLifetime.ApplicationExecutable, ExtractIconExW);

    internal delegate uint Extract(string path, int index, nint large, out nint small, uint count);
    internal static nint ExtractArtwork(string module, Func<string> bundlePath, Extract extract)
    {
        var path = module.Length == 0 ? bundlePath() : module;
        return extract(path, 0, 0, out var small, 1) == 1 && small != 0 ? small
            : throw new InvalidOperationException("Could not load the embedded DevTunnelSvc icon.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Procedure(nint hwnd, uint message, nuint w, nint l, nuint id, nuint state)
    {
        var reference = GCHandle.FromIntPtr((nint)state);
        if (message == 0x0082) // WM_NCDESTROY, on the owning thread
        {
            _ = RemoveWindowSubclass(hwnd, &Procedure, id);
            reference.Free();
        }
        else if (((WindowsWindow)reference.Target!).HandleMessage(message, l)) return 0;
        return DefSubclassProc(hwnd, message, w, l);
    }

    private static void Quit(nint hwnd) => Quit(hwnd, EndMenu, GetLastActivePopup, PostMessageW, PostQuitMessage);

    internal static void Quit(nint hwnd, Func<bool> endMenu, Func<nint, nint> activePopup, Post post, Action<int> quit)
    {
        // Shutdown can arrive inside TrackPopupMenuEx or the About dialog's nested message loop.
        _ = endMenu();
        var popup = activePopup(hwnd);
        if (popup != 0 && popup != hwnd) _ = post(popup, CloseMessage, 0, 0);
        quit(0);
    }

    internal sealed class WindowsWindow : IWindow
    {
        private readonly uint taskbarCreated;
        private readonly Shell shell;
        private readonly TrayMenu menu;
        private IconData data;
        private Exception? callbackFailure;
        internal nint Handle => data.Window;

        public WindowsWindow(nint hwnd, nint icon, uint taskbarCreated, Shell shell, TrayMenu menu)
        {
            if (hwnd == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            this.shell = shell;
            this.menu = menu;
            this.taskbarCreated = taskbarCreated;
            data = new IconData
            {
                Size = (uint)sizeof(IconData),
                Window = hwnd,
                Id = IconId,
                Flags = 0x87,
                Callback = CallbackMessage,
                Icon = icon,
                Version = 4
            }; // MESSAGE | ICON | TIP | SHOWTIP
            fixed (char* tip = data.Tip) "DevTunnelSvc".AsSpan().CopyTo(new Span<char>(tip, 128));
        }

        public void Show()
        {
            if (data.Icon == 0 || !shell.NotifyIcon(0, ref data) || !shell.NotifyIcon(4, ref data)) // NIM_ADD, NIM_SETVERSION
                throw new InvalidOperationException("Could not register the notification-area icon.");
        }

        public void Run()
        {
            while (ReadMessage(out var message))
                _ = shell.DispatchMessage(ref message);
            if (callbackFailure is { } failure) throw new InvalidOperationException(failure.Message, failure);
        }

        internal bool HandleMessage(uint message, nint parameter)
        {
            try
            {
                if (message == taskbarCreated) Show();
                else if (message == CloseMessage) shell.Quit(data.Window);
                else if (message == CallbackMessage)
                {
                    if ((uint)parameter == (IconId << 16 | ContextMenu))
                    {
                        menu.Show(data.Window, data.Icon);
                        _ = shell.NotifyIcon(3, ref data); // NIM_SETFOCUS
                    }
                }
                else return false;
            }
            catch (Exception ex)
            {
                // Never unwind a managed exception through Explorer's native callback.
                callbackFailure = ex;
                shell.Quit(data.Window);
            }
            return true;
        }

        private bool ReadMessage(out Message message)
        {
            var result = shell.GetMessage(out message, 0, 0, 0);
            if (result < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return result != 0;
        }

        public void Close()
        {
            if (!shell.CloseWindow(data.Window, CloseMessage, 0, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        public void Dispose()
        {
            _ = shell.NotifyIcon(2, ref data); // NIM_DELETE before releasing the HICON.
            menu.Dispose();
            _ = shell.Destroy(data.Window);
            if (data.Icon != 0) _ = shell.DestroyIcon(data.Icon);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        public fixed char Tip[128];
        public uint State, StateMask;
        public fixed char Info[256];
        public uint Version;
        public fixed char Title[64];
        public uint InfoFlags;
        public Guid Guid;
        public nint Balloon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }

    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint args);
    [LibraryImport("shell32", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconExW(string path, int index, nint largeIcons, out nint smallIcon, uint count);
    [LibraryImport("user32")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyIcon(nint icon);
    [LibraryImport("shell32")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool Shell_NotifyIconW(uint action, ref IconData data);
    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] private static partial uint RegisterWindowMessageW(string message);
    [LibraryImport("user32", SetLastError = true)] private static partial int GetMessageW(out Message message, nint window, uint first, uint last);
    [LibraryImport("user32")] private static partial nint DispatchMessageW(ref Message message);
    [LibraryImport("user32", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool PostMessageW(nint window, uint message, nuint w, nint l);
    [LibraryImport("user32", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool SendNotifyMessageW(nint window, uint message, nuint w, nint l);
    [LibraryImport("user32")] private static partial nint GetLastActivePopup(nint window);
    [LibraryImport("user32")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool EndMenu();
    [LibraryImport("user32")] private static partial void PostQuitMessage(int exitCode);
    [LibraryImport("user32")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint window);
    [LibraryImport("comctl32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> procedure, nuint id, nuint state);
    [LibraryImport("comctl32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> procedure, nuint id);
    [LibraryImport("comctl32")] private static partial nint DefSubclassProc(nint hwnd, uint message, nuint w, nint l);
}
