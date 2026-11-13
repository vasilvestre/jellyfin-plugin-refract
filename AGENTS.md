# AGENTS.md — jellyfin-plugin-refract

Read this before changing anything. It records what the build, the live server and the
Refract API actually do, not what they are supposed to do.

## What this is

A Jellyfin 12 plugin that marks a play as watched on [Refract](https://getrefract.app) for
each Jellyfin user who linked their own account. One direction only: Jellyfin to Refract.
Episodes use the show plus season and episode number, movies use a play date, anime fall
back to another numbering order.

| Piece | Role |
|---|---|
| `Plugin.cs` | Plugin identity, GUID `c9f4a1d2-7b3e-4f6a-9d21-3e5c8b0a7f14`, admin page declaration |
| `RefractMediator.cs` | Subscribes to `IUserDataManager.UserDataSaved`, matches a title, sends the play |
| `Api/RefractClient.cs` | HTTP to `https://getrefract.app/v1`, search, episode and movie writes, watchlist reads and writes |
| `WatchlistSyncTask.cs` | Scheduled task: keeps the Jellyfin Likes watchlist and the Refract watchlist identical, and lists the non-local titles on demand |
| `KefinTweaksWatchlist.cs` | Reads the JavaScript Injector settings to know whether the KefinTweaks watchlist is on |
| `Model/WatchlistEntry.cs` | The pair stored per synced title, and the keep/remove decision |
| `Model/MissingItem.cs` | A Refract watchlist title the library does not hold, for the Seerr request list |
| `Api/RefractAuthenticator.cs` | Device code grant, token cache, refresh token rotation |
| `Api/RefractController.cs` | Routes the plugin pages call: `Authorize`, `PollAuthorizationStatus`, `Deauthorize`, `Me`, `WatchlistMissing`, `Widget`, `Qr`, `Link` |
| `Configuration/PluginConfiguration.cs` | `RefractUsers[]` plus `ShowLinkButton` and `SyncWatchlist` |
| `Web/` | The admin page (`refract.html/js`), the injected widget (`widget.js`), the QR encoder (`qrcode.js`) |
| `MenuInjection.cs` | Injects the widget into the web client through File Transformation, if installed |

## Build, test, run

There is no .NET SDK on the workstation or on the seedbox. Use the SDK container; the NuGet
cache volume is what keeps an iteration at a few seconds instead of a minute.

```bash
cd ~/projects/oss/jellyfin-plugin-refract
docker run --rm --user $(id -u):$(id -g) \
  -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e NUGET_PACKAGES=/nuget \
  -v "$PWD":/src -v "$PWD/.nugetcache":/nuget -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet build Jellyfin.Plugin.Refract/Jellyfin.Plugin.Refract.csproj -c Release
```

Tests: same command with `dotnet test tests/Jellyfin.Plugin.Refract.Tests -c Release`. With
`--no-restore` when a restore already ran; a restore sometimes segfaults (exit 139) on a
loaded machine, and a plain retry fixes it.

Deploy to the live server (caroline) to try a change:

```bash
scp -q Jellyfin.Plugin.Refract/bin/Release/net10.0/Jellyfin.Plugin.Refract.dll \
  vasilves@le4-0-78-102caroline.pulsedmedia.com:~/.config/jellyfin/data/plugins/Refract_<version>/
ssh vasilves@le4-0-78-102caroline.pulsedmedia.com \
  'systemctl --user restart jellyfin.service'
```

- The plugins directory is `~/.config/jellyfin/data/plugins`, one folder per version. Remove
  the old folder, or Jellyfin loads both.
- Startup takes 75 to 160 seconds on that box, and the API answers 503 meanwhile. A 503 is
  not a routing bug.
- Confirm with `Loaded plugin: "Refract" "<version>"` in
  `~/.config/jellyfin/log/log_$(date +%Y%m%d).log`.
- Do not tick episodes while Jellyfin restarts: the plugin sees no events and the season
  toggle is lost silently.
- The configuration file `~/.config/jellyfin/data/plugins/configurations/Jellyfin.Plugin.Refract.xml`
  holds access and refresh tokens. Never print it, never paste it in an issue.

## Jellyfin 12 facts this plugin depends on

- Plugins target `net10.0` and reference `Jellyfin.Controller` and `Jellyfin.Data` at
  `12.1.0`. `build.yaml` declares `targetAbi: "12.1.0.0"`.
- `Jellyfin.Api` is **not** referenced by plugins. Read the caller id from the
  `Jellyfin-UserId` claim yourself; `User.GetUserId()` does not exist here.
- `User` has no `HasPermission`. Test
  `user.Permissions.Any(p => p.Kind == PermissionKind.IsAdministrator && p.Value)`.
- Keys from the `ApiKeys` table do **not** authenticate plugin controllers, by header or by
  query. Test routes with a browser session, not with an API key.
- `web/ConfigurationPages` requires elevation, so the admin page and `EnableInMainMenu` are
  administrator only. The page HTML itself (`web/ConfigurationPage?name=refract`) is public.
- Plugin JS is an ES module with a default-exported factory taking the view element. The URL
  carries `?v=<assembly version>`: bump the version when you change a page script, or the
  browser serves the cached old one and you debug the wrong code.

## File Transformation

Optional. The plugin registers an `index.html` transformation through reflection on
`Jellyfin.Plugin.FileTransformation.PluginInterface.RegisterTransformation`, because the
assembly cannot be referenced directly.

- The file name key must be the literal `index.html`. File Transformation prefers an exact
  key over the regex keys around it, so `index\.html` never runs.
- Register after startup, not during: File Transformation replays its own transformations on
  `ApplicationStarted`. `RefractMediator` registers at 15 and 60 seconds for that reason.
- Without the plugin, nothing is injected. The admin page stays the only way to link an
  account, and that is documented in the README.
- The injected script is a `<script src>` loader, not inline logic, so the browser fetches
  `Web/widget.js` with the version query.

## Refract API facts

- Credentials are per Jellyfin user, obtained with the device code grant (RFC 8628). The
  client id `WCPKGjzKDEEOYZSgrhsYtJoeElkENStp` is a public client: no secret, safe to commit.
- Access token one hour, refresh token 90 days with rotation. Store the new refresh token
  before using the access token, or a crash loses the link.
- Send a `User-Agent`. The Cloudflare edge in front of Refract rejects some default agents
  with `403 error code: 1010`.
- 120 requests per minute per credential. A bulk season toggle fires one save per episode in
  parallel, so writes are serialised per show with a `SemaphoreSlim`.
- Two writes on the same show at once answer `409 CONFLICT` with
  `detail: concurrent_modification`. Retry once after a short delay; `RefractClient` does.
- Movies need an `Idempotency-Key`. It is derived from user, item and watch date so a retry
  replays instead of duplicating. Tests cover it.
- `PUT history/episodes/{id}/{season}/{number}` is idempotent and leaves an existing watch,
  date included. So the plugin gives no signal when the watch was already there.
- Episode identity is show plus season plus number in **TMDB numbering**. `search/...?type=episode`
  translates the other orders. Anime are where tvdb and absolute disagree most.

## Git strategy

`main` is what the catalogue serves and what the release workflow tags. Keep it releasable.

1. Never commit a feature on `main`. Branch first: `fix/<topic>`, `feat/<topic>`,
   `docs/<topic>`.
2. Push the branch, then `gh pr create --fill`. Read the PR diff before merging it.
3. Merge with `gh pr merge --squash --delete-branch`, so `main` keeps one commit per change.
4. One commit, one concern. Check `git status` before `git add -A`: this repository has
   carried uncommitted work from other sessions, and a blind add mixes it into an unrelated
   commit.
5. Commit subjects are imperative, 72 characters or fewer, no trailing period.
6. The one exception to rule 1 is the release workflow: it pushes its own
   `Release <version>` commit to `main`. Do not fight it, rebase on top.
7. Never force-push `main`. Force-push a tag only while its release is broken and unpublished.
8. Delete the branch after the merge.

A change is done when `Build` and `Test` are green on the PR, not when the code compiles
locally.

## Releasing

`Directory.Build.props` holds the version, and the tag must match it exactly.

```bash
# bump <Version>, <AssemblyVersion>, <FileVersion>
git tag -a v1.0.15.0 -m "What changed, in one sentence"
git push origin v1.0.15.0
```

The `Release` workflow then builds, zips, creates the GitHub release, rewrites `manifest.json`
and `build.yaml`, and commits them to `main`. Do not run `gh release create` by hand: an
existing release makes the workflow upload instead of failing, but you will have skipped the
manifest edit.

## Boundaries

- Do not add a NuGet package. The plugin ships one DLL and Jellyfin provides the rest.
- Do not touch Jellyfin's web files on disk. Injection goes through File Transformation or
  does not happen.
- Do not log tokens, codes, or the contents of the plugin XML.
- Do not send anything to Refract from a test. The tests cover pure logic only.
