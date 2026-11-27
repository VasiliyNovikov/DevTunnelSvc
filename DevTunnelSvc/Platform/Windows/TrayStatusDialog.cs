using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using DevTunnelSvc.Service;
using DevTunnelSvc.Tunnels;

namespace DevTunnelSvc.Platform;

internal sealed class TrayStatusDialog : ThemedTrayForm
{
    private const string Cloud = "Available tunnels and ports", Local = "Local hosts and connections";
    private readonly CancellationTokenSource closed = new();
    private readonly Label signIn = Heading(), cloudHeading = Heading(), localHeading = Heading();
    private readonly ListView tunnels = Details(Cloud, ("Tunnel", 250), ("Hosts", 65), ("Clients", 65), ("Expires (UTC)", 155), ("Ports", 180));
    private readonly ListView mappings = Details(Local, ("Role", 65), ("Tunnel", 215), ("Tunnel port", 95), ("Local target", 120), ("Local listener", 120), ("State", 150));

    private TrayStatusDialog(Icon icon) : base(icon, "DevTunnelSvc Status")
    {
        ClientSize = new Size(840, 480);
        MinimumSize = new Size(600, 360);
        MaximizeBox = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var style in new[] { SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize })
            layout.RowStyles.Add(new RowStyle(style, 50));
        layout.Controls.Add(signIn, 0, 0);
        layout.Controls.Add(cloudHeading, 0, 1);
        layout.Controls.Add(tunnels, 0, 2);
        layout.Controls.Add(localHeading, 0, 3);
        layout.Controls.Add(mappings, 0, 4);
        var ok = new Button { Text = "OK", AutoSize = true, Anchor = AnchorStyles.Right, UseVisualStyleBackColor = true };
        ok.Click += (_, _) => Close();
        layout.Controls.Add(ok, 0, 5);
        Controls.Add(layout);
        AcceptButton = CancelButton = ok;
    }

    internal static TrayStatusTarget Open(Form owner, Icon icon, LocalStatus local)
    {
        var dialog = GetOrCreate(owner, icon, local);
        dialog.Present();
        return new(dialog.SetTunnels, dialog.closed.Token);
    }

    internal static TrayStatusDialog GetOrCreate(Form owner, Icon icon, LocalStatus local)
    {
        var dialog = owner.OwnedForms.OfType<TrayStatusDialog>().SingleOrDefault() ?? new TrayStatusDialog(icon) { Owner = owner };
        dialog.signIn.Text = local.SignedIn ? "Service running; signed in." : "Service running; signed out.";
        dialog.cloudHeading.Text = local.SignedIn ? $"{Cloud} (loading...)" : $"{Cloud} (sign in to view)";
        dialog.tunnels.Items.Clear();
        dialog.localHeading.Text = local.Mappings.Length == 0 ? $"{Local} (none)" : Local;
        Fill(dialog.mappings, local.Mappings.Select(MappingCells));
        return dialog;
    }

    internal void SetTunnels(IReadOnlyList<TunnelSummary> rows, string? error)
    {
        cloudHeading.Text = error is not null ? $"{Cloud}: {error}" : rows.Count == 0 ? $"{Cloud} (none)" : Cloud;
        Fill(tunnels, TunnelSummary.Rows(rows));
    }

    private static string[] MappingCells(LocalMappingStatus mapping)
    {
        var local = $"localhost:{mapping.LocalPort}";
        return [mapping.Host ? "Host" : "Client", mapping.Tunnel, mapping.TunnelPort.ToString(CultureInfo.InvariantCulture),
            mapping.Host ? local : "-", mapping.Host ? "-" : local, mapping.State];
    }

    private static Label Heading() => new() { Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false, Margin = new Padding(3, 5, 3, 3) };

    private static ListView Details(string name, params (string Title, int Width)[] columns)
    {
        var list = new ListView { View = View.Details, Dock = DockStyle.Fill, FullRowSelect = true, AccessibleName = name };
        foreach (var (title, width) in columns) list.Columns.Add(title, width);
        return list;
    }

    private static void Fill(ListView list, IEnumerable<string[]> rows)
    {
        list.BeginUpdate();
        try { list.Items.Clear(); list.Items.AddRange(rows.Select(cells => new ListViewItem(cells)).ToArray()); }
        finally { list.EndUpdate(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !closed.IsCancellationRequested) { closed.Cancel(); closed.Dispose(); }
        base.Dispose(disposing);
    }
}
