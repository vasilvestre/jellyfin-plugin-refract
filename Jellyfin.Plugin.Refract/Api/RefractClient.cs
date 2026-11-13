using System;
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
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly ILogger<RefractClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefractClient"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public RefractClient(ILogger<RefractClient> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether an API key is configured.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.ApiKey);

    /// <summary>
    /// Finds the Refract id of a show, trying each known provider id in order.
    /// </summary>
    /// <param name="ids">Provider id candidates, most reliable first.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The Refract id, or null when Refract knows no such show.</returns>
    public async Task<string> FindShowIdAsync(IEnumerable<(string IdType, string Value)> ids, CancellationToken cancellationToken)
    {
        foreach (var (idType, value) in ids)
        {
            var body = await GetAsync($"search/{idType}/{Uri.EscapeDataString(value)}?type=show", cancellationToken).ConfigureAwait(false);
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
    public async Task<string> FindMovieIdAsync(IEnumerable<(string IdType, string Value)> ids, CancellationToken cancellationToken)
    {
        foreach (var (idType, value) in ids)
        {
            var body = await GetAsync($"search/{idType}/{Uri.EscapeDataString(value)}?type=movie", cancellationToken).ConfigureAwait(false);
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
        string idType,
        string showId,
        int season,
        int number,
        string numbering,
        CancellationToken cancellationToken)
    {
        var path = $"search/{idType}/{Uri.EscapeDataString(showId)}?type=episode&season={season}&number={number}&numbering={numbering}";
        var body = await GetAsync(path, cancellationToken).ConfigureAwait(false);

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
        string showId,
        int season,
        int number,
        bool played,
        DateTime? watchedAt,
        CancellationToken cancellationToken)
    {
        var path = $"history/episodes/{Uri.EscapeDataString(showId)}/{season}/{number}";
        if (!played)
        {
            await SendAsync(HttpMethod.Delete, path, null, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var body = watchedAt.HasValue
            ? $"{{\"watched_at\":\"{watchedAt.Value.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}\"}}"
            : null;
        await SendAsync(HttpMethod.Put, path, body, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Logs a movie play.
    /// </summary>
    /// <param name="movieId">The Refract movie id.</param>
    /// <param name="watchedAt">When it was watched.</param>
    /// <param name="idempotencyKey">A key derived from the viewing, so a retry does not log twice.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task LogMovieAsync(string movieId, DateTime watchedAt, string idempotencyKey, CancellationToken cancellationToken)
    {
        var body = $"{{\"movie_id\":\"{movieId}\",\"watched_at\":\"{watchedAt.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}\"}}";
        await SendAsync(HttpMethod.Post, "history/movies", body, idempotencyKey, cancellationToken).ConfigureAwait(false);
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

    private async Task<string> GetAsync(string path, CancellationToken cancellationToken)
    {
        return await SendAsync(HttpMethod.Get, path, null, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendAsync(
        HttpMethod method,
        string path,
        string json,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var apiKey = Plugin.Instance?.Configuration.ApiKey;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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

            // 429 and 5xx ask for a retry; anything else is our bug and retrying will not help.
            var status = (int)response.StatusCode;
            if (attempt == 0 && (status == 429 || status >= 500))
            {
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
