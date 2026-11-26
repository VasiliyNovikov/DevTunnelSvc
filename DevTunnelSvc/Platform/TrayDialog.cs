using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DevTunnelSvc.Platform;

internal sealed unsafe partial class TrayDialog(TrayDialog.Surface surface)
{
    private const uint Background = 0x202020, Foreground = 0xF5F5F5;
    internal sealed record Surface(Func<bool> UseDark, Action<nint> Initialize, Action<nint, bool> Apply,
        Func<nint, nint> PaintBackground, Action<nint> PaintButton, Action<nint, nint> End);
    private bool dark;
    private Exception? failure;

    [SupportedOSPlatform("windows")]
    internal static void Show(nint owner, nint icon, TrayDialogContent content) =>
        WithBrush(CreateSolidBrush, brush => Show(owner, icon, content, brush), DeleteObject);

    [SupportedOSPlatform("windows")]
    private static void Show(nint owner, nint icon, TrayDialogContent content, nint brush)
    {
        nint artwork = 0;
        var dialog = Create(icon, brush, WindowsTheme.SystemTheme.UseDarkPalette, content.Scrollable, pixels => artwork = TrayIcon.LoadArtwork(pixels));
        try
        {
            fixed (byte* template = TrayDialogTemplate.Build(content))
            {
                var result = DialogBoxIndirectParamW(GetModuleHandleW(null), template, owner, dialog.Handle, 0);
                dialog.CheckResult(result, Marshal.GetLastPInvokeError());
            }
        }
        finally { if (artwork != 0) _ = TrayIcon.DestroyIcon(artwork); }
    }

    [SupportedOSPlatform("windows")]
    internal static TrayDialog Create(nint titleIcon, nint brush, Func<bool> useDark, bool scrollable, Func<int, nint> loadArtwork) => new(new(useDark, hwnd =>
    {
        _ = SendMessageW(hwnd, 0x0080, 0, titleIcon); // WM_SETICON / ICON_SMALL
        var control = GetDlgItem(hwnd, TrayDialogTemplate.IconId);
        if (!GetClientRect(control, out var rect)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var artwork = loadArtwork(Math.Min(rect.Right, rect.Bottom));
        _ = SendMessageW(control, 0x0170, (nuint)artwork, 0); // STM_SETICON
        if (scrollable) FitScrollbar(GetDlgItem(hwnd, TrayDialogTemplate.TextId));
    }, (hwnd, dark) => ApplyTheme(hwnd, dark, scrollable), dc => DarkBackground(dc, brush), DrawOk, (hwnd, result) => EndDialog(hwnd, result)));

    private static void FitScrollbar(nint edit)
    {
        var scroll = new ScrollInfo { Size = (uint)sizeof(ScrollInfo), Mask = 2 }; // SIF_PAGE
        if (!GetScrollInfo(edit, 1, ref scroll))
        {
            Console.Error.WriteLine($"Cannot measure status text; retaining the native scrollbar (Win32 {Marshal.GetLastPInvokeError()}).");
            return;
        }
        FitScrollbar(scroll.Page, () => SendMessageW(edit, 0x00BA, 0, 0), show => ShowScrollBar(edit, 1, show)); // EM_GETLINECOUNT / SB_VERT
    }

    internal static void FitScrollbar(uint visibleLines, Func<nint> wrappedLines, Action<bool> show)
    {
        // Measure wrapped lines at the full width; the scrollbar itself can cause an extra line.
        show(false);
        if (wrappedLines() > visibleLines) show(true);
    }

    internal static void WithBrush(Func<uint, nint> create, Action<nint> show, Func<nint, bool> delete)
    {
        var brush = create(Background);
        if (brush == 0) throw new InvalidOperationException("Could not create the tray dialog background.");
        try { show(brush); }
        finally { _ = delete(brush); }
    }

    internal void CheckResult(nint result, int error)
    {
        if (failure is not null) throw new InvalidOperationException($"Tray dialog failed: {failure.Message}", failure);
        if (result == -1) throw new Win32Exception(error);
    }

    internal nint Handle(nint hwnd, uint message, nuint w, nint l)
    {
        try { return Dispatch(hwnd, message, w, l); }
        catch (Exception ex)
        {
            // Report the failure after DialogBox returns, never through an unmanaged callback.
            failure = ex;
            surface.End(hwnd, 0);
            return 1;
        }
    }

    private nint Dispatch(nint hwnd, uint message, nuint w, nint l)
    {
        switch (message)
        {
            case 0x0110: // WM_INITDIALOG
                surface.Initialize(hwnd);
                Refresh(hwnd);
                return 1;
            case 0x0015 or 0x001A or 0x031A: // system colors, settings, visual styles
                Refresh(hwnd);
                return 0;
            case 0x0136 or 0x0138: // dialog and static control backgrounds
                return dark ? surface.PaintBackground((nint)w) : 0;
            case 0x002B when dark && w == 1: // WM_DRAWITEM for the dark default button
                surface.PaintButton(l);
                return 1;
            case 0x0111 when IsDismissCommand(w, l):
            case TrayIcon.CloseMessage:
                surface.End(hwnd, 1);
                return 1;
            default:
                return 0;
        }
    }

    private static bool IsDismissCommand(nuint w, nint control) => (ushort)w is 1 or 2 && (control == 0 || (w >> 16) == 0);

    private void Refresh(nint hwnd)
    {
        dark = surface.UseDark();
        surface.Apply(hwnd, dark);
    }

    private static void ApplyTheme(nint hwnd, bool dark, bool scrollable)
    {
        var build = Environment.OSVersion.Version.Build;
        WindowsTheme.ApplyTitleBar(build, dark,
            (attribute, enabled) => DwmSetWindowAttribute(hwnd, attribute, ref enabled, sizeof(int)), Console.Error.WriteLine);
        if (scrollable)
            WindowsTheme.ApplyScrollbar(build, dark, theme => SetWindowTheme(GetDlgItem(hwnd, TrayDialogTemplate.TextId), theme, null), Console.Error.WriteLine);
        // Let Windows draw the normal default button in light and high-contrast modes.
        _ = SendDlgItemMessageW(hwnd, 1, 0x00F4, dark ? 0xBu : 1u, 0); // BM_SETSTYLE: BS_OWNERDRAW / BS_DEFPUSHBUTTON
        _ = RedrawWindow(hwnd, 0, 0, 0x0085); // invalidate, erase, include children
    }

    private static nint DarkBackground(nint dc, nint brush)
    {
        _ = SetTextColor(dc, Foreground);
        _ = SetBkColor(dc, Background);
        return brush;
    }

    private static void DrawOk(nint pointer)
    {
        var item = *(DrawItem*)pointer;
        var (fill, showFocus) = ButtonAppearance(item.State);
        var dc = item.Dc;
        var saved = SaveDC(dc);
        if (saved == 0) throw new InvalidOperationException("Could not save the OK button drawing context.");
        try
        {
            var brush = GetStockObject(18); // DC_BRUSH, borrowed; its color belongs to this DC
            _ = SetDCBrushColor(dc, fill);
            _ = FillRect(dc, ref item.Bounds, brush);
            _ = SetDCBrushColor(dc, 0x808080);
            _ = FrameRect(dc, ref item.Bounds, brush);
            _ = SetBkMode(dc, 1); // TRANSPARENT
            _ = SetTextColor(dc, Foreground);
            _ = DrawTextW(dc, "OK", -1, ref item.Bounds, 0x25); // centered, single line
            if (showFocus)
            {
                _ = InflateRect(ref item.Bounds, -3, -3);
                _ = DrawFocusRect(dc, ref item.Bounds);
            }
        }
        finally { _ = RestoreDC(dc, saved); }
    }

    internal static (uint Fill, bool ShowFocus) ButtonAppearance(uint state) =>
        ((state & 1) != 0 ? 0x505050u : 0x353535u, (state & 0x0210) == 0x0010); // ODS_SELECTED, ODS_FOCUS, ODS_NOFOCUSRECT

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo { public uint Size, Mask; public int Min, Max; public uint Page; public int Position, TrackPosition; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawItem
    {
        public uint Type, Id, Item, Action, State;
        public nint Window, Dc;
        public TrayMenu.Rect Bounds;
        public nuint Data;
    }

    private delegate nint DialogProc(nint window, uint message, nuint w, nint l);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint DialogBoxIndirectParamW(nint instance, byte* template, nint owner, DialogProc procedure, nint parameter);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool EndDialog(nint hwnd, nint result);
    [LibraryImport("user32.dll")] private static partial nint SendMessageW(nint hwnd, uint message, nuint w, nint l);
    [LibraryImport("user32.dll")] private static partial nint SendDlgItemMessageW(nint hwnd, int id, uint message, nuint w, nint l);
    [LibraryImport("user32.dll")] private static partial nint GetDlgItem(nint hwnd, int id);
    [LibraryImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetClientRect(nint hwnd, out TrayMenu.Rect rect);
    [LibraryImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetScrollInfo(nint hwnd, int bar, ref ScrollInfo info);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowScrollBar(nint hwnd, int bar, [MarshalAs(UnmanagedType.Bool)] bool show);
    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int SetWindowTheme(nint hwnd, string? app, string? classes);
    [LibraryImport("dwmapi.dll")] private static partial int DwmSetWindowAttribute(nint hwnd, uint field, ref int enabled, uint size);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool RedrawWindow(nint hwnd, nint bounds, nint region, uint flags);
    [LibraryImport("gdi32.dll")] private static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint handle);
    [LibraryImport("gdi32.dll")] private static partial uint SetTextColor(nint dc, uint color);
    [LibraryImport("gdi32.dll")] private static partial uint SetBkColor(nint dc, uint color);
    [LibraryImport("gdi32.dll")] private static partial int SaveDC(nint dc);
    [LibraryImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool RestoreDC(nint dc, int state);
    [LibraryImport("gdi32.dll")] private static partial nint GetStockObject(int index);
    [LibraryImport("gdi32.dll")] private static partial uint SetDCBrushColor(nint dc, uint color);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("user32.dll")] private static partial int FillRect(nint dc, ref TrayMenu.Rect rect, nint brush);
    [LibraryImport("user32.dll")] private static partial int FrameRect(nint dc, ref TrayMenu.Rect rect, nint brush);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int DrawTextW(nint dc, string text, int length, ref TrayMenu.Rect rect, uint flags);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool InflateRect(ref TrayMenu.Rect rect, int x, int y);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool DrawFocusRect(nint dc, ref TrayMenu.Rect rect);
}
