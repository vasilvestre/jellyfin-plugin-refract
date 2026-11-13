# Refract for Jellyfin

Records the episodes and movies you watch in Jellyfin on [Refract](https://getrefract.app).

Jellyfin sets the played flag; the plugin sends it to Refract with the date Jellyfin
stored. Clearing the flag in Jellyfin does not clear the watch on Refract: the API
removes a movie play by its own play id, and the plugin keeps no copy of it.

## Requirements

- Jellyfin 12.1 or newer.
- A Refract account. Refract opens its API to a first group of members only: a
  member outside it cannot approve the app.

No key to create and no secret. Each user signs in once from the plugin settings.

## Install

1. Build the plugin or download `Jellyfin.Plugin.Refract.dll`.
2. Create `plugins/Refract_1.0.0.0/` in the Jellyfin data directory.
3. Copy the DLL into that directory and restart Jellyfin.
4. Open **Dashboard**, **Plugins**, **Refract**.
5. Pick a Jellyfin user and click **Sign in to Refract**.
6. Open the address shown, approve the code, and wait for the page to confirm.
   Repeat for each user who wants their own Refract account.


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
- **One Refract account.** All Jellyfin users write to the key's account.
- **No sync from Refract to Jellyfin yet.** The change feed makes it possible.

## Build

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet build Jellyfin.Plugin.Refract -c Release
```

The DLL is `Jellyfin.Plugin.Refract/bin/Release/net10.0/Jellyfin.Plugin.Refract.dll`.
Ship that file alone: the server already provides the Jellyfin assemblies.

## Licence

GPL-3.0, the licence the Jellyfin plugin ecosystem uses.
