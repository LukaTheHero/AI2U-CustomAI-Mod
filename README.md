# AI2U — Custom AI Endpoint (full project history)

The complete development tree of the [AI2U Custom AI Endpoint mod](https://www.nexusmods.com/aiu2withyoutiltheend/) —
every released version's source, build scripts, release notes and Nexus page text,
from V1 (a simple endpoint redirect) to V4.4 (a full prompt-architecture overhaul).

Published so people can fork it, learn from it, and play around. PRs welcome, but
the Nexus release remains the canonical build.

## Layout

```
V1 … V4.4/            one folder per released version, each self-contained:
  plugin/             C# source + build.sh
  dist/               release layout (BepInEx/plugins/…)
  NEXUS-DESCRIPTION.bbcode
  CHANGES.md
V4.4/installer/       the "easy installer" (Install.bat + PowerShell)
CanalpaCurrentVersion/  snapshot of my personal build's source
media/                gallery images used on the Nexus page
```

The latest version's source is `V4.4/plugin/`. Earlier folders are frozen history —
useful for seeing how a problem was found and fixed over time, since the comments
carry the investigation, not just the result.

## Building

No Visual Studio needed. Each `plugin/build.sh` compiles with the .NET Framework
`csc.exe` that ships with Windows, referencing the assemblies of your own installed
copy of the game (edit the paths at the top of the script to match your install).

```
cd V4.4/plugin
./build.sh --no-install     # compile only
./build.sh                  # compile + install to the game copy configured in the script
```

## What is deliberately NOT in this repo

- **No game files or extracted game text.** The mod reads the game's own authored
  content out of *your* installed copy at runtime. Dumps of that content are not
  mine to redistribute, so research notes containing them are excluded.
- **No BepInEx.** Separate project, own licence — the installer downloads it from
  the official release.
- **No API keys.** Configs in `dist/` are templates with empty key fields. If you
  fork this, keep it that way: never commit a filled-in config.
- **No third-party mods.** Earlier versions coexisted with a few other Nexus mods;
  their files belong to their authors.

## Licence

MIT for everything in this repository (see LICENSE). The game itself, its content,
and its trademarks belong to AlterStaff — this project is unofficial and
unaffiliated. Out of respect for the developers, the mod refuses to use their
metered servers with modified prompts: your own API key only.
!!!YOU ARE NOT ALLOWED TO MODIFY THIS MOD MALICIOUSLY TO USE THEIR SERVERS, I WILL REPORT YOU TO ALTERSTAFF.!!!
