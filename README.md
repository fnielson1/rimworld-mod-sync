# Mod Sync (RimWorld 1.6)

Copies the host's active mods, load order and mod settings to your game.

## Use

**Host:** Main menu → **Mod Sync** → **Start hosting**. Give friends the address shown (LAN), your public IP with TCP port 28150 forwarded, or your Tailscale/ZeroTier/Hamachi address. "Start hosting automatically" is in Options → Mod Settings → Mod Sync.

**Client:** Main menu → **Mod Sync** → enter the host's address → **Check** to see what would change → **Sync** → **Restart now**.

What happens on sync:

| Host's mod is...                   | Client gets it by...                                                           |
|------------------------------------|--------------------------------------------------------------------------------|
| Core / DLC                         | Nothing to download; a warning if you don't own it                             |
| Steam Workshop                     | Subscribing through Steam (or a direct download if Steam isn't running / the setting is on) |
| Local (`RimWorld/Mods`)            | Direct download from the host, compared by content hash, **only if you tick "I trust and know the host personally"** |
| Mod Sync itself                    | Skipped, so a running copy is never overwritten                                 |

Then the mod list is written in the host's load order, the host's `Config/Mod_*.xml` settings for those mods are copied, and the game restarts. Your game prefs and keybinds are left alone.

Anything replaced is backed up first, to `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\ModSync\Backup\<timestamp>`.

**Keeping Mod Sync itself up to date:** before anything else, the client compares its Mod Sync version with the host's. If the host's is newer, the client asks Steam to update Mod Sync from the Workshop. Once the new version is on disk, **Restart and continue** reloads the game and carries on with the sync. If Steam can't replace the files while the game is running, **Exit so Steam can update** quits the game, and the sync resumes when you next start it. A client that isn't using the Workshop copy (a local copy, or no Steam) is told to update by hand and given the Workshop page.

**Security:** Workshop mods come from Steam. Mods copied straight from the host (local mods, or Workshop mods when downloading from the host instead of subscribing) can't be verified and could contain anything. They're only installed if you tick **"I trust and know the host personally"**, which is asked fresh for every sync. Without the tick, those mods are skipped and left out of the mod list, and everything else still syncs. The host only serves its active mod folders and their settings files, and the client refuses any file path that escapes the target folder.

## Build

Requires the .NET SDK and RimWorld + Harmony (Workshop) installed.

```
cd Source
dotnet build -c Release                  # outputs 1.6/Assemblies/ModSync.dll
dotnet build -c Release -p:Deploy=true   # also copies the mod into RimWorld/Mods/ModSync
```

Use `-p:RimWorldDir="D:\SteamLibrary\steamapps\common\RimWorld"` if the game isn't in the default Steam location.

## Releasing an update

1. Bump `<modVersion>` in `About/About.xml`. Clients only update when the host's version is higher.
2. If the network format changed, bump `Protocol.Version` in `Source/Manifest.cs`. Never change the `CmdHello` request or its response: older clients rely on it to find out they're outdated.
3. Build and upload to the Workshop from RimWorld's mod manager. The first upload writes `About/PublishedFileId.txt`. Commit that file so a host running a local build can still point clients at the Workshop item.

## Layout

- `Source/SyncServer.cs`: host. Snapshots the mod list, hashes mods, and serves requests over TCP.
- `Source/SyncJob.cs`: client workflow. Plans the changes, downloads, subscribes, waits for Steam, and writes the mod list.
- `Source/SyncClient.cs`, `Manifest.cs`: wire protocol (one request per connection, binary).
- `Source/ModFolder.cs`: folder hashing and gzip pack/unpack.
- `Source/Dialog_ModSync.cs`, `ModSyncMod.cs`: UI, settings, main menu button.
