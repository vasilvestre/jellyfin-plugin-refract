namespace Jellyfin.Plugin.Refract.Model;

/// <summary>
/// A title on a member's Refract watchlist that has no local Jellyfin item yet.
/// </summary>
/// <param name="Title">The display title.</param>
/// <param name="Year">The release year, or null.</param>
/// <param name="TmdbId">The TMDB id used to request the title through Seerr.</param>
/// <param name="IsShow">True for a show, false for a movie.</param>
public sealed record MissingItem(string Title, int? Year, string TmdbId, bool IsShow);
