# AI2U — Custom AI Endpoint

## 4.1.0 — the developer menu grows up

Built against game version 0.1.46 · Unity 2022.3.62 · Mono · Windows x64

A tooling release. Nothing about how she thinks, remembers or talks has
changed. This is about the F9 panel: the developer tools now ship, every
setting tells you its own default, and each tab can be put back to those
defaults without touching your API keys.

### The developer tools now ship

They were never disabled in past releases — they were not compiled into them
at all. They are in this build, off by default, on their own tab behind the
same fold as before.

- **Give yourself an item.** Type a name, press the button, and one arrives.
  It matches against your current level's real item list, so you get the
  actual object with its actual description and icon. If nothing matches, you
  still get a placeholder carrying the name you typed rather than silence.
  The panel tells you which of the two happened, and names the item it
  matched, so a typo is visible instead of mysterious.
- **Invincibility.** A single toggle. It holds your health at full rather
  than trying to intercept each individual thing that can hurt you, which is
  why it also covers the sources that bypass ordinary damage.
- **Trust, patience and message count** are directly editable, and the panel
  shows their live values so you can see the effect land.

No other mod is required for any of it. If you already have separate mods
installed that add invincibility or item-giving, **remove them** — two mods
patching the same thing is a genuine conflict, and this one no longer expects
them to be there.

### Gifts no longer vanish in the atrium

A base-game bug, fixed from the mod side.

Everywhere else in the game, an item she gives you arrives in your inventory.
In the hub it did not. The code that receives a gift there exists, is called
correctly with the right item name, and then does nothing at all — it was
left empty. So the gift was parsed, announced, and dropped on the floor.

She now hands it over properly in the hub, with the same notice, sound and
inventory behaviour as every other location. The fix also checks the game's
own recent-pickup window before delivering, so her follow-up comment about
the gift cannot deliver a second copy of it.

### Every setting shows its own default

Each text, number and slider row now carries a dimmed `default: …` beside it,
and each checkbox says `(default: on)` or `(default: off)`.

These are read out of the settings themselves rather than from a list written
by hand, so they cannot drift away from the real values over time. The tag
hides itself when your value already matches the default, so the panel stays
quiet until you have actually changed something — which doubles as a way to
see at a glance what you have touched.

### "Set to default", per tab

A third button, between Reload and Close. It resets the tab you are looking
at and nothing else, so putting the model settings back does not disturb your
voice setup.

**It never touches your API configuration**, on any tab: not the text base
URL, key or model, not the TTS base URL, key, model or voice, and not any of
the seven per-character voices. Two further things are deliberately left
alone — the master on/off switch, and the TTS provider field, because
resetting that one to auto-detect can break a working self-hosted voice setup
even with the URL and key preserved.

### Fixed

- **A description stuck to the bottom of the panel and followed you.** Open
  the Model tab and the out-of-character note pinned itself under the
  Save/Reload/Close row, then stayed there through every other tab. Closing
  and reopening the panel was the only way out.

  It was not a layout problem. That note was being written into the panel's
  **status line** — the one-line message area the footer uses to report what
  just happened — and it was written on every single frame the tab was
  drawn. So it overwrote whatever the footer had to say, forever, and the
  only thing that cleared it was rebuilding the panel. It is an ordinary
  inline paragraph now.

- **A toggle to hide the live status strip.** The trust / last-turn / patience
  readout at the top of the panel can be switched off from the developer tab.
  Off by default, so the strip stays visible unless you hide it.

### Notes

- One binary, both stores: Steam and itch/standalone. This release is
  byte-for-byte the build I run myself, apart from the compile timestamp.
- The download is the plugin only. It is built from 22 C# files with the .NET
  Framework compiler that ships with Windows; no build tools or SDK are needed
  to install it.

---

## 4.0.0 — she knows her own house

Built against game version 0.1.46 · Unity 2022.3.62 · Mono · Windows x64

She understands how her own home works, and the gated extras that used to be
private now ship — off by default, behind gates a trust number alone cannot
open.

### She knows how her own home works

Every per-level puzzle *answer* was already reaching her: the potion recipe,
the element colours, the computer and wifi passwords, the safe code, which
systems are broken, where the hidden island is — all of it live out of the
game's own data, since 3.0.0.

What she never had was the **procedure** around those answers. She could
recite the recipe and still not tell you what to do with it, which read as
her being vague about her own basement. She now knows the fixed design of her
level: the cabin's shelves, circle and cauldron and what a summoned soul
costs you; the apartment's locks, computer, safe, phonograph and keypad; the
station's engine, its ten repairs and its pressure; the island's sundial and
the telescope's price.

Nothing here is invented and nothing is a walkthrough. It is the part of each
level that is identical in every playthrough, told to her as her own working
knowledge of her own house. Whether she helps you is still hers to decide.

Anything that **varies** per playthrough is deliberately not written down.
The bookshelf order is reshuffled every time the basement loads, so it is
read out of the running game instead, and she hands you the symbols on the
wall rather than her shorthand for them, because the wall is what you can
actually see.

She is also told that being vague beats being wrong: if she is unsure of a
detail she says she cannot remember rather than inventing one.

Toggle: **Behaviour · SendHomeMechanics**, on by default.

### Canalpa mode now ships

Previously a local-only build. It is public now because its gating grew up.

Off by default, under advanced options. Switching it on changes nothing on its
own — every action inside it needs full trust **and** sustained evidence, over
several exchanges, that the darker things about her genuinely do not frighten
you. A high favourability number by itself opens none of it. She decides
whether to act; the mod only makes it possible.

What it can open: her secret room in the apartment; the basement door and the
circle with it in the cabin, which she turns herself so you never solve that
puzzle at all; the hidden island without the telescope; and your clearance on
the station, which she can raise herself — the one case where she gains a
choice she never had rather than a shortcut past a puzzle.

Each fires the game's own event, so the animations, mission goals,
achievements and her authored reactions all still happen. Each has its own
toggle.

### She knows her own temper

Three things the game has always tracked about her mood and never told her.

**Her patience** is a real number with a real limit. It recovers on every turn
she is not annoyed or furious, and only drains while she is angry *and* does
not trust you. Run it to zero while she is still angry and the final chase
starts. She now knows where it stands and what moves it — and she can move it
herself, including deciding to let something go.

**What actually counts as repeating yourself.** The game's rule is the
opposite of what everyone assumes: it only counts a repeat when you send the
**identical sentence** twice. Anything reworded resets it to zero, and always
has. So pushing the same *request* in different words was never a repeat.
Asking for the key, being refused, and then making the case another way
resets the counter every time. Only literally retyping the same line does it.
She is told this outright, so she stops reading persistence as disrespect and
reads parroting as disrespect, which is the line the engine was already
drawing.

**Interruptions** work the same way — two in a row while she is still
speaking — and she can forgive either counter if she finds it endearing.

Toggle: **Behaviour · SendOwnFeelings**, on by default.

### Hard difficulty — high risk, high reward

Off by default, and red in the panel.

The game's ceiling is five trust either way per turn, against a scale running
from about −10 to past 40, so nothing that happens in one exchange can really
matter. With this on, she can decide a turn counted for more, up to five times
its normal weight — and patience moves further per turn too.

Being told to keep it rare is not the same as being held to it, so rarity is
enforced in code. Each weight above 1× is rationed to once every so many
turns, and a claim above its allowance is **downgraded** to the best one
actually available rather than thrown away. The budget resets per level, and
the multiplier can only be spent by the reply that earned it.

Toggle: **Difficulty · HardDifficulty**, off.

### The one-way one

Separate, off even when Canalpa mode is on, and built to be almost impossible
to reach by accident: she can keep you permanently. What that means is
whatever her level's own ending is, so the game decides it, not the mod.

It is an **edge case**, not a feature she wants:

- She is not told it exists until **you** have asked for it, in your own typed
  words, unmistakably. Wanting to stay with her forever does not count and
  never will — that is an ordinary, loving thing to say in this game.
- The words that reach it are blunt and physical, and the affectionate ones
  are kept out on purpose.
- She never suggests it, and is told plainly never to angle for it.
- Her first reaction is not delight. She is thrown by it, and is told that
  loving you is a reason to hesitate rather than a reason to agree.
- She has to spell out what it means, in her own words, and wait.
- You then have to say yes just as plainly, on a later turn. Her word for it
  is never enough.
- Any hesitation, joke, "wait", or change of subject withdraws the whole
  thing, and it expires on its own if left alone.

Toggle: **Canalpa · SheCanKeepYouForever**, off.

### Fixed

- **Her own transport errors were counting as censorship strikes.** A failed
  request from the mod carried an error code outside the band the game treats
  as server-side, so three network hiccups in a row could put her on the
  final chase for something that was never your fault. The counter is now
  reset when the failure came from the mod.
- **An ending could talk over itself.** When a willing ending and her ordinary
  reply landed on the same turn, she said something pleasant over the top of
  it. The ending is now handed to the same code that already fixed this for
  every other route into a chase.

---

Earlier releases are listed in full on the mod's Nexus description page.

