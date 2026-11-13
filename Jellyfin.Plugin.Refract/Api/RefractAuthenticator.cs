using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Refract.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Refract.Api;

/// <summary>
/// Device code sign-in and token refresh for Refract.
/// </summary>
public sealed class RefractAuthenticator : IDisposable
{
    /// <summary>
    /// Client id of the public Refract app registered for this plugin.
    /// </summary>
    /// <remarks>
    /// TODO(vasilvestre): paste the client id of the "Jellyfin" app registered in Refract,
    /// under Access and connected apps. A public client needs no secret.
    /// </remarks>
    public const string ClientId = "REFRACT_CLIENT_ID";

    private const string ApiResource = "https://getrefract.app/v1";
    private const string TokenEndpoint = "https://getrefract.app/api/auth/oauth2/token";
    private const string DeviceEndpoint = "https://getrefract.app/api/auth/device/code";

    // offline_access is what gets us a refresh token.
    private const string RequestedScope =
        "profile:read library:read history:read library:write history:write offline_access";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ILogger<RefractAuthenticator> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="RefractAuthenticator"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public RefractAuthenticator(ILogger<RefractAuthenticator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Asks Refract for a device code the member approves on their phone.
    /// </summary>
    /// <param name="userId">The Jellyfin user to link.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The user code to show and the address where the member types it.</returns>
    public async Task<(string UserCode, string VerificationUri, int Interval, int ExpiresIn)> StartAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = RequestedScope,
            ["resource"] = ApiResource
        });

        using var response = await _http.PostAsync(DeviceEndpoint, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Refract refused the device code request: {body}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var userCode = root.GetProperty("user_code").GetString();
        var deviceCode = root.GetProperty("device_code").GetString();
        var interval = root.TryGetProperty("interval", out var i) ? i.GetInt32() : 5;
        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 600;
        var verificationUri = root.TryGetProperty("verification_uri_complete", out var vc)
            ? vc.GetString()
            : root.TryGetProperty("verification_uri", out var v) ? v.GetString() : "https://getrefract.app/device";

        var config = Plugin.Instance.Configuration;
        config.PutUser(new RefractUser
        {
            LinkedMbUserId = userId,
            DeviceCode = deviceCode,
            PollInterval = interval,
            DeviceCodeExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn)
        });
        Plugin.Instance.UpdateConfiguration(config);

        _logger.LogInformation("Refract: waiting for {UserCode} on {Uri}", userCode, verificationUri);
        return (userCode, verificationUri, interval, expiresIn);
    }

    /// <summary>
    /// Asks Refract whether the pending device code was approved yet.
    /// </summary>
    /// <param name="userId">The Jellyfin user being linked.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>One of pending, slow_down, authorized, expired or denied.</returns>
    public async Task<string> PollAsync(Guid userId, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var user = config.GetUser(userId);
        if (user == null || string.IsNullOrEmpty(user.DeviceCode))
        {
            return "expired";
        }

        if (user.DeviceCodeExpiresAt < DateTime.UtcNow)
        {
            config.RemoveUser(userId);
            Plugin.Instance.UpdateConfiguration(config);
            return "expired";
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = user.DeviceCode,
            ["client_id"] = ClientId
        });

        using var response = await _http.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            StoreTokens(user, body);
            return "authorized";
        }

        var error = ReadError(body);
        switch (error)
        {
            case "authorization_pending":
                return "pending";
            case "slow_down":
                user.PollInterval += 5;
                Plugin.Instance.UpdateConfiguration(config);
                return "slow_down";
            case "access_denied":
                config.RemoveUser(userId);
                Plugin.Instance.UpdateConfiguration(config);
                return "denied";
            default:
                config.RemoveUser(userId);
                Plugin.Instance.UpdateConfiguration(config);
                _logger.LogWarning("Refract: device code polling failed, {Error}", error);
                return "expired";
        }
    }

    /// <summary>
    /// Gets a valid access token for a user, refreshing it when it expired.
    /// </summary>
    /// <param name="userId">The Jellyfin user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The access token, or null when the user is not linked.</returns>
    public async Task<string> GetAccessTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = Plugin.Instance.Configuration.GetUser(userId);
        if (user == null || !user.IsLinked)
        {
            return null;
        }

        if (user.ExpiresAt > DateTime.UtcNow.AddMinutes(1))
        {
            return user.AccessToken;
        }

        return await RefreshAsync(userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Forces a refresh, used after a 401.
    /// </summary>
    /// <param name="userId">The Jellyfin user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A fresh access token, or null when the refresh was refused.</returns>
    public Task<string> RefreshAsync(Guid userId, CancellationToken cancellationToken) =>
        RefreshCoreAsync(userId, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _refreshLock.Dispose();
    }

    private async Task<string> RefreshCoreAsync(Guid userId, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = Plugin.Instance.Configuration;
            var user = config.GetUser(userId);
            if (user == null || string.IsNullOrEmpty(user.RefreshToken))
            {
                return null;
            }

            // Another thread may have refreshed while we waited.
            if (user.ExpiresAt > DateTime.UtcNow.AddMinutes(1))
            {
                return user.AccessToken;
            }

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = user.RefreshToken,
                ["client_id"] = ClientId,
                ["resource"] = ApiResource
            });

            using var response = await _http.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The member disconnected the app, or the refresh token expired after 90 days.
                _logger.LogWarning("Refract: refresh refused for user {UserId}, {Error}", userId, ReadError(body));
                config.RemoveUser(userId);
                Plugin.Instance.UpdateConfiguration(config);
                return null;
            }

            StoreTokens(user, body);
            return user.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void StoreTokens(RefractUser user, string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        user.AccessToken = root.GetProperty("access_token").GetString();
        user.Scope = root.TryGetProperty("scope", out var scope) ? scope.GetString() : null;
        user.ExpiresAt = DateTime.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600);

        // Refract retires the old refresh token on every refresh: store the new one before anything else.
        if (root.TryGetProperty("refresh_token", out var refresh) && refresh.GetString() is { Length: > 0 } rotated)
        {
            user.RefreshToken = rotated;
        }

        user.DeviceCode = string.Empty;
        user.DeviceCodeExpiresAt = DateTime.MinValue;

        var config = Plugin.Instance.Configuration;
        config.PutUser(user);
        Plugin.Instance.UpdateConfiguration(config);
        _logger.LogInformation("Refract: user {UserId} linked, token valid until {Expires:u}", user.LinkedMbUserId, user.ExpiresAt);
    }

    private static string ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "unknown";
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }
}
