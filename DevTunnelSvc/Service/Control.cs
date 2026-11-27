using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DevTunnelSvc.Auth;

namespace DevTunnelSvc.Service;

internal sealed record Request(string Command, string? Tunnel = null, int TunnelPort = 0, int LocalPort = 0,
    GitHubCredential? SignIn = null, int Version = Control.ProtocolVersion, int? ServicePid = null, string? Region = null);
internal sealed record Reply(bool Success, string Message, int Version = Control.ProtocolVersion);

[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Reply))]
internal sealed partial class ControlJson : JsonSerializerContext;

internal static class Control
{
    // Host and connect now create durable mappings, not just sessions for this service lifetime.
    internal const int ProtocolVersion = 3;
    internal static readonly string PipeName = "DevTunnelSvc-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..20];
    internal const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    private const int MaxFrame = 32 * 1024;

    public static async Task<string> SendAsync(Request request, string pipeName, CancellationToken cancellation)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, Options,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(TimeSpan.FromSeconds(65));
        try { await pipe.ConnectAsync(1500, limit.Token); }
        catch (TimeoutException ex) { throw new IOException("Service is not available. Run 'devtunnelsvc start' first.", ex); }
        await WriteAsync(pipe, request, ControlJson.Default.Request, limit.Token);
        var reply = await ReadAsync(pipe, ControlJson.Default.Reply, limit.Token);
        if (reply.Version != ProtocolVersion)
            throw new InvalidDataException("Incompatible service protocol. Restart the service with the matching executable.");
        if (!reply.Success)
            throw new InvalidOperationException(reply.Message);
        return reply.Message;
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, CancellationToken ct)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, ct);
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size is < 1 or > MaxFrame)
            throw new InvalidDataException("Invalid control message length.");
        var body = new byte[size];
        await stream.ReadExactlyAsync(body, ct);
        return JsonSerializer.Deserialize(body, type) ?? throw new InvalidDataException("Empty control message.");
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, JsonTypeInfo<T> type, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, type);
        if (body.Length > MaxFrame)
            throw new InvalidDataException("Control message is too large.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }
}
