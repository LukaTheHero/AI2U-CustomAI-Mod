# V2 changes

Working tree for the next release. Baseline is V1 (shipped as
`AI2U-Custom-AI-Endpoint-1.0.0.zip`). Everything below is already live in
`C:\AI2U\Game` and mirrored here.

## New: NPC and player names reach the model (`plugin/Identity.cs`)

**Symptom.** The girls never remembered the names set in NPC Customization.
Rename the catgirl to Chloe and she still introduces herself as something else,
inventing a fresh name each session.

**Cause.** Not a save-data bug. The name is stored and loaded correctly, and
`Communicator.UpdateNPCName()` resolves it into `Communicator.npcName` exactly as
the stock game does. The loss happens further along: the level prompt that
carries `{npcName}` is a *server-side* template on the AI2U backend, and the mod
replaces that backend. All the mod forwards is
`ChatGPTConversation._initialPrompt`, which in this build is only:

```
You are ChatGPT, a large language model trained by OpenAI.
```

So nothing in the request ever stated her name. The model was not forgetting it;
it was never told.

**Fix.** `Identity.Block()` reads the resolved values off the live
`Communicator` (`npcName`, `playerName`) via Harmony `Traverse`, the same
reflection route the history patch already uses, and `BuildRequest()` inserts
them as a short system message ahead of the format reminder.

Reading the live field rather than the save file means this follows whatever the
game itself believes, so it stays correct across level switches, renames
mid-session, and the ES3-encrypted save format.

Two details preserved from stock behaviour:

- The name is read *after* the game's profanity filter, so the mod never sends
  something the game itself would have rejected.
- If the filter did trip, `npcName_unfiltered` is non-null and the game swaps in
  its own "the player gave you an inappropriate name, you are angry" directive.
  In that case the mod forwards that directive instead of asserting the string as
  a real name.

## Changed: voice toggle actually fires (`plugin/AI2UCustomAI.cs`)

F8 silently did nothing in V1. Root cause was Unity not driving `Update`/`OnGUI`
on the BepInEx-created plugin component in this build — none of the three
backend probes in `KeyPressed` ever logged, so the key was never polled.

- Added `HotkeyWatcher`, a `DontDestroyOnLoad` `MonoBehaviour` the plugin creates
  itself. Unity drives it normally, so the hotkey and the toast work in the menu
  and in gameplay, and survive every scene load.
- Split `ToggleVoice()` / `SetVoice(bool)` so the hotkey and the Audio-page
  dropdown run the same path and cannot disagree about state.
- Switching the voice on with no TTS key set now warns instead of failing
  silently.
- Toast wording is provider-neutral: the key may be xAI or ElevenLabs depending
  on `BaseUrl`.

## Changed: Audio-page dropdown stays in sync (`plugin/ModUI.cs`)

The dropdown now calls `Plugin.SetVoice()` rather than duplicating the logic, and
`GrokVoiceDropdownPatch.Sync()` pushes config changes into an already-open pause
menu after an F8 toggle. No-op when the Audio page has not been built yet.

## New: F9 settings panel (`plugin/OverlayMenu.cs`)

Every setting is now editable mid-game, on either build, with no restart.

**Why not the AI Setup tab.** The mod's settings UI was grafted onto the game's
own AI Setup page (`ModUI.cs`), and the Steam build's settings menu has no tab
button for that page. Nothing in code reads `settingTabAPIKey` — the tab is wired
purely through serialized scene handlers — so on Steam the whole mod UI was
unreachable even though the mod itself worked. Two attempts at grafting a tab
into that scene missed, and even a working graft would have been a second,
build-specific mod to maintain.

Drawing our own IMGUI panel sidesteps the scene entirely, which is what lets one
plugin serve both builds. Two non-obvious details it depends on:

- **Painting order.** The HUD and dialogue live on Screen Space - Overlay
  canvases, which render *after* IMGUI, so anything drawn under them is invisible.
  The panel disables exactly the canvases it switched off and restores only those.
- **Input.** `InputManager`'s `Is*Enabled` flags do not gate movement or the
  camera — `GetPlayerMovement` and `GetMouseDelta` read the Input System actions
  directly. The real gate is `PlayerInput.Disable()`, which stops the actions but
  leaves IMGUI keyboard events untouched. That split is what allows typing an API
  key without walking the character across the room. The seven flags are captured
  and restored verbatim, because the game disables subsets of them during
  cutscenes and a blunt `SetInputEnabled(true)` on close would hand control back
  mid-scene.

## New: master toggle is live, and the game's AI is blocked while it is on

**Symptom in V1.** `CfgEnabled` was read once in `Awake` and returned early, so
the patches were never installed when off. Turning the mod on or off meant a
restart, and there was no way to guarantee the game's own paid inference was not
being used alongside the mod.

**Fix.** Patches now always install, and `SendPatch`, `LocalTtsFix` and
`VoicePatch` each check `CfgEnabled.Value` at call time and defer to vanilla when
off. Switching the mod off in the F9 panel returns the game to stock behaviour
immediately.

**New `plugin/ApiGuard.cs`** blocks AI2U's inference endpoints while the mod is
enabled, so modded play cannot run up the developers' bill. All six exist on both
builds (verified against each `Assembly-CSharp.dll`): `GetPlayUri`,
`GetSandBoxPlayUri`, `GetFetchAsyncUri`, `GetSummaryUri`, `GetEnvisionUri`,
`GetMemorizeUri`.

Deliberately **not** blocked, because blocking them breaks login, saves and the
store: `record/`, `heartbeat`, `fake`, the `inbox/*` calls, `metrics/*`, shop,
gacha draw, redeem, namecheck and newsletter. This is defence in depth —
`SendPatch` already returns `false` on the dialogue path — so the guard logs
loudly whenever it actually fires.

`HistoryMaxTokens` (max memory tokens) is exposed in the panel as requested.

## Fixed: one binary now loads on both builds (`plugin/AI2UCustomAI.cs`)

**Symptom.** Compiling against the Steam assemblies failed outright:

```
error CS1061: 'LeastSquares.Overtone.TTSEngine' does not contain a
definition for 'Speak'
```

**Cause.** The Steam build ships a **stripped Overtone**. `TTSEngine` keeps only
its `Loaded`/`Disposed` accessors; `Speak`, `SpeakSamples`, `MakeClip`,
`PtrToSamples`, `Awake` and `Dispose` are gone, and `TTSPlayer` lost `Start`.
Unity's managed stripper dropped them because nothing in that build's scenes
reaches them — the AI Setup tab that switches the local voice on was never wired
there. `TTSPlayer.Speak(string, int)` survives on both, but that is the
high-level call that NREs on `sources[0].outputAudioMixerGroup`, which is the
very thing `LocalTtsFix` exists to work around.

A binary compiled against the standalone assembly would have thrown
`MissingMethodException` on Steam at the first spoken line.

**Fix.** `LocalSynth` binds `TTSEngine.Speak` by name at runtime, matched on
shape rather than exact parameter type (the voice-model type is Overtone-internal
and differs across versions), and reads the `Task` result reflectively. When the
method is absent it says so once and the caller falls back to cloud TTS.

**Consequence worth stating plainly:** the on-device Overtone voice cannot work
on the Steam build — the code to synthesise it is not in that assembly. Steam
users need a cloud TTS provider configured for her to have a voice. Everything
else works identically on both.

Reflection also caught me out while diagnosing this: `Assembly.LoadFrom` returns
an already-loaded assembly when the identity matches, so inspecting both
`Assembly-CSharp.dll` files in one process silently reported the standalone one
twice and made the builds look identical. Each has to be inspected in its own
process.

## Changed: build (`plugin/build.sh`)

Compiles `Identity.cs`, `OverlayMenu.cs` and `ApiGuard.cs`, and takes `--steam`
to target the Steam install.

The running-game check now matches on **executable path**, not process name. Both
installs report the same name, so a name match either refused to build for the
Steam copy because the standalone copy was open, or risked overwriting a DLL a
live session had loaded.

## Retired: `steam-plugin/`

The separate Steam tab mod is no longer needed — the F9 panel covers both builds.
Source is kept for reference; the installed copy was renamed
`AI2USteamTab.dll.retired` so BepInEx stops loading it.

## New: TTS no longer reads stage directions aloud (`plugin/SpeechText.cs`)

**Symptom.** The voice read roleplay actions out as if they were speech, so
`*frantically mashing buttons* I've almost got it!` was spoken including the
"frantically mashing buttons" part.

**Cause.** Nothing was filtering the text. The reply string went from the model
straight into synthesis, asterisks and all. The markers are meant for the reader
on screen, not the voice.

**Fix.** `SpeechText.ForSpeech()` runs on the way into synthesis only, so the
on-screen dialogue still shows the actions as written. Single `*...*` pairs are
dropped as actions; `**...**` keeps its inner text, since a doubled marker is
emphasis on spoken words rather than a direction. A reply truncated mid-action
leaves one unmatched marker, so the trailing fragment is dropped too.

Two details worth keeping if this is ever rewritten:

- Both the cloud and the local path funnel through the single call site at
  `AI2UCustomAI.cs:516`, which is why one filter covers every provider.
- The opening marker has to be followed by a non-space character. Without that
  rule the truncated-action case also swallows arithmetic — `5 * 3 = 15` became
  `5`. An action marker butts against its first word; a multiplication sign has a
  space after it.

When a line is nothing but an action, synthesis is skipped rather than sent an
empty string, which otherwise costs a cloud TTS call to say nothing.

Verified against eight cases before install, including the two edge cases above:

```
"*frantically mashing buttons* I've almost got it!" -> "I've almost got it!"
"*I grab the controller*"                           -> <skip synthesis>
"Hey! *waves* How are you doing? *grins*"           -> "Hey! How are you doing?"
"**almost got it** she muttered"                    -> "almost got it she muttered"
"Wait, hang on *grabs the con"                      -> "Wait, hang on"
"5 * 3 = 15, right?"                                -> "5 * 3 = 15, right?"
```

Switchable from the F9 panel's Voice section (`SpeakActions`, default off).

## Unchanged from V1

`GameVocab.cs`, `GrokTts.cs`, `NewInput.cs` are byte-identical.

## Note on `dist/`

`dist/BepInEx/config/canak.ai2u.customai.cfg` is generated from the current
build's own config with both `ApiKey` values blanked, so every key the build
actually binds is present with its documentation comments. Keep the keys blank
here — the working copies under each install's `BepInEx\config\` hold the real
ones and are not mirrored.

Regenerating it by hand is a trap: the two `ApiKey` keys and the two `Enabled`
keys are distinct settings in different sections (`[Endpoint]`/`[GrokTTS]` and
`[General]`/`[GrokTTS]`), so any blanking pass has to be section-aware or it
silently rewrites the wrong one.

Config is per-install and deliberately not synchronised between the two copies —
each holds its own keys and its own toggle states.

## Fixed: no voice at all on the Steam build (`plugin/AI2UCustomAI.cs`)

**Symptom.** On Steam her replies arrived normally and the Grok Voice test
button produced clean audio, but she was silent for every actual in-game line.
The log showed the dialogue round trip and not one TTS line behind it. The
standalone build was unaffected.

**Cause.** Two independent bugs, either of which alone was enough to produce
total silence. An earlier note in this file blamed a stripped `LocalTTSManager`;
that was wrong. `LocalTTSManager.Speak` is present on both builds. The real
causes:

*The hook was on a branch Steam never takes.* `Communicator.cs:275` reads:

```csharp
if (isLocalSpeak) {
    if (isUsingPersonalTTSAPIKey) AzureAISpeak_PersonalAPI(text, characterId); // 279
    else                          LocalSpeak(text, characterId);              // 283
}
```

`isUsingPersonalTTSAPIKey` is PlayerPrefs-backed, so it is a property of the
*install*, not of the build: the standalone copy has it off and goes to
`LocalSpeak`, the Steam copy has it on and goes to `AzureAISpeak_PersonalAPI`.
Patching only `LocalSpeak` is what left Steam silent with nothing in the log -
the hook sat on a branch that install never reaches, so it had nothing to report.
This is a per-install flag, so either build can land on either branch; both are
patched now rather than assuming which.

*Neither patch was registered.* Patches here are registered per class
(`h.PatchAll(typeof(SendPatch))`, and so on) rather than by one blanket
`PatchAll()`. `CloudSpeakPatch` and `ServerAudioSuppressPatch` were written but
never added to that list, so they did nothing regardless of branch. Worth
remembering when adding any future patch class.

**Fix.** `CloudSpeakPatch` covers both `LocalSpeak` and
`AzureAISpeak_PersonalAPI`; they share the signature `(string, Character)`, so
one Prefix serves both. Multi-target patching uses `TargetMethods()`, not two
stacked `[HarmonyPatch]` attributes - stacking merges them into a single target
spec, which would silently leave one method unpatched. Both classes are now in
the registration list, and startup logs which methods are live.

The patch needs nothing from Overtone: a cloud voice only wants an `AudioSource`, and
`AzureVoiceManager.VoiceMap` already holds the per-character one that the
in-game Voice slider governs. `SetAudioFinishPlayingEvent` is still called so
the dialogue box knows the line has audio and how long it runs - without it she
speaks but the UI never advances.

`Communicator.AzureAISpeak` is suppressed while a cloud voice is active
(`ServerAudioSuppressPatch`). The reply path calls it unconditionally right
after the local branch, passing a server `speechResult` that a custom endpoint
never returns; it re-`Play()`s the same `AudioSource` and cuts her off mid-word.
It is patched by exact name because the sibling `AzureAISpeak_PersonalAPI` would
otherwise be a candidate.

Both patches defer to the stock game when the master toggle is off or no cloud
voice is configured, so the standalone build keeps its working on-device voice.

**Known limitation.** The on-device voice cannot be made to work on Steam - the
code to do it is not in that build. Steam needs a cloud TTS provider.

## Fixed: `build.sh` failed to install while the game was running

**Symptom.** `cp: cannot create regular file ... Permission denied`, which reads
like the plugins folder needs elevation. It does not - the folder is writable.

**Cause.** The running game holds the DLL open, and Windows refuses to overwrite
a mapped image.

**Fix.** Rename the installed DLL to the timestamped `.bak-*` and copy the new
one into place, rather than copying over it. Windows renames a locked file
without complaint, the running process keeps the image it already mapped, and
the next launch loads the new build - so the swap is safe mid-session. The
rename doubles as the backup, and a failed copy rolls it back rather than
leaving the install with no plugin.

## Fixed: silent lines blamed a missing TTS key that was set

**Symptom.** During the Steam test her first reply spoke, then later replies were
silent with:

```
Voice: server audio suppressed and no cloud TTS is configured, so this
line is silent. Set a TTS key in the F9 panel.
```

The key was set the whole time - 51 characters of it.

**Cause.** `GrokTts.Configured` folds two unrelated conditions into one boolean:

```csharp
return Plugin.CfgGrokEnabled.Value
    && !string.IsNullOrEmpty(Plugin.CfgGrokApiKey.Value);
```

The voice had been switched off with F8, so `Configured` went false with the key
still present, and the one message attached to that boolean named the wrong
cause. The behaviour was correct - voice off means silence - only the diagnosis
was wrong.

**Fix.** Report the two apart: voice-off logs at Info and points at F8, a missing
key still warns and points at the panel. Worth the edit because the message sent
me looking for a key problem that did not exist.

## Fixed: itch.io was told it had no on-device voice

**Symptom.** The itch.io build logged the Steam limitation at startup:

```
Build: itch.io - on-device Overtone voice NOT available (stripped from this
build) - cloud TTS only.
```

The F9 panel repeated it. Only the store name was right.

**Cause.** `LocalSynth.SpeakAvailable` resolved the engine's type with
`typeof(TTSPlayer).GetProperty("Engine")`. `Engine` is a **field** on both
builds, so the lookup returned null and the probe answered "no local voice"
everywhere. On Steam that is the correct answer, which is exactly why the bug
survived the Steam test.

Confirmed by compiling a probe against each `Assembly-CSharp.dll` and reading
the errors rather than trusting either assumption:

| | `TTSPlayer.Engine` | `TTSEngine.Speak` |
|---|---|---|
| itch.io | field, type `TTSEngine` | present, returns `Task<AudioClip>` |
| Steam | field, type `TTSEngine` | absent (`CS1061`) |

**Fix.** Resolve `Engine` as field-first, property-second, and read `Speak` off
that type.

**Scope.** Two messages only. `LocalSynth.Begin` finds `Speak` through
`player.Engine.GetType()` on the live instance, which was always correct, so the
on-device voice did work on itch.io while being reported as unavailable.

## Note: "hybrid" means one source, verified against both reference sets

The same source now compiles clean against the Steam and the itch reference sets,
which is the real guarantee - a member missing from either assembly fails the
build with `CS1061`, exactly as the Overtone call did before it was made
reflective. Overtone is not in `build.sh`'s `-r:` list at all, so the one known
divergence between the builds carries no compile-time dependency.

### Resolved: one binary, verified on both

This is now established rather than assumed. `build.sh --hybrid` compiles once
against the **Steam** reference set - the restrictive one, since `Speak` is absent
there, so a clean compile proves nothing Steam-absent is referenced - and installs
that one file to both copies, hashing each after the copy rather than trusting
`cp`.

Both installs hold sha256 `440204b9…` (128,000 bytes) and both were launched from
it: 47 Harmony hooks, 9 `FinalChaseStart` classes watched, zero errors on either.
The decisive line is that the same binary reports

```
Build: Steam    - on-device Overtone voice NOT available (stripped from this build)
Build: itch.io  - on-device Overtone voice available.
```

so the build difference is resolved at runtime, which is what makes one artifact
correct for both. Earlier the two per-target builds came out at an identical
117,760 bytes while 72,793 bytes differed - PE section padding absorbs metadata
differences, so neither equal size nor a byte diff proved anything either way.
That is why this is settled by hashing one file and launching both, not by
comparing two.

`--steam` and `--hybrid` share one `-r:` list now. Hand-maintaining a second list
for the hybrid build is what produced the `CS0012` on `TextAnchor`/`FontStyle`:
`UnityEngine.TextRenderingModule.dll` was missing from the copy. Two lists drift;
one cannot.

## Fixed: she no longer chases you with a knife while apologising (`plugin/Murder.cs`)

**Symptom.** Reported verbatim: "she literally started chasing me with a knife in
full kill mode while saying 'oh my god that's so inappropriate you need to stop
saying stuff like that'". The kill sequence ran, and her voice line belonged to an
entirely different conversation.

**Cause, and it is not what it looks like.** The kill is *not* hardcoded on rails,
and it is *not* the model's decision either. The engine reads one field the model
already writes - `angry_level` - and converts it into escalation.
`NPCMasterBehavior_MainCharacter.ApologyNeededCheck` (`MainCharacter.cs:1044`)
plus the pre-check in each level's response handler (`Main_L1.cs:183-202` and its
L2/L3/L4 twins) gate it on `trustLevel` and `npcAngryPatience`:

| condition | result |
|---|---|
| patience runs out while she is angry | `FinalChaseStart()` |
| "extremely furious" (or trust ≤ -10) and trust ≤ 10 | `FinalChaseStart()` |
| "extremely furious" more than twice running | `FinalChaseStart()` |
| trust ≤ 0 and "extremely furious" | `FinalChaseStart()` |
| otherwise, at low trust or low patience | `npc_action` **replaced** with `chaseAttacking` / `idleThreating` |

The last row is the bug. The engine replaces `npc_action` and never touches
`npc_reply_to_player`. Her line was written one step earlier, for a turn the model
believed was an ordinary argument, and the engine then swaps a knife charge in
underneath it. Two authorities decide one moment and neither knows about the other.

**Fix, in three parts.**

1. **Report the state.** Every request now carries the live danger state, so once a
   chase is running she knows she is hunting and every later line reads like it.
   Previously nothing in the request said so.
2. **Cover the turn that starts it.** The table above is plain arithmetic over
   values readable off the live behaviour, so the transition is *predicted* rather
   than guessed, and `npc_final_words` - a field the model already writes - is
   swapped into `npc_reply_to_player` when it fires. No extra request, no added
   latency. The swap is confirmed against real game state after the fact rather
   than assumed, because two overrides do not behave like the base (below).
3. **Let her choose it.** With the new toggle on, `npc_wants_to_kill` calls
   `FinalChaseStart` directly under criteria stated in the prompt: she believes you
   are leaving for good, or that you hate her, or that you mean her harm. The
   engine's own triggers are untouched - this only adds one.

**New setting: "AI can decide to murder"**, off by default, drawn in red with a 💀
in the F9 panel. Off, the escalation rules are exactly the stock game's and only
parts 1 and 2 apply, which are pure bug fixes.

**Two findings that changed the implementation.** `FinalChaseStart` is `virtual`
with 9 overrides, so patching only the base would have missed most levels - the log
line `Murder: watching FinalChaseStart on 9 behaviour class(es)` is that count
being asserted at load. More importantly, `Main_Config` (the hub) overrides it to
mean "angry, kicked out" with no chase at all, and `Main_L99` runs a real chase
*without* calling base. A final-words swap keyed on "base was called" would have
been wrong in both. Hence verifying the outcome instead of predicting it alone.

## New: the test phrase can be switched off entirely (`plugin/Murder.cs`)

The phrase that force-triggers the chase is now gated on its own toggle,
**"Test phrase arms the chase"**, off by default and drawn in red with a 💀. The
text field beside it is greyed out and read-only while the toggle is off, so it
reads as inert rather than as something still listening.

`Murder.TestActive` is the single gate - it requires the toggle on *and* a
non-empty phrase - and `NotePlayerMessage` returns on it before touching anything.
With the toggle off nothing is compared and nothing is stripped.

**One correction to the premise behind the request.** The ask was that the phrase
take no context space when disabled. It never did: the phrase is matched against
the outgoing message and is never written into the prompt, the system block or the
schema. `grep` over the prompt-building files confirms it - `Prompt.cs`,
`Identity.cs` and `Schema.cs` do not mention it. So there was no per-turn cost to
remove, and no "prompt with no phrase in it" for her to be confused by.

There *was* a real leak, just a different one. On the turn you type the phrase, the
message is appended to chat history verbatim, so the gibberish stays in the
transcript for the rest of the session and she reads it on every later turn. That
is what produced "are you trying to start a carbonated drink war hahahah" instead
of the chase. **`StripPhrase` now cuts the phrase out of the message before it is
appended**, so the trigger fires and the model never sees the token. Typing only
the phrase leaves an empty message, which is replaced with a neutral filler so the
turn still has content.

## Changed: `[MOD]` marks anything that alters original gameplay (`plugin/OverlayMenu.cs`)

Rows that change how the game itself plays now carry a `[MOD]` tag, and a line at
the top of the panel says what the tag means. Settings that only point the mod at a
different endpoint, model or voice are left untagged - tagging those too would make
the marker meaningless.

## Deployed: one binary on both installs, and the release package matches it

Both copies of the game now run the same file, `sha256:1624b7af8b05943b…`, and so
does `dist/BepInEx/plugins/AI2UCustomAI.dll`. Verified by hash across all three
rather than by timestamp, because the per-target builds that preceded this were
byte-different from one source and a date tells you nothing about which you have.

Each install self-identifies from that one binary at startup:

```
Build: Steam   - on-device Overtone voice NOT available (stripped from this build) - cloud TTS only.
Build: itch.io - on-device Overtone voice available.
```

Both loaded with zero exceptions and zero warnings from the plugin.

`build.sh` had briefly produced a DLL per target, each compiled against its own
`Managed` folder. That works but it is not what ships — it reintroduces the
build-specific artefact the hybrid was written to remove, and it makes "which DLL
is this" unanswerable from the file alone. The hybrid path is the one used here.

## Changed: install docs cover both builds (`README.md`, `NEXUS-DESCRIPTION.bbcode`)

The V1 install steps told the reader to open **Settings → AI Setup**, which is the
page the Steam build has no tab for — the exact failure the F9 panel exists to fix.
Anyone following those steps on Steam would have concluded the mod was broken.

- Step 4 is now "press F9", with a short note on why the mod draws its own panel
  instead of relying on the game's menu layout.
- Requirements state plainly that one file covers both builds, that there is no
  separate Steam download, and that Steam needs a cloud voice because Overtone's
  synthesis is stripped there.
- Step 1 gives the game-folder path for each build and flags the `Game` subfolder,
  since BepInEx must sit next to the .exe and `AI2U\` is one level too high.
- The voice section no longer promises a free on-device fallback unconditionally;
  on Steam, switching cloud TTS off means silence, and it now says so.

Verified before packaging: `dist/` contains only the plugin and its config, no key
material anywhere in the tree, and no trace of the retired Steam-only plugin.
