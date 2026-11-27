using DevTunnelSvc.Auth;
using DevTunnelSvc.Platform;
using DevTunnelSvc.Tunnels;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc.Service;

internal static class ServiceHost
{
    internal static async Task RunAsync(string name, string directory, TextWriter log, Action<string> writeEvent,
        AutoUpdateActions backgroundUpdates, TrayLifetime.Factory? createTray, CancellationTokenSource lifetime)
    {
        using var http = GitHubOAuth.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        using var credential = new SessionCredential(new CredentialFile(directory), (token, ct) => GitHubOAuth.RefreshAsync(http, token, ct), TimeProvider.System);
        await using var backend = new TunnelBackend(() => credential.RequireTokenAsync(lifetime.Token), writeEvent: writeEvent);
        await using var service = new TunnelService(backend, credential, new MappingFile(directory), writeEvent);
        await using var updates = new AutoUpdates(directory, backgroundUpdates, writeEvent, lifetime.Token);
        // Close the tray and await any management query before disposing the backend or its credential.
        using var tray = new TrayLifetime(name, writeEvent, new(() => updates.Enabled, updates.Releases, service.ListTunnelsAsync), createTray);
        await tray.WaitAsync(ControlServer.RunAsync(name, (r, ct) => updates.DispatchAsync(r, service.ExecuteAsync, ct), log, writeEvent, lifetime.Token), lifetime);
    }
}
