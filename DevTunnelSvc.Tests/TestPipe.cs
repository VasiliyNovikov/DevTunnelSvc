using System.IO.Pipes;
using DevTunnelSvc.Service;

namespace DevTunnelSvc.Tests;

internal sealed class TestPipe(int timeoutSeconds) : IDisposable
{
    private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(timeoutSeconds));
    internal string Name { get; } = $"devtunnelsvc-test-{Guid.NewGuid():N}";
    internal CancellationToken Token => timeout.Token;

    internal Task<string> SendAsync(Request request) => SendAsync(request, Token);
    internal Task<string> SendAsync(Request request, CancellationToken ct) => Control.SendAsync(request, Name, ct);
    internal Task ServeAsync(TunnelService service, TextWriter errors, Action<string> writeEvent) =>
        ControlServer.RunAsync(Name, service.ExecuteAsync, errors, writeEvent, Token);

    internal NamedPipeServerStream Listen() => new(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, Control.Options);
    internal async Task<Request> ReadRequestAsync(NamedPipeServerStream server)
    {
        await server.WaitForConnectionAsync(Token);
        return await Control.ReadAsync(server, ControlJson.Default.Request, Token);
    }

    internal async Task StopAsync(Task server) { await timeout.CancelAsync(); await server; }
    public void Dispose() => timeout.Dispose();
}
