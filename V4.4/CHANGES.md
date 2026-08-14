# AI2U — Custom AI Endpoint

## 4.4.0 — the mod carries the writing

Built against game version 0.1.46 · Unity 2022.3.62 · Mono · Windows x64

### READ THIS FIRST: 4.4 is not just an API change

This is an overhaul. With it installed you are playing a meaningfully
different edition of AI2U — different thinking, different memory rules, and a
body of context the stock game never handed her. Many core behaviours differ.

If you want vanilla-with-your-own-key and nothing else, that mod does not
exist. Four major versions went into trying to build it. Here is why it can't.

Every release up to now read everything live out of your installed game files,
forwarded it, and let your model do what the developers' server used to do.
Nothing hardcoded, new content picked up for free, not one word of lore
invented by me. It never reached parity — not because the reading was
incomplete, but because a large part of what makes her her was never in your
game files at all.

The stock game does not keep her persona on your PC. It gathers the situation
locally, encrypts it into a header, and posts it to AlterStaff's servers — and
the prompt that turns that data into a character lives there, on hardware I do
not have and will not touch. The proof is small and conclusive: the game hands
their server a localization *key name* where the actual text should be. That
only works if something on the far end resolves it. Their server was doing
authoring work no amount of local reading can recover.

So every live-retrieval build shipped with holes in it. Forward everything
findable, and she still came back missing major pieces, because those pieces
were never mine to find. Not reverse engineering that failed for lack of
trying — the data is not in the building.

4.4 stops pretending otherwise. The mod now carries its own prompt
architecture: the rules, contracts, boundaries and framing the vendor server
used to supply. On top of that it forwards a great deal of the game's *own*
authored writing that vanilla never sent — the witch's diary, the station's
crew records, per-item reaction lines, and whole scene-detail arrays that were
being silently truncated before they reached her.

One line not crossed: the architecture is mine, her facts are still the
game's. Where the developers wrote something, you get their words. Where they
wrote nothing, the mod says nothing rather than inventing a substitute.

### What it costs

The prompt is now far larger than anything the stock game sent.

- Small local models will not cope. A 7B or 8B will lose the instructions,
  drop the reply format, or answer as the wrong character. Not a patchable
  bug — it is the size of the context meeting the limits of the model.
- Bring a strong model. For the best balance of quality against cost, aim at
  the class of Gemini 3.6 Flash: large context, cheap per call, holds the
  output format under load.
- Local is not ruled out, but think large models on serious hardware.

Having played both at length, I prefer this one by a lot. The context buys
conversations that stay coherent instead of quietly resetting, memories that
behave like memories, and moments that actually land. The old build was more
faithful to vanilla; this one is better to play. If that is not the trade you
want, 4.3 still works the old way.

### The summoned toy answered as the witch, in the witch's voice

You asked the magic circle a question and the witch answered it, out loud,
with her name and her secrets.

Two separate causes. Suppressing her persona blocks was necessary but not
sufficient — the game's authored framing for a summon never states that the
thing speaking *is* the summoned soul, because vanilla picked that persona
server-side. With her blocks correctly gone the summon had no identity at all,
and a model holding her location list answered as her. Absence did not read as
absence; it read as her. Separately the voice table excluded the summon and
the ghost on the stated reasoning that they are "not speaking roles" — which
is wrong, the summon is exactly what is speaking.

Now a boundary block states what it is not, and that it has one answer and no
earlier memory. Her name is asserted negatively, so it still comes from the
game. The summon and the ghost each have their own voice row in F9. No
personality was written for the toy — none exists in the files.

### The ending recap came back in Chinese

Self-inflicted. The prompt told the model to obey any language directive in
the record, and the record ends with the game's own summary term — whose
English slot is an empty string, so localization falls through to a populated
slot reading "summarize the game ending in Chinese."

Language now comes from the game's current-language setting and stray
directives are overridden. Markdown labels are banned in the prompt *and*
stripped in code, because the ending screen is player-facing and an
instruction the model may decline should not be the only thing between it and
your screen.

### She did not know things that were in her own house

Three distinct causes. Scene detail was capped at 40 lines while the largest
scene ships 46 authored entries, so content was cut with no warning. That same
block was the only knowledge section missing the framing the item and fact
blocks already had, so even forwarded detail read as optional. And a set of
authored per-item reaction lines for the two hub characters was unreachable:
the game builds key names ending `PromptUseL1001`/`L1002` while the shipped
text is filed under `PromptUseEvie`/`PromptUseParrot`.

Added: the witch's six diary entries, and the station's 11 authored file
bodies plus 13 crew notes — roughly 1.5KB each, shipped in the game,
displayed only in a terminal UI, never once sent to the model.

Negative result worth recording: the wiki's "Alt description" rows are a
presentation artifact. There are exactly 160 item description keys and no
alternate variants, so there was nothing there to recover.

### The witch did not know her own potion recipes

Each ingredient group is now paired with its own result field rather than by
list position. The formula re-rolls on every scene load, so position-based
pairing would go wrong the moment the shuffle changed. This reads the mapping
the data already carries, which holds every round.

### Memory, envision and summary are handled in-mod, on by default

The old toggle only *blocked* these three calls, which silently cost you her
saved memory of the last conversation — a failed request produces no reply
body, and the save key is written from that body. They are now answered from
your own endpoint. Turn it off and they fall through to the developers'
servers, which stays a working fallback rather than a broken feature.

### The reply length setting did not limit replies

It granted an allowance in the prompt and never enforced it. Rather than add a
second setting and leave you with two answers to "which one limits her
replies", the existing one now enforces, trimming at a sentence boundary in
the unit you set it in. Its description no longer claims to be a request.

### A raw localization key was reaching the model as prose

One code path passed an unresolved term name straight into the prompt. The
resolver's gate was widened to cover it.

## 4.2.2 — the ultimate trust check

Built against game version 0.1.46 · Unity 2022.3.62 · Mono · Windows x64

### Canalpa's secrets: her judgement replaces every coded gate

A player got her to confess everything — her whole dark side, held back
nothing — and the secret room stayed shut. Meanwhile she walked to the door,
invented a passcode, and narrated "unlocked!" over a door that never moved.

The cause was a mechanical gate the fiction could not see. Unlocking required
three passed "probes" — turns she marked as testing the water, graded by your
next reply. A full confession is not a probe, so the counter never moved: it
had no way to recognise that the player had skipped past everything the probes
were building toward. And since nothing told her the door was mechanically
locked, she roleplayed opening it — fiction and mechanics contradicting each
other, which is the exact failure this mod exists to kill.

So the probes, and every coded trust check on her secrets, are gone. The acts
are live whenever they are physically real, and **if she sets the field, it
fires — no veto, ever.** What she carries instead is her own stated bar, a
requirement she holds herself to rather than a trigger: trust beyond even
complete trust (per character: 45, 48, 50, 52 across the four levels), and
having already told you *everything* first — no secrets left between you. She
now sees her live trust number too, because the game's own label stops
distinguishing anything past 40, and she cannot hold herself to "past 45"
blind.

The rule that makes it honest, in both directions: **the field is the act.**
She is told never to describe an opening she did not set the field for — and a
field she sets always opens. Her word and the game state cannot disagree,
because they are the same statement.

Unchanged: the one-way ending keeps its hard trust floor and the full two-step
consent machinery. That is a safety gate, not a secrets gate.

An honest note: with no coded gate, her judgement is genuinely load-bearing.
If she decides to open a door at low trust, it opens — and the game's own
authored reaction to that can be violent. That is the design: the decision is
truly hers now.

---

## 4.2.1 — difficulty gets real numbers

Built against game version 0.1.46 · Unity 2022.3.62 · Mono · Windows x64

Until now the difficulty slider changed how she judges you. It still does —
and now the arithmetic follows.

### Hard and Masochist change the numbers, not just her

On Hard, trust gains land at 75% strength and losses at 150%. On Masochist,
gains at 50% and losses at 200% — a −2 becomes −4, a +5 becomes +2. Easy and
Normal leave the arithmetic at the game's own pace, and Normal still sends
nothing to the model at all.

The rounding rule, stated exactly: after scaling, results drop to the whole
number below — except a value between 0 and 1, which counts as 1. A real
action always registers at least a point, so Masochist is a crawl, never a
freeze. No fractions are banked; what lands is all there is.

The two layers divide the labour. Her personality controls how *often* she
rolls positive or negative — a Masochist that gives "positive" almost never is
doing most of the difficulty before any arithmetic runs. The numbers control
how much the survivors are *worth*. Every trust change goes through them,
world events included.

### Custom favorability speed: −500% to +500%

A new toggle and slider that governs exactly one thing: how fast trust rises
and falls. It does **not** override the difficulty feature as a whole — her
judgement, her testing and her temper all still come from your chosen tier.
Only the trust gain/loss percentages are replaced.

When it is on, it owns those numbers outright — even at 0%, which deliberately
means "no modifiers either way": vanilla trust speed on any tier, Masochist
included. Positive amplifies (+100% doubles every change, +500% is six times).
Negative dampens by division and never flips a change's direction (−100% is
half speed, −500% is one sixth). The same round-up-to-1 rule applies, so even
−500% crawls rather than freezes. The panel shows the resulting multiplier
live next to the slider.

### High risk, high reward rebuilt: named tiers instead of a score

It fired on nearly every warm turn, and generously — in testing, a stir-fried
egg landed +8. The cause: the model was choosing the magnitude, and a language
model placing a moment on a −20..+20 number line reaches high. It is bad at
calibrated numbers. It is good at a different question: *is this an egg or an
engagement?*

So that is now the question. She classifies the rare moment worth keeping as a
**core memory** into a named tier — the mod owns every number, and the tier's
fixed weight lands **on top** of the ordinary movement, never scaled by any
difficulty setting or slider:

- **Matters** (±1, every 5 turns) — small real acts of care or small real
  slights: something made just for her, a small gift; a dodged sincere
  question, a thoughtless jab. If it could happen on any pleasant evening, it
  is matters at most.
- **Serious** (±3, every 10 turns) — gestures that cost something, or real
  breaches: a confession said plainly and meant, an apology that owns the
  wrong — or a caught lie, something suspicious done behind her back.
- **Reframing** (±6, every 30 turns) — what changes how she reads the future,
  or you: binding yourself to her out loud *and holding up under her
  questions* — or being caught hunting for keys and exits.
- **Once-ever** (±20, once per level) — the before-and-after kind: total
  acceptance of everything she is, an engagement, a life on the line — or the
  heartbreak she cannot come back from having believed.

Most turns classify as none and pass untouched. **Good and bad moments each
run their own cooldowns** — a gift cannot shield a betrayal, and heartbreak
cannot lock out a proposal. A tier still cooling is downgraded to the largest
one open in that direction, never dropped silently.

### The status strip shows exactly who moved the needle

Every trust change is decomposed live, in the strip and in the log:

    −7 = −2 (game) −2 (masochist) −3 (high risk: serious)

The terms always sum to the total, so there is never a number you cannot
account for. World events are labelled `(game event)` so her reaction to
something you did is never mistaken for a reply's favorability.

### Removed

- **The gem editor.** It only ever edited a local mirror: gems are
  server-backed and the hub reloads them from your account on every load,
  silently wiping the change. Not fixable from the mod's side, so it is gone
  rather than broken.

### Notes

- The developer-cheat trust editor deliberately bypasses the whole pipeline —
  "set trust to exactly 30" means 30 on any difficulty.
- One binary, both stores: Steam and itch/standalone. This release is the
  build I run myself.

---

## 4.1.0 — the developer menu grows up

Built against game version 0.1.46 · Unity 2022.3.62 · Mono · Windows x64

A tooling release: the developer tools now ship, every setting tells you its
own default, and each tab can be put back to those defaults without touching
your API keys.

- **The developer tools ship.** Never disabled in past releases — they were
  not compiled into them at all. Off by default, on their own tab. Item give
  by typed name (with a placeholder fallback), invincibility, and direct
  editing of trust, patience and message count. No other mod required; remove
  any separate invincibility or item-give mods you have.
- **Gifts no longer vanish in the atrium.** A base-game bug: the hub's
  receive-item routine is empty, so gifts were parsed, announced, and dropped.
  The mod supplies the missing behaviour, including the recent-pickup window
  that prevents a duplicate delivery.
- **Every setting shows its own default**, read live from the settings
  themselves and hidden while your value matches — so the panel also shows
  you at a glance what you have changed.
- **"Set to default", per tab**, between Reload and Close. Never touches your
  API configuration: not the text or TTS base URL, key or model, not the
  voice ID, not any per-character voice.
- **Fixed:** a description could pin itself to the panel footer and follow you
  across every tab. **Added:** the live status strip can be hidden from the
  developer tab.

---

Earlier releases are listed in full on the mod's Nexus description page.
