using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Refract.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Refract;

/// <summary>
/// Turns Jellyfin play events into Refract writes.
/// </summary>
public sealed class RefractMediator : IHostedService, IDisposable
{
    // Refract stores episodes in TMDB order only, and never tells us which order Jellyfin used.
    private static readonly string[] Numberings = { "tmdb", "tvdb", "absolute" };

    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly RefractClient _client;
    private readonly ILogger<RefractMediator> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    // Remembers which provider id and numbering matched a series, so anime costs one request, not four.
    private readonly ConcurrentDictionary<Guid, (string IdType, string Numbering)> _numberingMatches = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RefractMediator"/> class.
    /// </summary>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="client">The Refract client.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="lifetime">The host lifetime, used to hook the web client once everything started.</param>
    public RefractMediator(
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        RefractClient client,
        ILogger<RefractMediator> logger,
        IHostApplicationLifetime lifetime)
    {
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _client = client;
        _logger = logger;
        _lifetime = lifetime;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStarted.Register(() => _ = RegisterWebClientAsync());
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _logger.LogInformation("Refract: listening for plays");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    // File Transformation replays its own transformations a few seconds after the
    // server starts, which drops a registration that ran before it. Registers a
    // second time once that replay is over; a duplicate is a no-op for the patch.
    private async Task RegisterWebClientAsync()
    {
        foreach (var seconds in new[] { 15, 60 })
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            MenuInjection.Register(_logger);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _userDataManager.UserDataSaved -= OnUserDataSaved;

    private void OnUserDataSaved(object sender, UserDataSaveEventArgs args)
    {
        // The watchlist sync writes Likes; that is not a play.
        if (args.SaveReason == UserDataSaveReason.Import)
        {
            return;
        }

        if (args.Item is not Movie && args.Item is not Episode)
        {
            return;
        }

        if (!RefractClient.IsLinked(args.UserId))
        {
            return;
        }

        var played = args.UserData?.Played ?? false;

        // Stopping at 20% saves user data with Played false. Only a manual toggle may clear a watch.
        if (!played && args.SaveReason != UserDataSaveReason.TogglePlayed)
        {
            return;
        }

        // Jellyfin's save path must never wait on the network.
        _ = Task.Run(() => RunAsync(args));
    }

    private async Task RunAsync(UserDataSaveEventArgs args)
    {
        try
        {
            var played = args.UserData?.Played ?? false;
            var watchedAt = args.UserData?.LastPlayedDate ?? DateTime.UtcNow;

            switch (args.Item)
            {
                case Episode episode:
                    await SyncEpisodeAsync(args.UserId, episode, played, watchedAt).ConfigureAwait(false);
                    break;
                case Movie movie:
                    await SyncMovieAsync(movie, played, watchedAt, args.UserId).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refract: could not sync {Name}", args.Item?.Name);
        }
    }

    private async Task SyncEpisodeAsync(Guid userId, Episode episode, bool played, DateTime watchedAt)
    {
        var series = episode.Series ?? _libraryManager.GetItemById(episode.SeriesId) as Series;
        if (series == null || episode.ParentIndexNumber == null || episode.IndexNumber == null)
        {
            _logger.LogDebug("Refract: {Name} has no series or no episode number", episode.Name);
            return;
        }

        var season = episode.ParentIndexNumber.Value;
        var number = episode.IndexNumber.Value;

        foreach (var (idType, value, numbering) in Attempts(series))
        {
            var order = numbering ?? "tmdb";
            var hit = await _client
                .FindEpisodeAsync(userId, idType, value, season, number, order, CancellationToken.None)
                .ConfigureAwait(false);
            if (hit == null)
            {
                continue;
            }

            _numberingMatches[series.Id] = (idType, order);
            await _client
                .SetEpisodeAsync(userId, hit.Value.ShowId, hit.Value.Season, hit.Value.Number, played, played ? watchedAt : null, CancellationToken.None)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Refract: {Show} S{Season:00}E{Number:00} {Action} ({IdType}/{Numbering})",
                series.Name,
                hit.Value.Season,
                hit.Value.Number,
                played ? "watched" : "unwatched",
                idType,
                numbering ?? "tmdb");
            return;
        }

        _logger.LogDebug("Refract: no id match for {Show} S{Season:00}E{Number:00}", series.Name, season, number);
    }

    private async Task SyncMovieAsync(Movie movie, bool played, DateTime watchedAt, Guid userId)
    {
        // Refract removes a play by its own play id, and the plugin keeps no copy of it.
        if (!played)
        {
            _logger.LogInformation("Refract: {Name} unmarked in Jellyfin, the play stays on Refract", movie.Name);
            return;
        }

        var movieId = await _client.FindIdAsync(userId, ProviderCandidates(movie), "movie", CancellationToken.None).ConfigureAwait(false);
        if (movieId == null)
        {
            _logger.LogDebug("Refract: no id match for movie {Name}", movie.Name);
            return;
        }

        await _client
            .LogMovieAsync(userId, movieId, watchedAt, IdempotencyKey(userId, movie.Id, watchedAt), CancellationToken.None)
            .ConfigureAwait(false);
        _logger.LogInformation("Refract: movie {Name} watched", movie.Name);
    }

    private IEnumerable<(string IdType, string Value, string Numbering)> Attempts(Series series)
    {
        var candidates = ProviderCandidates(series);
        if (_numberingMatches.TryGetValue(series.Id, out var known))
        {
            foreach (var (idType, value) in candidates)
            {
                if (idType != known.IdType)
                {
                    continue;
                }

                yield return (idType, value, known.Numbering);
                break;
            }
        }

        foreach (var (idType, value) in candidates)
        {
            foreach (var numbering in Numberings)
            {
                if (_numberingMatches.TryGetValue(series.Id, out known) && known.IdType == idType && known.Numbering == numbering)
                {
                    continue;
                }

                yield return (idType, value, numbering);
            }
        }
    }

    internal static List<(string IdType, string Value)> ProviderCandidates(BaseItem item)
    {
        var candidates = new List<(string, string)>(3);
        var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
        if (!string.IsNullOrEmpty(tmdb))
        {
            candidates.Add(("tmdb", tmdb));
        }

        var tvdb = item.GetProviderId(MetadataProvider.Tvdb);
        if (!string.IsNullOrEmpty(tvdb))
        {
            candidates.Add(("tvdb", tvdb));
        }

        var imdb = item.GetProviderId(MetadataProvider.Imdb);
        if (!string.IsNullOrEmpty(imdb))
        {
            candidates.Add(("imdb", imdb));
        }

        return candidates;
    }

    internal static string IdempotencyKey(Guid userId, Guid itemId, DateTime watchedAt)
    {
        // Deterministic on purpose: retrying the same viewing reuses the key, so Refract logs it once.
        var seed = $"{userId:N}:{itemId:N}:{watchedAt.ToUniversalTime():yyyyMMddHHmmss}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }
}
