# Drives a RimWorld window with real (SendInput) mouse and keyboard input, then saves fresh screenshots with
# Capture-RimWorld.ps1. This moves the real mouse, so leave it alone while the script runs.
#   -Which Host|Client           window to act on (Host = the instance started first)
#   -Click x,y                   click at x,y in that window's screenshot (rw-<pid>.png coordinates)
#   -Keys Escape|Enter|Tab|...   press keys in order
#   -Text "..."                  type text
#   -Wait seconds                wait before taking screenshots (default 1.5)
#   -OutDir                      where screenshots go (default: %TEMP%\ModSyncTest)
param(
    [ValidateSet('Host', 'Client')][string]$Which = 'Host',
    [int[]]$Click,
    [string[]]$Keys,
    [string]$Text,
    [double]$Wait = 1.5,
    [string]$OutDir = (Join-Path $env:TEMP 'ModSyncTest')
)
$ErrorActionPreference = 'Stop'

Add-Type @'
using System; using System.Runtime.InteropServices; using System.Threading;
public static class RwInput {
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; [FieldOffset(8)] public KEYBDINPUT ki; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);

    static void Send(INPUT i) { SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT))); }
    public static void Click(int x, int y) {
        SetCursorPos(x, y); Thread.Sleep(80);
        Send(new INPUT { type = 0, mi = new MOUSEINPUT { flags = 0x0002 } }); Thread.Sleep(60); // left down
        Send(new INPUT { type = 0, mi = new MOUSEINPUT { flags = 0x0004 } });                   // left up
    }
    public static void Key(ushort vk) {
        ushort scan = (ushort)MapVirtualKey(vk, 0);
        Send(new INPUT { type = 1, ki = new KEYBDINPUT { vk = vk, scan = scan } }); Thread.Sleep(50);
        Send(new INPUT { type = 1, ki = new KEYBDINPUT { vk = vk, scan = scan, flags = 0x0002 } }); Thread.Sleep(50);
    }
    public static void Char(char c) {
        Send(new INPUT { type = 1, ki = new KEYBDINPUT { scan = c, flags = 0x0004 } }); Thread.Sleep(20);          // KEYEVENTF_UNICODE
        Send(new INPUT { type = 1, ki = new KEYBDINPUT { scan = c, flags = 0x0004 | 0x0002 } }); Thread.Sleep(20);
    }
}
'@

$procs = @(Get-Process RimWorldWin64 | Sort-Object StartTime)
$p = if ($Which -eq 'Host') { $procs[0] } else { $procs[-1] }
$h = $p.MainWindowHandle
# Fails while another app (a Windows Security prompt, say) holds the foreground; the input then goes nowhere.
[void][RwInput]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 250

if ($Click) {
    $r = New-Object RwInput+R
    [void][RwInput]::GetWindowRect($h, [ref]$r)
    [RwInput]::Click($r.L + $Click[0], $r.T + $Click[1])
}
$vk = @{ Escape = 0x1B; Enter = 0x0D; Tab = 0x09; Backspace = 0x08; Space = 0x20; Delete = 0x2E; Home = 0x24; End = 0x23 }
foreach ($k in $Keys) { [RwInput]::Key([uint16]$vk[$k]) }
if ($Text) { foreach ($c in $Text.ToCharArray()) { [RwInput]::Char($c) } }

Start-Sleep -Milliseconds ([int]($Wait * 1000))
& (Join-Path $PSScriptRoot 'Capture-RimWorld.ps1') -OutDir $OutDir
