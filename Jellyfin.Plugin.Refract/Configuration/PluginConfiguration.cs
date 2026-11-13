using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Refract.Configuration;

/// <summary>
/// Refract plugin settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the Refract API key, created in the app under Access and connected apps.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}
