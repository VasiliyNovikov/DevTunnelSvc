using System.Globalization;
using DevTunnelSvc.Auth;
using DevTunnelSvc.Tunnels;

namespace DevTunnelSvc.Service;

internal sealed class TunnelService : IAsyncDisposable
{
    private readonly ITunnelBackend backend;
    private readonly SessionCredential credential;
    private readonly MappingFile mappings;
    private readonly Action<string> writeEvent;
    private readonly Dictionary<string, MappingSession> sessions = new(StringComparer.Ordinal);
    private IEnumerable<Mapping> SavedMappings => sessions.Values.Where(s => s.Saved).Select(s => s.Map);

    internal TunnelService(ITunnelBackend backend, SessionCredential credential, MappingFile mappings, Action<string> writeEvent)
    {
        this.backend = backend;
        this.credential = credential;
        this.mappings = mappings;
        this.writeEvent = writeEvent;
        foreach (var map in mappings.Read())
            sessions.Add(map.Name, new(map, ct => Authenticated().OpenAsync(map, ct), Task.Delay, writeEvent));
    }

    // The local control server serializes commands; SDK sessions continue independently.
    public async Task<string> ExecuteAsync(Request request, CancellationToken ct)
    {
        var operation = "request";
        try
        {
            var execute = ResolveCommand(request, ct);
            // Only a recognized command name may reach the log, never the request or its result.
            operation = request.Command;
            var result = await execute();
            if (operation is not ("status" or "service-pid")) writeEvent($"Command {operation} completed.");
            return result;
        }
        catch (Exception ex) { writeEvent($"Control {operation} failed: {AppLog.ErrorCode(ex)}"); throw; }
    }

    private Func<Task<string>> ResolveCommand(Request request, CancellationToken ct)
    {
        if (request.Version != Control.ProtocolVersion)
            throw new InvalidDataException("Incompatible control protocol.");
        return request.Command switch
        {
            "login" => () => LoginAsync(request.SignIn, ct),
            "status" => () => Task.FromResult(Status()),
            "service-pid" => () => Task.FromResult(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)),
            "logout" => () => EndSessionAsync(logout: true),
            "create" => () => Authenticated().CreateAsync(region: null, ct),
            // Older services reject this command instead of ignoring the region and creating the tunnel elsewhere.
            "create-in-region" => () => Authenticated().CreateAsync(request.Region ?? throw new ArgumentException("Missing region."), ct),
            "list" => () => ListAsync(ct),
            "regions" => () => RegionsAsync(ct),
            "delete" => () => DeleteAsync(TunnelKey.Parse(request.Tunnel), ct),
            "host" or "connect" => () => OpenAsync(Mapping.Create(request.Command == "host", request.Tunnel, request.TunnelPort, request.LocalPort), ct),
            "stop" or "disconnect" => () => StopAsync(request.Tunnel, clientOnly: request.Command == "disconnect"),
            "shutdown" => () => EndSessionAsync(logout: false),
            "shutdown-for-update" => () => request.ServicePid == Environment.ProcessId ? EndSessionAsync(logout: false)
                : throw new InvalidOperationException("The selected service instance has changed. Check for updates again."),
            _ => throw new ArgumentException("Unknown service command."),
        };
    }

    private ITunnelBackend Authenticated()
    {
        if (!credential.IsSignedIn) throw new InvalidOperationException("Not signed in or saved sign-in expired. Run 'login' first.");
        return backend;
    }

    private async Task<string> LoginAsync(GitHubCredential? signIn, CancellationToken ct)
    {
        if (sessions.Count != 0)
            throw new InvalidOperationException("Stop active mappings before changing accounts.");
        if (signIn is null) throw new ArgumentException("Missing sign-in credential.");
        // A failed logout must not carry a previous account's saved routes into a new grant.
        mappings.Save([]);
        await credential.ReplaceAsync(signIn, ct);
        return "Signed in. Credential saved.";
    }

    private string Status() => string.Join(Environment.NewLine,
        sessions.Values.Select(s => $"{s.Map.Name}  {s.State}  {s.Map.Route}")
            .Prepend(credential.IsSignedIn ? "Service running; signed in." : "Service running; signed out."));

    private async Task<string> ListAsync(CancellationToken ct) => TunnelSummary.FormatTable(await Authenticated().ListAsync(ct));

    private async Task<string> RegionsAsync(CancellationToken ct) => TunnelRegion.FormatTable(await Authenticated().ListRegionsAsync(ct));

    private async Task<string> OpenAsync(Mapping map, CancellationToken ct)
    {
        if (sessions.ContainsKey(map.Name))
            throw new InvalidOperationException($"Mapping {map.Name} already exists.");
        var relay = await Authenticated().OpenAsync(map, ct);
        try { mappings.Save(SavedMappings.Append(map)); }
        catch { await relay.DisposeAsync(); throw; }
        sessions.Add(map.Name, new(map, relay));
        return $"{map.Name}  {map.Route}";
    }

    private async Task<string> StopAsync(string? name, bool clientOnly)
    {
        if (name is null || !sessions.TryGetValue(name, out var item))
            throw new ArgumentException("Mapping not found. Use 'status' to see mapping names.");
        if (clientOnly && item.Map.Host)
            throw new ArgumentException("This is a hosted mapping. Use 'stop' to stop hosting it.");
        mappings.Save(SavedMappings.Where(map => map.Name != name));
        item.Saved = false;
        await item.DisposeAsync();
        sessions.Remove(name);
        return clientOnly ? $"Disconnected {name}." : $"Stopped {name}.";
    }

    private async Task<string> DeleteAsync(TunnelKey key, CancellationToken ct)
    {
        if (sessions.Values.Any(s => s.Map.Tunnel == key))
            throw new InvalidOperationException("Stop this tunnel's active mappings before deleting it.");
        return await Authenticated().DeleteAsync(key, ct) ? $"Deleted {key}." : throw new InvalidOperationException($"Tunnel {key} was not found.");
    }

    private async Task<string> EndSessionAsync(bool logout)
    {
        try { await DisposeAsync(); }
        finally
        {
            if (logout)
            {
                try { mappings.Save([]); }
                finally { await credential.ReplaceAsync(null, CancellationToken.None); }
            }
        }
        return logout ? "Signed out. Saved credential removed." : "Service stopping.";
    }

    public async ValueTask DisposeAsync()
    {
        try { await Task.WhenAll(sessions.Values.Select(s => s.DisposeAsync().AsTask())); }
        finally { sessions.Clear(); }
    }
}
