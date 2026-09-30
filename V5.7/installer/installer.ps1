# AI2U Custom AI Endpoint - automatic installer.
#
# What it does, in order, stopping with a plain-English message on any failure:
#   1. Find every copy of the game, pick the likeliest, and have the player
#      confirm that folder (or pick another copy, browse, or paste a path).
#   2. Verify it really is the game and really is the x64 Windows build.
#   3. Download BepInEx 5 x64 from its official GitHub release and lay it down,
#      unless a working BepInEx 5 is already present.
#   4. Lay down the mod DLL from the payload folder next to this script.
#   5. Verify every file landed where the loader needs it.
#
# It never deletes anything: existing files are backed up beside themselves
# with a .bak-<date> suffix before being replaced.

param(
    # Test hook: stop once the folder is chosen and print it. Nothing is
    # installed, so the chooser can be checked without touching a game.
    [switch]$ChooseOnly
)

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

# ---- 1. find the game, then let the player confirm the folder ------------
#
# A player with more than one copy - Steam plus an itch or copied folder, or
# two Steam libraries - could end up with the mod in the copy they do not
# play. One copy found was installed on a bare Enter, even when the copy they
# play was somewhere the scan did not look; several copies were a bare list of
# numbers with nothing to tell them apart. Now every copy found is listed with
# what is in it and when it last ran with mods, the likeliest one is picked,
# and nothing is installed until the player has seen that folder and pressed
# Enter on it - or picked another copy, or browsed to one the scan missed.

Step 'Looking for the game...'

$candidates = New-Object System.Collections.Generic.List[string]

# A folder as one canonical string. A drive root keeps its backslash:
# GetFullPath on a bare "D:" means "the current folder on D:", not the root.
function FullDir([string]$d) {
    $d = $d.TrimEnd('\')
    if ($d -match '^[A-Za-z]:$') { $d += '\' }
    return [IO.Path]::GetFullPath($d)
}

function AddCandidate([string]$dir) {
    if ([string]::IsNullOrWhiteSpace($dir)) { return }
    try {
        # String concat, not Join-Path: Join-Path throws under Stop preference
        # when the drive itself does not exist.
        if (Test-Path -LiteralPath ($dir.TrimEnd('\') + '\' + $ExeName)) {
            $candidates.Add((FullDir $dir))
        }
    } catch { }
}

# Every AI2U* folder directly under $base, and the Game folder inside each.
function AddAi2uFoldersUnder([string]$base) {
    try {
        Get-ChildItem -LiteralPath $base -Directory -Filter 'AI2U*' -ErrorAction Stop | ForEach-Object {
            AddCandidate $_.FullName
            AddCandidate ($_.FullName + '\Game')
        }
    } catch { }
}

# Steam: every library folder from libraryfolders.vdf. Steam's own install
# path comes from the registry as well, for Steam installed off Program Files.
# ${env:ProgramFiles(x86)} needs the braces: without them PowerShell parses
# "(x86)" as a call and silently yields nothing, which made the Steam scan
# find zero libraries on the very machine it was written on.
$steamRoots = @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")
try {
    $sp = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath
    if ($sp) { $steamRoots += ($sp -replace '/', '\') }
} catch { }
foreach ($root in ($steamRoots | Select-Object -Unique)) {
    $vdf = $root.TrimEnd('\') + '\steamapps\libraryfolders.vdf'
    if (-not (Test-Path -LiteralPath $vdf)) { continue }
    $libs = @($root)
    try {
        # Steam writes this file as UTF-8. Read in the ANSI code page, a library
        # path with an accented letter came out mangled and was never scanned.
        foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw -Encoding UTF8), '"path"\s+"([^"]+)"')) {
            # vdf escapes backslashes, so the captured value has doubled ones.
            # In PowerShell '\\\\' is the two-character literal \\ - replace it
            # with one. Getting this wrong produced paths that both resolved to
            # the same folder, so the picker listed one library twice.
            $libs += $m.Groups[1].Value -replace '\\\\', '\'
        }
    } catch { }
    foreach ($lib in ($libs | Select-Object -Unique)) {
        AddCandidate ($lib.TrimEnd('\') + '\steamapps\common\AI2U\Game')
        AddAi2uFoldersUnder ($lib.TrimEnd('\') + '\steamapps\common')
    }
}

# itch: every app the itch app has installed, one level down.
try {
    Get-ChildItem -LiteralPath "$env:APPDATA\itch\apps" -Directory -ErrorAction Stop | ForEach-Object {
        AddCandidate $_.FullName
        AddCandidate ($_.FullName + '\Game')
    }
} catch { }

# Copied or unzipped copies: AI2U* folders at the top of every fixed drive and
# in the usual Games / Program Files folders on it.
$roots = @()
try {
    $roots = @([IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady } |
        ForEach-Object { $_.RootDirectory.FullName })
} catch { $roots = @('C:\') }
foreach ($r in $roots) {
    foreach ($base in @($r, ($r + 'Games'), ($r + 'Program Files'), ($r + 'Program Files (x86)'))) {
        AddAi2uFoldersUnder $base
    }
}

# A copy that is running right now is certainly one the player plays.
try {
    foreach ($proc in @(Get-Process -ErrorAction SilentlyContinue)) {
        $pp = $null
        try { $pp = $proc.Path } catch { }
        if ($pp -and ([IO.Path]::GetFileName($pp) -eq $ExeName)) { AddCandidate ([IO.Path]::GetDirectoryName($pp)) }
    }
} catch { }

# One row per folder. The hashtable is case-insensitive, so two spellings of
# one path do not become two rows.
$seen = @{}
$found = @()
foreach ($c in $candidates) {
    if (-not $seen.ContainsKey($c)) { $seen[$c] = $true; $found += $c }
}

function Describe([string]$dir) {
    $d = $dir.TrimEnd('\')
    $src = 'other folder'
    if ($dir -match '\\steamapps\\common\\') { $src = 'Steam' }
    elseif ($env:APPDATA -and $dir.StartsWith($env:APPDATA + '\itch\', [StringComparison]::OrdinalIgnoreCase)) { $src = 'itch' }
    $last = $null
    try {
        $log = $d + '\BepInEx\LogOutput.log'
        if (Test-Path -LiteralPath $log) { $last = (Get-Item -LiteralPath $log).LastWriteTime }
    } catch { }
    New-Object PSObject -Property @{
        Dir     = $dir
        Source  = $src
        BepInEx = (Test-Path -LiteralPath ($d + '\winhttp.dll')) -and (Test-Path -LiteralPath ($d + '\BepInEx\core\BepInEx.Preloader.dll'))
        Mod     = (Test-Path -LiteralPath ($d + '\BepInEx\plugins\AI2UCustomAI.dll'))
        LastRun = $last
    }
}

# What the player picked - a folder, the .exe itself, the game's BepInEx or
# _Data folder, or the folder one above Game - read as the folder the .exe is
# in. $null when none of those holds the game.
function ResolveGameDir([string]$p) {
    if ([string]::IsNullOrWhiteSpace($p)) { return $null }
    $p = $p.Trim().Trim('"').Trim().TrimEnd('\')
    if ($p.Length -eq 2 -and $p[1] -eq ':') { $p += '\' }
    try {
        if (Test-Path -LiteralPath $p -PathType Leaf) { $p = [IO.Path]::GetDirectoryName($p) }
        $tries = @($p, ($p.TrimEnd('\') + '\Game'))
        $up = [IO.Path]::GetDirectoryName($p.TrimEnd('\'))
        if ($up) { $tries += $up }
        foreach ($t in $tries) {
            if ($t -and (Test-Path -LiteralPath ($t.TrimEnd('\') + '\' + $ExeName))) {
                return (FullDir $t)
            }
        }
    } catch { }
    return $null
}

# The Windows folder picker. Returns the folder, '' when the player cancels,
# or $null when no picker can open here (the caller then asks for a path).
function BrowseForFolder([string]$start) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = "Pick the AI2U game folder - the one that contains `"$ExeName`"."
        $dlg.ShowNewFolderButton = $false
        if ($start -and (Test-Path -LiteralPath $start)) { $dlg.SelectedPath = $start }
        # A hidden topmost owner, so the picker opens in front of this window
        # instead of behind it.
        $owner = New-Object System.Windows.Forms.Form
        $owner.TopMost = $true
        $owner.ShowInTaskbar = $false
        try { $res = $dlg.ShowDialog($owner) } finally { $owner.Dispose() }
        if ($res -eq [System.Windows.Forms.DialogResult]::OK) { return $dlg.SelectedPath }
        return ''
    } catch {
        return $null
    }
}

# A key pressed before this question (the "press any key" before the installer
# started, an impatient double-Enter) must not answer it.
function FlushKeys {
    try { while ([Console]::KeyAvailable) { [void][Console]::ReadKey($true) } } catch { }
}

function Ask([string]$prompt) {
    FlushKeys
    $a = Read-Host $prompt
    if ($null -eq $a) { Fail 'No answer could be read, so nothing was installed.' }
    return $a.Trim()
}

$info = @($found | ForEach-Object { Describe $_ })

# The likeliest copy: the one that most recently ran with mods; failing that
# the Steam copy; failing that the first found.
$choice = $null
$why = $null
if ($info.Count -gt 0) {
    $best = -1
    $bestTime = [datetime]::MinValue
    for ($i = 0; $i -lt $info.Count; $i++) {
        if ($info[$i].LastRun -and $info[$i].LastRun -gt $bestTime) { $best = $i; $bestTime = $info[$i].LastRun }
    }
    if ($best -ge 0 -and $info.Count -gt 1) { $why = 'the copy that most recently ran with mods' }
    if ($best -lt 0) {
        for ($i = 0; $i -lt $info.Count; $i++) { if ($info[$i].Source -eq 'Steam') { $best = $i; break } }
        if ($best -ge 0 -and $info.Count -gt 1) { $why = 'the Steam copy' }
    }
    if ($best -lt 0) { $best = 0; if ($info.Count -gt 1) { $why = 'the first copy found' } }
    if ($info.Count -eq 1) { $why = 'the only copy found' }
    $choice = $info[$best].Dir
} else {
    Write-Host ''
    Write-Host '  Could not find the game automatically.' -ForegroundColor Yellow
}

$rounds = 0
while ($true) {
    $rounds++
    if ($rounds -gt 40) { Fail 'No folder was confirmed, so nothing was installed.' }

    Write-Host ''
    if ($info.Count -gt 0) {
        if ($info.Count -eq 1) { Write-Host '  Found one copy of the game:' -ForegroundColor Yellow }
        else { Write-Host "  Found $($info.Count) copies of the game:" -ForegroundColor Yellow }
        for ($i = 0; $i -lt $info.Count; $i++) {
            $x = $info[$i]
            $on = $x.Dir -eq $choice
            $mark = ' '
            $col = 'Gray'
            if ($on) { $mark = '>'; $col = 'White' }
            $bepTxt = 'no'
            if ($x.BepInEx) { $bepTxt = 'yes' }
            $modTxt = 'not yet'
            if ($x.Mod) { $modTxt = 'installed' }
            $runTxt = 'never'
            if ($x.LastRun) { $runTxt = $x.LastRun.ToString('yyyy-MM-dd HH:mm') }
            Write-Host ("  {0} [{1}] {2}" -f $mark, ($i + 1), $x.Dir) -ForegroundColor $col
            Write-Host ("        {0}  |  BepInEx: {1}  |  this mod: {2}  |  last run with mods: {3}" -f $x.Source, $bepTxt, $modTxt, $runTxt) -ForegroundColor DarkGray
        }
        Write-Host ''
    }

    if ($choice) {
        Write-Host '  The mod will be installed into:' -ForegroundColor Yellow
        Write-Host "      $choice" -ForegroundColor Green
        if ($why) { Write-Host "      ($why)" -ForegroundColor DarkGray }
        Write-Host ''
        Write-Host '    Enter  install there'
        if ($info.Count -gt 1) { Write-Host "    1-$($info.Count)    pick another copy from the list" }
        Write-Host '    B      browse for a different folder (or paste a folder path here)'
        Write-Host '    Q      quit without installing'
        if ($info.Count -gt 1) {
            Write-Host ''
            Write-Host '  Play more than one copy? Run this installer once for each.' -ForegroundColor DarkGray
        }
    } else {
        Write-Host '    B      browse for the game folder (or paste a folder path here)'
        Write-Host '    Q      quit without installing'
        Write-Host ''
        Write-Host "  It is the folder that contains `"$ExeName`"." -ForegroundColor DarkGray
        Write-Host '  In Steam: right-click the game > Manage > Browse local files.' -ForegroundColor DarkGray
    }

    $ans = Ask '  Your choice'
    if ($ans -eq '') {
        if ($choice) { break }
        continue
    }
    if ($ans -match '^[Qq]$') {
        Write-Host ''
        Write-Host '  Nothing was installed.' -ForegroundColor Yellow
        exit 0
    }
    $n = 0
    if ($ans -match '^\d+$') {
        if ([int]::TryParse($ans, [ref]$n) -and $n -ge 1 -and $n -le $info.Count) {
            $choice = $info[$n - 1].Dir
            $why = 'your pick'
        } else {
            Write-Host '  That is not one of the listed numbers.' -ForegroundColor Red
        }
        continue
    }

    $picked = $ans
    if ($ans -match '^[Bb]$') {
        $picked = BrowseForFolder $choice
        if ($null -eq $picked) {
            Write-Host '  The folder window could not open here, so paste the folder path instead.' -ForegroundColor Yellow
            $picked = Ask '  Game folder (Enter to go back)'
        }
        if ([string]::IsNullOrWhiteSpace($picked)) { continue }
    }

    $dir = ResolveGameDir $picked
    if ($dir) {
        $choice = $dir
        $why = 'your pick'
        $known = $false
        foreach ($x in $info) { if ($x.Dir -eq $dir) { $known = $true } }
        if (-not $known) { $info += Describe $dir }
    } else {
        Write-Host "  `"$picked`" does not contain `"$ExeName`", and neither does a Game folder inside it." -ForegroundColor Red
    }
}

$game = $choice
Good "Installing into: $game"

if ($ChooseOnly) {
    Write-Host "CHOSEN=$game"
    exit 0
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

# Every path from here on is -LiteralPath. The chooser accepts folders like
# "AI2U [itch]", and as a wildcard "[itch]" means "one of i, t, c, h": the
# existence checks below came back false, existing files were overwritten
# without their backups, and the last check reported a failure after the
# files had already been changed.
$haveLoader = (Test-Path -LiteralPath (Join-Path $game 'winhttp.dll')) -and
              (Test-Path -LiteralPath (Join-Path $game 'BepInEx\core\BepInEx.Preloader.dll'))

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
            if (-not (Test-Path -LiteralPath $destDir)) { [void][IO.Directory]::CreateDirectory($destDir) }
            if (Test-Path -LiteralPath $dest) {
                Copy-Item -LiteralPath $dest -Destination "$dest.bak-$(Get-Date -Format yyyyMMdd-HHmmss)" -Force
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
if (-not (Test-Path -LiteralPath $payload)) {
    Fail ('The installer is incomplete: payload\AI2UCustomAI.dll is missing next to it. ' +
          'Re-extract the WHOLE downloaded zip - do not run Install.bat from inside the zip window.')
}

$plugins = Join-Path $game 'BepInEx\plugins'
if (-not (Test-Path -LiteralPath $plugins)) { [void][IO.Directory]::CreateDirectory($plugins) }

$target = Join-Path $plugins 'AI2UCustomAI.dll'
if (Test-Path -LiteralPath $target) {
    Copy-Item -LiteralPath $target -Destination "$target.bak-$(Get-Date -Format yyyyMMdd-HHmmss)" -Force
}
Copy-Item -LiteralPath $payload -Destination $target -Force

# Unblock defensively in case the payload itself carries mark-of-the-web from
# the browser download of the installer zip.
Unblock-File -LiteralPath $target -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath (Join-Path $game 'BepInEx') -Recurse -Filter *.dll |
    ForEach-Object { Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue }
Unblock-File -LiteralPath (Join-Path $game 'winhttp.dll') -ErrorAction SilentlyContinue

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
    if (-not (Test-Path -LiteralPath $f)) { Fail "Verification failed: $f is missing." }
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
