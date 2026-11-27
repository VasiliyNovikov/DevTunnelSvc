using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Windows.Forms;

namespace DevTunnelSvc.Platform;

internal sealed class TrayDialog : ThemedTrayForm
{
    private readonly Control body;

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Use the bundled executable's icon when the managed module has no file path.")]
    internal static Icon LoadArtwork(int pixels)
    {
        var module = typeof(TrayDialog).Assembly.Location;
        return Icon.ExtractIcon(module.Length == 0 ? ServiceLifetime.ApplicationExecutable() : module, 0, pixels)
            ?? throw new InvalidOperationException("Could not load the embedded DevTunnelSvc icon.");
    }

    internal static void ShowOwned(Form owner, Icon icon, TrayDialogContent content) => GetOrCreate(owner, icon, content).Present();

    internal static TrayDialog GetOrCreate(Form owner, Icon icon, TrayDialogContent content)
    {
        foreach (var window in owner.OwnedForms)
        {
            if (window is not TrayDialog dialog || dialog.Text != content.Title) continue;
            dialog.body.Text = content.Text;
            return dialog;
        }
        return new TrayDialog(icon, content) { Owner = owner };
    }

    internal TrayDialog(Icon titleIcon, TrayDialogContent content, string? confirmAction = null) : base(titleIcon, content.Title)
    {
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = confirmAction is not null || content.Scrollable ? new Size(600, 290) : new Size(408, 144);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 2 };
        Controls.Add(layout);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(Artwork(), 0, 0);
        body = Body(content);
        layout.Controls.Add(body, 1, 0);
        AddButtons(layout, confirmAction);
    }

    private void AddButtons(TableLayoutPanel layout, string? confirmAction)
    {
        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, WrapContents = false };
        Button Add(string text, DialogResult result)
        {
            var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(80, 25), DialogResult = result, UseVisualStyleBackColor = true };
            buttons.Controls.Add(button);
            return button;
        }
        if (confirmAction is not null) Add(confirmAction, DialogResult.Yes);
        var dismiss = Add(confirmAction is null ? "OK" : "Cancel", DialogResult.Cancel);
        dismiss.Click += (_, _) => Close();
        layout.Controls.Add(buttons, 1, 1);
        AcceptButton = CancelButton = dismiss;
        ActiveControl = dismiss;
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
