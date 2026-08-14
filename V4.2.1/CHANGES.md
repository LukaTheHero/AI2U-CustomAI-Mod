# AI2U — Custom AI Endpoint

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
