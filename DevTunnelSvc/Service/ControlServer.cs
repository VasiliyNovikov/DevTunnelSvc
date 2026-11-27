using System.IO.Pipes;
using System.Text.Json;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc.Service;

internal static class ControlServer
{
    public static async Task RunAsync(string name, Func<Request, CancellationToken, Task<string>> execute, TextWriter log, Action<string> writeEvent, CancellationToken ct)
    {
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            Control.Options | PipeOptions.FirstPipeInstance);
        writeEvent("Control pipe ready.");
        while (!ct.IsCancellationRequested)
        {
            var accepted = false;
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                accepted = true;
                if (await HandleAsync(pipe, execute, ct))
                    return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or JsonException)
            {
                var message = $"Control connection failed: {AppLog.ErrorCode(ex)}";
                writeEvent(message);
                log.WriteLine(message);
            }
            finally { if (accepted) pipe.Disconnect(); }
        }
    }

    private static async Task<bool> HandleAsync(Stream pipe, Func<Request, CancellationToken, Task<string>> execute, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var request = await Control.ReadAsync(pipe, ControlJson.Default.Request, deadline.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        Reply reply;
        try { reply = new(true, await execute(request, deadline.Token)); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { reply = new(false, "Tunnel operation timed out."); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Return actionable errors at the IPC boundary, never serialize exceptions or request credentials.
            reply = new(false, ErrorMessage(ex));
        }
        using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        writeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        await Control.WriteAsync(pipe, reply, ControlJson.Default.Reply, writeTimeout.Token);
        return reply.Success && request.Command is "shutdown" or "shutdown-for-update" or AutoUpdates.Shutdown;
    }

    internal static string ErrorMessage(Exception error) => error is HttpRequestException http
        ? $"Dev Tunnels HTTP request failed ({http.StatusCode})." : error.Message;
}
