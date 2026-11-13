using System;
using System.Linq;
using Jellyfin.Plugin.Refract.Model;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Refract.Configuration;

/// <summary>
/// Refract plugin settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the Jellyfin users linked to a Refract account.
    /// </summary>
    public RefractUser[] RefractUsers { get; set; } = Array.Empty<RefractUser>();

    /// <summary>
    /// Gets or sets a value indicating whether the web client shows the link button.
    /// Turn it off when the members of the server do not all use Refract.
    /// </summary>
    public bool ShowLinkButton { get; set; } = true;

    /// <summary>
    /// Gets the credential of a Jellyfin user.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>The credential, or null when the user never linked an account.</returns>
    public RefractUser GetUser(Guid userId) => RefractUsers.FirstOrDefault(u => u.LinkedMbUserId == userId);

    /// <summary>
    /// Adds or replaces the credential of a Jellyfin user.
    /// </summary>
    /// <param name="user">The credential to store.</param>
    public void PutUser(RefractUser user)
    {
        var users = RefractUsers.Where(u => u.LinkedMbUserId != user.LinkedMbUserId).ToList();
        users.Add(user);
        RefractUsers = users.ToArray();
    }

    /// <summary>
    /// Removes the credential of a Jellyfin user.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    public void RemoveUser(Guid userId)
    {
        RefractUsers = RefractUsers.Where(u => u.LinkedMbUserId != userId).ToArray();
    }
}
