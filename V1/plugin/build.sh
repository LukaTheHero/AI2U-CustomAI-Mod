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

GAME="/c/AI2U/Game"
MANAGED="$GAME/AI2U - With you til the end_Data/Managed"
CORE="$GAME/BepInEx/core"
PLUGINS="$GAME/BepInEx/plugins"
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
  AI2UCustomAI.cs GameVocab.cs GrokTts.cs NewInput.cs ModUI.cs 2>&1 | grep -v 'warning CS1701\|previous warning' || true

if [ ! -f AI2UCustomAI_new.dll ]; then
  echo "!! compile produced no output" >&2
  exit 1
fi

if pgrep -f "With you til the end" >/dev/null 2>&1 \
   || tasklist 2>/dev/null | grep -qi "With you til the e"; then
  echo "!! the game is running - close it, then re-run this script" >&2
  echo "   compiled DLL is staged at $HERE/AI2UCustomAI_new.dll" >&2
  exit 1
fi

echo "==> installing"
if [ -f "$PLUGINS/AI2UCustomAI.dll" ]; then
  cp "$PLUGINS/AI2UCustomAI.dll" \
     "$PLUGINS/AI2UCustomAI.dll.bak-$(date +%Y%m%d-%H%M%S)"
fi
cp AI2UCustomAI_new.dll "$PLUGINS/AI2UCustomAI.dll"
echo "==> installed to $PLUGINS/AI2UCustomAI.dll"
