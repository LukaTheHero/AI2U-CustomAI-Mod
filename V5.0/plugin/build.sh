#!/usr/bin/env bash
# Builds the AI2U custom-endpoint plugin and installs it into the game.
#
# Must be built with the game's OWN base assemblies (-nostdlib+ -noconfig),
# not the ones csc picks up from csc.rsp. The Unity runtime ships an older
# netstandard/System.Runtime, and mixing them makes UploadHandlerRaw resolve
# to a Span<byte> overload that does not exist at runtime.
#
# The game locks AI2UCustomAI.dll while it is running. Close the game first
# or the install step fails with "Permission denied".
set -euo pipefail

# Two installs exist on this machine and both can run at once, so the target is
# explicit rather than guessed:
#   ./build.sh           -> the itch.io/standalone copy in C:\AI2U
#   ./build.sh --steam   -> the Steam copy
#   ./build.sh --hybrid  -> ONE binary, installed to both (this is what ships)
#
# --hybrid compiles against the STEAM reference set deliberately. Steam is the
# restrictive one - Overtone's Speak is stripped there - so a clean compile
# against it proves nothing Steam-absent is referenced, and the same file then
# runs on itch too. The per-target modes remain for bisecting a build problem
# down to one reference set.
#
# The running-process check is matched on the full exe path, so building for one
# copy never refuses merely because the other copy is open.
STEAM_GAME="/c/Program Files (x86)/Steam/steamapps/common/AI2U/Game"
STEAM_WIN='C:\Program Files (x86)\Steam\steamapps\common\AI2U\Game'
ITCH_GAME="/c/AI2U/Game"
ITCH_WIN='C:\AI2U\Game'

# --release can appear in any position, so it composes with the target flag:
# ./build.sh --hybrid --release. It is parsed out here rather than being a
# fourth TARGET case, because it is orthogonal to which copy gets installed.
# --no-install compiles and reports the hash but leaves both game copies alone.
# It exists because verifying that the RELEASE configuration still compiles is a
# routine check, and without this flag that check overwrites the personal install
# with a binary that has the private features stripped out. Silently, and looking
# exactly like a successful build.
RELEASE=0
NOINSTALL=0
ARGS=()
for a in "$@"; do
  case "$a" in
    --release)    RELEASE=1 ;;
    --no-install) NOINSTALL=1 ;;
    # Accepted and ignored: the private features are already the default. The
    # flag reads like it ought to exist, so it must not fall through to TARGET
    # and quietly select one copy instead of both.
    --canalpa|--private|--cheats) ;;
    # Targets are accepted dashed as well as bare, so they have to be let
    # through to ARGS before the unknown-flag guard below sees them.
    --hybrid|--steam|--standalone) ARGS+=("$a") ;;
    # Anything else starting with a dash is a typo, not a target. Without this
    # an unknown flag lands in TARGET, hits the catch-all below and installs to
    # a single copy while reporting success.
    --*)
      echo "!! unknown flag: $a" >&2
      echo "   flags:   --release  --no-install  --canalpa (default, no-op)" >&2
      echo "   targets: hybrid | steam | standalone" >&2
      exit 1
      ;;
    *)            ARGS+=("$a") ;;
  esac
done

TARGET="${ARGS[0]:-standalone}"
case "$TARGET" in
  --hybrid|hybrid)
    REF_GAME="$STEAM_GAME"
    TARGET_DIRS=("$STEAM_GAME" "$ITCH_GAME")
    TARGET_WINS=("$STEAM_WIN" "$ITCH_WIN")
    echo "==> target: BOTH copies from one binary (compiled against Steam)"
    ;;
  --steam|steam)
    REF_GAME="$STEAM_GAME"
    TARGET_DIRS=("$STEAM_GAME")
    TARGET_WINS=("$STEAM_WIN")
    echo "==> target: Steam copy"
    ;;
  *)
    REF_GAME="$ITCH_GAME"
    TARGET_DIRS=("$ITCH_GAME")
    TARGET_WINS=("$ITCH_WIN")
    echo "==> target: itch.io/standalone copy"
    ;;
esac

MANAGED="$REF_GAME/AI2U - With you til the end_Data/Managed"
CORE="$REF_GAME/BepInEx/core"
CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

cd "$HERE"

# Canalpa mode SHIPS as of 4.0. It was private alongside the cheats until its
# gating grew up: every action is behind full trust plus sustained evidence, the
# one irreversible action additionally needs the player to ask for it explicitly
# in their own typed words twice, turns apart, and the mode itself is off by
# default under advanced options. So CANALPA is defined in every build, release
# included, and its sources compile unconditionally.
#
# The define and the #if blocks stay rather than being deleted. They are the
# switch that made the private/public split possible, and leaving them intact
# keeps it available if the mode ever has to come back out of a release without
# unpicking four files by hand.
CANALPA_SRC="Canalpa.cs Consent.cs"
CANALPA_DEF="CANALPA"

# The developer cheats SHIP as of 4.0 too, so the release build is now byte-for-
# byte the same configuration as the local one. That was a deliberate reversal:
# they were private while they drove three third-party companion mods, because
# shipping a panel whose buttons only work if a stranger's DLL happens to be
# installed is worse than not shipping it. Cheats.cs now implements item-give,
# invincibility and the atrium gift fix against the game's own API, so every
# button works out of the box for everyone and there is nothing left to hide.
#
# The CHEATS define is gone rather than being left defined-everywhere: a define
# that is never false is not a switch, it is noise that makes the reader look for
# a configuration that does not exist.
PRIVATE_SRC="Cheats.cs"
PRIVATE_DEF="-define:$CANALPA_DEF"
# --release now differs from a local build only in where it installs to. It is
# still worth keeping as a flag: it is the one that does not overwrite the
# personal install, and it is the shape the packaging step expects.
if [ "$RELEASE" = "1" ]; then
  echo "==> RELEASE build: identical configuration to local (cheats and Canalpa both ship)"
fi

echo "==> compiling"
# Remove any previous output first: otherwise a failed compile leaves the old
# DLL in place and the existence check below reports a stale build as success.
rm -f AI2UCustomAI_new.dll
"$CSC" -nologo -noconfig -target:library -optimize+ -nostdlib+ \
  $PRIVATE_DEF \
  -out:AI2UCustomAI_new.dll \
  -r:"$MANAGED/mscorlib.dll" \
  -r:"$MANAGED/System.dll" \
  -r:"$MANAGED/System.Core.dll" \
  -r:"$MANAGED/netstandard.dll" \
  -r:"$MANAGED/System.Runtime.dll" \
  -r:"$CORE/BepInEx.dll" \
  -r:"$CORE/0Harmony.dll" \
  -r:"$MANAGED/Assembly-CSharp.dll" \
  -r:"$MANAGED/UnityEngine.dll" \
  -r:"$MANAGED/UnityEngine.CoreModule.dll" \
  -r:"$MANAGED/UnityEngine.UnityWebRequestModule.dll" \
  -r:"$MANAGED/UnityEngine.AudioModule.dll" \
  -r:"$MANAGED/UnityEngine.UnityWebRequestAudioModule.dll" \
  -r:"$MANAGED/UnityEngine.InputLegacyModule.dll" \
  -r:"$MANAGED/UnityEngine.IMGUIModule.dll" \
  -r:"$MANAGED/UnityEngine.TextRenderingModule.dll" \
  -r:"$MANAGED/UnityEngine.UI.dll" \
  -r:"$MANAGED/UnityEngine.UIModule.dll" \
  -r:"$MANAGED/Unity.TextMeshPro.dll" \
  -r:"$MANAGED/Unity.InputSystem.dll" \
  -r:"$MANAGED/Newtonsoft.Json.dll" \
  AI2UCustomAI.cs GameVocab.cs GrokTts.cs Identity.cs NewInput.cs ModUI.cs \
  OverlayMenu.cs ApiGuard.cs SpeechText.cs Platform.cs Murder.cs Items.cs Ooc.cs \
  Lore.cs Bios.cs Voices.cs Mechanics.cs Feelings.cs Difficulty.cs Extras.cs Roleplay.cs \
  $CANALPA_SRC $PRIVATE_SRC 2>&1 | grep -v 'warning CS1701\|previous warning' || true

if [ ! -f AI2UCustomAI_new.dll ]; then
  echo "!! compile produced no output" >&2
  exit 1
fi

echo "==> built $(sha256sum AI2UCustomAI_new.dll | cut -c1-16)... ($(stat -c%s AI2UCustomAI_new.dll) bytes)"

# Before the running-process check, because --no-install has no reason to care
# whether the game is open.
if [ "$NOINSTALL" = "1" ]; then
  echo "==> --no-install: compiled only, both game copies left untouched"
  echo "    staged at $HERE/AI2UCustomAI_new.dll"
  exit 0
fi

# Matched on the executable path, not the process name: both installs report the
# same name, so a name match would refuse to build for the Steam copy purely
# because the itch copy is open - or worse, overwrite a DLL that a live session
# has loaded. Every target is checked BEFORE anything is installed, so --hybrid
# cannot leave one copy updated and the other not.
for i in "${!TARGET_WINS[@]}"; do
  W="${TARGET_WINS[$i]}"
  RUNNING="$(powershell.exe -NoProfile -Command \
    "Get-Process -Name 'AI2U*' -ErrorAction SilentlyContinue |
       Where-Object { \$_.Path -like '${W}*' } |
       ForEach-Object { \$_.Id }" 2>/dev/null | tr -d '\r' | tr '\n' ' ')"
  if [ -n "${RUNNING// /}" ]; then
    echo "!! that copy is running (PID(s):${RUNNING%% }) - close it, then re-run" >&2
    echo "   target: $W" >&2
    echo "   compiled DLL is staged at $HERE/AI2UCustomAI_new.dll" >&2
    exit 1
  fi
done

for i in "${!TARGET_DIRS[@]}"; do
PLUGINS="${TARGET_DIRS[$i]}/BepInEx/plugins"
echo "==> installing to ${TARGET_WINS[$i]}"
# Rename, then copy - do not overwrite in place. A running game holds the DLL
# open, so a plain cp over it fails with "Permission denied" (which reads like a
# folder-permission problem and is not: the folder is writable). Windows will
# happily rename a locked file, and the running process keeps the image it has
# already mapped, so the swap is safe mid-session and the next launch picks up
# the new build. The rename doubles as the backup.
TS="$(date +%Y%m%d-%H%M%S)"
BAK="$PLUGINS/AI2UCustomAI.dll.bak-$TS"
ROLLBACK=""
if [ -f "$PLUGINS/AI2UCustomAI.dll" ]; then
  if mv "$PLUGINS/AI2UCustomAI.dll" "$BAK"; then
    ROLLBACK="$BAK"
    echo "    previous build kept as $(basename "$BAK")"
  else
    echo "!! could not move the installed DLL aside" >&2
    exit 1
  fi
fi

# Put the old build back rather than leaving the install with no plugin at all.
if ! cp AI2UCustomAI_new.dll "$PLUGINS/AI2UCustomAI.dll"; then
  echo "!! copy failed" >&2
  if [ -n "$ROLLBACK" ]; then
    mv "$ROLLBACK" "$PLUGINS/AI2UCustomAI.dll"
    echo "!! rolled back to the previous build" >&2
  fi
  exit 1
fi
# Hash the installed copy rather than trusting cp: this is the check that makes
# "one binary on both" a verified claim instead of an assumption.
echo "    installed  sha256 $(sha256sum "$PLUGINS/AI2UCustomAI.dll" | cut -c1-16)..."
done

echo "==> done"
