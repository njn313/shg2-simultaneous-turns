# Packs the player files into Shogun2-SharedTurns_<first 8 of the DLL hash>.zip in the repo folder, for sharing a
# test build. No game files: players build the mod from their own game with Install.bat.
# Needs re\game\empire.retail.dll (the pristine copy) and, for the 8-player hash, re\build\p8\empire.retail.dll (the
# 8-player mod v1.4 built from it, see NOTES.md). Fails if README.md or "HOW TO INSTALL.txt" don't quote the build's
# hashes (update them first). Staging goes to re\build\zipstage.
#   powershell -ExecutionPolicy Bypass -File make_zip.ps1      (or double-click make_zip.bat)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$name = 'Shogun2-SharedTurns'
$files = @('Install.bat', 'Uninstall.bat', 'install.ps1', 'verify_load.ps1', 'README.md', 'human_test.md', 'HOW TO INSTALL.txt',
	'LICENSE', 'lib\Patcher.cs')

# the build's DLL hashes, from the pristine game copy and the 8-player build (neither is written to)
$build = Join-Path $repo 're\build'
New-Item -ItemType Directory -Force $build | Out-Null
Add-Type -Path (Join-Path $repo 'lib\Patcher.cs')
$hashes = @()
foreach ($in in @((Join-Path $repo 're\game\empire.retail.dll'), (Join-Path $build 'p8\empire.retail.dll'))) {
	if (-not (Test-Path $in)) { throw "$in not found: it's needed to compute the build's DLL hashes." }
	$dll = Join-Path $build 'zip_check.dll'
	[SharedTurnsPatcher]::Build($in, $dll) | Out-Null
	$hashes += (Get-FileHash $dll -Algorithm SHA256).Hash
	Remove-Item $dll
}
"DLL sha256 $($hashes[0]) (original), $($hashes[1]) (8-player mod v1.4)"
foreach ($h in $hashes) {
	if (-not (Select-String -Path (Join-Path $repo 'README.md') -SimpleMatch $h -Quiet)) { throw "README.md doesn't list $($h): update its hash table first." }
	if (-not (Select-String -Path (Join-Path $repo 'HOW TO INSTALL.txt') -SimpleMatch $h.Substring(0, 8) -Quiet)) { throw "HOW TO INSTALL.txt doesn't name $($h.Substring(0, 8)): update the hash lines first." }
}

# stage: text files with Windows line endings (Notepad, cmd)
$stage = Join-Path $build "zipstage\$name"
if (Test-Path (Split-Path $stage -Parent)) { Remove-Item (Split-Path $stage -Parent) -Recurse -Force }
foreach ($f in $files) {
	$src = Join-Path $repo $f; $dst = Join-Path $stage $f
	if (-not (Test-Path $src)) { throw "missing $f" }
	New-Item -ItemType Directory -Force (Split-Path $dst -Parent) | Out-Null
	$text = [IO.File]::ReadAllText($src) -replace "`r`n", "`n" -replace "`n", "`r`n"
	[IO.File]::WriteAllText($dst, $text, (New-Object Text.UTF8Encoding($false)))
}

# zip entries with forward slashes (PS 5.1's ZipFile.CreateFromDirectory writes backslashes), in a fixed order
Add-Type -AssemblyName System.IO.Compression
$zip = Join-Path $repo ("{0}_{1}.zip" -f $name, $hashes[0].Substring(0, 8))
$tmp = Join-Path $build 'zipstage\new.zip'
$fs = [IO.File]::Open($tmp, 'CreateNew')
$za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
foreach ($f in ($files | Sort-Object)) {
	$e = $za.CreateEntry("$name/" + $f.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
	$s = $e.Open(); $bytes = [IO.File]::ReadAllBytes((Join-Path $stage $f)); $s.Write($bytes, 0, $bytes.Length); $s.Dispose()
}
$za.Dispose(); $fs.Dispose()
# only now replace the zips of earlier builds
Get-ChildItem $repo -Filter "${name}_*.zip" | Remove-Item
Move-Item $tmp $zip
Remove-Item (Split-Path $stage -Parent) -Recurse -Force
"Wrote $zip ($([math]::Round((Get-Item $zip).Length / 1KB)) KB)"
