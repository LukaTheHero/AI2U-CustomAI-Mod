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

TARGET="${1:-standalone}"
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

echo "==> compiling"
# Remove any previous output first: otherwise a failed compile leaves the old
# DLL in place and the existence check below reports a stale build as success.
rm -f AI2UCustomAI_new.dll
"$CSC" -nologo -noconfig -target:library -optimize+ -nostdlib+ \
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
  OverlayMenu.cs ApiGuard.cs SpeechText.cs Platform.cs Murder.cs 2>&1 | grep -v 'warning CS1701\|previous warning' || true

if [ ! -f AI2UCustomAI_new.dll ]; then
  echo "!! compile produced no output" >&2
  exit 1
fi

echo "==> built $(sha256sum AI2UCustomAI_new.dll | cut -c1-16)... ($(stat -c%s AI2UCustomAI_new.dll) bytes)"

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
