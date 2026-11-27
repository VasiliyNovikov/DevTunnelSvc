using System.Text.Json.Serialization;

namespace DevTunnelSvc.Service;

internal sealed record LocalStatus(bool SignedIn, LocalMappingStatus[] Mappings)
{
    internal const string Command = "status-details";
}

internal sealed record LocalMappingStatus(bool Host, string Tunnel, int TunnelPort, int LocalPort, string State)
{
    internal static LocalMappingStatus From(MappingSession session) =>
        new(session.Map.Host, session.Map.Tunnel.ToString(), session.Map.TunnelPort, session.Map.LocalPort, session.State);
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LocalStatus))]
internal sealed partial class StatusJson : JsonSerializerContext;
