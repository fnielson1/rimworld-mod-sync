# Builds and deploys Mod Sync, then starts two RimWorld instances (Host, Client) outside Steam for testing the
# Multiplayer integration on one machine. Each gets its own save data folder, seeded from your real one, which is
# never modified.
#
# The Client runs from its own game folder (exe copied, big data folders linked to the real install) so it has its
# own empty Mods folder: RimWorld always uses the Mods folder next to the exe. The Host gets a small local test mod
# the Client doesn't have, so joining exercises downloading a mod from the host.
#
# Joining through Steam can't be tested this way: both instances use the same Steam account.
#
#   -Reset       re-seed both save data folders and the Client's Mods folder
#   -NoBuild     skip building / deploying Mod Sync
#   -RimWorldDir RimWorld install folder, if not the default Steam location
param(
    [switch]$Reset,
    [switch]$NoBuild,
    [string]$RimWorldDir = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld'
)
$ErrorActionPreference = 'Stop'

$repo       = Split-Path $PSScriptRoot -Parent
$rw         = $RimWorldDir
$real       = "$env:USERPROFILE\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios"
$testDir    = "$env:USERPROFILE\RimWorldTest"
$clientGame = Join-Path $testDir 'ClientGame'
$modSync    = 'fnielson.modsync'
$testMod    = 'fnielson.modsynctest'
# Left out of the client's mod list so joining produces a mismatch even without the test mod.
$dropFromClient = 'cat2002.showhair'

if (-not $NoBuild) {
    Push-Location "$repo\Source"
    try { dotnet build -c Release -p:Deploy=true -p:RimWorldDir="$rw" | Select-String -Pattern 'error|Build succeeded' }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
}

# ---- Local test mod, Host only ----
$testModDir = "$rw\Mods\ModSyncTest"
if (-not (Test-Path "$testModDir\About\About.xml")) {
    New-Item -ItemType Directory -Force "$testModDir\About", "$testModDir\Defs" | Out-Null
    @"
<?xml version="1.0" encoding="utf-8"?>
<ModMetaData>
  <name>Mod Sync Test</name>
  <author>fnielson</author>
  <packageId>$testMod</packageId>
  <supportedVersions><li>1.6</li></supportedVersions>
  <description>Local mod used to test downloading mods from the host with Mod Sync. Adds nothing.</description>
</ModMetaData>
"@ | Set-Content "$testModDir\About\About.xml" -Encoding utf8
    '<?xml version="1.0" encoding="utf-8"?><Defs />' | Set-Content "$testModDir\Defs\Empty.xml" -Encoding utf8
}

# ---- Client game folder ----
if ($Reset -and (Test-Path "$clientGame\Mods")) { Remove-Item "$clientGame\Mods" -Recurse -Force }
New-Item -ItemType Directory -Force $clientGame, "$clientGame\Mods" | Out-Null
foreach ($dir in 'RimWorldWin64_Data', 'MonoBleedingEdge', 'Data') {
    if (-not (Test-Path "$clientGame\$dir")) { New-Item -ItemType Junction -Path "$clientGame\$dir" -Target "$rw\$dir" | Out-Null }
}
foreach ($file in 'RimWorldWin64.exe', 'UnityPlayer.dll', 'UnityCrashHandler64.exe', 'steam_appid.txt', 'Version.txt') {
    Copy-Item "$rw\$file" "$clientGame\$file" -Force
}
# The Client needs Mod Sync itself; everything else it gets from the host.
if (Test-Path "$clientGame\Mods\ModSync") { Remove-Item "$clientGame\Mods\ModSync" -Recurse -Force }
Copy-Item "$rw\Mods\ModSync" "$clientGame\Mods\ModSync" -Recurse

function Initialize-SaveData($name, [string[]]$drop, [string[]]$add) {
    $dir = Join-Path $testDir $name
    if ($Reset -and (Test-Path $dir)) { Remove-Item $dir -Recurse -Force }
    if (-not (Test-Path "$dir\Config")) {
        New-Item -ItemType Directory -Force "$dir\Config" | Out-Null
        Copy-Item "$real\Config\*" "$dir\Config" -Recurse
        if (Test-Path "$real\Saves") { Copy-Item "$real\Saves" $dir -Recurse }

        # Windowed, so both instances fit on screen.
        $prefs = "$dir\Config\Prefs.xml"
        if (Test-Path $prefs) {
            $c = Get-Content $prefs -Raw
            $c = $c -replace '<screenWidth>\d+</screenWidth>', '<screenWidth>1280</screenWidth>' `
                    -replace '<screenHeight>\d+</screenHeight>', '<screenHeight>720</screenHeight>' `
                    -replace '<fullscreen>\w+</fullscreen>', '<fullscreen>False</fullscreen>'
            Set-Content $prefs $c -Encoding utf8 -NoNewline
        }
    }

    # Applied every launch, so a sync from an earlier test doesn't leave the lists matching.
    $path = "$dir\Config\ModsConfig.xml"
    $xml = [xml](Get-Content $path -Raw)
    $active = $xml.ModsConfigData.activeMods
    foreach ($node in @($active.ChildNodes)) {
        if ($drop -contains $node.InnerText -or $add -contains $node.InnerText) { [void]$active.RemoveChild($node) }
    }
    foreach ($id in $add) { $node = $xml.CreateElement('li'); $node.InnerText = $id; [void]$active.AppendChild($node) }
    $xml.Save($path)
    $dir
}

$hostDir   = Initialize-SaveData 'Host' @() @($modSync, $testMod)
$clientDir = Initialize-SaveData 'Client' @($dropFromClient, $testMod) @($modSync)

Add-Type @'
using System; using System.Runtime.InteropServices;
public static class RwWindow { [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f); }
'@

$offset = 0
foreach ($i in @(@{ Name = 'Host'; Dir = $hostDir; Game = $rw }, @{ Name = 'Client'; Dir = $clientDir; Game = $clientGame })) {
    $log = Join-Path $i.Dir 'Player.log'
    # -username: both instances share one Steam account, and Multiplayer turns away a duplicate name.
    $cmd = "`"$($i.Game)\RimWorldWin64.exe`" `"-savedatafolder=$($i.Dir)`" -username=$($i.Name) -window-mode windowed -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -logFile `"$log`""
    # Started through WMI so the game isn't a child of this shell: closing or recycling the shell would otherwise close the game too.
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd; CurrentDirectory = $i.Game }
    if ($r.ReturnValue -ne 0) { throw "Couldn't start $($i.Name) (Win32_Process.Create returned $($r.ReturnValue))" }
    $p = Get-Process -Id $r.ProcessId
    "Started $($i.Name) (pid $($p.Id)) from $($i.Game): save data $($i.Dir), log $log"
    # Cascade the windows so both can be reached (two 1280-wide windows don't fit side by side on 1920).
    for ($t = 0; $t -lt 60 -and $p.MainWindowHandle -eq [IntPtr]::Zero; $t++) { Start-Sleep -Milliseconds 500; $p.Refresh() }
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) { [void][RwWindow]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, $offset, $offset * 321 / 624, 0, 0, 0x0005) }
    $offset += 624 # 1296x759 window + 624,321 = flush with a 1920x1080 screen's corner
    Start-Sleep -Seconds 5 # stagger startup so the two don't fight over shared files
}
