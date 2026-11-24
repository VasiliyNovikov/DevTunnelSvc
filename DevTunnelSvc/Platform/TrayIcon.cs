using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DevTunnelSvc.Platform;

internal sealed unsafe partial class TrayIcon : IDisposable
{
    private readonly Thread thread;
    private readonly TaskCompletionSource<IWindow> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IWindow window;

    [SupportedOSPlatform("windows")]
    public TrayIcon(Action<Exception> failed) : this(OpenWindowsWindow, failed) { }

    internal TrayIcon(Func<IWindow> factory, Action<Exception> failed)
    {
        thread = new Thread(() => Pump(factory, failed)) { IsBackground = true, Name = "DevTunnelSvc tray" };
        thread.Start();
        window = ready.Task.GetAwaiter().GetResult();
    }

    private void Pump(Func<IWindow> factory, Action<Exception> failed)
    {
        try
        {
            using var owned = factory();
            owned.Show();
            ready.SetResult(owned);
            while (owned.WaitForTaskbarRestart()) owned.Show();
        }
        catch (Exception ex) { if (!ready.TrySetException(ex)) failed(ex); }
    }

    public void Dispose()
    {
        if (thread.IsAlive) window.Close();
        thread.Join();
    }

    internal interface IWindow : IDisposable
    {
        void Show();
        bool WaitForTaskbarRestart();
        void Close();
    }

    internal delegate bool Notify(uint action, ref IconData data);
    internal delegate int Receive(out Message message, nint window, uint first, uint last);
    internal delegate nint Dispatch(ref Message message);
    internal delegate bool Post(nint window, uint message, nuint w, nint l);
    internal sealed record Shell(Notify NotifyIcon, Receive GetMessage, Dispatch DispatchMessage, Post PostMessage, Func<nint, bool> Destroy);

    [SupportedOSPlatform("windows")]
    private static IWindow OpenWindowsWindow() => new WindowsWindow(
        CreateWindowExW(0, "STATIC", "DevTunnelSvc", 0, 0, 0, 0, 0, 0, 0, 0, 0),
        LoadIconW(0, 32512), RegisterWindowMessageW("TaskbarCreated"),
        new(Shell_NotifyIconW, GetMessageW, DispatchMessageW, PostMessageW, DestroyWindow));

    internal sealed class WindowsWindow : IWindow
    {
        private readonly uint taskbarCreated;
        private readonly Shell shell;
        private IconData data;

        public WindowsWindow(nint hwnd, nint icon, uint taskbarCreated, Shell shell)
        {
            if (hwnd == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            this.shell = shell;
            this.taskbarCreated = taskbarCreated;
            data = new IconData { Size = (uint)sizeof(IconData), Window = hwnd, Id = 1, Flags = 6, Icon = icon };
            fixed (char* tip = data.Tip) "DevTunnelSvc".AsSpan().CopyTo(new Span<char>(tip, 128));
        }

        public void Show()
        {
            if (data.Icon == 0 || !shell.NotifyIcon(0, ref data))
                throw new InvalidOperationException("Could not register the notification-area icon.");
        }

        public bool WaitForTaskbarRestart()
        {
            while (ReadMessage(out var message) && message.Id != 0x0010)
            {
                if (message.Id == taskbarCreated) return true;
                _ = shell.DispatchMessage(ref message);
            }
            return false;
        }

        private bool ReadMessage(out Message message)
        {
            var result = shell.GetMessage(out message, 0, 0, 0);
            if (result < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return result != 0;
        }

        public void Close()
        {
            if (!shell.PostMessage(data.Window, 0x0010, 0, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        public void Dispose() { _ = shell.NotifyIcon(2, ref data); _ = shell.Destroy(data.Window); }
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
    [LibraryImport("user32")] private static partial nint LoadIconW(nint instance, nint icon);
    [LibraryImport("shell32")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool Shell_NotifyIconW(uint action, ref IconData data);
    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)] private static partial uint RegisterWindowMessageW(string message);
    [LibraryImport("user32", SetLastError = true)] private static partial int GetMessageW(out Message message, nint window, uint first, uint last);
    [LibraryImport("user32")] private static partial nint DispatchMessageW(ref Message message);
    [LibraryImport("user32", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostMessageW(nint window, uint message, nuint w, nint l);
    [LibraryImport("user32")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint window);
}
