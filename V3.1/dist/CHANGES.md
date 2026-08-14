# V3.1 changes

Released 2026-08-08. Baseline is V3.0.

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
