using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="RefractController"/> class.
    /// </summary>
    /// <param name="authenticator">The device code sign-in.</param>
    public RefractController(RefractAuthenticator authenticator)
    {
        _authenticator = authenticator;
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
        var config = Plugin.Instance.Configuration;
        config.RemoveUser(userGuid);
        Plugin.Instance.UpdateConfiguration(config);
        return NoContent();
    }
}
