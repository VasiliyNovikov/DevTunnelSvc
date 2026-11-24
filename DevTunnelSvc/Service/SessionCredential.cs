using DevTunnelSvc.Auth;

namespace DevTunnelSvc.Service;

internal sealed class SessionCredential(CredentialFile file, Func<string, CancellationToken, Task<GitHubCredential>> renew,
    TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim changes = new(1, 1);
    private GitHubCredential? pendingRotation;

    internal bool IsSignedIn
    {
        get
        {
            var value = Volatile.Read(ref pendingRotation) ?? file.Read();
            var now = clock.GetUtcNow();
            return value is not null && (value.AccessValidAt(now) || value.CanRefreshAt(now));
        }
    }

    internal async Task ReplaceAsync(GitHubCredential? value, CancellationToken ct)
    {
        await changes.WaitAsync(ct);
        try
        {
            if (value is null)
            {
                Volatile.Write(ref pendingRotation, null);
                file.Delete();
                return;
            }
            file.Write(value);
            Volatile.Write(ref pendingRotation, null);
        }
        finally { changes.Release(); }
    }

    internal async Task<string> RequireTokenAsync(CancellationToken ct)
    {
        await changes.WaitAsync(ct);
        try
        {
            var value = pendingRotation is { } pending ? CommitRotation(pending) : file.Read();
            if (value is null) throw new InvalidOperationException("Not signed in. Run 'login' first.");
            var now = clock.GetUtcNow();
            if (value.AccessValidAt(now.AddMinutes(1))) return value.AccessToken;
            if (value.CanRefreshAt(now))
                return await RenewAsync(value.RefreshToken!, ct);
            if (value.AccessValidAt(now)) return value.AccessToken;
            throw new SignInExpiredException();
        }
        finally { changes.Release(); }
    }

    private async Task<string> RenewAsync(string refreshToken, CancellationToken ct)
    {
        GitHubCredential rotated;
        try { rotated = await renew(refreshToken, ct); }
        catch (SignInExpiredException) { file.Delete(); throw; }
        // GitHub invalidates the old pair on rotation. Retry a failed disk write, not the old grant.
        Volatile.Write(ref pendingRotation, rotated);
        return CommitRotation(rotated).AccessToken;
    }

    private GitHubCredential CommitRotation(GitHubCredential value)
    {
        file.Write(value);
        Volatile.Write(ref pendingRotation, null);
        return value;
    }

    public void Dispose()
    {
        Volatile.Write(ref pendingRotation, null);
        changes.Dispose();
    }
}
