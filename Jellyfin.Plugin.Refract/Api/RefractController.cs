using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Mime;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Refract.Model;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Net.Codecrete.QrCodeGenerator;

namespace Jellyfin.Plugin.Refract.Api;

/// <summary>
/// Backs the plugin configuration page.
/// </summary>
[ApiController]
[Authorize]
[Route("[controller]")]
[Produces(MediaTypeNames.Application.Json)]
public class RefractController : ControllerBase
{
    private readonly RefractAuthenticator _authenticator;
    private readonly IUserManager _userManager;
    private readonly RefractClient _client;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefractController"/> class.
    /// </summary>
    /// <param name="authenticator">The device code sign-in.</param>
    /// <param name="userManager">Resolves the calling user.</param>
    /// <param name="client">The Refract client, to read the caller's watchlist.</param>
    /// <param name="libraryManager">The library, to resolve titles to local items.</param>
    public RefractController(
        RefractAuthenticator authenticator,
        IUserManager userManager,
        RefractClient client,
        ILibraryManager libraryManager)
    {
        _authenticator = authenticator;
        _userManager = userManager;
        _client = client;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Serves the public page where a member links their own Refract account. Anonymous
    /// on purpose: Jellyfin keeps its plugin pages for administrators, and a member still
    /// has to send the login form to their own server. The page asks for no permission.
    /// </summary>
    /// <returns>The page.</returns>
    [AllowAnonymous]
    [HttpGet("Link")]
    public ActionResult Link()
    {
        var html = ReadEmbedded("link.html");
        if (html is null)
        {
            return NotFound();
        }

        var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0";
        return Content(html.Replace("__VERSION__", version), MediaTypeNames.Text.Html);
    }

    /// <summary>
    /// Serves the widget that adds the in-page button and modal. Anonymous: the
    /// script holds no secret, it reads the session the browser already has.
    /// </summary>
    /// <returns>The script.</returns>
    [AllowAnonymous]
    [HttpGet("Widget")]
    public ActionResult Widget() => Resource("widget.js", "application/javascript");

    /// <summary>
    /// Serves the shared device-code sign-in flow that the widget and the link
    /// page both load.
    /// </summary>
    /// <returns>The script.</returns>
    [AllowAnonymous]
    [HttpGet("Flow")]
    public ActionResult Flow() => Resource("flow.js", "application/javascript");

    private ActionResult Resource(string name, string contentType)
    {
        var contents = ReadEmbedded(name);
        if (contents is null)
        {
            return NotFound();
        }

        return Content(contents, contentType);
    }

    private string ReadEmbedded(string name)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(typeof(Plugin).Namespace + ".Web." + name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Says whether the calling user linked a Refract account. Safe for a non admin:
    /// it reports one boolean and never a token.
    /// </summary>
    /// <returns>The linked state of the caller.</returns>
    [HttpGet("Me")]
    public ActionResult<object> Me()
    {
        var caller = CallerId;
        var linked = Plugin.Instance.Configuration.GetUser(caller)?.IsLinked == true;
        return Ok(new { linked });
    }

    /// <summary>
    /// Lists the caller's Refract watchlist titles that have no local item yet, so the
    /// web client can offer a "request through Seerr" action. Never returns a token.
    /// </summary>
    /// <returns>The titles and the TMDB id each one needs to be requested.</returns>
    [HttpGet("WatchlistMissing")]
    public async Task<ActionResult<object>> WatchlistMissing(CancellationToken cancellationToken)
    {
        var caller = CallerId;
        IReadOnlyList<MissingItem> missing = Array.Empty<MissingItem>();
        if (Plugin.Instance.Configuration.GetUser(caller)?.IsLinked == true)
        {
            missing = await WatchlistSyncTask.ComputeMissingAsync(caller, _client, _libraryManager, cancellationToken).ConfigureAwait(false);
        }

        var items = missing
            .Select(m => new { title = m.Title, year = m.Year, tmdbId = m.TmdbId, mediaType = m.IsShow ? "tv" : "movie" })
            .ToArray();
        return Ok(new { items });
    }

    /// <summary>
    /// Starts the device code sign-in for a Jellyfin user.
    /// </summary>
    /// <param name="userGuid">The Jellyfin user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The code to display and the page where the member types it.</returns>
    [HttpPost("Users/{userGuid}/Authorize")]
    public async Task<ActionResult<object>> Authorize([FromRoute] Guid userGuid, CancellationToken cancellationToken)
    {
        if (!CanActOn(userGuid))
        {
            return Forbid();
        }

        var (userCode, verificationUri, interval, expiresIn) =
            await _authenticator.StartAsync(userGuid, cancellationToken).ConfigureAwait(false);

        var qrPng = Convert.ToBase64String(
            QrCode.EncodeText(verificationUri, QrCode.Ecc.Medium).ToPngBitmap(4, 8));
        return Ok(new { userCode, verificationUri, interval, expiresIn, qrPng });
    }

    /// <summary>
    /// Tells the page whether the member approved the code yet.
    /// </summary>
    /// <param name="userGuid">The Jellyfin user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>One of pending, slow_down, authorized, expired or denied.</returns>
    [HttpGet("Users/{userGuid}/PollAuthorizationStatus")]
    public async Task<ActionResult<object>> Poll([FromRoute] Guid userGuid, CancellationToken cancellationToken)
    {
        if (!CanActOn(userGuid))
        {
            return Forbid();
        }

        var status = await _authenticator.PollAsync(userGuid, cancellationToken).ConfigureAwait(false);
        return Ok(new { status });
    }

    /// <summary>
    /// Forgets the tokens of a Jellyfin user.
    /// </summary>
    /// <param name="userGuid">The Jellyfin user id.</param>
    /// <returns>No content.</returns>
    [HttpPost("Users/{userGuid}/Deauthorize")]
    public ActionResult Deauthorize([FromRoute] Guid userGuid)
    {
        if (!CanActOn(userGuid))
        {
            return Forbid();
        }

        var config = Plugin.Instance.Configuration;
        config.RemoveUser(userGuid);
        Plugin.Instance.UpdateConfiguration(config);
        return NoContent();
    }

    /// <summary>
    /// Gets the id of the calling user. Jellyfin puts it in a custom claim; an API key
    /// has no user id, so the caller stays unknown and gets a refusal.
    /// </summary>
    private Guid CallerId
    {
        get
        {
            var raw = User.FindFirst("Jellyfin-UserId")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
        }
    }

    /// <summary>
    /// A user links their own account. An administrator may link for anyone.
    /// </summary>
    /// <param name="userGuid">The account the request targets.</param>
    /// <returns>True when the caller may act on that account.</returns>
    private bool CanActOn(Guid userGuid)
    {
        if (userGuid == CallerId)
        {
            return true;
        }

        var caller = _userManager.GetUserById(CallerId);
        return caller?.Permissions.Any(p => p.Kind == PermissionKind.IsAdministrator && p.Value) == true;
    }
}
