using System;
using Jellyfin.Plugin.Refract.Model;

namespace Jellyfin.Plugin.Refract.Tests;

/// <summary>
/// The pairs the watchlist sync keeps. A wrong answer deletes a title from a
/// member's watchlist on the other side.
/// </summary>
public class WatchlistEntryTests
{
    private static readonly Guid Item = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>A pair that both sides hold survives a round trip.</summary>
    [Fact]
    public void RoundTripsAPlainPair()
    {
        var entry = WatchlistEntry.Parse(new WatchlistEntry(Item, "8b0e5c1a", false).ToString());

        Assert.Equal(Item, entry?.MbId);
        Assert.Equal("8b0e5c1a", entry?.RefractId);
        Assert.False(entry?.Tracked);
    }

    /// <summary>A title Refract tracks with another status keeps that flag.</summary>
    [Fact]
    public void RoundTripsATrackedPair()
    {
        var entry = WatchlistEntry.Parse(new WatchlistEntry(Item, "8b0e5c1a", true).ToString());

        Assert.True(entry?.Tracked);
    }

    /// <summary>Anything the plugin cannot read is dropped, never guessed.</summary>
    /// <param name="value">A stored value that does not hold a pair.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("11111111222233334444555555555555")]
    [InlineData("notaguid|8b0e5c1a")]
    [InlineData("11111111-2222-3333-4444-555555555555|")]
    [InlineData("11111111-2222-3333-4444-555555555555|8b0e5c1a|garbage")]
    [InlineData("11111111-2222-3333-4444-555555555555|8b0e5c1a|tracked|x")]
    public void RejectsAMalformedValue(string? value)
    {
        Assert.Null(WatchlistEntry.Parse(value));
    }

    /// <summary>
    /// The side that dropped the title decides the action. A title Refract tracks
    /// with another status is never removed.
    /// </summary>
    /// <param name="stillLiked">The Jellyfin item is still liked.</param>
    /// <param name="onWatchlist">Refract still lists it as planned.</param>
    /// <param name="tracked">Refract tracks it with another status.</param>
    /// <param name="expected">The action the sync must take.</param>
    [Theory]
    [InlineData(true, true, false, WatchlistEntry.Action.Keep)]
    [InlineData(true, false, false, WatchlistEntry.Action.ClearLike)]
    [InlineData(false, true, false, WatchlistEntry.Action.RemoveOnRefract)]
    [InlineData(false, false, false, WatchlistEntry.Action.Forget)]
    [InlineData(true, false, true, WatchlistEntry.Action.Keep)]
    [InlineData(true, true, true, WatchlistEntry.Action.Keep)]
    [InlineData(false, false, true, WatchlistEntry.Action.Forget)]
    public void DecidesFromWhatEachSideHolds(bool stillLiked, bool onWatchlist, bool tracked, WatchlistEntry.Action expected)
    {
        Assert.Equal(expected, WatchlistEntry.Decide(stillLiked, onWatchlist, tracked));
    }
}
