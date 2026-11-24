using DevTunnelSvc.Auth;
using DevTunnelSvc.Tunnels;

namespace DevTunnelSvc.Service;

internal sealed class TunnelService(ITunnelBackend backend, SessionCredential credential) : IAsyncDisposable
{
    private readonly Dictionary<string, (Mapping Map, ILiveTunnel Relay)> sessions = new(StringComparer.Ordinal);

    // The local control server serializes commands; SDK sessions continue independently.
    public Task<string> ExecuteAsync(Request request, CancellationToken ct)
    {
        if (request.Version != Control.ProtocolVersion)
            throw new InvalidDataException("Incompatible control protocol.");
        return request.Command switch
        {
            "login" => LoginAsync(request.SignIn, ct),
            "status" => Task.FromResult(Status()),
            "logout" => EndSessionAsync(logout: true),
            "create" => Authenticated().CreateAsync(ct),
            "delete" => DeleteAsync(TunnelKey.Parse(request.Tunnel), ct),
            "host" or "connect" => OpenAsync(Mapping.Create(request.Command == "host", request.Tunnel, request.TunnelPort, request.LocalPort), ct),
            "stop" => StopAsync(request.Tunnel),
            "shutdown" => EndSessionAsync(logout: false),
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
        await credential.ReplaceAsync(signIn ?? throw new ArgumentException("Missing sign-in credential."), ct);
        return "Signed in. Credential saved.";
    }

    private string Status() => string.Join(Environment.NewLine,
        sessions.Values.Select(s => $"{s.Map.Name}  {s.Relay.State}  {s.Map.Route}")
            .Prepend(credential.IsSignedIn ? "Service running; signed in." : "Service running; signed out."));

    private async Task<string> OpenAsync(Mapping map, CancellationToken ct)
    {
        if (sessions.ContainsKey(map.Name))
            throw new InvalidOperationException($"Mapping {map.Name} already exists.");
        var relay = await Authenticated().OpenAsync(map, ct);
        sessions.Add(map.Name, (map, relay));
        return $"{map.Name}  {map.Route}";
    }

    private async Task<string> StopAsync(string? name)
    {
        if (name is null || !sessions.TryGetValue(name, out var item))
            throw new ArgumentException("Mapping not found. Use 'status' to see mapping names.");
        await item.Relay.DisposeAsync();
        sessions.Remove(name);
        return $"Stopped {name}.";
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
        finally { if (logout) await credential.ReplaceAsync(null, CancellationToken.None); }
        return logout ? "Signed out. Saved credential removed." : "Service stopping.";
    }

    public async ValueTask DisposeAsync()
    {
        try { await Task.WhenAll(sessions.Values.Select(s => s.Relay.DisposeAsync().AsTask())); }
        finally { sessions.Clear(); }
    }
}
