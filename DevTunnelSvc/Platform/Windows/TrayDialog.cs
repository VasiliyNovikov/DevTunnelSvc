using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DevTunnelSvc.Platform;

internal sealed class TrayDialog : Form
{
    private bool dark = Application.IsDarkModeEnabled;

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Use the bundled executable's icon when the managed module has no file path.")]
    internal static Icon LoadArtwork(int pixels)
    {
        var module = typeof(TrayDialog).Assembly.Location;
        return Icon.ExtractIcon(module.Length == 0 ? ServiceLifetime.ApplicationExecutable() : module, 0, pixels)
            ?? throw new InvalidOperationException("Could not load the embedded DevTunnelSvc icon.");
    }

    internal static void ShowOwned(Form owner, Icon icon, TrayDialogContent content)
    {
        var dialog = new TrayDialog(icon, content);
        try { dialog.Show(owner); dialog.Activate(); }
        catch { dialog.Dispose(); throw; }
    }

    internal TrayDialog(Icon titleIcon, TrayDialogContent content)
    {
        Text = content.Title;
        Icon = titleIcon;
        ShowInTaskbar = MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = content.Scrollable ? new Size(600, 290) : new Size(408, 144);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 2 };
        Controls.Add(layout);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(Artwork(), 0, 0);
        layout.Controls.Add(Body(content), 1, 0);
        var ok = new Button { Text = "OK", Anchor = AnchorStyles.Right, AutoSize = true, MinimumSize = new Size(80, 25), UseVisualStyleBackColor = true };
        ok.Click += (_, _) => Close();
        layout.Controls.Add(ok, 1, 1);
        AcceptButton = CancelButton = ok;
        ActiveControl = ok;
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
        // SystemEvents dispatches to the WinForms context captured on the dialog thread.
        // Post the palette refresh so the system-events thread can finish its notification first.
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

    private static Control Body(TrayDialogContent content) => content.Scrollable
        ? new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            BackColor = SystemColors.Control,
            ForeColor = SystemColors.ControlText,
            AccessibleName = content.Title,
            Text = content.Text
        }
        : new Label { Dock = DockStyle.Fill, UseMnemonic = false, Text = content.Text };

    private static PictureBox Artwork()
    {
        var picture = new PictureBox { Size = new Size(44, 44), Margin = new Padding(0, 0, 16, 0), SizeMode = PictureBoxSizeMode.CenterImage };
        void Update(object? sender, EventArgs e)
        {
            var pixels = Math.Min(picture.ClientSize.Width, picture.ClientSize.Height);
            if (!picture.IsHandleCreated || pixels <= 0 || picture.Image?.Width == pixels) return;
            using var icon = LoadArtwork(pixels);
            var previous = picture.Image;
            picture.Image = icon.ToBitmap();
            previous?.Dispose();
        }
        picture.HandleCreated += Update;
        picture.SizeChanged += Update;
        picture.Disposed += (_, _) => picture.Image?.Dispose();
        return picture;
    }
}
