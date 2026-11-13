using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Refract.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Refract;

/// <summary>
/// Refract plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Refract";

    /// <inheritdoc />
    public override Guid Id => new Guid("c9f4a1d2-7b3e-4f6a-9d21-3e5c8b0a7f14");

    /// <inheritdoc />
    public override string Description => "Record the episodes and movies you watch on getrefract.app.";

    /// <summary>
    /// Gets the running plugin instance.
    /// </summary>
    public static Plugin Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "refract",
                EmbeddedResourcePath = GetType().Namespace + ".Web.refract.html"
            },
            new PluginPageInfo
            {
                Name = "refractjs",
                EmbeddedResourcePath = GetType().Namespace + ".Web.refract.js"
            }
        };
    }
}
