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
	@(0x1070420A, 'end turn latch'), @(0x1088655A, 'member latch'), @(0x107041CA, 'group tick'), @(0x1066CCDC, 'save group'), @(0x106559A7, 'load group'),
	@(0x106AA5E8, 'can end turn 1'), @(0x106AA5FE, 'can end turn 2'), @(0x106AA6AB, 'mover allowed 1'), @(0x106AA777, 'mover allowed 2'),
	@(0x106AA89F, 'action allowed'), @(0x106E4F73, 'attack allowed'), @(0x108FFE3B, 'mover owner'), @(0x108FFEC8, 'resume orders 1'),
	@(0x108FFEF4, 'resume orders 2'), @(0x108FFF4A, 'resume orders 3'), @(0x106EC0E0, 'char moved'), @(0x106C5A84, 'order notify'),
	@(0x1069867B, 'disband allowed'), @(0x1111E84F, 'end turn ui'), @(0x11155403, 'turn time up'), @(0x1118C09F, 'order cursor'),
	@(0x106AA710, 'pf mover acts'), @(0x10900082, 'pf stop reissue'), @(0x10900137, 'pf moves done'), @(0x108D2699, 'pf command done'),
	@(0x10703F27, 'pf commands'), @(0x1115AB26, 'pf ui hover 1'), @(0x111516B2, 'pf ui hover 2'), @(0x11160272, 'pf ui click 1'),
	@(0x1114AE22, 'pf ui click 2'), @(0x112517A5, 'pf ui recruit panel'), @(0x11232FB1, 'pf ui card state'), @(0x10883DD0, 'move log'), @(0x11129E27, 'click log 1'), @(0x1115199D, 'click log 2'),
	@(0x1114174C, 'own action turn'), @(0x105FD550, 'pf turn start'),
	@(0x106EA3B9, 'battle new 1'), @(0x106EA603, 'battle new 2'), @(0x1057FCA0, 'battle state'), @(0x1088A5DD, 'battle ready'),
	@(0x106B4088, 'end moves'), @(0x10607876, 'resume members'), @(0x10883846, 'attack record'), @(0x108A7986, 'attack check'),
	@(0x1081A7E0, 'pf built for'), @(0x108746B1, 'access log 1'), @(0x108745E1, 'access log 2'), @(0x105EA330, 'pending push'),
	@(0x105B60E0, 'pending pop'), @(0x10884342, 'negotiation gate'), @(0x108840E2, 'war log'), @(0x1088381B, 'attack log'),
	@(0x1104EBC0, 'message log'),
	@(0x11122988, 'ui turn gate'), @(0x11142F57, 'save message'), @(0x1122FF86, 'clan button'), @(0x112457A9, 'unit panel 1'),
	@(0x1124ED39, 'unit panel 2'), @(0x1125458A, 'unit panel 3'), @(0x11254772, 'unit panel 4'), @(0x11143D98, 'demand surrender 1'),
	@(0x111798B2, 'demand surrender 2'), @(0x11152230, 'ui negotiation'), @(0x111D7230, 'open negotiations'),
	@(0x1110C516, 'hud flags 1'), @(0x11155F27, 'hud flags 2'), @(0x11156B0C, 'hud flags 3'), @(0x11155EFE, 'hud close'),
	@(0x1110ADBD, 'hud new ui'), @(0x111DA3B6, 'hud poll'), @(0x11076A70, 'hud shade'))
# the desync fixes: this mod's on the original, the 8-player mod's own (in .s2mp) on top of it
$desync = @(
	@(0x100B190C, 'frame fp pin'), @(0x10227D40, 'tick fp pin'), @(0x10EE9A71, 'battle no-kick'), @(0x10EF9A87, 'report no-kick'),
	@(0x10F05914, 'auto resync'), @(0x10F10E2D, 'forced resync'))
$on8p = $secs.ContainsKey('.s2mp')
# standalone only, with no counterpart in the 8-player mod
if (-not $on8p) { $hooks += $desync + @(@(0x10EF9AC1, 'log host kick'), @(0x10EF9AD1, 'log leave'), @(0x10703EA0, 'campaign tick pin')) }
# a diagnostics build (install.ps1 -Diagnostics; ApplyDiagnostics in lib\Patcher.cs): its log-only hooks and 18h more data
$diag = [BitConverter]::ToString($fileSec).Contains([BitConverter]::ToString([Text.Encoding]::ASCII.GetBytes('DIAG battle set-up')))
"diagnostics build  : $diag"
if ($diag) {
	$hooks += @(@(0x105A5A96, 'diag may retreat 1'), @(0x105A5B53, 'diag may retreat 2'), @(0x105A5B79, 'diag may retreat 3'),
		@(0x10605176, 'diag side retreat'), @(0x1060535B, 'diag spot 1'), @(0x10605554, 'diag spot 2'), @(0x1060553C, 'diag move garrison 1'),
		@(0x106055C0, 'diag move garrison 2'), @(0x106055E1, 'diag move'), @(0x1088393A, 'diag siege object'),
		@(0x10883987, 'diag besieged order'), @(0x108839BE, 'diag attack order'))
}
foreach ($hook in $hooks) {
	$ins = Mem $hook[0] 5
	$dest = ([int64]$hook[0] + 5 + [BitConverter]::ToInt32($ins, 1)) -band 0xFFFFFFFF
	$in = ($ins[0] -eq 0xE8 -or $ins[0] -eq 0xE9) -and $dest -ge $mod.Va -and $dest -lt ($mod.Va + $mod.Size)
	"hook {0,-17}: {1:X8} {2} -> {3:X8}  in .s2st: {4}" -f $hook[1], $hook[0], (Hex $ins[0..0]), $dest, $in
	if (-not $in) { $ok = $false }
}
if ($on8p) {
	$p8 = $secs['.s2mp']
	foreach ($hook in $desync) {
		$ins = Mem $hook[0] 5
		$dest = ([int64]$hook[0] + 5 + [BitConverter]::ToInt32($ins, 1)) -band 0xFFFFFFFF
		$in = $dest -ge $p8.Va -and $dest -lt ($p8.Va + $p8.Size)
		"8p hook {0,-14}: {1:X8} {2} -> {3:X8}  in .s2mp: {4}" -f $hook[1], $hook[0], (Hex $ins[0..0]), $dest, $in
		if (-not $in) { $ok = $false }
	}
}
# save/load screens switch only LC_TIME (push 5), not LC_ALL (push 0): this mod's or the 8-player mod's byte
$loc = Mem 0x110B84E1 3
"save screen locale : {0} (expected 6A 05 E8)" -f (Hex $loc)
if ((Hex $loc) -ne '6A 05 E8') { $ok = $false }
# End Turn names the local faction: mov eax,[ecx+0B0h] and 6 nops in place of [[[ecx+8]+1498h]+2Ch]
$endTurn = Mem 0x1111E8BF 12
"end turn faction   : {0}" -f (Hex $endTurn)
if ((Hex $endTurn) -ne '8B 81 B0 00 00 00 90 90 90 90 90 90') { $ok = $false }
# the mod's data from DataFloor 11CB1C00 (group state, log flags, pathfinder state, the save's group, the attack
# table, the top display's state; above the 8-player mod's, which ends at 11CB1910 in v1.11 / v1.12): mapped, inside
# .data, zero
$floor = 0x11CB1C00; $need = $(if ($diag) { 0x2A8 } else { 0x290 })
$dataEnd = [int64]$secs['.data'].Va + $secs['.data'].Size
$data = Mem $floor $need
$nonzero = @($data | Where-Object { $_ -ne 0 }).Count
".data              : ends at {0:X8} (needs {1:X8}); mod data nonzero bytes: {2}" -f $dataEnd, ($floor + $need), $nonzero
if ($dataEnd -lt ($floor + $need) -or $nonzero -ne 0) { $ok = $false }

[void][St]::FreeLibrary($h)
if ($ok) { "RESULT: patched DLL loads and the patch is intact in memory" } else { "RESULT: PROBLEM - see above"; exit 1 }
