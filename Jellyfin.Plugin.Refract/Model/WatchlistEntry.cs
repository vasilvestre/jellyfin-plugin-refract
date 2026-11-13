using System;

namespace Jellyfin.Plugin.Refract.Model;

/// <summary>
/// One watchlist entry as the last sync left it: a Jellyfin item and the Refract
/// title it maps to. The plugin keeps the pairs so it can tell which side dropped
/// a title between two runs.
/// </summary>
public readonly record struct WatchlistEntry(Guid MbId, string RefractId, bool Tracked)
{
    /// <summary>
    /// What the sync does with an entry the previous run wrote.
    /// </summary>
    public enum Action
    {
        /// <summary>
        /// Both sides still hold the title.
        /// </summary>
        Keep,

        /// <summary>
        /// The member took the title off the Refract watchlist.
        /// </summary>
        ClearLike,

        /// <summary>
        /// The member took the title off the Jellyfin watchlist.
        /// </summary>
        RemoveOnRefract,

        /// <summary>
        /// Nothing left to do: both sides dropped it, or Refract tracks it elsewhere.
        /// </summary>
        Forget
    }

    /// <summary>
    /// Reads an entry from its stored form, "mbId|refractId" or "mbId|refractId|tracked".
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The entry, or null when the value does not parse.</returns>
    public static WatchlistEntry? Parse(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var parts = value.Split('|');
        if (parts.Length < 2 || parts.Length > 3 || !Guid.TryParse(parts[0], out var mbId) || parts[1].Length == 0)
        {
            return null;
        }

        // A third field the plugin did not write means a corrupt record. Dropping it
        // is safe: the next run resolves the title again.
        if (parts.Length == 3 && parts[2] != "tracked")
        {
            return null;
        }

        return new WatchlistEntry(mbId, parts[1], parts.Length == 3);
    }

    /// <summary>
    /// Says what to do with an entry, given what each side holds now.
    /// </summary>
    /// <param name="stillLiked">The Jellyfin item is still a favourite.</param>
    /// <param name="onWatchlist">Refract still lists the title as planned.</param>
    /// <param name="tracked">Refract holds the title with another status.</param>
    /// <returns>The action for this entry.</returns>
    public static Action Decide(bool stillLiked, bool onWatchlist, bool tracked)
    {
        if (tracked)
        {
            return stillLiked ? Action.Keep : Action.Forget;
        }

        if (stillLiked && onWatchlist)
        {
            return Action.Keep;
        }

        if (stillLiked)
        {
            // The title left the Refract watchlist, so Jellyfin follows.
            return Action.ClearLike;
        }

        // The title left the Jellyfin watchlist, so Refract follows.
        return onWatchlist ? Action.RemoveOnRefract : Action.Forget;
    }

    /// <summary>
    /// Writes the entry in its stored form.
    /// </summary>
    /// <returns>The value to store.</returns>
    public override string ToString()
    {
        return Tracked ? $"{MbId:N}|{RefractId}|tracked" : $"{MbId:N}|{RefractId}";
    }
}
