using System.ComponentModel;
using System.Runtime.InteropServices;
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
        Assert.IsTrue(theme.UseDarkPalette(), "Missing menu APIs should not disable the independent About palette.");
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
    public void AboutRefreshesAnOpenDialogAndReturnsPaintingToWindowsInLightMode()
    {
        var dark = false;
        List<string> actions = [];
        var dialog = new AboutDialog(new(() => dark, hwnd => actions.Add($"icon:{hwnd}"),
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
    public void AboutClosesOnOkEscapeAndCloseButNotOtherControlNotifications()
    {
        List<(nint, nint)> ends = [];
        var dialog = new AboutDialog(new(() => false, _ => Assert.Fail(), (_, _) => Assert.Fail(), _ => throw new AssertFailedException(),
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
        var normal = AboutDialog.ButtonAppearance(0);
        var pressed = AboutDialog.ButtonAppearance(1);
        Assert.IsFalse(normal.ShowFocus);
        Assert.IsFalse(pressed.ShowFocus);
        Assert.AreNotEqual(normal.Fill, pressed.Fill);
        Assert.AreEqual((pressed.Fill, true), AboutDialog.ButtonAppearance(0x11));
        Assert.AreEqual((normal.Fill, false), AboutDialog.ButtonAppearance(0x210)); // keyboard cues hidden
        Assert.AreEqual((normal.Fill, true), AboutDialog.ButtonAppearance(0x10));
    }

    [TestMethod]
    public void CallbackFailuresEndTheModalLoopAndFreeTheOwnedBrushBeforeSurfacing()
    {
        var released = 0;
        var ended = 0;
        var dialog = new AboutDialog(new(() => throw new IOException("theme read failed"), _ => { }, (_, _) => Assert.Fail(),
            _ => throw new AssertFailedException(), _ => Assert.Fail(), (hwnd, result) =>
            { Assert.AreEqual(((nint)11, (nint)0), (hwnd, result)); ended++; }));
        dialog.CheckResult(1, 0);
        Assert.AreEqual(87, Assert.ThrowsExactly<Win32Exception>(() => dialog.CheckResult(-1, 87)).NativeErrorCode);
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => AboutDialog.WithBrush(color =>
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
        Assert.Contains("About dialog background", Assert.ThrowsExactly<InvalidOperationException>(() =>
            AboutDialog.WithBrush(_ => 0, _ => Assert.Fail(), _ => throw new AssertFailedException())).Message);
    }

    [TestMethod]
    [DataRow("DevTunnelSvc 1.2.3\r\n\r\nA & B")]
    [DataRow("DevTunnelSvc 10.2.3\r\n\r\nA & B")]
    public void AboutTemplateLaysOutTheIconLiteralVersionAndDefaultButton(string message)
    {
        var template = AboutTemplate.Build(message);
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
        Assert.AreEqual("About DevTunnelSvc", Text());
        Assert.IsGreaterThan((ushort)0, r.ReadUInt16());
        Assert.AreEqual("Segoe UI", Text());
        foreach (var (id, atom, style, text) in new[] { (100, 0x82, 0x43u, ""), (101, 0x82, 0x80u, message), (1, 0x80, 0x10001u, "OK") })
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
    public void WindowsCanThemeTheNativeAboutDialogWithoutShowingIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string message = "DevTunnelSvc 1.2.3\r\n\r\nHosts & forwards";
        var dark = true;
        var brush = GetStockObject(4); // Borrow a real black brush for this hidden window.
        var dialog = AboutDialog.Create(0, brush, () => dark);
        DialogProc procedure = dialog.Handle;
        var hwnd = CreateDialogIndirectParamW(0, AboutTemplate.Build(message), 0, procedure, 0);
        Assert.AreNotEqual((nint)0, hwnd, $"CreateDialogIndirectParamW failed: {Marshal.GetLastPInvokeError()}");
        try
        {
            Assert.AreNotEqual((nint)0, GetDlgItem(hwnd, AboutTemplate.IconId));
            var text = new char[128];
            Assert.AreEqual((uint)message.Length, GetDlgItemTextW(hwnd, 101, text, text.Length));
            Assert.AreEqual(message, new string(text, 0, message.Length));
            Assert.AreEqual(2u, GetDlgItemTextW(hwnd, 1, text, text.Length));
            Assert.AreEqual("OK", new string(text, 0, 2));
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
        finally { Assert.IsTrue(DestroyWindow(hwnd)); GC.KeepAlive(procedure); }
    }

    private delegate nint DialogProc(nint hwnd, uint message, nuint w, nint l);
    [DllImport("user32", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateDialogIndirectParamW(nint instance, byte[] template, nint owner, DialogProc procedure, nint data);
    [DllImport("user32", ExactSpelling = true)] private static extern nint GetDlgItem(nint hwnd, int id);
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
