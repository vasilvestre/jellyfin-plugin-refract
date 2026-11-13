using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Refract.Api;

/// <summary>
/// Thin client over the Refract API, https://getrefract.app/v1.
/// </summary>
public sealed class RefractClient : IDisposable
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("https://getrefract.app/v1/"),
        Timeout = TimeSpan.FromSeconds(20),
        // The edge in front of Refract rejects some default user agents outright.
        DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("Jellyfin.Plugin.Refract", "1.0.2.0") } }
    };

    // Refract refuses two writes on the same show at once (409, concurrent_modification).
    // Ticking a whole season fires one save per episode in parallel, so the writes queue here.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _showLocks = new();

    private readonly ILogger<RefractClient> _logger;
    private readonly RefractAuthenticator _authenticator;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefractClient"/> class.
    /// </summary>
    /// <param name="authenticator">The device code sign-in and token store.</param>
    /// <param name="logger">The logger.</param>
    public RefractClient(RefractAuthenticator authenticator, ILogger<RefractClient> logger)
    {
        _authenticator = authenticator;
        _logger = logger;
    }

    /// <summary>
    /// Says whether a Jellyfin user linked a Refract account.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>True when the user holds a refresh token.</returns>
    public static bool IsLinked(Guid userId) => Plugin.Instance?.Configuration.GetUser(userId)?.IsLinked == true;

    /// <summary>
    /// Finds the Refract id of a show, trying each known provider id in order.
    /// </summary>
    /// <param name="ids">Provider id candidates, most reliable first.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The Refract id, or null when Refract knows no such show.</returns>
    public async Task<string> FindShowIdAsync(Guid userId, IEnumerable<(string IdType, string Value)> ids, CancellationToken cancellationToken)
    {
        foreach (var (idType, value) in ids)
        {
            var body = await GetAsync(userId, $"search/{idType}/{Uri.EscapeDataString(value)}?type=show", cancellationToken).ConfigureAwait(false);
            var found = ReadId(body, "ids");
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the Refract id of a movie, trying each known provider id in order.
    /// </summary>
    /// <param name="ids">Provider id candidates, most reliable first.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The Refract id, or null when Refract knows no such movie.</returns>
    public async Task<string> FindMovieIdAsync(Guid userId, IEnumerable<(string IdType, string Value)> ids, CancellationToken cancellationToken)
    {
        foreach (var (idType, value) in ids)
        {
            var body = await GetAsync(userId, $"search/{idType}/{Uri.EscapeDataString(value)}?type=movie", cancellationToken).ConfigureAwait(false);
            var found = ReadId(body, "ids");
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Translates an episode number into TMDB numbering, the only order Refract accepts.
    /// </summary>
    /// <param name="idType">Provider of the show id.</param>
    /// <param name="showId">The show id in that provider.</param>
    /// <param name="season">The season number as Jellyfin stores it.</param>
    /// <param name="number">The episode number as Jellyfin stores it.</param>
    /// <param name="numbering">The order those numbers follow: tmdb, tvdb or absolute.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The Refract show id with the TMDB season and number, or null.</returns>
    public async Task<(string ShowId, int Season, int Number)?> FindEpisodeAsync(
        Guid userId,
        string idType,
        string showId,
        int season,
        int number,
        string numbering,
        CancellationToken cancellationToken)
    {
        var path = $"search/{idType}/{Uri.EscapeDataString(showId)}?type=episode&season={season}&number={number}&numbering={numbering}";
        var body = await GetAsync(userId, path, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
        {
            return null;
        }

        var first = data[0];
        var showRefractId = first.GetProperty("show").GetProperty("ids").GetProperty("refract").GetString();
        var episode = first.GetProperty("episode");
        if (showRefractId == null || !episode.TryGetProperty("season", out var s) || !episode.TryGetProperty("number", out var n))
        {
            return null;
        }

        return (showRefractId, s.GetInt32(), n.GetInt32());
    }

    /// <summary>
    /// Marks an episode watched or not watched.
    /// </summary>
    /// <param name="showId">The Refract show id.</param>
    /// <param name="season">The season in TMDB numbering.</param>
    /// <param name="number">The episode in TMDB numbering.</param>
    /// <param name="played">True to mark watched, false to clear it.</param>
    /// <param name="watchedAt">When it was watched, for a watch.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SetEpisodeAsync(
        Guid userId,
        string showId,
        int season,
        int number,
        bool played,
        DateTime? watchedAt,
        CancellationToken cancellationToken)
    {
        var path = $"history/episodes/{Uri.EscapeDataString(showId)}/{season}/{number}";
        var gate = _showLocks.GetOrAdd(showId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!played)
            {
                await SendAsync(userId, HttpMethod.Delete, path, null, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            var body = watchedAt.HasValue
                ? $"{{\"watched_at\":\"{watchedAt.Value.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}\"}}"
                : null;
            await SendAsync(userId, HttpMethod.Put, path, body, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Logs a movie play.
    /// </summary>
    /// <param name="movieId">The Refract movie id.</param>
    /// <param name="watchedAt">When it was watched.</param>
    /// <param name="idempotencyKey">A key derived from the viewing, so a retry does not log twice.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task LogMovieAsync(Guid userId, string movieId, DateTime watchedAt, string idempotencyKey, CancellationToken cancellationToken)
    {
        var body = $"{{\"movie_id\":\"{movieId}\",\"watched_at\":\"{watchedAt.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}\"}}";
        await SendAsync(userId, HttpMethod.Post, "history/movies", body, idempotencyKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private static string ReadId(string body, string idsProperty)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
        {
            return null;
        }

        var ids = data[0].TryGetProperty(idsProperty, out var raw) ? raw : data[0];
        return ids.TryGetProperty("refract", out var refract) ? refract.GetString() : null;
    }

    private async Task<string> GetAsync(Guid userId, string path, CancellationToken cancellationToken)
    {
        return await SendAsync(userId, HttpMethod.Get, path, null, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendAsync(
        Guid userId,
        HttpMethod method,
        string path,
        string json,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var refreshed = false;
        var retriedRateLimit = false;
        var retriedConflict = false;

        while (true)
        {
            var accessToken = await _authenticator.GetAccessTokenAsync(userId, cancellationToken).ConfigureAwait(false);
            if (accessToken == null)
            {
                throw new InvalidOperationException("Refract: this Jellyfin user has no linked Refract account.");
            }

            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (json != null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            if (idempotencyKey != null)
            {
                request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            }

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return responseBody;
            }

            var status = (int)response.StatusCode;

            // The access token lasts one hour. A 401 means it died early, for example after a revoke.
            if (status == 401 && !refreshed)
            {
                refreshed = true;
                await _authenticator.RefreshAsync(userId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Another client, a phone for example, was writing the same title. Retry once.
            if (!retriedConflict && status == 409 && responseBody.Contains("concurrent_modification", StringComparison.Ordinal))
            {
                retriedConflict = true;
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
                continue;
            }

            // 429 and 5xx ask for a retry; anything else is our bug and retrying will not help.
            if (!retriedRateLimit && (status == 429 || status >= 500))
            {
                retriedRateLimit = true;
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(status == 429 ? 5 : 2);
                _logger.LogWarning("Refract answered {Status} on {Path}, retrying in {Wait}", status, path, wait);
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new HttpRequestException(
                $"Refract answered {(HttpStatusCode)status} on {method} {path}: {Truncate(responseBody)}");
        }
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300];
}
