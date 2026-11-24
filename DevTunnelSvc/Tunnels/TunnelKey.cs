using Microsoft.DevTunnels.Contracts;

namespace DevTunnelSvc.Tunnels;

internal sealed record TunnelKey(string Id, string Cluster)
{
    public static TunnelKey Parse(string? value)
    {
        if (value?.Split('.') is not [var id, var cluster] ||
            !TunnelConstraints.IsValidNewTunnelId(id) || !TunnelConstraints.IsValidClusterId(cluster))
            throw new ArgumentException("Expected a tunnel ID and cluster, for example my-tunnel.use2.");
        return new(id, cluster);
    }

    public override string ToString() => $"{Id}.{Cluster}";
}

internal sealed record Mapping(bool Host, TunnelKey Tunnel, int TunnelPort, int LocalPort)
{
    public string Name => Host ? $"host:{Tunnel}" : $"connect:{Tunnel}:{LocalPort}";
    public string Route => Host ? $"{Tunnel}:{TunnelPort} -> localhost:{LocalPort}" : $"localhost:{LocalPort} -> {Tunnel}:{TunnelPort}";

    public static Mapping Create(bool host, string? id, int tunnelPort, int localPort)
    {
        if (tunnelPort is < 1 or > 65535 || localPort is < 1 or > 65535)
            throw new ArgumentException("Both port numbers must be between 1 and 65535.");
        return new(host, TunnelKey.Parse(id), tunnelPort, localPort);
    }
}
