# Updating the mod after a game patch

Everything needed to get this mod working again after AI2U updates.
Written against game version **0.1.46** (Unity 2022.3.62, **Mono** backend).

---

## First: what an update actually breaks

The launcher (`AI2ULauncher.exe`) re-patches game files. In practice:

| Thing | Survives an update? |
|---|---|
| `BepInEx/` folder and `winhttp.dll` | Usually yes |
| `BepInEx/plugins/AI2UCustomAI.dll` | **Often deleted** |
| `BepInEx/config/*.cfg` (your keys) | Usually yes |
| `Assembly-CSharp.dll` (the game code we hook) | **Replaced** |

So there are two cases, and they need different work.

### Case 1 — the mod is just missing (most common)

Copy the DLL back:

```
C:\Projects\AI2U-CustomAI\dist\BepInEx\plugins\AI2UCustomAI.dll
  ->  C:\AI2U\Game\BepInEx\plugins\
```

Launch and check `BepInEx\LogOutput.log` for `Patched.`. If it's there, done.

### Case 2 — the game code changed

If the mod loads but throws, or `Patched.` never appears, the hooks moved.
Work through the verification steps below.

**Avoid this entirely:** launch with `C:\..Launchers\AI2U (Modded).bat`, which
starts the .exe directly and never triggers the patcher. Only use the official
launcher when you actually want to update.

---

## Rebuilding

```bash
bash /c/Users/canak/ai2u-mod/plugin/build.sh
```

Requires nothing but Windows' built-in .NET Framework compiler.
The script refuses to install while the game is running, and deletes its
output before compiling so a failed build can't be mistaken for success.

Source of truth lives in **two** places — keep them in sync:

- `C:\Users\canak\ai2u-mod\plugin\` — working copy, what `build.sh` compiles
- `C:\Projects\AI2U-CustomAI\plugin\` — the shareable copy

**Critical compiler flags:** `-nostdlib+ -noconfig`, referencing the game's own
`mscorlib.dll` / `System.dll` / `netstandard.dll` from its `Managed` folder —
not the ones csc picks up by default. Mixing them makes `UploadHandlerRaw`
bind to a `Span<byte>` overload that does not exist in Unity's Mono runtime,
and it fails at runtime rather than at compile time.

---

## Re-decompiling after an update

```bash
# dnSpy .NET FRAMEWORK build (the .NET 10 build will not run here)
"C:\Users\canak\ai2u-mod\dnspy-nf\dnSpy.Console.exe" --no-color \
  -o "C:\Users\canak\ai2u-mod\src3" \
  "C:\AI2U\Game\AI2U - With you til the end_Data\Managed\Assembly-CSharp.dll"
```

Existing dumps: `src` (pre-2026-08-06), `src2` (post). Diff old vs new to see
what moved:

```bash
diff -rq /c/Users/canak/ai2u-mod/src2/Assembly-CSharp \
         /c/Users/canak/ai2u-mod/src3/Assembly-CSharp | head -40
```

---

## Everything the mod hooks

If a rebuild is needed, verify each of these still exists with the same shape.
Anything that moved must be updated in the source.

### Harmony patches

| Target | Kind | Purpose |
|---|---|---|
| `ChatGPTConversation.SendToChatGPT(string, Action<string,int>)` | Prefix | Intercepts dialogue, sends it to your endpoint instead |
| `LocalTTSManager.Speak(string, Character)` | Prefix | Replaces broken local TTS; routes cloud audio |
| `Communicator.Awake` | Postfix | Forces `isLocalSpeak = true` |
| `UIManager_APIKeyPage.SetUpPage` | Postfix | Builds the whole modded AI Setup page |
| `UIManager_APIKeyPage.ButtonPressed_Apply` | Postfix | Persists the page's fields to config |
| `UIManager_Audio.LoadSettings` | Postfix | Adds the AI Voice on/off control |

### UI members the settings page depends on

All reached by name via Traverse, so a rename breaks the page silently — the
patch catches its own exceptions so the settings screen still opens.

**`UIManager_APIKeyPage`** — `dropdown_Text`, `iF_Text`,
`m_GOTextInputSection`, `button_Apply`, `dropdown_Voice`,
`m_GOVoiceInputSection`, `iF_Voice`, `iF_Voice_Region`

**`UIManager_Audio`** — `dd_tts`

**Child objects found by name** inside `InputSection_Voice_APIKey`:
`API Key Text` (hidden — said "Azure TTS API Key") and `API Key Text_Region`
(relabelled to "Voice"). Set `LogPayloads = true` and open AI Setup to get a
full layout dump with names, positions and sizes — that dump is how these were
found, and re-running it is the fastest way to re-find them.

### Reflected members (Traverse / plain reflection)

**`ChatGPTConversation`**
- `_model` — must equal `ChatGPTAzure` or we don't intervene
- `_chat` — the `Chat` history object
- `_chatHistoryMaxTokens` — stock **3072**; we overwrite with `HistoryMaxTokens`
- `ResolveChatGPTAzure(string)` — we call this to hand the reply back
- `isInAsync` (public static)

**`LocalTTSManager`** → `_player`
**`TTSPlayer`** → `azureVoiceManager`, `Engine`, `Voice`, `sources`
**`AzureVoiceManager`** → `VoiceMap`, `SetAudioFinishPlayingEvent(AudioClip)`
**`Communicator`** → `isLocalSpeak`, `isUsingPersonalTTSAPIKey` *(private static)*

**`NPCController`** — the live vocabulary source
- `m_npcAllActivities` — `Dictionary<string, NPCActivities>`, the valid actions
- `m_locationDictionary` — `Dictionary<string, AreaTriggerDetector>`, per level
- `facialController` → `FacialController.m_expressionGroupList[].name`

### Hardcoded vocabulary (verify if behaviour breaks)

Actions come from the live dictionary, but two lists are compiled into the
game and mirrored in `GameVocab.cs`:

**`npc_body_animation`** — the string switch in `NPCController.ShowAnimation`:
`idle, idling, idly, chill_idle, angry_idle, talk, nod, laugh, shy, stretch,
cheers, dance, troublesome`

**`angry_level`** — only these register as angry, per `NPCUitility.IsNPCAngry`:
`annoyed, furious, extremely furious` (plus `chill` as the calm end)

For reference, the 14 actions in `m_npcAllActivities` as of 0.1.46:
`other, sitting, sitting_down, standing, walking, chaseAttacking,
idleThreating, hugging, kissing, cooking, eat, playing_games,
following_player, following_player_closely`

Note there is **no** `idle` action and **no** `follow_player` — only
`following_player`. Models invent both constantly; that's what the clamping is
for.

---

## Hard-won gotchas

Things that cost real debugging time. Don't re-learn them.

**Unity Mono lacks `String.Split(char, StringSplitOptions)`.** Use the
`char[]` overload. csc compiles the newer one fine and it throws
`MissingMethodException` at runtime.

**The game sends an empty system prompt.** `{"role":"system","content":""}` —
the persona normally lives on their server. Character info arrives as
`story_guide` inside the user message. This is why the mod injects the engine
constraints itself.

**Local TTS is broken in the shipped game.** `LocalTTSManager.Speak` calls the
async `TTSPlayer.Speak` without awaiting, then touches
`_player.sources[0].outputAudioMixerGroup`, which is unwired → NPE, and the
async continuation dies silently. Overtone *does* synthesize correctly; the
audio just never reaches an AudioSource. The mod plays it on
`AzureVoiceManager.VoiceMap[character]` instead, which has the right mixer
group so the in-game Voice slider still works.

**`x-tts-override` picks the stock voice tier.** `TtsMode` has five entries but
the settings UI only exposes two. Level 4 and any Eiona variant force
`speech-02-turbo` regardless of the setting.

**Reasoning models need `reasoning.exclude`.** Without it Gemini 3.x burns the
whole `max_tokens` budget narrating its thinking and returns no JSON. Keep
`MaxTokens` ≥ ~1500 for the same reason.

**OpenRouter omits usage data unless asked.** Send `usage: {include: true}` or
every response looks free.

**xAI TTS returns raw MP3 bytes**, despite its schema documenting base64 JSON.
The mod sniffs the first non-whitespace byte for `{` and handles both.

**Unity can't build an AudioClip from an in-memory MP3.** It has to go through
a file URL — hence the temp file in `Mp3Decoder`.

**The game uses Unity's new Input System, not legacy Input.** 260 references to
`InputAction`/`PlayerInput`. With the legacy backend disabled,
`UnityEngine.Input.GetKeyDown` *throws* rather than returning false. `NewInput.cs`
falls back to `Keyboard.current` and logs which backend it settled on. Do not
silently swallow that exception — doing so hid the real cause for two rounds of
debugging on a hotkey that appeared to do nothing.

**I2 Localization overwrites any label you write.** `LocalizeDropdown.OnEnable`
→ `OnLocalize` → `UpdateLocalization` rebuilds a dropdown's option list from
localisation terms, a frame after a patch sets it. Same for `Localize` on plain
text. `UiGraft.StripLocalizers` destroys those components first. Symptom: the
label looks correct for one frame, then reverts.

**Instantiated UI clones land exactly on top of their source.** Copying a
`RectTransform` copies its `anchoredPosition`, so three cloned input fields
stack invisibly and look like nothing happened. Either offset explicitly, or
let a `LayoutGroup` place them — but check which, because a guard that skips
positioning when a LayoutGroup is present will silently do nothing if the guard
is wrong. Prefer *measuring* an existing relationship (the gap between the text
dropdown and its first field) over hand-tuned pixel constants; two guessed
constants in a row were wrong before that change.

**`PlayerPrefs["LocalTTS"]` is written from the voice dropdown's index.** The
modded page repurposes that dropdown, so `SaveVoiceFields` re-asserts
`LocalTTS = 1`. Without it, Apply sets 0 and she goes mute waiting for server
audio a custom endpoint never sends.

**Voice APIs are not interchangeable the way chat APIs are.** They differ in
URL, body *and* auth header:

| Shape | URL | Auth | Voice goes in |
|---|---|---|---|
| xAI | `/v1/tts` | `Authorization: Bearer` | body (`voice_id`) |
| ElevenLabs | `/v1/text-to-speech/<id>` | `xi-api-key` | the **URL path** |
| OpenAI | `/v1/audio/speech` | `Authorization: Bearer` | body (`voice`) |

Appending xAI's `/tts` to an ElevenLabs base gives a bare
`404 {"detail":"Not Found"}` that looks like a broken mod. Shape is
auto-detected from the host; `Provider` overrides it.

**ElevenLabs 402 is a plan limit, not a bug.** Voice *Library* voices need a
paid plan via the API; the free tier returns
`402 "Free users cannot use library voices via the API"`. Built-in voices
(Sarah `EXAVITQu4vr4xnSDxMaL`, Lily `pFZP5JQG7iQjIQuC4Bku`) work free. Their
models must be `eleven_*` — an OpenAI-style `tts-1` is swapped for
`eleven_multilingual_v2` rather than sent and rejected.

**Providers differ hugely in output loudness.** xAI's is much quieter than
ElevenLabs'. `GrokTts.Level` peak-normalises to 0.97 then applies `Volume`.
It relies on `AudioClip.GetData`/`SetData`, which can fail depending on the
load type Unity picks for a decoded MP3 — both calls are wrapped, and failure
plays the clip unmodified rather than crashing.

---

## Verifying a rebuild worked

Launch and check `C:\AI2U\Game\BepInEx\LogOutput.log`:

```
[Info   :AI2U Custom AI Endpoint] Patched. Endpoint: https://openrouter.ai/api/v1  Model: google/gemini-3.6-flash
[Info   :AI2U Custom AI Endpoint] Voice: Grok TTS is ON - press F8 in-game to switch it.
```

Then talk to a character and confirm:

```
[Info   :AI2U Custom AI Endpoint] Vocabulary read from the live scene:
[Info   :AI2U Custom AI Endpoint]   npc_action (14): other, sitting, ...
[Info   :AI2U Custom AI Endpoint] --> https://openrouter.ai/api/v1/chat/completions
[Info   :AI2U Custom AI Endpoint] billing: prompt=1874 completion=312 cost=$0.007492 | session: 3 calls $0.0212
[Info   :AI2U Custom AI Endpoint] Grok TTS: voice=iris chars=142 54KB 3.5s audio in 1.2s
```

If `npc_action` reports far fewer than 14, `m_npcAllActivities` changed shape.
If locations are empty, `m_locationDictionary` moved or is populated later.

---

## Backups on disk

- `C:\Users\canak\ai2u-mod\Assembly-CSharp.dll.bak` — pre-update (0.1.46 base)
- `C:\Users\canak\ai2u-mod\Assembly-CSharp.NEW.dll.bak` — post 2026-08-06
- `BepInEx\plugins\AI2UCustomAI.dll.bak-<timestamp>` — every build.sh install

Keep a copy of `Assembly-CSharp.dll` before each game update. Diffing the old
against the new is the fastest way to see what a patch changed.
