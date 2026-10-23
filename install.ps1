# Shogun 2 Shared Turns (experimental): installer and uninstaller.
# Patches the player's own empire.retail.dll: the original game DLL, or the 8-player co-op mod v1.4 when that is
# installed (install the 8-player mod first, then this one). The DLL it patched is kept next to it as
# empire.retail.dll.sharedturns-base and put back by -Uninstall. Every player must end up with the same DLL hash,
# so everyone picks the same turn mode (asked when -Mode isn't given; Enter = hybrid).
#
#   powershell -ExecutionPolicy Bypass -File install.ps1 [-Mode hybrid|simultaneous] [-GamePath "<game folder>"]
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall [-GamePath "<game folder>"]
param([string]$GamePath = '', [string]$Mode = '', [switch]$Uninstall)
$ErrorActionPreference = 'Stop'
if (-not ('SharedTurnsPatcher' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'lib\Patcher.cs') }
function Sha($p) { (Get-FileHash $p -Algorithm SHA256).Hash }

# ---- the game folder: the Steam library whose manifest lists Shogun 2 (app 34330) -------------------------------
function FindGame {
	$steam = @()
	foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
		$v = Get-ItemProperty $key -ErrorAction SilentlyContinue
		if ($v -and $v.SteamPath) { $steam += $v.SteamPath }
		if ($v -and $v.InstallPath) { $steam += $v.InstallPath }
	}
	$libraries = @()
	foreach ($s in $steam) {
		$libraries += $s
		$vdf = Join-Path $s 'steamapps\libraryfolders.vdf'
		if (Test-Path $vdf) {
			foreach ($m in [regex]::Matches([IO.File]::ReadAllText($vdf), '"path"\s+"([^"]+)"')) { $libraries += $m.Groups[1].Value -replace '\\\\', '\' }
		}
	}
	foreach ($lib in ($libraries | Select-Object -Unique)) {
		$acf = Join-Path $lib 'steamapps\appmanifest_34330.acf'
		if (-not (Test-Path $acf)) { continue }
		$dir = [regex]::Match([IO.File]::ReadAllText($acf), '"installdir"\s+"([^"]+)"').Groups[1].Value
		$p = Join-Path $lib "steamapps\common\$dir"
		if ($dir -and (Test-Path (Join-Path $p 'empire.retail.dll'))) { return $p }
	}
	return $null
}
if (-not $GamePath) { $GamePath = FindGame }
if (-not $GamePath) { throw 'Could not find Total War: SHOGUN 2 in your Steam libraries. Run again with -GamePath "<game folder>".' }
"Game folder: $GamePath"
$dll = Join-Path $GamePath 'empire.retail.dll'
$base = "$dll.sharedturns-base"
if (-not (Test-Path $dll)) { throw "empire.retail.dll not found in $GamePath." }
if (Get-Process -Name 'Shogun2' -ErrorAction SilentlyContinue) { throw 'Close Shogun 2 first.' }

function BaseName($hash) {
	if ($hash -eq [SharedTurnsPatcher]::OriginalSha256) { return 'the original game DLL' }
	if ($hash -eq [SharedTurnsPatcher]::EightPlayerV14Sha256) { return 'the 8-player co-op mod v1.4' }
	return $null
}
$current = Sha $dll
$installed = [SharedTurnsPatcher]::IsThisMod($dll)

if ($Uninstall) {
	if (-not $installed) {
		"Shared Turns is not installed (empire.retail.dll has no Shared Turns patch); left as it is."
		if (Test-Path $base) { Remove-Item $base; "Removed an old empire.retail.dll.sharedturns-base." }
		return
	}
	if (-not (Test-Path $base)) { throw 'The backup empire.retail.dll.sharedturns-base is missing. Use Steam "Verify integrity of game files" (then reinstall the 8-player mod if you use it).' }
	$name = BaseName (Sha $base)
	if (-not $name) { throw 'The backup empire.retail.dll.sharedturns-base is not a DLL this mod knows; not restoring it. Use Steam "Verify integrity of game files".' }
	Copy-Item $base $dll -Force
	Remove-Item $base
	"Uninstalled: empire.retail.dll is $name again."
	return
}

# ---- install / update ----------------------------------------------------------------------------------------------
if (-not $Mode) {
	""
	"Turn mode (every player must choose the same one):"
	"  1 = hybrid:       players who are not at war with each other play at the same time;"
	"                    a player at war with another player plays alone (default)"
	"  2 = simultaneous: all players always play at the same time, even at war with each other"
	$answer = Read-Host "Mode [1]"
	if ($answer -eq '2') { $Mode = 'simultaneous' } elseif ($answer -eq '' -or $answer -eq '1') { $Mode = 'hybrid' }
	else { throw "Unknown choice '$answer': type 1 or 2. Nothing was changed." }
}
if ($Mode -ne 'hybrid' -and $Mode -ne 'simultaneous') { throw "-Mode must be hybrid or simultaneous. Nothing was changed." }
if (BaseName $current) {
	Copy-Item $dll $base -Force
	$from = BaseName $current
}
elseif ($installed) {
	# an earlier build of this mod: build again from the DLL it was made from
	if (-not (Test-Path $base)) { throw 'Shared Turns is installed but its backup empire.retail.dll.sharedturns-base is missing. Use Steam "Verify integrity of game files" (then reinstall the 8-player mod if you use it) and run this again.' }
	$from = BaseName (Sha $base)
	if (-not $from) { throw 'The backup empire.retail.dll.sharedturns-base is not a DLL this mod knows. Use Steam "Verify integrity of game files" and run this again.' }
}
else {
	throw ("empire.retail.dll (sha256 $current) is neither the original game DLL, nor the 8-player co-op mod v1.4, nor a build " +
		"of this mod. Another mod or another game version: not touching it. (A newer 8-player mod needs a newer Shared Turns.)")
}
$tmp = "$dll.sharedturns-new"
[SharedTurnsPatcher]::Build($base, $tmp, $Mode) | Out-Null
$new = Sha $tmp
if ($new -eq $current) { Remove-Item $tmp; "Shared Turns is already installed (on $from, $Mode turns)." }
else {
	Move-Item $tmp $dll -Force
	if ($installed) { "Updated Shared Turns (on $from, $Mode turns)." } else { "Installed Shared Turns on $from, $Mode turns (kept as empire.retail.dll.sharedturns-base)." }
}
""
"empire.retail.dll sha256 $new"
"Every player must see the same hash: the same turn mode, and all with the 8-player mod or all without (README lists all four)."
""
"Reminders:"
"  - EXPERIMENTAL. Shogun 2 multiplayer is VAC-protected and this changes the engine DLL: use spare Steam accounts."
"  - Undo:  Uninstall.bat  (or install.ps1 -Uninstall)"
"  - Adding the 8-player mod later: uninstall this first, install the 8-player mod, then install this again."
