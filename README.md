# AI2U — Custom AI Endpoint

The source of the current release of the [AI2U Custom AI Endpoint mod](https://www.nexusmods.com/aiu2withyoutiltheend/) —
C# source, build script, release notes and Nexus page text.

Published so people can fork it, learn from it, and play around. PRs welcome, but
the Nexus release remains the canonical build.

## Layout

```
V5.1/                 the current release, self-contained:
  plugin/             C# source + build.sh
  dist/               release layout (BepInEx/plugins/…)
  installer/          the "easy installer" (Install.bat + PowerShell)
  NEXUS-DESCRIPTION.bbcode
  CHANGES.md
CanalpaCurrentVersion/  snapshot of my personal build's source
media/                gallery images used on the Nexus page
```

**This repository only ever carries the latest version.** Older releases are not
kept here — when a new version ships, its folder replaces the previous one. Every
version's release notes stay readable in `V5.1/CHANGES.md`, which holds the full
changelog back to V1, and older builds remain downloadable from the Nexus Files
tab.

## Building

No Visual Studio needed. Each `plugin/build.sh` compiles with the .NET Framework
`csc.exe` that ships with Windows, referencing the assemblies of your own installed
copy of the game (edit the paths at the top of the script to match your install).

```
cd V5.1/plugin
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

## ⛔ The one hard line

> **Do not point any fork of this code at AlterStaff's servers. Ever.**
>
> Every AI request their servers process costs them real money. This mod exists
> on one founding rule: **your own API key, your own bill.** The dialogue
> interception, the guard patches, the endpoint routing — all of it is built to
> keep modded traffic *off* their infrastructure, and that restraint is the only
> reason a mod like this can exist in good conscience.
>
> If you strip those guards out and free-ride modified prompts through their
> paid endpoints, you are not modding — you are stealing compute from a small
> team, on my code, with my name upstream of it. **I will find out, I will
> report you to AlterStaff with everything I have, and I will help them shut
> you down.** Forks that cross this line get DMCA'd without a warning shot.
>
> Fork anything else. Custom characters, new endpoints, wild experiments — go
> nuts, that's why this is public. But the developers made something worth
> modding, and nobody gets to make them pay for our fun.

