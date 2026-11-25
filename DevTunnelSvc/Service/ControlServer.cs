using System.IO.Pipes;
using System.Text.Json;

namespace DevTunnelSvc.Service;

internal static class ControlServer
{
    public static async Task RunAsync(string name, TunnelService service, TextWriter log, CancellationToken ct)
    {
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            Control.Options | PipeOptions.FirstPipeInstance);
        while (!ct.IsCancellationRequested)
        {
            var accepted = false;
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                accepted = true;
                if (await HandleAsync(pipe, service, ct))
                    return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or JsonException)
            {
                log.WriteLine($"Control connection failed: {ex.GetType().Name}");
            }
            finally { if (accepted) pipe.Disconnect(); }
        }
    }

    private static async Task<bool> HandleAsync(Stream pipe, TunnelService service, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var request = await Control.ReadAsync(pipe, ControlJson.Default.Request, deadline.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        Reply reply;
        try { reply = new(true, await service.ExecuteAsync(request, deadline.Token)); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { reply = new(false, "Tunnel operation timed out."); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Return actionable errors at the IPC boundary, never serialize exceptions or request credentials.
            reply = new(false, ex is HttpRequestException http ? $"Dev Tunnels HTTP request failed ({http.StatusCode})." : ex.Message);
        }
        using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        writeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        await Control.WriteAsync(pipe, reply, ControlJson.Default.Reply, writeTimeout.Token);
        return reply.Success && request.Command is "shutdown" or "shutdown-for-update";
    }
}
