using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DevTunnelSvc.Platform;

internal abstract class ThemedTrayForm : Form
{
    private bool dark = Application.IsDarkModeEnabled;

    protected ThemedTrayForm(Icon icon, string title)
    {
        Text = title;
        Icon = icon;
        ShowInTaskbar = MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    internal void Present()
    {
        try
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
        }
        catch { Dispose(); throw; }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        base.Dispose(disposing);
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Post to the dialog thread so the system-events thread can finish its notification first.
        if (IsHandleCreated && !Disposing && dark != Application.IsDarkModeEnabled) BeginInvoke((Action)RefreshTheme);
    }

    private void RefreshTheme()
    {
        if (!IsHandleCreated || dark == Application.IsDarkModeEnabled) return;
        dark = Application.IsDarkModeEnabled;
        Application.SetColorMode(SystemColorMode.System);
        // WinForms applies the non-client theme when it creates the window handle.
        RecreateHandle();
    }
}
