# Refract for Jellyfin

Records the episodes and movies you watch in Jellyfin on [Refract](https://getrefract.app).

Jellyfin sets the played flag; the plugin sends it to Refract with the date Jellyfin
stored. Clearing the flag in Jellyfin does not clear the watch on Refract: the API
removes a movie play by its own play id, and the plugin keeps no copy of it.

## Requirements

- Outbound HTTPS to `https://getrefract.app` from the Jellyfin server. Nothing else, no proxy.
- Jellyfin 12.1 or newer.
- A Refract account. Refract opens its API to a first group of members only: a
  member outside it cannot approve the app.

No key to create and no secret. Each user links their own account once, from the web client.

## Install

From the catalogue:

1. Open **Dashboard**, **Plugins**, **Repositories**.
2. Add `https://raw.githubusercontent.com/vasilvestre/jellyfin-plugin-refract/main/manifest.json`.
3. Open the **Catalogue**, find **Refract**, install it, and restart Jellyfin.

By hand:

1. Download `jellyfin-plugin-refract_<version>.zip` from the releases page.
2. Unzip it into `plugins/Refract_<version>/` in the Jellyfin data directory.
3. Restart Jellyfin.

Then link each account. A member does it from the web client:

1. Open Jellyfin in a browser while logged into the account to link.
2. Click the **Link Refract** button in the bottom right corner.
3. Type the eight-character code in the Refract app, under **Access and connected apps**,
   then **Approve a device**. The app shows the account the code links to.

The button appears through [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation),
which serves a patched `index.html`. It is optional: without it, an administrator links
accounts from **Dashboard**, **Plugins**, **Refract** by picking the user, and the member
approves the code in their own Refract app. Administrators always keep that page.

After a plugin upgrade, reload the page without the cache (Ctrl+Shift+R on Firefox and
Chrome). The widget URL carries the plugin version, so the browser picks up the new one.


## How a title is matched

Jellyfin keeps no TMDB id on episodes, so the plugin resolves them in two steps:

1. It reads the TMDB id of the series, falling back to TheTVDB then IMDb.
2. It asks Refract to translate the season and episode numbers from the series'
   numbering to TMDB numbering.

Anime is where those orders differ. The plugin tries `tmdb`, then `tvdb`, then
`absolute`, and remembers the first combination that matched the series. Enable
debug logging on the plugin to see each attempt.

## Sign-in

The plugin signs in with the OAuth 2.1 device code grant, RFC 8628. A Jellyfin
plugin runs on the server and has no browser, so the member approves a short code
on their phone at `getrefract.app/device`. The client is public: it holds no
secret, and the tokens belong to the member, who can revoke them from the Refract
app at any time.

The access token lasts one hour. The plugin refreshes it before it expires and
stores the rotated refresh token before using it. A refused refresh means the
member disconnected the app: the plugin drops the link and stops syncing.

## Known limits

- **No partial progress.** Refract stores watched or not watched. The resume
  position does not travel.
- **No rewatch from Jellyfin.** The plugin marks an episode watched. Refract keeps
  the first watch date.
- **One Refract account per Jellyfin user.** Each link stores its own tokens.
- **No sync from Refract to Jellyfin yet.** The change feed makes it possible.

## Build

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet build Jellyfin.Plugin.Refract -c Release
```

The DLL is `Jellyfin.Plugin.Refract/bin/Release/net10.0/Jellyfin.Plugin.Refract.dll`.
Ship that file alone: the server already provides the Jellyfin assemblies.

## Credits

The icon in `img/logo.png` is the Refract app icon, used to identify the service the
plugin talks to. Refract owns it; this project claims no rights over the mark.

## Licence

GPL-3.0, the licence the Jellyfin plugin ecosystem uses.
