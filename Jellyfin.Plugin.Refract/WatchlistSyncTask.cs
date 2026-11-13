using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Refract.Api;
using Jellyfin.Plugin.Refract.Model;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Refract;

/// <summary>
/// Keeps each linked member's Jellyfin watchlist and Refract watchlist identical.
/// </summary>
/// <remarks>
/// The Jellyfin watchlist is the Likes field: the KefinTweaks watchlist button
/// calls updateUserItemRating, and that is the field it writes. Jellyfin 12 keeps
/// no separate Likes column, so a like is a 10 rating and an unlike a 1 rating.
/// The plugin stores the pairs it left on both sides, so a title missing from one
/// side is a removal rather than a title the other side never had.
/// Only movies and shows are synced; Refract has no season or episode watchlist.
/// </remarks>
public sealed class WatchlistSyncTask : IScheduledTask
{
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly RefractClient _client;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<WatchlistSyncTask> _logger;

    // A long first sync can outlast the next tick; one run at a time is enough.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchlistSyncTask"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="client">The Refract client.</param>
    /// <param name="paths">The server paths, to find the JavaScript Injector settings.</param>
    /// <param name="logger">The logger.</param>
    public WatchlistSyncTask(
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        RefractClient client,
        IApplicationPaths paths,
        ILogger<WatchlistSyncTask> logger)
    {
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _client = client;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Lists the Refract watchlist titles a user has no local item for, so the web
    /// client can offer a "request through Seerr" action. Computed on demand: the
    /// previous run's cache was empty after every restart until the next tick.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="client">The Refract client.</param>
    /// <param name="libraryManager">The library, to resolve titles to local items.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The titles Refract lists as planned but the library does not hold.</returns>
    public static async Task<IReadOnlyList<MissingItem>> ComputeMissingAsync(
        Guid userId,
        RefractClient client,
        ILibraryManager libraryManager,
        CancellationToken cancellationToken)
    {
        var watchlist = await client.GetWatchlistAsync(userId, cancellationToken).ConfigureAwait(false);
        var index = BuildProviderIndex(libraryManager);
        var missing = new List<MissingItem>();
        foreach (var title in watchlist)
        {
            if (Resolve(index, title) == null && !string.IsNullOrEmpty(title.TmdbId))
            {
                missing.Add(new MissingItem(title.Title, title.Year, title.TmdbId, title.IsShow));
            }
        }

        return missing;
    }

    /// <inheritdoc />
    public string Name => "Sync the Refract watchlist";

    /// <inheritdoc />
    public string Key => "RefractWatchlistSync";

    /// <inheritdoc />
    public string Description =>
        "Copies each linked member's watchlist between Jellyfin and Refract. Needs the KefinTweaks watchlist.";

    /// <inheritdoc />
    public string Category => "Refract";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(15).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.SyncWatchlist != true)
        {
            return;
        }

        if (!KefinTweaksWatchlist.IsEnabled(_paths))
        {
            _logger.LogWarning("Refract: the watchlist sync is on but KefinTweaks has no watchlist, nothing to sync");
            return;
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var linked = config.RefractUsers.Where(u => u.IsLinked).ToArray();
            var index = BuildProviderIndex(_libraryManager);
            for (var i = 0; i < linked.Length; i++)
            {
                try
                {
                    await SyncUserAsync(linked[i], index, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One account failing must not hold back the others.
                    _logger.LogError(ex, "Refract: could not sync the watchlist of {User}", linked[i].LinkedMbUserId);
                }

                progress?.Report((i + 1) * 100d / linked.Length);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // One library pass per run, so a Refract title resolves without a search per title.
    private static Dictionary<string, BaseItem> BuildProviderIndex(ILibraryManager libraryManager)
    {
        var index = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
        var items = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true
        });

        foreach (var item in items)
        {
            var kind = item is Series ? "show" : "movie";
            AddToIndex(index, item, MetadataProvider.Tmdb, kind);
            AddToIndex(index, item, MetadataProvider.Tvdb, kind);
        }

        return index;
    }

    private static void AddToIndex(Dictionary<string, BaseItem> index, BaseItem item, MetadataProvider provider, string kind)
    {
        var value = item.GetProviderId(provider);
        if (!string.IsNullOrEmpty(value))
        {
            index[$"{provider.ToString().ToLowerInvariant()}:{value}:{kind}"] = item;
        }
    }

    private static string ProviderKey(MetadataProvider provider, string value, bool isShow)
    {
        return $"{provider.ToString().ToLowerInvariant()}:{value}:{(isShow ? "show" : "movie")}";
    }

    private static BaseItem Resolve(Dictionary<string, BaseItem> index, RefractClient.WatchlistTitle title)
    {
        var isShow = title.IsShow;
        foreach (var (provider, value) in new[] { (MetadataProvider.Tmdb, title.TmdbId), (MetadataProvider.Tvdb, title.TvdbId) })
        {
            if (!string.IsNullOrEmpty(value) && index.TryGetValue(ProviderKey(provider, value, isShow), out var item))
            {
                return item;
            }
        }

        return null;
    }

    // A favourite that Refract already lists needs no search and no write.
    private static string OnWatchlist(IReadOnlyDictionary<string, string> watchlist, BaseItem item)
    {
        var isShow = item is Series;
        foreach (var provider in new[] { MetadataProvider.Tmdb, MetadataProvider.Tvdb })
        {
            var value = item.GetProviderId(provider);
            if (!string.IsNullOrEmpty(value) && watchlist.TryGetValue(ProviderKey(provider, value, isShow), out var refractId))
            {
                return refractId;
            }
        }

        return null;
    }

    private async Task SyncUserAsync(RefractUser user, Dictionary<string, BaseItem> index, CancellationToken cancellationToken)
    {
        var mbUser = _userManager.GetUserById(user.LinkedMbUserId);
        if (mbUser == null)
        {
            return;
        }

        var liked = _libraryManager.GetItemList(new InternalItemsQuery
        {
            User = mbUser,
            IsLiked = true,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true
        });

        var watchlist = await _client.GetWatchlistAsync(user.LinkedMbUserId, cancellationToken).ConfigureAwait(false);
        var likedIds = liked.Select(item => item.Id).ToHashSet();
        var refractIds = watchlist.Select(title => title.RefractId).ToHashSet(StringComparer.Ordinal);
        var stored = (user.SyncedItems ?? Array.Empty<string>())
            .Select(WatchlistEntry.Parse)
            .Where(entry => entry.HasValue)
            .Select(entry => entry!.Value)
            .ToArray();

        var kept = new Dictionary<Guid, string>();
        var seenItems = new HashSet<Guid>();
        var seenTitles = new HashSet<string>(StringComparer.Ordinal);
        var removed = 0;

        try
        {
            foreach (var entry in stored)
            {
                seenItems.Add(entry.MbId);
                seenTitles.Add(entry.RefractId);

                var stillLiked = likedIds.Contains(entry.MbId);
                var onWatchlist = refractIds.Contains(entry.RefractId);
                switch (WatchlistEntry.Decide(stillLiked, onWatchlist, entry.Tracked))
                {
                    case WatchlistEntry.Action.Keep:
                        kept[entry.MbId] = entry.ToString();
                        break;

                    case WatchlistEntry.Action.ClearLike:
                        _logger.LogInformation("Refract: {Id} left the Refract watchlist", entry.RefractId);
                        SaveLike(mbUser, entry.MbId, false);
                        likedIds.Remove(entry.MbId);
                        removed++;
                        break;

                    case WatchlistEntry.Action.RemoveOnRefract:
                        _logger.LogInformation("Refract: {Id} left the Jellyfin watchlist", entry.RefractId);
                        await _client.RemoveFromWatchlistAsync(user.LinkedMbUserId, entry.RefractId, cancellationToken).ConfigureAwait(false);
                        removed++;
                        break;
                }
            }

            var known = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var title in watchlist)
            {
                if (!string.IsNullOrEmpty(title.TmdbId))
                {
                    known[ProviderKey(MetadataProvider.Tmdb, title.TmdbId, title.IsShow)] = title.RefractId;
                }

                if (!string.IsNullOrEmpty(title.TvdbId))
                {
                    known[ProviderKey(MetadataProvider.Tvdb, title.TvdbId, title.IsShow)] = title.RefractId;
                }
            }

            // Jellyfin to Refract: a favourite the last run did not know.
            foreach (var item in liked)
            {
                if (seenItems.Contains(item.Id))
                {
                    continue;
                }

                var refractId = OnWatchlist(known, item);
                if (refractId == null)
                {
                    refractId = await FindRefractIdAsync(user.LinkedMbUserId, item, cancellationToken).ConfigureAwait(false);
                    if (refractId == null)
                    {
                        _logger.LogDebug("Refract: no Refract match for {Name}", item.Name);
                        continue;
                    }

                    var added = await _client.AddToWatchlistAsync(user.LinkedMbUserId, refractId, cancellationToken).ConfigureAwait(false);
                    kept[item.Id] = new WatchlistEntry(item.Id, refractId, !added).ToString();
                    _logger.LogInformation("Refract: {Name} added to the Refract watchlist", item.Name);
                }
                else
                {
                    kept[item.Id] = new WatchlistEntry(item.Id, refractId, false).ToString();
                }

                seenItems.Add(item.Id);
                seenTitles.Add(refractId);
            }

            // Refract to Jellyfin: a planned title the last run did not know.
            foreach (var title in watchlist)
            {
                if (seenTitles.Contains(title.RefractId))
                {
                    continue;
                }

                var item = Resolve(index, title);
                if (item == null)
                {
                    _logger.LogDebug("Refract: no Jellyfin match for {Id}", title.RefractId);
                    continue;
                }

                seenTitles.Add(title.RefractId);
                kept[item.Id] = new WatchlistEntry(item.Id, title.RefractId, false).ToString();
                if (likedIds.Contains(item.Id))
                {
                    continue;
                }

                SaveLike(mbUser, item.Id, true);
                likedIds.Add(item.Id);
                _logger.LogInformation("Refract: {Name} added to the Jellyfin watchlist", item.Name);
            }
        }
        finally
        {
            if (removed > 3)
            {
                _logger.LogWarning("Refract: removed {Count} titles for {User} in one run", removed, user.LinkedMbUserId);
            }

            // Keep the pairs of a run that stopped halfway: the writes already happened.
            user.SyncedItems = kept.Values.ToArray();
            Plugin.Instance!.UpdateConfiguration(Plugin.Instance.Configuration);
        }
    }

    private async Task<string> FindRefractIdAsync(Guid userId, BaseItem item, CancellationToken cancellationToken)
    {
        var candidates = new List<(string IdType, string Value)>(3);
        foreach (var (idType, provider) in new[] { ("tmdb", MetadataProvider.Tmdb), ("tvdb", MetadataProvider.Tvdb), ("imdb", MetadataProvider.Imdb) })
        {
            var value = item.GetProviderId(provider);
            if (!string.IsNullOrEmpty(value))
            {
                candidates.Add((idType, value));
            }
        }

        return item is Series
            ? await _client.FindShowIdAsync(userId, candidates, cancellationToken).ConfigureAwait(false)
            : await _client.FindMovieIdAsync(userId, candidates, cancellationToken).ConfigureAwait(false);
    }

    private void SaveLike(User user, Guid itemId, bool liked)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return;
        }

        var data = _userDataManager.GetUserData(user, item);

        // An unrated item is already off the watchlist, and writing false would rate it 1.
        if (data == null || data.Likes == liked || (data.Likes is null && !liked))
        {
            return;
        }

        data.Likes = liked;

        // Import tells the play watcher this save is not a watch.
        _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.Import, CancellationToken.None);
    }
}
