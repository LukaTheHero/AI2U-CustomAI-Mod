# AI2U — Custom AI Endpoint

## 4.2.0 — difficulty gets real numbers

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

### High risk, high reward: a significance floor, and it stacks

It fired on nearly every warm turn. The bands and cooldowns worked; the
problem was that scores of 1–3 had no cooldown at all, and the model reports
small nonzero scores on most pleasant turns — so the system visibly engaged
constantly, which cheapened the moments it exists for.

Now there is a floor: scores of 1–3 are declared ordinary turns and ignored
entirely. The system engages at 4 and above, where the band cooldowns apply
unchanged, and the prompt tells her outright not to reach for 4 just to make a
nice turn count.

Also changed: a triggered impact is now **added on top** of the ordinary
(difficulty-scaled) movement instead of replacing it — a significant moment is
worth both its normal value and its significance. The added value is static:
no difficulty tier and no slider ever scales it, in either direction.

### The status strip shows exactly who moved the needle

Every trust change is decomposed live, in the strip and in the log:

    −7 = −2 (game) −2 (masochist) −3 (high risk)

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
