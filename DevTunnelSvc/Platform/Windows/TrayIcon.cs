using System.Runtime.ExceptionServices;
using System.Windows.Forms;

namespace DevTunnelSvc.Platform;

internal sealed class TrayIcon : IDisposable
{
    private readonly Thread thread;
    private readonly CancellationTokenSource shutdown = new();
    private int disposed;

    internal TrayIcon(string pipeName, Action<string> writeEvent, TrayQueries queries, Action<Exception?> stopped)
        : this(pipeName, writeEvent, queries, stopped, tray => tray.Visible = true) { }

    internal TrayIcon(string pipeName, Action<string> writeEvent, TrayQueries queries, Action<Exception?> stopped, Action<NotifyIcon> show)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() =>
        {
            try { Run(pipeName, writeEvent, queries, show, ready.SetResult); }
            catch (Exception ex) { if (!ready.TrySetException(ex)) stopped(ex); return; }
            stopped(null);
        })
        { IsBackground = true, Name = "DevTunnelSvc tray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { ready.Task.GetAwaiter().GetResult(); }
        catch { shutdown.Dispose(); throw; }
    }

    private void Run(string pipeName, Action<string> writeEvent, TrayQueries queries, Action<NotifyIcon> show, Action ready)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, threadScope: true);
        var ct = shutdown.Token;
        using var owner = TrayWindowHost.CreateOwner();
        _ = owner.Handle; // Marshal service shutdown to the tray thread even when no dialog is open.
        using var icon = TrayDialog.LoadArtwork(SystemInformation.SmallIconSize.Width);
        using var actions = new TrayMenu(pipeName, shutdown.Cancel, queries, TrayMenu.LaunchUpdater, local => TrayStatusDialog.Open(owner, icon, local),
            content => TrayDialog.ShowOwned(owner, icon, content), writeEvent);
        using var popup = CreatePopup(actions, text => TrayWindowHost.ConfirmAutoUpdate(owner, icon, text, ct), ct);
        using var tray = new NotifyIcon { Icon = icon, Text = "DevTunnelSvc", ContextMenuStrip = popup };
        show(tray);
        using var stop = ct.Register(() => owner.BeginInvoke((Action)(async () =>
        {
            await Task.WhenAll(actions.PendingStatus, actions.PendingConfiguration, actions.PendingUpdate);
            Application.ExitThread();
        })));
        Pump(ready);
    }

    private static void Pump(Action ready)
    {
        Exception? failure = null;
        void Fail(object sender, ThreadExceptionEventArgs e) { failure = e.Exception; Application.ExitThread(); }
        Application.ThreadException += Fail;
        try { ready(); Application.Run(); }
        finally { Application.ThreadException -= Fail; }
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    internal static ContextMenuStrip CreatePopup(TrayMenu actions, Func<string, bool> confirm, CancellationToken ct)
    {
        var popup = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        ToolStripItem Add(string text, TrayMenu.Command command) => popup.Items.Add(text, null, async (_, _) => await actions.SelectAsync(command, ct));
        var status = Add("&Status", TrayMenu.Command.Status);
        var selectedTag = actions.AvailableTag;
        static string UpdateText(string? tag) => tag is null ? "Check for &updates" : $"&Update to {tag}";
        var update = popup.Items.Add(UpdateText(selectedTag), null, async (_, _) => await actions.SelectUpdateAsync(selectedTag, ct));
        var automatic = new ToolStripMenuItem("Auto-up&date");
        automatic.Click += async (_, _) => await actions.ConfigureAutoUpdateAsync(!automatic.Checked, confirm, ct);
        popup.Items.Add(automatic);
        Add("&About", TrayMenu.Command.About);
        popup.Items.Add(new ToolStripSeparator());
        Add("E&xit", TrayMenu.Command.Exit);
        popup.Opening += (_, e) =>
        {
            Application.SetColorMode(SystemColorMode.System);
            status.Enabled = actions.PendingStatus.IsCompleted;
            selectedTag = actions.AvailableTag;
            update.Text = UpdateText(selectedTag);
            update.Enabled = !actions.Updating && actions.PendingUpdate.IsCompleted;
            automatic.Checked = actions.AutoUpdateEnabled;
            automatic.Enabled = actions.PendingConfiguration.IsCompleted;
            e.Cancel = ct.IsCancellationRequested;
        };
        return popup;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        thread.Join();
        shutdown.Dispose();
    }
}
