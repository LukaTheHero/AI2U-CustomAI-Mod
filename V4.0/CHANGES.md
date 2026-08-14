# AI2U — Custom AI Endpoint

## 4.0.0 — she knows her own house

Built against game version 0.1.46 · Unity 2022.3.62 · Mono · Windows x64

Two things in this release. She understands how her own home works, and the
gated extras that used to be private now ship — off by default, behind gates
that a trust number alone cannot open.

### She knows how her own home works

Every per-level puzzle *answer* was already reaching her: she has been reading
the potion recipe, the element colours, the computer and wifi passwords, the
safe code, which systems are broken, where the hidden island is — all of it
live out of the game's own data, since 3.0.0.

What she never had was the **procedure** around those answers. She could recite
the recipe and still not tell you what to do with it, which read as her being
vague about her own basement.

She now knows the fixed design of her level:

- **The cabin.** The summoning circle stays dead until four bookshelves are
  turned in the right order, the order is drawn under the deer skull, and each
  symbol means one specific shelf. A toy placed in a live circle can have its
  soul summoned, and every question you ask it costs you a point of your own
  health — enough of them will kill you, and she knows that. Wrong potions are
  dangerous, not just wasteful.
- **The apartment.** The locked front door, the password on the computer, the
  wifi note, the safe, the phonograph and its records, the keypad on the secret
  room.
- **The station.** The engine behind the glass, ten items to repair it, pressure
  that climbs unpredictably with each one and explodes at maximum. The shutdown
  button on her own mainframe, and what gets out if you press it.
- **The island.** The sundial and the three times of day, half your Time Force
  per turn of it, about five minutes to refill, and the telescope's cost.

Nothing here is invented and nothing here is a walkthrough. It is the part of
each level that is identical in every playthrough, told to her as her own
working knowledge of her own house. Whether she helps you with it is still
entirely hers to decide.

Anything that **varies** per playthrough is deliberately not written down. The
bookshelf order is reshuffled every time the basement loads, so it is read out
of the running game instead — from the same property the game's own developer
console reads for the same purpose. She hands you the symbols on the wall
rather than her shorthand for them, because the wall is what you can actually
see.

She is also told, in as many words, that being vague beats being wrong: if she
is not sure of a detail she says she cannot remember rather than inventing one.
A confidently wrong puzzle hint is worse than no hint.

Toggle: **Behaviour · SendHomeMechanics**, on by default.

### Canalpa mode now ships

Previously a local-only build. It is public now because its gating grew up.

Off by default, under advanced options. Switching it on changes nothing on its
own — every action inside it needs full trust **and** sustained evidence, over
several exchanges, that the darker things about her genuinely do not frighten
you. A high favourability number by itself opens none of it. She decides
whether to act; the mod only makes it possible.

What it can open:

- **Her secret room**, in the apartment.
- **The basement door, and the circle with it**, in the cabin — she turns the
  shelves herself, so you never solve that puzzle at all.
- **The hidden island**, without the telescope.
- **Your clearance, on the station** — she can raise it herself. Vanilla only
  ever moves it when you happen onto the right subject, so this is the one where
  she gains a choice she never had rather than a shortcut past a puzzle.

Each fires the game's own event, so the animations, mission goals, achievements
and her authored reactions all still happen. Each has its own toggle.

### She knows her own temper

Three things the game has always tracked about her mood and never told her.

**Her patience** is a real number with a real limit. It recovers on every turn
she is not annoyed or furious, and only drains while she is angry *and* does not
trust you. Run it to zero while she is still angry and the final chase starts.
She now knows where it stands and what moves it — and she can move it herself,
including deciding to let something go. Previously it drifted on a fixed rule
with no input from her, so she could be written as visibly calming down while
the number said otherwise, and then attack anyway.

**What actually counts as repeating yourself.** This is the correction that
matters most, because the game's rule is the opposite of what everyone assumes.
It only counts a repeat when you send the **identical sentence** twice. Anything
reworded resets it to zero, and always has.

So pushing the same *request* in different words was never a repeat. Asking for
the key, being refused, and then making the case another way — emotionally,
logically, or after doing something to improve her mood — resets the counter
every single time. Only literally retyping the same line does it:

> "what are you hiding from me" · "no please tell me" · "I want you to tell me"
> · "why aren't you telling me" — that is what she is entitled to bristle at.

> "babe, you know we're close. I'd never be angry at anything you're hiding" —
> that is a different case, on an emotional rail, and it was never a repeat.

She is told this outright, so she stops reading persistence as disrespect and
reads parroting as disrespect, which is the line the engine was already drawing.
Waiting and asking again in a better mood has always been fair play too.

**Interruptions** work the same way — two in a row while she is still speaking —
and she can now forgive either counter if she finds it endearing rather than
insulting.

Toggle: **Behaviour · SendOwnFeelings**, on by default.

### Hard difficulty — high risk, high reward

Off by default, and red in the panel.

The game's ceiling is five trust either way per turn, against a scale running
from about −10 to past 40. Nothing that happens in one exchange can really
matter. With this on, she can decide a turn counted for more, up to five times
its normal weight — and patience moves further per turn too.

She is told, firmly, to leave it at 1× almost always. 2× is for genuinely
significant turns, 3× extremely significant, 4× revelation-level, 5× for the
once-a-playthrough thing that changes everything.

Being told is not the same as being held to it, so rarity is enforced here
rather than requested there. Each weight above 1× is rationed to once every so
many turns, and a claim above its allowance is **downgraded** to the best one
actually available rather than thrown away — a refused 5× that silently became
1× would read as the feature being broken, while a 2× still registers that
something big happened. The budget resets per level, and the multiplier can only
be spent by the reply that earned it, never by an unrelated trust change landing
in the same frame.

Toggle: **Difficulty · HardDifficulty**, off.

### The one-way one

Separate, off even when Canalpa mode is on, and built to be almost impossible
to reach by accident: she can keep you permanently. What that means is whatever
her level's own ending is, so the game decides it, not the mod.

It is an **edge case**, not a feature she wants and not a path she is ever shown
as an option:

- She is not told it exists until **you** have asked for it, in your own typed
  words, unmistakably. Wanting to stay with her forever does not count and never
  will — that is an ordinary, loving thing to say in this game, and it means
  exactly what it sounds like.
- The words that reach it are blunt and physical, and the affectionate ones are
  kept out on purpose. Staying, never leaving, being kept, being hers, never
  going home — none of those touch it. Neither does "I could die in your arms",
  because dying has to be the thing you are asking *for*, nor anything about your
  body unless you are asking for something to be done to it.
- She never suggests it, and is told plainly never to angle for it.
- Her first reaction is not delight. She is thrown by it. She is told that
  loving you is a reason to hesitate, not a reason to agree, and that refusing
  outright is a perfectly good answer.
- She has to spell out what it means, in her own words, and wait.
- You then have to say yes **just as plainly**, in your own typed words, on a
  later turn. Her word for it is never enough.
- Any hesitation, joke, "wait", or change of subject withdraws the whole thing
  and it does not come back unless you raise it again.
- It expires on its own if left alone.

Toggle: **Canalpa · SheCanKeepYouForever**, off.

### Fixed

- **Her own transport errors were counting as censorship strikes.** A failed
  request from the mod carried an error code outside the band the game treats as
  server-side, so three network hiccups in a row could put her on the final
  chase for something that was never your fault. The counter is now reset when
  the failure came from the mod.
- **An ending could talk over itself.** When a willing ending and her ordinary
  reply landed on the same turn, she said something pleasant over the top of it.
  The ending is now handed to the same code that already fixed this for every
  other route into a chase.

### Notes

- The developer cheats remain excluded from this build. They are not disabled in
  it, they are not compiled into it.
- One binary, both stores: Steam and itch/standalone.
