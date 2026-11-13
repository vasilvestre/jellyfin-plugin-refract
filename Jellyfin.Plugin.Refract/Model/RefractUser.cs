using System;

namespace Jellyfin.Plugin.Refract.Model;

/// <summary>
/// One Jellyfin user linked to a Refract account.
/// </summary>
public class RefractUser
{
    /// <summary>
    /// Gets or sets the Jellyfin user id.
    /// </summary>
    public Guid LinkedMbUserId { get; set; }

    /// <summary>
    /// Gets or sets the OAuth access token, valid for one hour.
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the refresh token. Refract retires the old one on every refresh.
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the access token stops working, UTC.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets the granted scopes, as Refract echoed them.
    /// </summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device code waiting for approval, if any.
    /// </summary>
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the polling interval the server asked for, in seconds.
    /// </summary>
    public int PollInterval { get; set; } = 5;

    /// <summary>
    /// Gets or sets when the pending device code expires, UTC.
    /// </summary>
    public DateTime DeviceCodeExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets the watchlist pairs the last sync left on both sides, as
    /// "mbId|refractId" or "mbId|refractId|tracked". They are what lets the sync
    /// read a title missing from one side as a removal.
    /// </summary>
    public string[] SyncedItems { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets a value indicating whether the user holds tokens.
    /// </summary>
    public bool IsLinked => !string.IsNullOrEmpty(RefreshToken);
}
