# Saves a screenshot of each running RimWorld window as rw-<pid>.png, in -OutDir (default: %TEMP%\ModSyncTest).
# Uses PrintWindow, which renders the window itself, so other windows covering it are never captured.
param([string]$OutDir = (Join-Path $env:TEMP 'ModSyncTest'))

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class CapWin { [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags); }
'@

New-Item -ItemType Directory -Force $OutDir | Out-Null
$running = @(Get-Process RimWorldWin64 -ErrorAction SilentlyContinue | Sort-Object StartTime)
# Drop screenshots of instances that have exited, so an old image is never mistaken for a current one.
Get-ChildItem $OutDir -Filter 'rw-*.png' | Where-Object { $running.Id -notcontains [int]($_.BaseName -replace 'rw-', '') } | Remove-Item

foreach ($p in $running) {
    $r = New-Object CapWin+R
    [void][CapWin]::GetWindowRect($p.MainWindowHandle, [ref]$r)
    $w = $r.Ri - $r.L; $h = $r.B - $r.T
    if ($w -le 0 -or $h -le 0) { continue }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [CapWin]::PrintWindow($p.MainWindowHandle, $hdc, 2) # PW_RENDERFULLCONTENT, needed for DirectX windows
    $g.ReleaseHdc($hdc)
    $out = Join-Path $OutDir "rw-$($p.Id).png"
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "$($p.Id) started $($p.StartTime.ToString('T')): $out$(if (-not $ok) { ' (PrintWindow failed)' })"
}
