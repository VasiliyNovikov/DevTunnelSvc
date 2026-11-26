using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DevTunnelSvc.Platform;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class WindowsAppearanceTests
{
    [TestMethod]
    public void MenuReadsTheCurrentAppPreferenceButHighContrastTakesPrecedence()
    {
        var highContrast = false;
        var light = false;
        var denied = false;
        List<int> modes = [];
        List<string> warnings = [];
        var theme = new WindowsTheme(true, () => highContrast, () =>
        {
            Assert.IsFalse(highContrast);
            return denied ? throw new UnauthorizedAccessException("settings denied") : !light;
        }, modes.Add, warnings.Add);
        theme.PrepareMenu();
        highContrast = true;
        theme.PrepareMenu();
        highContrast = false;
        light = true;
        theme.PrepareMenu();
        light = false;
        denied = true;
        theme.PrepareMenu();
        denied = false;
        theme.PrepareMenu();
        Assert.AreEqual("1,0,0,0,1", string.Join(',', modes));
        Assert.Contains("settings denied", warnings.Single());
    }

    [TestMethod]
    public void NativeAppearanceUsesTheCurrentUsersAppSettingOnlyWhenContrastPermitsIt()
    {
        var flags = 0x7Eu; // supported high-contrast features, but HCF_HIGHCONTRASTON is off
        var canRead = true;
        object? appSetting = 0;
        List<string> warnings = [];
        bool Read(uint action, uint size, ref WindowsTheme.HighContrast value, uint options)
        {
            Assert.AreEqual(0x0042u, action);
            Assert.AreEqual((uint)Marshal.SizeOf<WindowsTheme.HighContrast>(), value.Size);
            Assert.AreEqual(value.Size, size);
            Assert.AreEqual(0u, options);
            value.Flags = flags;
            return canRead;
        }
        var theme = WindowsTheme.FromSettings(true, Read, (path, name, fallback) =>
        {
            Assert.IsTrue(canRead && (flags & 1) == 0);
            Assert.AreEqual(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", path);
            Assert.AreEqual("AppsUseLightTheme", name);
            return appSetting ?? fallback;
        }, _ => Assert.Fail(), warnings.Add);
        Assert.IsTrue(theme.UseDarkPalette());
        flags |= 1;
        Assert.IsFalse(theme.UseDarkPalette());
        flags = 0;
        appSetting = null; // absent: Windows defaults to light
        Assert.IsFalse(theme.UseDarkPalette());
        canRead = false;
        Assert.IsFalse(theme.UseDarkPalette());
        Assert.Contains("system colors", warnings.Single());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnsupportedOrMissingMenuThemeApisKeepTheNativeMenuUsable(bool missingExport)
    {
        List<string> warnings = [];
        var attempts = 0;
        var theme = new WindowsTheme(missingExport, () => false, () => true,
            _ => { attempts++; throw new EntryPointNotFoundException("missing export"); }, warnings.Add);
        theme.PrepareMenu();
        theme.PrepareMenu();
        Assert.AreEqual(missingExport ? 1 : 0, attempts);
        Assert.HasCount(attempts, warnings);
        if (missingExport) Assert.Contains("missing export", warnings.Single());
        Assert.IsTrue(theme.UseDarkPalette(), "Missing menu APIs should not disable the independent dialog palette.");
    }

    [TestMethod]
    [DataRow(17762, false, 0)]
    [DataRow(17763, true, 19)]
    [DataRow(19040, false, 19)]
    [DataRow(19041, true, 20)]
    public void TitleBarUsesTheSupportedAttributeOrLeavesWindowsInControl(int build, bool dark, int attribute)
    {
        List<string> warnings = [];
        var applied = 0;
        WindowsTheme.ApplyTitleBar(build, dark, (a, enabled) =>
        {
            applied = (int)a;
            Assert.AreEqual(dark ? 1 : 0, enabled);
            return dark ? 0 : unchecked((int)0x80070057);
        }, warnings.Add);
        Assert.AreEqual(attribute, applied);
        if (attribute != 0 && !dark) Assert.Contains("80070057", warnings.Single());
        else Assert.IsEmpty(warnings);
    }

    [TestMethod]
    [DataRow(2, false)]
    [DataRow(3, false)]
    [DataRow(4, true)]
    public void StatusUsesAllAvailableWidthBeforeDecidingWhetherToShowTheScrollbar(int lines, bool overflow)
    {
        var visible = true;
        TrayDialog.FitScrollbar(3, () => { Assert.IsFalse(visible, "Measure wrapping without a scrollbar."); return lines; }, show => visible = show);
        Assert.AreEqual(overflow, visible);
    }

    [TestMethod]
    public void ScrollbarUsesTheDarkThemeOnlyWhenSupportedAndRestoresSystemAppearance()
    {
        List<string?> classes = [];
        List<string> warnings = [];
        int Apply(string? theme) { classes.Add(theme); return theme is null ? 0 : unchecked((int)0x80070057); }
        WindowsTheme.ApplyScrollbar(18361, true, Apply, warnings.Add);
        Assert.IsEmpty(classes);
        WindowsTheme.ApplyScrollbar(18362, true, Apply, warnings.Add);
        WindowsTheme.ApplyScrollbar(19041, false, Apply, warnings.Add);
        Assert.HasCount(2, classes);
        Assert.AreEqual("DarkMode_Explorer", classes[0]);
        Assert.IsNull(classes[1]);
        Assert.Contains("80070057", warnings.Single());
    }

    [TestMethod]
    public void TrayDialogRefreshesAndReturnsPaintingToWindowsInLightMode()
    {
        var dark = false;
        List<string> actions = [];
        var dialog = new TrayDialog(new(() => dark, hwnd => actions.Add($"icon:{hwnd}"),
            (hwnd, useDark) => actions.Add($"{hwnd}:{useDark}"), dc => { Assert.AreEqual((nint)22, dc); return 33; },
            item => actions.Add($"button:{item}"), (_, _) => Assert.Fail()));
        Assert.AreEqual((nint)1, dialog.Handle(11, 0x0110, 0, 0));
        foreach (var change in new uint[] { 0x0015, 0x001A, 0x031A })
        {
            dark = !dark;
            Assert.AreEqual((nint)0, dialog.Handle(11, change, 0, 0));
            foreach (var paint in new uint[] { 0x0136, 0x0138 })
                Assert.AreEqual((nint)(dark ? 33 : 0), dialog.Handle(11, paint, 22, 0));
            Assert.AreEqual((nint)(dark ? 1 : 0), dialog.Handle(11, 0x002B, 1, 44));
            Assert.AreEqual((nint)0, dialog.Handle(11, 0x002B, 101, 44));
        }
        Assert.AreEqual("icon:11,11:False,11:True,button:44,11:False,11:True,button:44", string.Join(',', actions));
    }

    [TestMethod]
    public void TrayDialogClosesOnOkEscapeAndCloseButNotOtherControlNotifications()
    {
        List<(nint, nint)> ends = [];
        var dialog = new TrayDialog(new(() => false, _ => Assert.Fail(), (_, _) => Assert.Fail(), _ => throw new AssertFailedException(),
            _ => Assert.Fail(), (hwnd, result) => ends.Add((hwnd, result))));
        Assert.AreEqual((nint)0, dialog.Handle(11, 0x0111, 3, 0));
        Assert.AreEqual((nint)0, dialog.Handle(11, 0x0111, 0x10001, 55));
        Assert.AreEqual((nint)0, dialog.Handle(11, 0x9999, 0, 0));
        Assert.AreEqual((nint)1, dialog.Handle(11, 0x0111, 1, 55)); // BN_CLICKED
        Assert.AreEqual((nint)1, dialog.Handle(11, 0x0111, 2, 0));  // Escape
        Assert.AreEqual((nint)1, dialog.Handle(11, 0x0010, 0, 0));
        CollectionAssert.AreEqual(Enumerable.Repeat(((nint)11, (nint)1), 3).ToArray(), ends);
    }

    [TestMethod]
    public void DarkDefaultButtonIndicatesPressAndHonorsWindowsFocusCues()
    {
        var normal = TrayDialog.ButtonAppearance(0);
        var pressed = TrayDialog.ButtonAppearance(1);
        Assert.IsFalse(normal.ShowFocus);
        Assert.IsFalse(pressed.ShowFocus);
        Assert.AreNotEqual(normal.Fill, pressed.Fill);
        Assert.AreEqual((pressed.Fill, true), TrayDialog.ButtonAppearance(0x11));
        Assert.AreEqual((normal.Fill, false), TrayDialog.ButtonAppearance(0x210)); // keyboard cues hidden
        Assert.AreEqual((normal.Fill, true), TrayDialog.ButtonAppearance(0x10));
    }

    [TestMethod]
    public void CallbackFailuresEndTheModalLoopAndFreeTheOwnedBrushBeforeSurfacing()
    {
        var released = 0;
        var ended = 0;
        var dialog = new TrayDialog(new(() => throw new IOException("theme read failed"), _ => { }, (_, _) => Assert.Fail(),
            _ => throw new AssertFailedException(), _ => Assert.Fail(), (hwnd, result) =>
            { Assert.AreEqual(((nint)11, (nint)0), (hwnd, result)); ended++; }));
        dialog.CheckResult(1, 0);
        Assert.AreEqual(87, Assert.ThrowsExactly<Win32Exception>(() => dialog.CheckResult(-1, 87)).NativeErrorCode);
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => TrayDialog.WithBrush(color =>
        {
            Assert.AreEqual(0x202020u, color);
            return 33;
        }, brush =>
        {
            Assert.AreEqual((nint)33, brush);
            Assert.AreEqual((nint)1, dialog.Handle(11, 0x0110, 0, 0));
            dialog.CheckResult(0, 0);
        }, brush => { Assert.AreEqual((nint)33, brush); released++; return true; }));
        Assert.Contains("theme read failed", failure.Message);
        Assert.AreEqual((1, 1), (ended, released));
        Assert.Contains("tray dialog background", Assert.ThrowsExactly<InvalidOperationException>(() =>
            TrayDialog.WithBrush(_ => 0, _ => Assert.Fail(), _ => throw new AssertFailedException())).Message);
    }

    [TestMethod]
    [DataRow("About DevTunnelSvc", "DevTunnelSvc 1.2.3\r\n\r\nA & B", false)]
    [DataRow("About DevTunnelSvc", "DevTunnelSvc 10.2.3\r\n\r\nA & B", false)]
    [DataRow("DevTunnelSvc Status", "Service running; signed in.\r\nMapping A & B", true)]
    public void DialogTemplateLaysOutTheIconLiteralTextAndDefaultButton(string title, string message, bool scrollable)
    {
        var template = TrayDialogTemplate.Build(new(title, message, scrollable));
        using var buffer = new MemoryStream(template);
        using var r = new BinaryReader(buffer, Encoding.Unicode);
        string Text() { var start = (int)buffer.Position; while (r.ReadUInt16() != 0) { } return Encoding.Unicode.GetString(template, start, (int)buffer.Position - start - 2); }
        Assert.AreNotEqual(0u, r.ReadUInt32() & 0x40); // DS_SETFONT
        Assert.AreEqual(0u, r.ReadUInt32());
        Assert.AreEqual(3, (int)r.ReadUInt16());
        r.ReadInt32();
        var width = r.ReadInt16();
        var height = r.ReadInt16();
        Assert.AreEqual(0u, r.ReadUInt32()); // no menu, default dialog class
        Assert.AreEqual(title, Text());
        Assert.IsGreaterThan((ushort)0, r.ReadUInt16());
        Assert.AreEqual("Segoe UI", Text());
        var body = scrollable ? (101, 0x81, 0x210844u, message) : (101, 0x82, 0x80u, message);
        foreach (var (id, atom, style, text) in new[] { (100, 0x82, 0x243u, ""), body, (1, 0x80, 0x10001u, "OK") })
        {
            buffer.Position = (buffer.Position + 3) & ~3L;
            Assert.AreEqual(0x50000000u | style, r.ReadUInt32());
            Assert.AreEqual(0u, r.ReadUInt32());
            var x = r.ReadInt16();
            var y = r.ReadInt16();
            var w = r.ReadInt16();
            var h = r.ReadInt16();
            Assert.IsTrue(x >= 0 && y >= 0 && w > 0 && h > 0 && x + w <= width && y + h <= height);
            Assert.AreEqual(id, (int)r.ReadUInt16());
            Assert.AreEqual(ushort.MaxValue, r.ReadUInt16());
            Assert.AreEqual(atom, (int)r.ReadUInt16());
            Assert.AreEqual(text, Text());
            Assert.AreEqual((ushort)0, r.ReadUInt16());
        }
        Assert.AreEqual(buffer.Length, buffer.Position);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void WindowsCanThemeTheNativeTrayDialogsWithoutShowingThem(bool status, bool overflow)
    {
        if (OperatingSystem.IsWindows()) CheckNativeDialog(status, overflow);
    }

    [SupportedOSPlatform("windows")]
    private static void CheckNativeDialog(bool status, bool overflow)
    {
        var report = "Service running; signed in.\r\n" + string.Join(" ", Enumerable.Range(1, overflow ? 150 : 3).Select(n => $"Example mapping {n}"));
        var content = status ? new TrayDialogContent("DevTunnelSvc Status", report, true)
            : new TrayDialogContent("About DevTunnelSvc", "DevTunnelSvc 1.2.3\r\n\r\nHosts & forwards");
        var dark = true;
        var brush = GetStockObject(4); // Borrow a real black brush for this hidden window.
        var titleIcon = TrayIcon.LoadArtwork(16);
        nint artwork = 0;
        var pixels = 0;
        var dialog = TrayDialog.Create(titleIcon, brush, () => dark, status, size => { pixels = size; return artwork = TrayIcon.LoadArtwork(size); });
        DialogProc procedure = dialog.Handle;
        var hwnd = CreateDialogIndirectParamW(0, TrayDialogTemplate.Build(content), 0, procedure, 0);
        try
        {
            Assert.AreNotEqual((nint)0, hwnd, $"CreateDialogIndirectParamW failed: {Marshal.GetLastPInvokeError()}");
            dialog.CheckResult(1, 0);
            var iconControl = GetDlgItem(hwnd, TrayDialogTemplate.IconId);
            Assert.IsTrue(GetClientRect(iconControl, out var rect));
            Assert.AreEqual(Math.Min(rect.Right, rect.Bottom), pixels);
            Assert.IsGreaterThan(16, pixels);
            Assert.AreEqual(titleIcon, SendMessageW(hwnd, 0x007F, 0, 0)); // WM_GETICON / ICON_SMALL
            Assert.AreNotEqual(titleIcon, artwork);
            Assert.AreEqual(artwork, SendMessageW(iconControl, 0x0171, 0, 0)); // STM_GETICON
            CheckDialogText(hwnd, content, overflow);
            var button = GetDlgItem(hwnd, 1);
            Assert.AreEqual(0xB, GetWindowLongW(button, -16) & 0xF);
            var dc = GetDC(hwnd);
            try
            {
                Assert.AreEqual(brush, SendMessageW(hwnd, 0x0138, dc, GetDlgItem(hwnd, 101)));
                Assert.AreEqual(0x202020u, GetBkColor(dc));
                Assert.AreEqual(0xF5F5F5u, GetTextColor(dc));
            }
            finally { _ = ReleaseDC(hwnd, dc); }
            dark = false;
            _ = SendMessageW(hwnd, 0x001A, 0, 0);
            Assert.AreEqual(1, GetWindowLongW(button, -16) & 0xF);
        }
        finally
        {
            if (hwnd != 0) Assert.IsTrue(DestroyWindow(hwnd));
            if (artwork != 0) Assert.IsTrue(TrayIcon.DestroyIcon(artwork));
            Assert.IsTrue(TrayIcon.DestroyIcon(titleIcon));
            GC.KeepAlive(procedure);
        }
    }

    private static void CheckDialogText(nint hwnd, TrayDialogContent content, bool overflow)
    {
        var text = new char[content.Text.Length + 1];
        Assert.AreEqual((uint)content.Text.Length, GetDlgItemTextW(hwnd, TrayDialogTemplate.TextId, text, text.Length));
        Assert.AreEqual(content.Text, new string(text, 0, content.Text.Length));
        Assert.AreEqual(2u, GetDlgItemTextW(hwnd, 1, text, text.Length));
        Assert.AreEqual("OK", new string(text, 0, 2));
        var body = GetDlgItem(hwnd, TrayDialogTemplate.TextId);
        Assert.AreEqual(overflow, (GetWindowLongW(body, -16) & 0x200000) != 0); // WS_VSCROLL
        if (!overflow) return;
        _ = SendMessageW(body, 0x00B6, 0, 79); // EM_LINESCROLL
        Assert.IsTrue(SendMessageW(body, 0x00CE, 0, 0) > 0); // EM_GETFIRSTVISIBLELINE
    }

    private delegate nint DialogProc(nint hwnd, uint message, nuint w, nint l);
    [DllImport("user32", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateDialogIndirectParamW(nint instance, byte[] template, nint owner, DialogProc procedure, nint data);
    [DllImport("user32", ExactSpelling = true)] private static extern nint GetDlgItem(nint hwnd, int id);
    [DllImport("user32", ExactSpelling = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint hwnd, out TrayMenu.Rect rect);
    [DllImport("user32", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern uint GetDlgItemTextW(nint hwnd, int id, [Out] char[] text, int length);
    [DllImport("user32", ExactSpelling = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32", ExactSpelling = true)] private static extern nint SendMessageW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32", ExactSpelling = true)] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32", ExactSpelling = true)] private static extern nint GetDC(nint hwnd);
    [DllImport("user32", ExactSpelling = true)] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32", ExactSpelling = true)] private static extern nint GetStockObject(int index);
    [DllImport("gdi32", ExactSpelling = true)] private static extern uint GetTextColor(nint dc);
    [DllImport("gdi32", ExactSpelling = true)] private static extern uint GetBkColor(nint dc);
}
