#!/usr/bin/env bash
# Builds the release zips for one version folder - and refuses to produce them
# unless the binary inside is provably the version on the label.
#
# WHY THIS EXISTS
# ---------------
# 5.4.0 shipped to Nexus containing a 5.3.0 DLL. Three separate users found it
# before I did, one of them by hashing the file (md5 1ba5d5a4..., the 5.3 build).
#
# The mechanism was mundane and would happen again: build.sh compiles to
# plugin/AI2UCustomAI_new.dll and installs THAT into the game copies, so a
# developer testing the build sees the new features working perfectly. But the
# zips were assembled by hand from V5.4/dist/BepInEx/plugins/AI2UCustomAI.dll,
# a file nothing in the build had written since 5.3. Every step succeeded, the
# local install was genuinely correct, and the release was wrong.
#
# So the fix is not "remember to copy the DLL". It is this: packaging refreshes
# dist from the compiled binary itself, then reads the version string back out
# of the bytes that actually landed inside each zip and compares it against the
# version declared in the source. A mismatch is a hard failure with a non-zero
# exit, not a warning that scrolls past.
#
#   ./package.sh            # compile fresh, then package (the normal path)
#   ./package.sh --no-build # package whatever is already staged, still verified
#
# The version is never passed in as an argument on purpose. It is read from
# AI2UCustomAI.cs, which is the same constant the plugin reports to BepInEx and
# the update check compares against, so there is exactly one place to change it
# and no way for a typo to disagree with the binary.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERDIR="$(cd "$HERE/.." && pwd)"
cd "$HERE"

BUILD=1
for a in "$@"; do
  case "$a" in
    --no-build) BUILD=0 ;;
    *) echo "!! unknown flag: $a  (flags: --no-build)" >&2; exit 1 ;;
  esac
done

# The single source of truth. Matched on the const rather than the BepInPlugin
# attribute because the attribute is a literal in an annotation and the const is
# what the rest of the mod reads; build.sh already keeps them equal.
# sed, not grep -oP: this machine's grep refuses -P outside a unibyte or UTF-8
# locale, and a release script that only works in one shell's locale is a trap.
VERSION="$(sed -n 's/.*public const string VERSION[[:space:]]*=[[:space:]]*"\([0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*\)".*/\1/p' AI2UCustomAI.cs | head -1)"
if [ -z "$VERSION" ]; then
  echo "!! could not read VERSION from AI2UCustomAI.cs" >&2
  exit 1
fi
echo "==> packaging version $VERSION  (from AI2UCustomAI.cs)"

# Guard against the other half of the 5.4 mistake: a version folder whose name
# disagrees with the source it contains. V5.4/ holding a 5.3.0 source tree is
# exactly as wrong as a 5.3.0 DLL, and far harder to spot later.
FOLDER="$(basename "$VERDIR")"
SHORT="${VERSION%.0}"
if [ "$FOLDER" != "V$SHORT" ] && [ "$FOLDER" != "V$VERSION" ]; then
  echo "!! folder/version mismatch: $FOLDER contains source for $VERSION" >&2
  exit 1
fi

if [ "$BUILD" = "1" ]; then
  echo "==> compiling release build (no install)"
  ./build.sh --hybrid --release --no-install
fi

STAGED="$HERE/AI2UCustomAI_new.dll"
[ -f "$STAGED" ] || { echo "!! no compiled DLL at $STAGED" >&2; exit 1; }

# Read the version out of the compiled bytes. The BepInPlugin version lives in
# the metadata as a UTF-16LE literal, so both encodings are checked: finding the
# right string is not enough, finding a DIFFERENT release's string alongside it
# means a stale object file got linked in and the build is not trustworthy.
verify_dll() {
  python - "$1" "$VERSION" <<'PY'
import re, sys
path, want = sys.argv[1], sys.argv[2]
data = open(path, 'rb').read()
pat = rb'[0-9]+\.[0-9]+\.[0-9]+'
found = set(m.decode() for m in re.findall(pat, data))
# The metadata literal is UTF-16LE, so the same triple is looked for a second
# time with a null after every byte. Raw-bytes pattern on purpose: the null and
# dot escapes are read by the regex engine itself, and routing them through
# Python's own string escaping instead produced a pattern that matched nothing
# while still looking correct - the ASCII scan above was doing all the work.
found |= set(m.decode('utf-16le') for m in re.findall(
    rb'(?:[0-9]\x00)+\.\x00(?:[0-9]\x00)+\.\x00(?:[0-9]\x00)+', data))
# Only mod-version-shaped strings matter here; the assembly is full of
# unrelated triples (framework versions, Unity module versions, dotted ids).
mine = sorted(v for v in found if v.startswith(want.split('.')[0] + '.'))
if want not in mine:
    print("FAIL: %s does not contain version %s (saw: %s)" % (path, want, mine or 'none'))
    sys.exit(1)
other = [v for v in mine if v != want]
if other:
    print("FAIL: %s also contains foreign version(s) %s" % (path, other))
    sys.exit(1)
print("  ok  %s -> %s" % (path.split('/')[-1], want))
PY
}

echo "==> verifying the compiled binary"
verify_dll "$STAGED"

# Refresh dist FROM the compiled binary. This copy is the whole fix: dist is now
# an output of packaging, never an input a human has to remember to update.
DIST="$VERDIR/dist/BepInEx/plugins"
mkdir -p "$DIST"
cp "$STAGED" "$DIST/AI2UCustomAI.dll"
echo "==> dist refreshed from the compiled binary"

BUILT_MD5="$(md5sum "$STAGED" | cut -d' ' -f1)"
NAME="AI2U-Custom-AI-Endpoint-$VERSION"
MANUAL="$VERDIR/$NAME.zip"
EASY="$VERDIR/$NAME-EasyInstaller.zip"
rm -f "$MANUAL" "$EASY"

# python zipfile rather than a zip binary: this machine has no zip on PATH, and
# a silently missing command is how the last release went out wrong.
python - "$VERDIR" "$DIST/AI2UCustomAI.dll" "$MANUAL" "$EASY" <<'PY'
import os, sys, zipfile
verdir, dll, manual, easy = sys.argv[1:5]
changes  = os.path.join(verdir, 'CHANGES.md')
inst     = os.path.join(verdir, 'installer')

with zipfile.ZipFile(manual, 'w', zipfile.ZIP_DEFLATED) as z:
    z.write(dll, 'BepInEx/plugins/AI2UCustomAI.dll')
    z.write(changes, 'CHANGES.md')

with zipfile.ZipFile(easy, 'w', zipfile.ZIP_DEFLATED) as z:
    z.write(dll, 'payload/AI2UCustomAI.dll')
    for f in ('Install.bat', 'installer.ps1', 'README-FIRST.txt'):
        z.write(os.path.join(inst, f), f)
    z.write(changes, 'CHANGES.md')
print("  wrote", os.path.basename(manual))
print("  wrote", os.path.basename(easy))
PY

# The check that 5.4 lacked: open the finished artefacts and inspect the bytes a
# player would actually receive. Hash equality proves it is the file just built;
# the version scan proves that file is the release it claims to be. Verifying
# the staged DLL alone would have passed in 5.4 too - the staged DLL was fine.
echo "==> verifying the zips a player downloads"
python - "$MANUAL" "$EASY" "$BUILT_MD5" "$VERSION" <<'PY'
import hashlib, re, sys, zipfile
manual, easy, want_md5, want_ver = sys.argv[1:5]
expect = {manual: 'BepInEx/plugins/AI2UCustomAI.dll', easy: 'payload/AI2UCustomAI.dll'}
bad = False
for path, member in expect.items():
    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        if member not in names:
            print("FAIL: %s has no %s (has %s)" % (path, member, names)); bad = True; continue
        data = z.read(member)
        md5 = hashlib.md5(data).hexdigest()
        vers = set(m.decode() for m in re.findall(rb'[0-9]+\.[0-9]+\.[0-9]+', data))
        vers |= set(m.decode('utf-16le') for m in re.findall(
            rb'(?:[0-9]\x00)+\.\x00(?:[0-9]\x00)+\.\x00(?:[0-9]\x00)+', data))
        mine = sorted(v for v in vers if v.startswith(want_ver.split('.')[0] + '.'))
        ok_md5 = md5 == want_md5
        ok_ver = mine == [want_ver]
        print("  %s  %s" % ('ok ' if (ok_md5 and ok_ver) else 'FAIL', path.split('\\')[-1].split('/')[-1]))
        print("       md5 %s %s" % (md5, '' if ok_md5 else '!= built ' + want_md5))
        print("       version strings in shipped bytes: %s" % (mine or 'none'))
        if not (ok_md5 and ok_ver): bad = True
sys.exit(1 if bad else 0)
PY

echo "==> PACKAGED AND VERIFIED: $VERSION"
echo "    $MANUAL"
echo "    $EASY"
echo "    every shipped DLL is md5 $BUILT_MD5 and reports $VERSION"
