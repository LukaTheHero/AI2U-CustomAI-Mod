# canak's private build — the most current one

This folder is the **private** version. It is not what goes on Nexus.

Two builds now come out of one source tree, separated by a compile switch:

| | private (here) | public (Nexus) |
|---|---|---|
| build command | `./build.sh --hybrid` | `./build.sh --release` |
| `CANALPA` defined | yes | no |
| developer cheats panel | yes | **absent from the binary** |
| Canalpa mode | yes | **absent from the binary** |

"Absent from the binary" is literal — the code sits behind `#if CANALPA`, so the
public DLL does not contain the strings, the config entries or the panel rows.
It is not a hidden toggle someone can flip in a config file.

## What is in here

    BepInEx/plugins/AI2UCustomAI.dll      the private build
    BepInEx/plugins/GiftYourselfAnything.dll
    BepInEx/plugins/InvincibilityMod.dll
    BepInEx/plugins/RestoreAtriumGIfts.dll   (note the capital I — upstream typo)
    BepInEx/config/com.luigirocks900.InvincibilityMod.cfg
    source/                               full source at the time of this build

Drop the `BepInEx` folder over `Game\BepInEx` in either install.

## The three bundled companion mods

All three are by **Luigirocks900**, all three are cheat/convenience mods, and none
of them ship to Nexus with my mod — they are separate downloads that happen to be
bundled here for my own convenience.

| Mod | GUID | What it does |
|---|---|---|
| Gift Yourself Anything | `com.Luigirocks900.GiftYourselfAnything` | spawn any item into your inventory |
| Invincibility | `com.luigirocks900.InvincibilityMod` | survive the chase |
| RestoreAtriumGifts | `com.Luigirocks900.RestoreAtriumGifts` | lets NPCs hand over items in the Atrium, which vanilla forbids |

My F9 panel **detects all three by GUID** and shows a row for each under
developer cheats, so they are visible and drivable from one place.

### Invincibility: F2 is deliberately off

`Keybind = None` in its config, in both installs. The toggle lives in my panel
instead, so there is one place to change it and no stray keypress can flip it
mid-chase. If the row ever says the mod is missing, that is a load failure — the
keybind will not save you, because it no longer exists.

### RestoreAtriumGifts: no conflict with my mod

It patches `NPCMasterBehavior_Main_Config.ReceiveItem`. I patch the reply side
(`Communicator.ReceiveChatGPTReply`, `ChatGPTConversation.SendToChatGPT`) and the
URI builders. Different methods, no overlap, load order does not matter. My
prompt never claims Atrium gifting is impossible, so the two do not contradict
each other.

## Rebuilding

    cd V3.2/plugin
    ./build.sh --hybrid                 # private, installs to both game copies
    ./build.sh --release --no-install   # public, compiles only, touches nothing

`--no-install` matters: a plain `--release` **overwrites the installed private
DLL**, which is how the cheats panel briefly vanished on 2026-08-09. Always pass
it when you only want to check that the public build still compiles.

After any private rebuild, refresh the DLL in this folder so it stays the most
current copy:

    cp "/c/AI2U/Game/BepInEx/plugins/AI2UCustomAI.dll" BepInEx/plugins/
