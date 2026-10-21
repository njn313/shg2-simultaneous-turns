# Loads a patched empire.retail.dll with the real Windows loader, without running any of it, and checks the patch in
# memory. The DLL's preferred address is blocked first, so the loader must relocate it (as ASLR does): the mod's
# section must come out byte-identical to the file and every hook must still land in it. Must run in 32-bit
# PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -ExecutionPolicy Bypass -File verify_load.ps1 <dll>
param([Parameter(Mandatory = $true)][string]$Dll)
$ErrorActionPreference = 'Stop'
if ([IntPtr]::Size -ne 4) { throw "Run this in 32-bit PowerShell: C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" }
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class St {
	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	public static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
	[DllImport("kernel32.dll")] public static extern bool FreeLibrary(IntPtr module);
	[DllImport("kernel32.dll", SetLastError = true)]
	public static extern IntPtr VirtualAlloc(IntPtr at, IntPtr size, uint type, uint protect);
	public static byte[] Copy(IntPtr at, int n) { var r = new byte[n]; Marshal.Copy(at, r, 0, n); return r; }
}
"@
$path = (Resolve-Path $Dll).Path
$file = [IO.File]::ReadAllBytes($path)
$preferred = 0x10000000
function Hex($bytes) { ($bytes | ForEach-Object { $_.ToString('X2') }) -join ' ' }

# section table from the file
$pe = [BitConverter]::ToInt32($file, 0x3C)
$nSec = [BitConverter]::ToUInt16($file, $pe + 6)
$table = $pe + 24 + [BitConverter]::ToUInt16($file, $pe + 20)
$secs = @{}
for ($i = 0; $i -lt $nSec; $i++) {
	$o = $table + 40 * $i
	$name = [Text.Encoding]::ASCII.GetString($file, $o, 8).TrimEnd([char]0)
	$secs[$name] = @{ Va = [uint32]($preferred + [BitConverter]::ToUInt32($file, $o + 12)); Size = [BitConverter]::ToInt32($file, $o + 8); Raw = [BitConverter]::ToInt32($file, $o + 20) }
}
if (-not $secs.ContainsKey('.s2st')) { throw "no .s2st section: not a build of this mod" }
$mod = $secs['.s2st']
"base               : " + $(if ($secs.ContainsKey('.s2mp')) { 'on top of the 8-player mod (.s2mp present)' } else { 'original game DLL' })

# block the preferred address, then load (DONT_RESOLVE_DLL_REFERENCES: map and relocate only, no DllMain, no imports)
$block = [St]::VirtualAlloc([IntPtr]$preferred, [IntPtr]0x10000, 0x2000, 0x01)   # MEM_RESERVE, PAGE_NOACCESS
$h = [St]::LoadLibraryExW($path, [IntPtr]::Zero, 0x1)
if ($h -eq [IntPtr]::Zero) { throw "LoadLibraryEx failed, error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
$base = [int64]$h.ToInt32() -band 0xFFFFFFFF
"mapped at          : {0:X8} (preferred {1:X8}{2})" -f $base, $preferred, $(if ($base -ne $preferred) { ', relocated' } else { ': NOT relocated, the address could not be blocked' })
function Mem([uint32]$va, [int]$n) { [St]::Copy([IntPtr]([int64]$va - $preferred + $base), $n) }
$ok = $true
if ($block -eq [IntPtr]::Zero -or $base -eq $preferred) { $ok = $false }

# the mod's section: mapped exactly as in the file (nothing in it is relocated)
$fileSec = New-Object byte[] $mod.Size
[Array]::Copy($file, $mod.Raw, $fileSec, 0, $mod.Size)
$same = [Linq.Enumerable]::SequenceEqual([byte[]](Mem $mod.Va $mod.Size), [byte[]]$fileSec)
"section .s2st      : {0:X8}, {1} bytes, identical to the file: {2}" -f $mod.Va, $mod.Size, $same
if (-not $same) { $ok = $false }
# the log routine comes first (pushfd / pushad), its file name is in the section's data
$start = Mem $mod.Va 2
$hasName = [BitConverter]::ToString($fileSec).Contains([BitConverter]::ToString([Text.Encoding]::Unicode.GetBytes('shared_turns.log')))
"log routine        : {0} at the section start; shared_turns.log in the section: {1}" -f (Hex $start), $hasName
if ((Hex $start) -ne '9C 60' -or -not $hasName) { $ok = $false }

# every hook (call or jmp rel32) must land in the section; the list matches ApplyGroupTurns in lib\Patcher.cs
$hooks = @(
	@(0x10646DCA, 'group reset new'), @(0x10649B33, 'group reset load'), @(0x10701DB0, 'group start'), @(0x10704313, 'group start load'),
	@(0x106A5FD9, 'group turn end'), @(0x106A603B, 'picker skip'), @(0x106A609E, 'new round'), @(0x10886507, 'end turn command'),
	@(0x1070420A, 'end turn latch'), @(0x1088655A, 'member latch'), @(0x107041CA, 'group tick'),
	@(0x106AA5E8, 'can end turn 1'), @(0x106AA5FE, 'can end turn 2'), @(0x106AA6AB, 'mover allowed 1'), @(0x106AA777, 'mover allowed 2'),
	@(0x106AA89F, 'action allowed'), @(0x106E4F73, 'attack allowed'), @(0x108FFE3B, 'mover owner'), @(0x108FFEC8, 'resume orders 1'),
	@(0x108FFEF4, 'resume orders 2'), @(0x108FFF4A, 'resume orders 3'), @(0x106EC0E0, 'char moved'), @(0x106C5A84, 'order notify'),
	@(0x1069867B, 'disband allowed'), @(0x1111E84F, 'end turn ui'), @(0x11155403, 'turn time up'), @(0x1118C09F, 'order cursor'),
	@(0x106AA710, 'pf mover acts'), @(0x10900082, 'pf stop reissue'), @(0x10900137, 'pf moves done'), @(0x108D2699, 'pf command done'),
	@(0x10703F27, 'pf commands'), @(0x1115AB26, 'pf ui hover 1'), @(0x111516B2, 'pf ui hover 2'), @(0x11160272, 'pf ui click 1'),
	@(0x1114AE22, 'pf ui click 2'), @(0x10883DD0, 'move log'), @(0x11129E27, 'click log 1'), @(0x1115199D, 'click log 2'),
	@(0x1114174C, 'own action turn'), @(0x105FD550, 'pf turn start'),
	@(0x106EA3B9, 'battle new 1'), @(0x106EA603, 'battle new 2'), @(0x1057FCA0, 'battle state'), @(0x1088A5DD, 'battle ready'))
foreach ($hook in $hooks) {
	$ins = Mem $hook[0] 5
	$dest = ([int64]$hook[0] + 5 + [BitConverter]::ToInt32($ins, 1)) -band 0xFFFFFFFF
	$in = ($ins[0] -eq 0xE8 -or $ins[0] -eq 0xE9) -and $dest -ge $mod.Va -and $dest -lt ($mod.Va + $mod.Size)
	"hook {0,-17}: {1:X8} {2} -> {3:X8}  in .s2st: {4}" -f $hook[1], $hook[0], (Hex $ins[0..0]), $dest, $in
	if (-not $in) { $ok = $false }
}
# End Turn names the local faction: mov eax,[ecx+0B0h] and 6 nops in place of [[[ecx+8]+1498h]+2Ch]
$endTurn = Mem 0x1111E8BF 12
"end turn faction   : {0}" -f (Hex $endTurn)
if ((Hex $endTurn) -ne '8B 81 B0 00 00 00 90 90 90 90 90 90') { $ok = $false }
# the mod's data 11CB1538..11CB15AF (group state, log flags, pathfinder state): mapped, inside .data, zero
$dataEnd = [int64]$secs['.data'].Va + $secs['.data'].Size
$data = Mem 0x11CB1538 0x78
$nonzero = @($data | Where-Object { $_ -ne 0 }).Count
".data              : ends at {0:X8} (needs 11CB15B0); mod data nonzero bytes: {1}" -f $dataEnd, $nonzero
if ($dataEnd -lt 0x11CB15B0 -or $nonzero -ne 0) { $ok = $false }

[void][St]::FreeLibrary($h)
if ($ok) { "RESULT: patched DLL loads and the patch is intact in memory" } else { "RESULT: PROBLEM - see above"; exit 1 }
