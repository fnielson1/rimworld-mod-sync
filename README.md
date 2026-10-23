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

**With the Multiplayer mod:** nothing to set up. While you host a Multiplayer game, Mod Sync hosts too: on TCP with the same port numbers as Multiplayer's direct address and on the Mod Sync port, and over Steam when the game accepts Steam joins. When someone joins with different mods, Mod Sync opens with the server's address filled in and **Restart and rejoin** brings them straight back into the game. Joins through Steam sync over Steam itself, so the host needs no open ports for those. Both are switched on by default in Options → Mod Settings → Mod Sync.

The plan starts from the mod list Multiplayer already received on join, so most syncs don't contact the host's Mod Sync at all: Workshop mods are subscribed (or updated through Steam if their files differ from the host's), the load order is written, and Multiplayer applies the host's mod settings after the restart. The host's Mod Sync is only asked when that isn't enough: local mods you're missing or that differ, a different Mod Sync version, Workshop mods to download from the host (the Mod Settings option, or no Steam), or a host that doesn't share mod settings through Multiplayer. If it can't be reached then, the sync carries on with Multiplayer's data and lists what it couldn't get. A host that doesn't run Mod Sync at all works too; Mod Sync is then turned off on the client to match.

**Security:** Workshop mods come from Steam. Mods copied straight from the host (local mods, or Workshop mods when downloading from the host instead of subscribing) can't be verified and could contain anything. They're only installed if you tick **"I trust and know the host personally"**, which is asked fresh for every sync. Without the tick, those mods are skipped and left out of the mod list, and everything else still syncs. The host only serves its active mod folders and their settings files, and the client refuses any file path that escapes the target folder.

## Build

Requires the .NET SDK. RimWorld and Harmony are referenced through NuGet packages, so the game doesn't need to be installed to build.

```
cd Source
dotnet build -c Release                  # outputs 1.6/Assemblies/ModSync.dll
dotnet build -c Release -p:Deploy=true   # also copies the mod into RimWorld/Mods/ModSync
```

Use `-p:RimWorldDir="D:\SteamLibrary\steamapps\common\RimWorld"` with `Deploy` if the game isn't in the default Steam location.

The built DLL isn't committed. GitHub Actions builds every push to `main` and every pull request, and uploads the ready-to-install mod folder as the `ModSync` artifact on the run.

## Testing with Multiplayer on one PC

The scripts in `tools/` (PowerShell, Windows) run a Host and a Client copy of RimWorld side by side, outside Steam:

```
tools\Start-TwoInstances.ps1            # build, deploy, start both
tools\Start-TwoInstances.ps1 -NoBuild   # start both with the deployed build
tools\Start-TwoInstances.ps1 -Reset     # start over from your current config
```

Each copy gets its own save data folder under `%USERPROFILE%\RimWorldTest`, copied from your real one, which is left alone. The Client runs from `RimWorldTest\ClientGame`, a second game folder whose large data folders are links to your install, so it has its own empty `Mods` folder. A small empty local mod, `Mod Sync Test`, is put in your real `Mods` folder and turned on for the Host only, so joining downloads it from the host. The first Client launch triggers a Windows Firewall prompt for that copy; allow it on private networks for LAN joins.

Then host the Host's game through Multiplayer and join `127.0.0.1` from the Client. The logs are `Player.log` in each save data folder.

`Capture-RimWorld.ps1` screenshots both game windows (and only them), and `Send-RimWorld.ps1 -Which Host -Click 892,257` clicks or types in one, using screenshot coordinates. It moves the real mouse.

Joining through Steam can't be tested this way, because both copies use the same Steam account.

## Releasing an update

1. Bump `<modVersion>` in `About/About.xml`. Clients only update when the host's version is higher.
2. If the network format changed, bump `Protocol.Version` in `Source/Manifest.cs`. Never change the `CmdHello` request or its response: older clients rely on it to find out they're outdated.
3. Build and upload to the Workshop from RimWorld's mod manager. The first upload writes `About/PublishedFileId.txt`. Commit that file so a host running a local build can still point clients at the Workshop item.

## Layout

- `Source/SyncServer.cs`: host. Snapshots the mod list, hashes mods, and serves requests over TCP.
- `Source/SyncJob.cs`: client workflow. Plans the changes, downloads, subscribes, waits for Steam, and writes the mod list.
- `Source/SyncClient.cs`, `Manifest.cs`: wire protocol (one request per connection, binary).
- `Source/SteamTransport.cs`: the same requests over Steam peer-to-peer, for Multiplayer games joined through Steam.
- `tools/`: scripts for testing two RimWorld copies on one PC.
- `Source/ModFolder.cs`: folder hashing and gzip pack/unpack.
- `Source/Dialog_ModSync.cs`, `ModSyncMod.cs`: UI, settings, main menu button.
- `Source/MultiplayerCompat.cs`: optional Multiplayer integration, patched by name at runtime so Multiplayer isn't needed to build or run.
