# AI2U Custom AI Endpoint - automatic installer.
#
# What it does, in order, stopping with a plain-English message on any failure:
#   1. Find the game folder (Steam library scan, common itch paths, or ask).
#   2. Verify it really is the game and really is the x64 Windows build.
#   3. Download BepInEx 5 x64 from its official GitHub release and lay it down,
#      unless a working BepInEx 5 is already present.
#   4. Lay down the mod DLL from the payload folder next to this script.
#   5. Verify every file landed where the loader needs it.
#
# It never deletes anything: existing files are backed up beside themselves
# with a .bak-<date> suffix before being replaced.

$ErrorActionPreference = 'Stop'

# BepInEx 5.4.23.2 is pinned rather than "latest" on purpose: it is the build
# this mod is tested against and documented with on Nexus, and its unpacked
# layout is known to this script. Only x64 is offered because the game only
# ships as x64 - offering a choice would be offering a wrong answer.
$BepUrl  = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip'
$BepZip  = "$env:TEMP\BepInEx_win_x64_5.4.23.2.zip"
$ExeName = 'AI2U - With you til the end.exe'

function Fail($msg) {
    Write-Host ''
    Write-Host "  PROBLEM: $msg" -ForegroundColor Red
    Write-Host ''
    Write-Host '  Nothing was broken - the installer stops before changing anything' -ForegroundColor Yellow
    Write-Host '  it cannot finish. Fix the above and run it again, or ask in the' -ForegroundColor Yellow
    Write-Host '  Discord and paste this whole window.' -ForegroundColor Yellow
    exit 1
}

function Step($msg) { Write-Host "  * $msg" -ForegroundColor Cyan }
function Good($msg) { Write-Host "  OK $msg" -ForegroundColor Green }

# ---- 1. find the game ----------------------------------------------------

Step 'Looking for the game...'

$candidates = New-Object System.Collections.Generic.List[string]

# Steam: read every library folder from libraryfolders.vdf, then the manifest.
# ${env:ProgramFiles(x86)} needs the braces: without them PowerShell parses
# "(x86)" as a call and silently yields nothing, which made the Steam scan
# find zero libraries on the very machine it was written on.
$steamRoots = @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")
foreach ($root in $steamRoots) {
    $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        $libs = @($root)
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
            # vdf escapes backslashes, so the captured value has doubled ones.
            # In PowerShell '\\\\' is the two-character literal \\ - replace it
            # with one. Getting this wrong produced paths that both resolved to
            # the same folder, so the picker listed one library twice.
            $libs += $m.Groups[1].Value -replace '\\\\', '\'
        }
        foreach ($lib in ($libs | Select-Object -Unique)) {
            $guess = $lib + '\steamapps\common\AI2U\Game'
            if (Test-Path ($guess + '\' + $ExeName)) { $candidates.Add($guess) }
        }
    }
}

# itch and manual installs: the handful of layouts seen in the wild. The itch
# app defaults to an apps folder under the user profile; manual unzips tend to
# land on a drive root.
$itchGuesses = @(
    "$env:USERPROFILE\AppData\Roaming\itch\apps\ai2u-with-you-til-the-end\Game",
    "$env:USERPROFILE\AppData\Roaming\itch\apps\ai2u\Game",
    'C:\AI2U\Game', 'D:\AI2U\Game', 'C:\Games\AI2U\Game', 'D:\Games\AI2U\Game'
)
foreach ($g in $itchGuesses) {
    # String concat, not Join-Path: Join-Path throws under Stop preference when
    # the drive itself does not exist, and D:\ legitimately may not.
    if (Test-Path ($g + '\' + $ExeName)) { $candidates.Add($g) }
}

# Normalize before dedupe: candidates arrive as strings from three different
# sources, and two spellings of one folder must not become two picker rows.
$candidates = @($candidates | ForEach-Object { [IO.Path]::GetFullPath($_) } | Select-Object -Unique)

function AskForPath {
    Write-Host ''
    Write-Host "  Find the folder that contains `"$ExeName`"" -ForegroundColor Yellow
    Write-Host '  (in Steam: right-click the game > Manage > Browse local files,'
    Write-Host '   then open the "Game" folder inside), and paste the path here.'
    Write-Host ''
    $p = (Read-Host '  Game folder').Trim('"').Trim()
    if (-not (Test-Path ($p + '\' + $ExeName))) {
        Fail "That folder does not contain `"$ExeName`"."
    }
    return $p
}

$game = $null
if ($candidates.Count -eq 1) {
    Write-Host ''
    Write-Host "  Found the game at: $($candidates[0])" -ForegroundColor Yellow
    $yn = Read-Host '  Install there? (Y/n)'
    if ($yn -match '^[Nn]') { $game = AskForPath } else { $game = $candidates[0] }
    Good "Using: $game"
} elseif ($candidates.Count -gt 1) {
    Write-Host ''
    Write-Host '  Found more than one copy of the game:' -ForegroundColor Yellow
    for ($i = 0; $i -lt $candidates.Count; $i++) {
        Write-Host "    [$($i+1)] $($candidates[$i])"
    }
    Write-Host "    [0] Somewhere else (type a path)"
    $pick = Read-Host '  Type the number of the copy you play'
    $idx = -1
    if (-not [int]::TryParse($pick, [ref]$idx) -or $idx -lt 0 -or $idx -gt $candidates.Count) {
        Fail 'That was not one of the listed numbers.'
    }
    if ($idx -eq 0) { $game = AskForPath } else { $game = $candidates[$idx - 1] }
} else {
    Write-Host ''
    Write-Host '  Could not find the game automatically.' -ForegroundColor Yellow
    $game = AskForPath
}

# ---- 2. sanity-check the build -------------------------------------------

# x64 check: byte 4 of the PE header's Machine field. The game only ships x64,
# so a mismatch means the wrong thing was pointed at, not a wrong download.
$exe = Join-Path $game $ExeName
$fs = [IO.File]::OpenRead($exe)
try {
    $br = New-Object IO.BinaryReader($fs)
    $fs.Position = 0x3C
    $peOff = $br.ReadInt32()
    $fs.Position = $peOff + 4
    $machine = $br.ReadUInt16()
} finally { $fs.Close() }
if ($machine -ne 0x8664) {
    Fail 'This game executable is not 64-bit, which no known AI2U build is. Wrong file?'
}

if (Get-Process | Where-Object { $_.Path -eq $exe }) {
    Fail 'The game is running. Close it fully, then run this installer again.'
}

Good 'Game folder checks out (64-bit, not running).'

# ---- 3. BepInEx -----------------------------------------------------------

$haveLoader = (Test-Path (Join-Path $game 'winhttp.dll')) -and
              (Test-Path (Join-Path $game 'BepInEx\core\BepInEx.Preloader.dll'))

if ($haveLoader) {
    # BepInEx 6 has a different core layout (BepInEx.Core.dll, no Preloader in
    # the same shape) so reaching here means a 5.x tree. Left alone: replacing
    # a working loader gains nothing and can lose someone's other mods.
    Good 'BepInEx 5 is already installed - leaving it exactly as it is.'
} else {
    Step 'Downloading BepInEx 5 x64 from its official GitHub release...'
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $BepUrl -OutFile $BepZip -UseBasicParsing
    } catch {
        Fail ("Could not download BepInEx. Are you online? If your network blocks GitHub, " +
              "install BepInEx by hand per the Nexus instructions, then rerun this. ($_)")
    }

    Step 'Unpacking into the game folder...'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($BepZip)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $dest = Join-Path $game $entry.FullName
            $destDir = Split-Path $dest -Parent
            if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
            if (Test-Path $dest) {
                Copy-Item $dest "$dest.bak-$(Get-Date -Format yyyyMMdd-HHmmss)" -Force
            }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
        }
    } finally { $zip.Dispose() }
    Remove-Item $BepZip -Force -ErrorAction SilentlyContinue

    # The zip came off the internet through this script, so the extracted files
    # carry no mark-of-the-web - which sidesteps the single most common manual
    # install failure (Windows silently refusing to load a "blocked" DLL).
    Good 'BepInEx 5 installed.'
}

# ---- 4. the mod ------------------------------------------------------------

Step 'Installing the mod...'

$payload = Join-Path $PSScriptRoot 'payload\AI2UCustomAI.dll'
if (-not (Test-Path $payload)) {
    Fail ('The installer is incomplete: payload\AI2UCustomAI.dll is missing next to it. ' +
          'Re-extract the WHOLE downloaded zip - do not run Install.bat from inside the zip window.')
}

$plugins = Join-Path $game 'BepInEx\plugins'
if (-not (Test-Path $plugins)) { New-Item -ItemType Directory -Force -Path $plugins | Out-Null }

$target = Join-Path $plugins 'AI2UCustomAI.dll'
if (Test-Path $target) {
    Copy-Item $target "$target.bak-$(Get-Date -Format yyyyMMdd-HHmmss)" -Force
}
Copy-Item $payload $target -Force

# Unblock defensively in case the payload itself carries mark-of-the-web from
# the browser download of the installer zip.
Unblock-File $target -ErrorAction SilentlyContinue
Get-ChildItem (Join-Path $game 'BepInEx') -Recurse -Filter *.dll |
    Unblock-File -ErrorAction SilentlyContinue
Unblock-File (Join-Path $game 'winhttp.dll') -ErrorAction SilentlyContinue

Good 'Mod installed.'

# ---- 5. verify -------------------------------------------------------------

Step 'Checking everything is where the loader needs it...'

$must = @(
    (Join-Path $game 'winhttp.dll'),
    (Join-Path $game 'doorstop_config.ini'),
    (Join-Path $game 'BepInEx\core\BepInEx.Preloader.dll'),
    $target
)
foreach ($f in $must) {
    if (-not (Test-Path $f)) { Fail "Verification failed: $f is missing." }
}

Write-Host ''
Write-Host '  =====================================================' -ForegroundColor Green
Write-Host '   DONE. Now:' -ForegroundColor Green
Write-Host '   1. Start the game.' -ForegroundColor Green
Write-Host '   2. Press F9 in-game to open the mod panel.' -ForegroundColor Green
Write-Host '   3. Put in your API key and model, hit Test.' -ForegroundColor Green
Write-Host '  =====================================================' -ForegroundColor Green
Write-Host ''
Write-Host '  If the F9 panel does not open, send BepInEx\LogOutput.log'
Write-Host '  from your game folder to the Discord.'
