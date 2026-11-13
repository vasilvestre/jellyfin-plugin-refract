using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Refract;

/// <summary>
/// Adds a "Link Refract" button to the web client for users who linked nothing yet.
/// Jellyfin only shows plugin pages to administrators, so a member cannot reach the
/// settings page. File Transformation serves the patched index.html without touching
/// the files on disk. It is optional: without it, an administrator links every account.
/// </summary>
internal static class MenuInjection
{
    private const string Marker = "id=\"refract-link-button\"";

    private static ILogger _logger;
    private static int _calls;

    /// <summary>
    /// Registers the index.html transformation when File Transformation is installed.
    /// Call it once, after every plugin loaded, so its service provider exists.
    /// </summary>
    /// <param name="logger">The plugin logger.</param>
    public static void Register(ILogger logger)
    {
        _logger = logger;
        try
        {
            var fileTransformation = AssemblyLoadContext.All
                .SelectMany(context => context.Assemblies)
                .FirstOrDefault(assembly => assembly.GetName().Name == "Jellyfin.Plugin.FileTransformation");

            if (fileTransformation is null)
            {
                logger.LogInformation("Refract: File Transformation is absent, an administrator links each account");
                return;
            }

            var newtonsoft = AssemblyLoadContext.All
                .SelectMany(context => context.Assemblies)
                .FirstOrDefault(assembly => assembly.GetName().Name == "Newtonsoft.Json");

            var pluginInterface = fileTransformation.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            var register = pluginInterface?.GetMethod("RegisterTransformation");
            var jObjectType = newtonsoft?.GetType("Newtonsoft.Json.Linq.JObject");
            var parse = jObjectType?.GetMethod("Parse", new[] { typeof(string) });

            if (register is null || parse is null)
            {
                logger.LogWarning("Refract: File Transformation is installed but its interface was not found");
                return;
            }

            // File Transformation takes its own JObject, loaded in its own context.
            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["id"] = Guid.Parse("2f6c1d40-8b3a-4e51-9f27-6d0c4a9b7e31"),
                ["fileNamePattern"] = "index.html",
                ["callbackAssembly"] = typeof(MenuInjection).Assembly.FullName,
                ["callbackClass"] = typeof(MenuInjection).FullName,
                ["callbackMethod"] = nameof(Patch)
            });

            register.Invoke(null, new[] { parse.Invoke(null, new object[] { payload }) });
            logger.LogInformation("Refract: registered the index.html transformation");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Refract: could not register the File Transformation");
        }
    }

    /// <summary>
    /// Gets or sets the served file, filled by File Transformation.
    /// </summary>
    public sealed class Payload
    {
        /// <summary>
        /// Gets or sets the current contents of the file.
        /// </summary>
        public string Contents { get; set; }
    }

    /// <summary>
    /// Adds the button script to index.html. File Transformation calls this through
    /// reflection, on every request for a matching file.
    /// </summary>
    /// <param name="payload">The file being served.</param>
    /// <returns>The patched file.</returns>
    public static string Patch(Payload payload)
    {
        var html = payload == null ? null : payload.Contents;
        int call = System.Threading.Interlocked.Increment(ref _calls);
        if (call > 5)
        {
            call = -1;
        }

        if (html is null || html.Contains(Marker, StringComparison.Ordinal))
        {
            if (call > 0)
            {
                _logger?.LogInformation("Refract: index.html pass {Call}, marker already there ({Length} characters)", call, html == null ? -1 : html.Length);
            }

            return html;
        }

        var script = "<script " + Marker + ">" + Script() + "</script>";
        var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        var patched = head < 0 ? html + script : html.Insert(head, script);
        if (call > 0)
        {
            _logger?.LogInformation("Refract: index.html pass {Call}, {In} characters in, {Out} characters out", call, html.Length, patched.Length);
        }

        return patched;
    }

    // Loads the widget script, which builds the button and the modal itself. The
    // base path is only known in the browser, so the tag cannot be static.
    // The version query defeats the browser cache: without it an old widget keeps
    // running after an upgrade and the new behaviour never shows up.
    private static string Script()
    {
        var version = typeof(MenuInjection).Assembly.GetName().Version?.ToString() ?? "0";
        return
            "(function(){var b=location.pathname.replace(/\\/web\\/.*$/,'');" +
            "if(window.__refractWidget)return;window.__refractWidget=1;" +
            "var s=document.createElement('script');s.src=b+'/Refract/Widget?v=" + version + "';s.async=true;" +
            "document.head.appendChild(s)})();";
    }
}
