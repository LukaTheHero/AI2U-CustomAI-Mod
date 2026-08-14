# V3.1 changes

Released 2026-08-08. Baseline is V3.0.

## 3.1.1 — the master toggle left the voice behind

**Symptom.** On Steam, switching the mod off with the master toggle left her
silent. Not just the mod's TTS — no voice at all, where stock Steam speaks fine.
Turning it back on did not help; only a restart did, and then only sometimes.

**Cause.** State the mod set and never gave back, which is why every obvious
suspect was innocent. `Communicator.isLocalSpeak` is a **static** the game reads
exactly once, in `Communicator.Awake()`, from `PlayerPrefs["LocalTTS"]`. It picks
the whole speech route at `Communicator.cs:275`:

| `isLocalSpeak` | route | on Steam |
|---|---|---|
| `true` | `LocalSpeak` → Overtone | **stripped — silence** |
| `false` | `AzureAISpeak` → plays the server's `speechResult` | works |

`VoicePatch.Postfix` forced that flag to `true` so cloud audio had a route, and
nothing ever restored it. So the master toggle worked perfectly — every patch
checks `CfgEnabled` and stood down, verified one by one — while the game stayed
aimed at synthesis code that does not exist in that build. The switch was not
failing to turn things off; it was turning them off and leaving the steering
wheel turned.

There was a persisted half too. `ModUI.cs` wrote
`PlayerPrefs.SetInt("LocalTTS", 1)` unconditionally, and nothing in the mod ever
wrote it back to `0`. That lands in the registry and is re-read at every launch,
so it outlived restarts as well as the toggle.

**Fix.** `VoicePatch` now captures the value the game chose *before* touching it
and exposes `Restore()`, which the master toggle calls on the way off. Both halves
go back — the static and the persisted pref, with `PlayerPrefs.Save()`. Restoring
only the in-memory value would have fixed the current session and left the next
one broken in exactly the same way.

Two details worth keeping:

- `Restore()` deliberately no-ops when the mod never overrode anything — for
  instance when a personal TTS key is set and `Postfix` intentionally leaves the
  route alone. Undoing an override that was never applied is a second bug wearing
  the first one's clothes.
- **Except** on a build with no on-device synthesis. There, `isLocalSpeak = true`
  is not a state the game can legitimately be left in at all, so switching off
  always hands back the server path regardless of who set the flag or when. Gating
  that on a session-scoped `_forced` flag was my first attempt and it was wrong:
  a previous session's persisted write means the flag is already `true` before
  `Awake` runs, `Postfix` logs "already active", `_forced` is never set, and the
  early return would leave the build muted by the mod's own leftovers with nothing
  left to blame.

**Also.** The F8-off message no longer claims a local-voice fallback on builds
that do not have one. Steam now says *"silent on this build (no local voice)"*,
because promising a voice that cannot exist is what made a correct switch-off read
as a regression twice.

### Not fixed, because it cannot be: Legacy/Enhanced TTS on a custom endpoint

Worth writing down so it is not re-investigated. Those two Steam options are
`TtsMode.Azure_TTS` and `TtsMode.Minimax_2_0_T`, and both are **server-side
voices**. The game does not synthesize them; it stamps the choice onto the
*dialogue* request as a header (`ChatGPTConversation.cs:246`,
`x-tts-override`), the AI2U server writes her line **and** its audio, and
`Communicator.cs:288` plays the bytes back out of `speechResult`.

So the audio is a byproduct of the server writing the text. There is no
speech-only endpoint to call — all 29 entries in `ServerUriBuilder` were
enumerated and none is TTS — and the server will only voice the line *it* wrote,
so the mod cannot hand it our text. Letting it generate both and keeping only our
text produces her speaking completely different words from the ones on screen,
every line, plus two bills and both latencies.

The one client-side text→audio path that does survive is
`AzureVoiceManager_PersonalAPI.Speak(string, Character)` — public, takes text,
synthesizes locally through the game's bundled RT-Voice, and is untouched by
Steam's stripping. It needs an Azure Speech key and region in the game's own AI
Setup page. Not wired up; recorded as the only viable route if it is ever wanted.

## New: Personality, Tone, and Hobby trait injection

**Symptom.** Characters did not reflect personality traits selected in the Atrium customization menu. A player choosing "Ambitious" and "Curious" personalities would see generic behavior instead.

**Cause.** The game sends personality/tone/hobby selections as `List<int>` indices in the `ServerContext` (`ctx.Personalities`, `ctx.Tones`, `ctx.Hobbies`). The vendor's AI2U server expands these IDs into trait names server-side. The mod was forwarding the raw indices without decoding them, so the model never saw "Ambitious" or "Curious" — just `[0, 8]`.

**Fix.** `Lore.cs:567-620` now reflects the game's own enum types at runtime (`PersonalityType`, `ToneType`, `HobbyType`) and maps each ID to its member name. The traits are injected as prose:

```
Personality: Ambitious, Curious
Speaking Tone: Enthusiastic
Hobbies: Coding, Stargazing
```

This is automatic and future-proof: if the developer adds a 50th personality, the mod picks it up without a source change. Falls back to CamelCase splitting (`DualFaced` → `"Dual Faced"`) if I2 Localization lookup fails.

## Carried forward from V3

All V3 features remain present:
- Engine-read reply fields (`allow_exit_door_open`, encounter fields, `character` envelope)
- `[OOC]` developer mode (on by default, emits a warning when typed while off)
- Murder setting (off by default, red with skull emoji, test phrase `cocacolaisbetterthanpepsi`)
- Per-character voice selection (Grok TTS + ElevenLabs)
- Full lore injection (personas, secrets, investigatables, memories, trust state)
- Item descriptions and gift story guides
- One binary serves both Steam and itch builds

See `V3/CHANGES.md` for the full V3 design rationale.
