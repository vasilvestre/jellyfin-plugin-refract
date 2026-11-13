using System;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    /// <summary>
    /// Initializes a new instance of the <see cref="RefractController"/> class.
    /// </summary>
    /// <param name="authenticator">The device code sign-in.</param>
    /// <param name="userManager">Resolves the calling user.</param>
    public RefractController(RefractAuthenticator authenticator, IUserManager userManager)
    {
        _authenticator = authenticator;
        _userManager = userManager;
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

        return Ok(new { userCode, verificationUri, interval, expiresIn });
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
