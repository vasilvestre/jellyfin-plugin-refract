using Jellyfin.Plugin.Refract;

namespace Jellyfin.Plugin.Refract.Tests;

/// <summary>
/// The browser injection. File Transformation matches "index.html" as a regex,
/// so it also rewrites the *-index-html.*.chunk.js bundles; appending there
/// breaks the page with "Unexpected token '&lt;'".
/// </summary>
public class MenuInjectionTests
{
    /// <summary>A file that is not HTML must come back unchanged.</summary>
    [Fact]
    public void NonHtmlFilesAreReturnedUntouched()
    {
        const string bundle = "\"use strict\";(self.webpackChunk=self.webpackChunk||[]).push([[76331],{}]);";

        Assert.Equal(bundle, MenuInjection.InsertButton(bundle));
    }

    /// <summary>HTML gets the script just before its closing head.</summary>
    [Fact]
    public void ScriptGoesBeforeTheClosingHead()
    {
        var patched = MenuInjection.InsertButton("<html><head><title>x</title></head><body></body></html>");

        Assert.Contains("id=\"refract-link-button\"", patched);
        Assert.True(patched.IndexOf("refract-link-button", StringComparison.Ordinal) < patched.IndexOf("</head>", StringComparison.Ordinal));
    }
}
