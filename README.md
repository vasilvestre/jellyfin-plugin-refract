# Refract for Jellyfin

Records the episodes and movies you watch in Jellyfin on [Refract](https://getrefract.app).

Jellyfin sets the played flag; the plugin sends it to Refract with the date Jellyfin
stored. Clearing the flag in Jellyfin does not clear the watch on Refract: the API
removes a movie play by its own play id, and the plugin keeps no copy of it.

## Requirements

- Jellyfin 12.1 or newer.
- A Refract API key. In the app open **Integrations**, then **Access and connected
  apps**, and create a key with the scopes `profile:read`, `library:read`,
  `history:read`, `library:write`, `history:write`.
  Refract opens the API to a first group of members only. A member outside it
  cannot create a key.

## Install

1. Build the plugin or download `Jellyfin.Plugin.Refract.dll`.
2. Create `plugins/Refract_1.0.0.0/` in the Jellyfin data directory.
3. Copy the DLL into that directory and restart Jellyfin.
4. Open **Dashboard**, **Plugins**, **Refract**, paste the key, and save.

## How a title is matched

Jellyfin keeps no TMDB id on episodes, so the plugin resolves them in two steps:

1. It reads the TMDB id of the series, falling back to TheTVDB then IMDb.
2. It asks Refract to translate the season and episode numbers from the series'
   numbering to TMDB numbering.

Anime is where those orders differ. The plugin tries `tmdb`, then `tvdb`, then
`absolute`, and remembers the first combination that matched the series. Enable
debug logging on the plugin to see each attempt.

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
