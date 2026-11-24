using System.Net.Http.Json;
using System.Net;
using Microsoft.DevTunnels.Contracts;

namespace DevTunnelSvc.Auth;

internal sealed record TokenResult(string? AccessToken, string? Error, int? ExpiresIn, string? RefreshToken, int? RefreshTokenExpiresIn)
{
    public override string ToString() => "[GitHub token response]";

    internal GitHubCredential ToCredential(DateTimeOffset now)
    {
        var result = new GitHubCredential(AccessToken ?? "", Expiry(now, ExpiresIn), RefreshToken, Expiry(now, RefreshTokenExpiresIn));
        result.Validate();
        return result;
    }

    private static DateTimeOffset? Expiry(DateTimeOffset now, int? seconds) => seconds switch
    {
        null => null,
        > 0 => now.AddSeconds(seconds.Value),
        _ => throw new InvalidDataException("GitHub returned an invalid token lifetime."),
    };
}

internal sealed class SignInExpiredException() : InvalidOperationException("Saved GitHub sign-in has expired or was revoked. Run 'login' again.");

internal static class GitHubOAuth
{
    private static readonly Uri TokenEndpoint = new("https://github.com/login/oauth/access_token");
    internal static HttpClient CreateClient() => new() { DefaultRequestHeaders = { UserAgent = { new("DevTunnelSvc", Program.Version) } } };

    internal static async Task<GitHubCredential> RefreshAsync(HttpClient http, string refreshToken, CancellationToken ct)
    {
        var result = await ExchangeAsync(http, new()
        {
            ["client_id"] = TunnelServiceProperties.Production.GitHubAppClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct);
        if (result?.Error is "bad_refresh_token" or "invalid_grant" or "expired_token")
            throw new SignInExpiredException();
        if (result is not { Error: null, RefreshToken: not null })
            throw new InvalidDataException("GitHub did not renew the saved credential. Retry or run 'login' again.");
        return result.ToCredential(DateTimeOffset.UtcNow);
    }

    internal static async Task<TokenResult?> ExchangeAsync(HttpClient http, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var response = await PostAsync(http, TokenEndpoint, fields, ct);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var rejected = await response.Content.ReadFromJsonAsync(LoginJson.Default.TokenResult, ct);
            if (rejected?.Error is { Length: > 0 } error)
                return new(null, error, null, null, null);
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(LoginJson.Default.TokenResult, ct);
    }

    internal static async Task<HttpResponseMessage> PostAsync(HttpClient http, Uri uri, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Accept.ParseAdd("application/json");
        return await http.SendAsync(request, ct);
    }
}
