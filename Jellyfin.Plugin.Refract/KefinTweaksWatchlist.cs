using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Refract;

/// <summary>
/// Reads the KefinTweaks settings from the JavaScript Injector plugin.
/// </summary>
/// <remarks>
/// KefinTweaks keeps no watchlist of its own: it lists and toggles fields on the
/// Jellyfin items. The Refract sync only runs while that feature is on, so it
/// asks first instead of copying a set the member never curated.
/// </remarks>
public static class KefinTweaksWatchlist
{
    private const string ConfigFile = "Jellyfin.Plugin.JavaScriptInjector.xml";

    // The config is a JavaScript object literal, indented by the KefinTweaks page.
    private static readonly Regex WatchlistOn = new("\"watchlist\"\\s*:\\s*true", RegexOptions.Compiled);

    /// <summary>
    /// Says whether KefinTweaks is installed with its watchlist feature on.
    /// </summary>
    /// <param name="paths">The server paths, to find the JavaScript Injector settings.</param>
    /// <returns>True when KefinTweaks is installed with its watchlist feature on.</returns>
    public static bool IsEnabled(IApplicationPaths paths)
    {
        var file = Path.Combine(paths.PluginConfigurationsPath, ConfigFile);
        if (!File.Exists(file))
        {
            return false;
        }

        try
        {
            var document = XDocument.Load(file);
            var scripts = document.Descendants("CustomJavaScriptEntry")
                .Where(entry => (entry.Element("Name")?.Value ?? string.Empty)
                    .StartsWith("KefinTweaks", StringComparison.Ordinal))
                .Select(entry => entry.Element("Script")?.Value ?? string.Empty)
                .ToArray();

            return scripts.Any(script => WatchlistOn.IsMatch(script));
        }
        catch (Exception)
        {
            // A half-written settings file must not stop the server from starting.
            return false;
        }
    }
}
