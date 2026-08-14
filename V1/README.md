# AI2U Custom AI Endpoint

**Play *AI2U — With you til the end* on your own AI, with your own model, at your own cost.**

Point the game at OpenRouter, OpenAI, LinkAPI, or a model running on your own PC.
You pick the model. You see exactly what you spend. And she remembers your whole
conversation instead of quietly forgetting the first half of it.

Everything is configured **inside the game's own settings screen**, with Test
buttons that turn green or red so you know it works before you talk to anyone.

---

## Why this exists

The game already has a "Use Personal OpenAI API Key" option. It does not do what
it sounds like.

Reading the decompiled game code, that setting does **not** call OpenAI. It puts
your key in an `x-gpt-key` header and forwards it to the AI2U server, which still
picks the model and still builds the prompt. That is why there is no model
dropdown anywhere in the game — you were never choosing the model.

This mod cuts that out. Your endpoint, your model, your prompt, direct.

---

## What actually changes

### You choose the model

Any OpenAI-compatible endpoint. Gemini, Claude, GPT, a local Llama — if it speaks
the `chat/completions` format, it works.

### She stops forgetting

The stock game trims conversation history to **3072 tokens** and silently deletes
her oldest messages. That is why a vanilla character loses the thread of a long
talk. Default here is **500,000**, so the whole conversation stays intact.

### She stops ignoring you

This is the part that matters most, and the least obvious.

When you say *"follow me"*, the model has to answer with an action name the game
engine recognises — exactly. If it invents `follow_player` when the engine expects
`following_player`, the engine discards it silently. She says "sure, let's go!"
and then stands perfectly still. It looks like she is ignoring you.

So the mod reads the **live** list of valid actions, locations, animations and
expressions straight out of the running game, hands that to the model as a hard
whitelist, and repairs near-misses before the engine ever sees them. It follows
whichever level and character you are in, because it is read at runtime rather
than hardcoded.

### Better voice, if you want it

The built-in local voice works out of the box and costs nothing. Plug in a key
from xAI (Grok), ElevenLabs, or any OpenAI-compatible speech endpoint and she
speaks with that instead.

---

## What I personally use

> **Text — OpenRouter with `google/gemini-3.6-flash`.**
> Fast, cheap, and it holds the required JSON format without drifting. This is
> what I run.
>
> **Voice — Grok TTS from xAI, voice `iris`.**
> This is what I run day to day. It sounds great and it is the cheaper of the two.
>
> **ElevenLabs is noticeably better than Grok** — more natural, better emotional
> range. It also costs meaningfully more. If voice quality is what you care about
> most, use ElevenLabs and accept the bill. If you want great-sounding voice at a
> sane price, Grok `iris` is the sweet spot.

**Tested status, stated plainly:** I have personally tested **Grok** and
**ElevenLabs** for voice. The third voice option is the generic
OpenAI-compatible `/v1/audio/speech` shape — it is implemented and should work
with any provider using that standard, but I have not tested it myself. For
**text**, any OpenAI-compatible endpoint works; I use OpenRouter.

---

## Requirements

- *AI2U — With you til the end* on PC (built against **0.1.46**, Unity 2022.3.62, Mono)
- **BepInEx 5, x64** — a free mod loader, downloaded separately (see below)
- An API key from any OpenAI-compatible provider, with a little credit on it

**BepInEx is not bundled with this mod.** It is a separate project with its own
licence, so you download it yourself. It is one extra step and takes a minute.

---

## Install

### 1. Install BepInEx 5 (x64)

Download from <https://github.com/BepInEx/BepInEx/releases> — you want the file
named like **`BepInEx_x64_5.4.23.2.zip`**. Not the x86 build, not BepInEx 6.

Unzip it into your game folder — the one containing
`AI2U - With you til the end.exe`. When done, sitting next to that .exe you
should see:

```
BepInEx/
winhttp.dll
doorstop_config.ini
```

If `winhttp.dll` is not next to the .exe, BepInEx will not load. That is the
single most common install mistake.

### 2. Run the game once, then close it

This lets BepInEx create its folders.

### 3. Copy the mod in

From this package's `dist/` folder, copy the `BepInEx` folder into your game
folder, merging when Windows asks. That gives you:

```
BepInEx/plugins/AI2UCustomAI.dll
BepInEx/config/canak.ai2u.customai.cfg
```

### 4. Set it up in-game

Launch, then open **Settings → AI Setup**. Fill in three fields:

| Field | Value |
|---|---|
| **Base URL** | `https://openrouter.ai/api/v1` |
| **API key** | from <https://openrouter.ai/keys> — starts with `sk-or-v1-` |
| **Model** | `google/gemini-3.6-flash` |

Press **Test**. Green means you are done. Press **Apply** to save.

Leave the provider dropdown alone — with the mod installed it reads
`MODDED: <your model>`, which is the correct setting.

You will need a few dollars of credit on your provider account.

---

## Text providers

Any OpenAI-compatible endpoint works. Known-good:

| Provider | Base URL | Notes |
|---|---|---|
| **OpenRouter** | `https://openrouter.ai/api/v1` | What I use. Hundreds of models behind one key. |
| **OpenAI** | `https://api.openai.com/v1` | Works directly, unlike the game's built-in option. |
| **LinkAPI** | `https://api.linkapi.ai/v1` | Works; Test auto-corrects the URL shape. |
| **Local** | `http://localhost:1234/v1` | LM Studio, llama.cpp, Ollama. Free. Leave the key blank. |

Models I have run successfully:

| Model | Notes |
|---|---|
| `google/gemini-3.6-flash` | **My daily driver.** Fast, cheap, format-reliable. |
| `google/gemini-3.1-pro-preview` | Smarter, noticeably pricier. |
| `anthropic/claude-sonnet-5` | Strong at roleplay and staying in character. |
| Local small models | Free, but they fumble the JSON more — raise `RetriesOnBadJson` to 3–4. |

If your provider needs a slightly different URL shape, the **Test** button tries
the common variants and corrects the setting for you automatically.

---

## Optional: a better voice

The built-in local voice is functional but rough. In **AI Setup**, below the voice
dropdown, there are four more fields:

- **Voice API URL**
- **Voice API key** — a *separate* key from your text one
- **Voice model**
- **Voice**

Press **Test Voice**. It synthesizes a line and plays it out loud, so you hear the
voice before committing to it. Then set the voice dropdown to
`MODDED: <voice> (cloud)` to turn it on.

The request shape is **detected from the URL** — you never configure which
provider is which.

| Provider | Base URL | Voice field | Tested |
|---|---|---|---|
| **xAI (Grok)** | `https://api.x.ai/v1` | `iris` — 26 built-in voices | **Yes — what I use** |
| **ElevenLabs** | `https://api.elevenlabs.io/v1` | a voice ID from your Voices page | **Yes — better, pricier** |
| **OpenAI-compatible** | `https://api.openai.com/v1` | that provider's voice name | Implemented, untested |

xAI's 26 voices: `carina`, `zagan`, `helix`, `orion`, `luna`, `iris`, `altair`,
`zenith`, `perseus`, `helios`, `lux`, `kepler`, `rigel`, `cosmo`, `celeste`,
`ursa`, `sirius`, `lumen`, `castor`, `naksh`, `atlas`, `ara`, `eve`, `leo`,
`rex`, `sal`.

**Voice is billed separately, per character of dialogue**, by whichever provider
you use. That is a second meter running alongside your text provider. If a
request fails, that line quietly falls back to the local voice rather than going
silent.

### ElevenLabs on a free account

Voice **Library** voices require a **paid** ElevenLabs plan when used through the
API. On the free tier they return `402` and the Test button says **"Paid plan"** —
that is a billing limit, not a broken mod.

Their own built-in voices work free: **Sarah** `EXAVITQu4vr4xnSDxMaL` or **Lily**
`pFZP5JQG7iQjIQuC4Bku`.

Use one of their model names: `eleven_multilingual_v2`, `eleven_turbo_v2_5`, or
`eleven_v3`.

### Turning voice off to stop billing

Press **F8** in-game, or set the voice dropdown back to
`MODDED: local voice (free)`.

- **Off** — she keeps talking using the free on-device voice. Cloud billing stops
  the instant you switch.
- **On** — the good voice is back, and so is the meter.

Your choice is saved and survives a restart.

### Loudness

Providers hand back wildly different volume — **Grok's output is noticeably
quieter than ElevenLabs'**. `NormalizeLoudness` is on by default and evens that
out by scaling each clip to a consistent level. Want her louder or quieter
overall? Set `Volume` in the config (`1.0` is neutral, `1.5` is half again as
loud).

---

## Confirming it works

Talk to any character, then open `BepInEx/LogOutput.log` in your game folder:

```
[Info: AI2U Custom AI Endpoint] Patched. Endpoint: https://openrouter.ai/api/v1  Model: google/gemini-3.6-flash
[Info: AI2U Custom AI Endpoint] --> https://openrouter.ai/api/v1/chat/completions
[Info: AI2U Custom AI Endpoint] billing: prompt=1874 completion=312 cost=$0.007492 | session: 3 calls $0.0212
[Info: AI2U Custom AI Endpoint] Voice level scaled x2.14 (normalised)
```

That `billing:` line is your **real** spend, reported by the provider — not an
estimate.

---

## What it costs

Roughly **a cent or two per exchange** on Gemini 3.6 Flash. A long session runs
well under a dollar.

If it looks like nothing is being spent, check your provider's **Activity** page
rather than the credit balance — a few cents will not visibly move a balance
displayed to two decimal places.

Voice, if enabled, is billed separately by the voice provider per character
spoken. Grok is the cheaper of the two I have tested; ElevenLabs costs more and
sounds better.

---

## Memory: how much she remembers

The stock game keeps only 3072 tokens of conversation and silently drops her
oldest messages. This mod raises that:

```ini
[Memory]
HistoryMaxTokens = 500000
```

That is a **ceiling, not a reservation** — cost only grows as history actually
fills, so short conversations cost exactly what they did before. Keep it under
your model's context window (Gemini 3.6 Flash allows 1,048,576). Lower it if you
want a hard cap on how expensive a very long session can get.

Do not confuse this with `MaxTokens` under `[Sampling]`, which is the length of a
**single reply**. They are unrelated.

---

## Troubleshooting

**The Test button is red.** It names the problem: `Bad key` (401), `No credit`
(402), `Bad URL` (404), `Rate limited` (429), `No connection`, or `Bad model?` if
the endpoint answered but rejected the model name.

**`Patched.` never appears in the log.** BepInEx is not loading. Confirm
`winhttp.dll` sits next to the .exe and that you installed the **x64** build.

**She says nothing at all.** Search `LogOutput.log` for `error`.

**Replies are empty or malformed.** Your model is likely too small for the schema.
Raise `RetriesOnBadJson`. Do **not** drop `MaxTokens` below ~1500 on a reasoning
model — it spends hidden thinking tokens before writing anything, so a tight
budget yields an empty reply.

**She agrees to do things but never does them.** Ensure
`ClampToAllowedValues = true`. This is the setting that keeps her actions legal.

**Test Voice says "Paid plan".** ElevenLabs free-tier limit — see above.

**The voice is too quiet.** Keep `NormalizeLoudness = true` and raise `Volume`.

**The game updated and the mod vanished.** The launcher replaces game files.
Re-copy `dist/BepInEx/plugins/AI2UCustomAI.dll`; your config is preserved.
Launching the .exe directly instead of through the launcher avoids this. If
re-copying is not enough — it loads but errors, or never logs `Patched.` — the
game's code moved and the mod needs a rebuild. See **UPDATING.md**.

---

## Building from source

Needs only the .NET Framework compiler that ships with Windows. No Visual Studio,
no SDK.

```bash
bash plugin/build.sh
```

Edit the paths at the top of `build.sh` if your game is not at `C:\AI2U\Game`. It
compiles against the game's own assemblies and refuses to install while the game
is running.

| File | Role |
|---|---|
| `AI2UCustomAI.cs` | Plugin entry, config, request/response bridge, value repair |
| `GameVocab.cs` | Reads valid actions/locations/expressions from the live game |
| `GrokTts.cs` | Cloud voice synthesis — xAI / ElevenLabs / OpenAI shapes, loudness |
| `ModUI.cs` | The in-game AI Setup page: fields, Test buttons, voice switch |
| `NewInput.cs` | Hotkey support across both Unity input backends |

---

## Notes

This routes dialogue away from the developers' metered servers, so treat it as a
personal-use tool.

**Keep your API keys private.** They are billable, and anyone holding the file can
spend your credit. Never share your filled-in `canak.ai2u.customai.cfg`.

Unofficial and unaffiliated with AlterStaff.
