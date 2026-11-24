namespace DevTunnelSvc.Auth;

internal sealed record GitHubCredential(string AccessToken, DateTimeOffset? ExpiresAt = null,
    string? RefreshToken = null, DateTimeOffset? RefreshExpiresAt = null)
{
    public override string ToString() => "[GitHub credential]";

    internal void Validate()
    {
        if (!IsToken(AccessToken) || (RefreshToken is not null && (ExpiresAt is null || !IsToken(RefreshToken))) ||
            (RefreshExpiresAt is not null && RefreshToken is null))
            throw new ArgumentException("Invalid GitHub sign-in credential.");
    }

    private static bool IsToken(string? value) => !string.IsNullOrEmpty(value) && value.All(c => c is >= '!' and <= '~');

    internal bool AccessValidAt(DateTimeOffset time) => ExpiresAt is null || ExpiresAt > time;
    internal bool CanRefreshAt(DateTimeOffset time) => RefreshToken is not null && (RefreshExpiresAt is null || RefreshExpiresAt > time);
}
