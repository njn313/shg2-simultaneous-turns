# Shogun 2 Shared Turns (experimental): installer and uninstaller.
# Patches the player's own empire.retail.dll: the original game DLL, or the 8-player co-op mod when that is installed
# (install the 8-player mod first, then this one). This build was checked with the 8-player mod v1.13; any other
# version is built on too, with a warning. The DLL it patched is kept next to it as empire.retail.dll.sharedturns-base
# and put back by -Uninstall. Every player must end up with the same DLL hash, so everyone picks the same turn mode
# (asked when -Mode isn't given; Enter = hybrid) and has the same 8-player mod version, or none.
#
#   powershell -ExecutionPolicy Bypass -File install.ps1 [-Mode hybrid|simultaneous] [-GamePath "<game folder>"]
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall [-GamePath "<game folder>"]
# -Diagnostics: a diagnostics build for one bug hunt (extra lines in shared_turns.log); its hash differs from the
# README's, so every player in that test installs it the same way. A normal install replaces it.
param([string]$GamePath = '', [string]$Mode = '', [switch]$Uninstall, [switch]$Diagnostics)
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

# the original game DLL or any 8-player mod build: [SharedTurnsPatcher]::InputName names it (null for anything else)
$current = Sha $dll
$installed = [SharedTurnsPatcher]::IsThisMod($dll)

if ($Uninstall) {
	if (-not $installed) {
		"Shared Turns is not installed (empire.retail.dll has no Shared Turns patch); left as it is."
		if (Test-Path $base) { Remove-Item $base; "Removed an old empire.retail.dll.sharedturns-base." }
		return
	}
	if (-not (Test-Path $base)) { throw 'The backup empire.retail.dll.sharedturns-base is missing. Use Steam "Verify integrity of game files" (then reinstall the 8-player mod if you use it).' }
	$name = [SharedTurnsPatcher]::InputName($base)
	if (-not $name) { throw 'The backup empire.retail.dll.sharedturns-base is neither the original game DLL nor the 8-player co-op mod; not restoring it. Use Steam "Verify integrity of game files".' }
	Copy-Item $base $dll -Force
	Remove-Item $base
	"Uninstalled: empire.retail.dll is $name again."
	return
}

# ---- install / update ----------------------------------------------------------------------------------------------
# build on the DLL itself, or under an earlier build of this mod on the DLL that one was made from
$from = [SharedTurnsPatcher]::InputName($dll)
$source = $dll
if (-not $from -and $installed) {
	if (-not (Test-Path $base)) { throw 'Shared Turns is installed but its backup empire.retail.dll.sharedturns-base is missing. Use Steam "Verify integrity of game files" (then reinstall the 8-player mod if you use it) and run this again.' }
	$from = [SharedTurnsPatcher]::InputName($base)
	$source = $base
	if (-not $from) { throw 'The backup empire.retail.dll.sharedturns-base is neither the original game DLL nor the 8-player co-op mod. Use Steam "Verify integrity of game files" and run this again.' }
}
elseif (-not $from) {
	throw ("empire.retail.dll (sha256 $current) is neither the original game DLL, nor the 8-player co-op mod, nor a build " +
		"of this mod. Another mod or another game version: not touching it.")
}
$supported = [SharedTurnsPatcher]::IsSupported($source)
if (-not $supported) {
	""
	"WARNING: this game has $from."
	"  This version of Shared Turns was checked with the 8-player co-op mod v1.13 only. It installs anyway (and stops if"
	"  the two mods would change the same code), but this combination is not officially supported and may misbehave in"
	"  game. Updating the 8-player mod to v1.13 is the safe choice. Every player needs the same 8-player version."
}
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
$tmp = "$dll.sharedturns-new"
[SharedTurnsPatcher]::Diagnostics = [bool]$Diagnostics
if ($Diagnostics) { ""; "DIAGNOSTICS BUILD: extra log lines for a bug hunt; every player in the test installs it with -Diagnostics." }
try { [SharedTurnsPatcher]::Build($source, $tmp, $Mode) | Out-Null }
catch {
	if (Test-Path $tmp) { Remove-Item $tmp }
	$e = $_.Exception
	if ($e.InnerException) { $e = $e.InnerException }
	if ($supported) { throw "Building Shared Turns on $from failed: $($e.Message). Nothing was changed. Please report this." }
	throw ("Shared Turns can't be built on $from`: $($e.Message). Nothing was changed. Update the 8-player mod to v1.13 " +
		"(or get a Shared Turns made for your version) and run this again.")
}
$new = Sha $tmp
if ($new -eq $current) { Remove-Item $tmp; "Shared Turns is already installed (on $from, $Mode turns)." }
else {
	if ($source -eq $dll) { Copy-Item $dll $base -Force }
	Move-Item $tmp $dll -Force
	if ($installed) { "Updated Shared Turns (on $from, $Mode turns)." } else { "Installed Shared Turns on $from, $Mode turns (kept as empire.retail.dll.sharedturns-base)." }
}
""
"empire.retail.dll sha256 $new"
"Every player must see the same hash: the same turn mode, and all with the same 8-player mod version or all without"
"(README lists the hashes for the original game and the 8-player mod v1.13)."
""
"Reminders:"
"  - EXPERIMENTAL. Steam no longer lists VAC for Shogun 2, but this changes the engine DLL: a spare account is the extra-safe choice."
if (-not $supported) { "  - Not officially supported: $from (this build was checked with the 8-player mod v1.13)." }
"  - Undo:  Uninstall.bat  (or install.ps1 -Uninstall)"
"  - Adding the 8-player mod later: uninstall this first, install the 8-player mod, then install this again."
