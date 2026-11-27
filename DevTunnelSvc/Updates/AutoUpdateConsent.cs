using DevTunnelSvc.Service;

namespace DevTunnelSvc.Updates;

internal static class AutoUpdateConsent
{
    internal const string Status = "auto-update-status", Enable = "auto-update-enable", Disable = "auto-update-disable";
    internal const string Enabled = "Auto-update is enabled.", Disabled = "Auto-update is disabled.";
    internal const string Question = "Enable automatic updates?\n\nDevTunnelSvc will check for new stable releases now and every 5 minutes while the service is running. " +
        "It will install them without asking again, stopping and restarting the service. Active tunnel connections will be interrupted. " +
        "Saved port mappings will be restored, but connected applications may need to reconnect.";

    internal static async Task<string> ChangeAsync(bool enable, Func<Request, CancellationToken, Task<string>> send,
        Func<string, CancellationToken, Task<bool>> confirm, CancellationToken ct)
    {
        if (!enable) return await send(new(Disable), ct);
        var state = await send(new(Status), ct);
        if (state == Enabled) return state;
        if (state != Disabled) throw new InvalidDataException("Unexpected auto-update setting returned by the service.");
        if (!await confirm(Question, ct)) return "Auto-update was not enabled.";
        ct.ThrowIfCancellationRequested();
        return await send(new(Enable), ct);
    }

    internal static async Task<bool> ConfirmAsync(TextReader input, TextWriter output, string question, CancellationToken ct)
    {
        await output.WriteAsync($"{question.ReplaceLineEndings()} [y/N] ".AsMemory(), ct);
        await output.FlushAsync(ct);
        var answer = (await input.ReadLineAsync(ct))?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
