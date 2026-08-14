using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ChatGPTUtility;
using LeastSquares.Overtone;

namespace AI2UCustomAI
{
    [BepInPlugin("canak.ai2u.customai", "AI2U Custom AI Endpoint", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;
        public static Plugin Instance;

        // Config is an instance member of BaseUnityPlugin; this lets the static
        // patch classes persist settings without carrying a reference around.
        public static void SaveCfg()
        {
            if (Instance != null) Instance.Config.Save();
        }

        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<string> CfgBaseUrl;
        public static ConfigEntry<string> CfgApiKey;
        public static ConfigEntry<string> CfgModel;
        public static ConfigEntry<float> CfgTemperature;
        public static ConfigEntry<int> CfgMaxTokens;
        public static ConfigEntry<int> CfgHistoryMaxTokens;
        public static ConfigEntry<bool> CfgLogPayloads;
        public static ConfigEntry<int> CfgRetries;
        public static ConfigEntry<bool> CfgHideReasoning;
        public static ConfigEntry<bool> CfgJsonMode;
        public static ConfigEntry<bool> CfgClampValues;
        public static ConfigEntry<bool> CfgForceLocalVoice;
        public static ConfigEntry<bool> CfgGrokEnabled;
        public static ConfigEntry<string> CfgGrokBaseUrl;
        public static ConfigEntry<string> CfgGrokApiKey;
        public static ConfigEntry<string> CfgGrokVoiceId;
        public static ConfigEntry<string> CfgGrokLanguage;
        public static ConfigEntry<float> CfgGrokSpeed;
        public static ConfigEntry<int> CfgGrokSampleRate;
        public static ConfigEntry<bool> CfgGrokNormalize;
        public static ConfigEntry<string> CfgTtsProvider;
        public static ConfigEntry<string> CfgTtsModel;
        public static ConfigEntry<bool> CfgTtsNormalize;
        public static ConfigEntry<float> CfgTtsVolume;
        public static ConfigEntry<KeyCode> CfgGrokToggleKey;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            CfgEnabled = Config.Bind("General", "Enabled", true,
                "Route NPC dialogue to your own OpenAI-compatible endpoint instead of the AI2U game server.");
            CfgBaseUrl = Config.Bind("Endpoint", "BaseUrl", "https://openrouter.ai/api/v1",
                "OpenAI-compatible base URL. '/chat/completions' is appended automatically.");
            CfgApiKey = Config.Bind("Endpoint", "ApiKey", "",
                "Your API key, sent as 'Authorization: Bearer <key>'.");
            CfgModel = Config.Bind("Endpoint", "Model", "google/gemini-3.6-flash",
                "Model identifier passed to the endpoint.");
            CfgTemperature = Config.Bind("Sampling", "Temperature", 0.9f, "Sampling temperature.");
            CfgMaxTokens = Config.Bind("Sampling", "MaxTokens", 3000,
                "Max tokens per reply. Reasoning models spend hidden tokens against this budget before "
                + "emitting any JSON, so keep it generous. Below ~1500 Gemini 3.x returns nothing usable.");
            CfgHistoryMaxTokens = Config.Bind("Memory", "HistoryMaxTokens", 500000,
                "How much conversation the NPC keeps before the game deletes her oldest messages. This "
                + "is memory, not reply length - it overrides the game's own 3072 cap, which is why she "
                + "forgets the start of a long talk. A ceiling, not a reservation: cost only grows as the "
                + "history actually fills. Keep it under the model's context window (Gemini 3.6 Flash: "
                + "1048576).");
            CfgRetries = Config.Bind("Sampling", "RetriesOnBadJson", 2,
                "Retries when the model returns unparseable JSON. Smaller models need more.");
            CfgHideReasoning = Config.Bind("Sampling", "HideReasoning", true,
                "Send reasoning.exclude so chain-of-thought is kept out of the reply. Required for "
                + "Gemini 3.x and other reasoning models, which otherwise narrate instead of answering.");
            CfgJsonMode = Config.Bind("Sampling", "ForceJsonMode", false,
                "Send response_format=json_object. Tightens output, but some endpoints reject it.");
            CfgForceLocalVoice = Config.Bind("Voice", "ForceLocalVoice", true,
                "Switch the NPC to on-device Overtone TTS. Required when using a custom endpoint: the "
                + "game normally plays base64 audio the AI2U server returns alongside the reply, and a "
                + "custom endpoint cannot supply that, so the NPC would stay silent. Turn this off only "
                + "if you have set your own Azure Speech key in the game's AI Setup page.");
            CfgGrokEnabled = Config.Bind("GrokTTS", "Enabled", false,
                "Speak her lines with a cloud text-to-speech service instead of the on-device voice. "
                + "Costs money at whichever provider you point it at, so it is off by default. When a "
                + "request fails the mod falls back to the local voice rather than going silent.");
            CfgTtsProvider = Config.Bind("GrokTTS", "Provider", "auto",
                "Which request shape to send, because voice APIs are not interchangeable the way chat "
                + "APIs are:\n"
                + "  auto        work it out from the URL (recommended)\n"
                + "  xai         api.x.ai/v1/tts - Bearer key, voice in the body\n"
                + "  elevenlabs  /v1/text-to-speech/<voice> - xi-api-key header, voice in the URL\n"
                + "  openai      /v1/audio/speech - Bearer key; most other providers and local\n"
                + "              servers copy this one\n"
                + "Only set this by hand if auto-detection guesses wrong, which mainly happens with "
                + "self-hosted endpoints whose address gives no clue.");
            CfgTtsModel = Config.Bind("GrokTTS", "Model", "tts-1",
                "Voice model name. Ignored by xAI, whose endpoint takes no model parameter. "
                + "ElevenLabs needs one of its own (eleven_multilingual_v2, eleven_turbo_v2_5, ...); "
                + "an OpenAI-style name here is swapped for eleven_multilingual_v2 rather than "
                + "sent and rejected.");
            CfgGrokBaseUrl = Config.Bind("GrokTTS", "BaseUrl", "https://api.x.ai/v1",
                "xAI API root. '/tts' is appended automatically.");
            CfgGrokApiKey = Config.Bind("GrokTTS", "ApiKey", "",
                "Your xAI API key, sent as 'Authorization: Bearer <key>'. Separate from the text key "
                + "above - this one is billed by xAI, not OpenRouter.");
            CfgGrokVoiceId = Config.Bind("GrokTTS", "VoiceId", "iris",
                "Built-in voice or a custom voice ID. The 26 stock voices: carina, zagan, helix, orion, "
                + "luna, iris, altair, zenith, perseus, helios, lux, kepler, rigel, cosmo, celeste, ursa, "
                + "sirius, lumen, castor, naksh, atlas, ara, eve, leo, rex, sal.");
            CfgGrokLanguage = Config.Bind("GrokTTS", "Language", "en",
                "BCP-47 language code, or 'auto' to detect per line. xAI requires this field.");
            CfgGrokSpeed = Config.Bind("GrokTTS", "Speed", 1.0f,
                "Playback rate baked into the synthesis. Below 1.0 is slower, above is faster.");
            CfgGrokSampleRate = Config.Bind("GrokTTS", "SampleRate", 24000,
                "Output sample rate. Allowed: 8000, 16000, 22050, 24000, 44100, 48000.");
            CfgGrokNormalize = Config.Bind("GrokTTS", "TextNormalization", true,
                "Let xAI expand numbers and abbreviations into spoken form before synthesis.");
            CfgTtsNormalize = Config.Bind("GrokTTS", "NormalizeLoudness", true,
                "Bring every voice up to a consistent level. Providers hand back wildly different "
                + "loudness - xAI's output is noticeably quieter than ElevenLabs' - so the clip is "
                + "scaled by its own peak before playing. Turn off if you would rather set the level "
                + "yourself with Volume below.");
            CfgTtsVolume = Config.Bind("GrokTTS", "Volume", 1.0f,
                "Extra gain applied after normalisation. 1.0 leaves it alone, 1.5 is half again as "
                + "loud, 0.5 is half. Pushing well past 1.0 on an already-normalised clip will clip "
                + "and distort.");
            CfgGrokToggleKey = Config.Bind("GrokTTS", "ToggleKey", KeyCode.F8,
                "Press this in-game to switch Grok TTS on or off without restarting. Turning it off "
                + "stops xAI billing immediately; she keeps talking with the free on-device voice. "
                + "The choice is saved, so it survives a restart. Set to None to disable the hotkey.");
            CfgClampValues = Config.Bind("Schema", "ClampToAllowedValues", true,
                "Snap out-of-range enum fields onto the values the game actually understands. The allowed "
                + "lists are read from the level's own system prompt, so this follows whichever level is "
                + "loaded. Turn off only to debug what a model emits raw.");
            CfgLogPayloads = Config.Bind("Debug", "LogPayloads", true,
                "Log outgoing prompts and raw replies to BepInEx\\LogOutput.log.");

            if (!CfgEnabled.Value)
            {
                Log.LogInfo("Disabled by config; leaving the game untouched.");
                return;
            }

            try
            {
                Harmony h = new Harmony("canak.ai2u.customai");
                h.PatchAll(typeof(SendPatch));
                h.PatchAll(typeof(ModUiPatch));
                h.PatchAll(typeof(ModUiApplyPatch));
                h.PatchAll(typeof(GrokVoiceDropdownPatch));
                if (CfgForceLocalVoice.Value)
                {
                    h.PatchAll(typeof(VoicePatch));
                    h.PatchAll(typeof(LocalTtsFix));
                }
                Log.LogInfo("Patched. Endpoint: " + CfgBaseUrl.Value + "  Model: " + CfgModel.Value);
                if (string.IsNullOrEmpty(CfgApiKey.Value))
                    Log.LogWarning("ApiKey is empty. Set it in BepInEx\\config\\canak.ai2u.customai.cfg");
                if (CfgGrokToggleKey.Value != KeyCode.None)
                    Log.LogInfo("Voice: Grok TTS is " + (CfgGrokEnabled.Value ? "ON" : "OFF")
                        + " - press " + CfgGrokToggleKey.Value + " in-game to switch it.");
            }
            catch (Exception e)
            {
                Log.LogError("Failed to patch: " + e);
            }
        }

        // The game ships Unity's new Input System, and when a project is built
        // with the legacy backend disabled, UnityEngine.Input throws instead of
        // returning false. Try legacy once, then fall back to the new system.
        static int _inputMode; // 0 = untried, 1 = legacy, 2 = new, 3 = unavailable

        static bool KeyPressed(KeyCode key)
        {
            if (_inputMode == 3) return false;

            if (_inputMode == 0)
            {
                try
                {
                    bool hit = Input.GetKeyDown(key);
                    _inputMode = 1;
                    Log.LogInfo("Hotkey: using legacy Input.");
                    return hit;
                }
                catch (Exception e)
                {
                    Log.LogInfo("Hotkey: legacy Input unavailable (" + e.GetType().Name
                        + "); switching to the new Input System.");
                    _inputMode = 2;
                }
            }

            if (_inputMode == 1)
            {
                try { return Input.GetKeyDown(key); }
                catch (Exception) { _inputMode = 2; }
            }

            try
            {
                return NewInput.WasPressed(key);
            }
            catch (Exception e)
            {
                Log.LogWarning("Hotkey disabled - no usable input backend: " + e.Message);
                _inputMode = 3;
                return false;
            }
        }

        // The game uses legacy Input throughout, so polling it here is safe.
        private void Update()
        {
            if (CfgGrokToggleKey == null || CfgGrokToggleKey.Value == KeyCode.None) return;
            if (!KeyPressed(CfgGrokToggleKey.Value)) return;

            bool now = !CfgGrokEnabled.Value;
            CfgGrokEnabled.Value = now;

            // Persist so the choice survives a restart.
            try { Config.Save(); }
            catch (Exception e) { Log.LogWarning("Could not save the voice setting: " + e.Message); }

            if (now && string.IsNullOrEmpty(CfgGrokApiKey.Value))
            {
                _toast = "Grok TTS ON - but no xAI key is set";
                Log.LogWarning("Grok TTS switched on, but GrokTTS/ApiKey is empty; "
                    + "lines will keep using the local voice.");
            }
            else
            {
                _toast = now
                    ? "Grok TTS ON  (" + CfgGrokVoiceId.Value + ") - xAI billing active"
                    : "Grok TTS OFF - local voice, no xAI billing";
                Log.LogInfo(now
                    ? "Grok TTS enabled by hotkey; xAI billing resumes."
                    : "Grok TTS disabled by hotkey; xAI billing stopped, using the local voice.");
            }

            _toastUntil = Time.realtimeSinceStartup + 3f;
        }

        static string _toast;
        static float _toastUntil;
        static Texture2D _toastBg;

        static Texture2D SolidTexture(Color c)
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        // Brief on-screen confirmation so the toggle is visible without the log.
        //
        // Drawn at the TOP of the screen on purpose: the game's dialogue UI is a
        // Screen Space - Overlay canvas along the bottom, and those render after
        // IMGUI, so anything drawn down there is painted over. Uses its own solid
        // background texture rather than GUI.skin.box, which has no background
        // assigned in a built player and renders as invisible text.
        private void OnGUI()
        {
            if (_toast == null || Time.realtimeSinceStartup > _toastUntil) return;

            if (_toastBg == null) _toastBg = SolidTexture(new Color(0f, 0f, 0f, 0.85f));

            // Draw above other IMGUI, and undo any scaling the game left behind.
            int prevDepth = GUI.depth;
            Matrix4x4 prevMatrix = GUI.matrix;
            GUI.depth = -1000;
            GUI.matrix = Matrix4x4.identity;

            GUIStyle style = new GUIStyle();
            style.fontSize = 18;
            style.alignment = TextAnchor.MiddleCenter;
            style.normal.textColor = Color.white;
            style.normal.background = _toastBg;
            style.padding = new RectOffset(20, 20, 12, 12);

            Vector2 size = style.CalcSize(new GUIContent(_toast));
            float w = size.x + 40f, h = size.y + 24f;
            Rect r = new Rect((Screen.width - w) / 2f, 40f, w, h);

            GUI.DrawTexture(r, _toastBg);
            GUI.Label(r, _toast, style);

            GUI.depth = prevDepth;
            GUI.matrix = prevMatrix;
        }
    }

    [HarmonyPatch(typeof(ChatGPTConversation), "SendToChatGPT", new Type[] { typeof(string), typeof(Action<string, int>) })]
    public static class SendPatch
    {
        static bool Prefix(ChatGPTConversation __instance, string message, Action<string, int> errorCallback)
        {
            try
            {
                Traverse t = Traverse.Create(__instance);

                // Only take over the server-routed path; leave legacy direct-OpenAI modes alone.
                object model = t.Field("_model").GetValue();
                if (model == null || model.ToString() != "ChatGPTAzure")
                    return true;

                Chat chat = t.Field("_chat").GetValue<Chat>();
                if (chat == null)
                {
                    Plugin.Log.LogWarning("_chat was null; deferring to the original code path.");
                    return true;
                }

                // The game trims history down to _chatHistoryMaxTokens (stock: 3072) inside
                // ResolveChatGPTAzure, which we still call to hand the reply back. Re-apply our
                // ceiling on every send so the trim never runs before the model's real limit.
                int wantHistory = Plugin.CfgHistoryMaxTokens.Value;
                if (wantHistory > 0 && t.Field("_chatHistoryMaxTokens").GetValue<int>() != wantHistory)
                {
                    t.Field("_chatHistoryMaxTokens").SetValue(wantHistory);
                    Plugin.Log.LogInfo("history ceiling raised to " + wantHistory + " tokens");
                }

                chat.AppendMessage(Chat.Speaker.User, message, 0);
                ChatGPTConversation.isInAsync = true;

                __instance.StartCoroutine(Bridge.Send(__instance, chat.CurrentChat, errorCallback));
                return false; // skip the vanilla request to the AI2U server
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Prefix failed, falling back to vanilla: " + e);
                return true;
            }
        }
    }

    // Overtone synthesizes the line correctly, then the game throws it away.
    //
    // LocalTTSManager.Speak calls the async TTSPlayer.Speak without awaiting it, then immediately
    // touches _player.sources[0].outputAudioMixerGroup on the next line. In this build that array
    // is not wired up, so the synchronous line raises a NullReferenceException and the async
    // continuation that would have called sources[i].Play() fails the same way -- silently, since
    // nobody observes the Task. The log shows "Done. Returned 'N' samples" for every line: the
    // audio exists, it just never reaches an AudioSource.
    //
    // AzureVoiceManager.VoiceMap already holds a correctly configured AudioSource per character
    // (right mixer group, so the in-game Voice volume slider still applies). That is the same
    // source AzureVoiceManager_PersonalAPI plays through, so route the generated clip there.
    [HarmonyPatch(typeof(LocalTTSManager), "Speak")]
    public static class LocalTtsFix
    {
        static bool Prefix(LocalTTSManager __instance, string Text, Character currentCharacterID)
        {
            try
            {
                TTSPlayer player = Traverse.Create(__instance).Field("_player").GetValue<TTSPlayer>();
                if (player == null || player.Engine == null || player.Voice == null)
                {
                    Plugin.Log.LogWarning("TTS: Overtone player not ready; leaving the original path alone.");
                    return true;
                }

                AudioSource dest = ResolveVoiceSource(player, currentCharacterID);
                if (dest == null)
                {
                    Plugin.Log.LogError("TTS: no AudioSource available for " + currentCharacterID + "; cannot play.");
                    return false;
                }

                player.StartCoroutine(SpeakRoutine(player, Text, dest));
                return false; // replace the original, which would throw before playing anything
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("TTS prefix failed, deferring to the original: " + e);
                return true;
            }
        }

        // Prefer the character's own configured source; fall back to the player's array, then to
        // any source on the object, creating one only as a last resort.
        static AudioSource ResolveVoiceSource(TTSPlayer player, Character id)
        {
            AzureVoiceManager avm = Traverse.Create(player).Field("azureVoiceManager").GetValue<AzureVoiceManager>();
            if (avm != null && avm.VoiceMap != null)
            {
                if (avm.VoiceMap.ContainsKey(id) && avm.VoiceMap[id] != null) return avm.VoiceMap[id];
                foreach (KeyValuePair<Character, AudioSource> kv in avm.VoiceMap)
                    if (kv.Value != null) return kv.Value;
            }

            if (player.sources != null)
                for (int i = 0; i < player.sources.Length; i++)
                    if (player.sources[i] != null) return player.sources[i];

            AudioSource own = player.GetComponent<AudioSource>();
            if (own != null) return own;

            AudioSource added = player.gameObject.AddComponent<AudioSource>();
            added.playOnAwake = false;
            Plugin.Log.LogWarning("TTS: no configured AudioSource found; added a bare one (voice volume slider will not apply).");
            return added;
        }

        static IEnumerator SpeakRoutine(TTSPlayer player, string text, AudioSource dest)
        {
            // Grok first when configured; on any failure fall through to the
            // on-device voice so a dropped request never leaves her mute.
            if (GrokTts.Configured)
            {
                AudioClip remote = null;
                IEnumerator call = GrokTts.Synthesize(text, delegate(AudioClip c) { remote = c; });
                while (call.MoveNext()) yield return call.Current;

                if (remote != null)
                {
                    Play(player, dest, remote);
                    yield break;
                }
                Plugin.Log.LogWarning("Grok TTS unavailable for this line; using the local voice.");
            }

            Task<AudioClip> task = null;
            try
            {
                task = player.Engine.Speak(text, player.Voice.VoiceModel);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("TTS: synthesis could not start: " + e.Message);
                yield break;
            }

            while (!task.IsCompleted)
                yield return null;

            if (task.IsFaulted)
            {
                Plugin.Log.LogError("TTS: synthesis failed: "
                    + (task.Exception != null ? task.Exception.GetBaseException().Message : "unknown"));
                yield break;
            }

            AudioClip clip = task.Result;
            if (clip == null)
            {
                Plugin.Log.LogWarning("TTS: synthesis returned no clip (is the voice model loaded?).");
                yield break;
            }

            Play(player, dest, clip);
        }

        // Shared by both voices: put the clip on the character's AudioSource and
        // tell the game when it ends, which is what drives mouth movement and
        // hands the turn back to the player.
        static void Play(TTSPlayer player, AudioSource dest, AudioClip clip)
        {
            dest.clip = clip;
            dest.loop = false;
            dest.volume = 1f;
            dest.pitch = 1f;
            dest.Play();

            try
            {
                AzureVoiceManager avm = Traverse.Create(player).Field("azureVoiceManager").GetValue<AzureVoiceManager>();
                if (avm != null)
                    Traverse.Create(avm).Method("SetAudioFinishPlayingEvent", new object[] { clip }).GetValue();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("TTS: could not hook the audio-finished event: " + e.Message);
            }

            if (Plugin.CfgLogPayloads.Value)
                Plugin.Log.LogInfo("TTS: playing " + clip.length.ToString("0.0") + "s on " + dest.name);
        }
    }

    // Communicator.Awake reads the voice mode from PlayerPrefs["LocalTTS"], where 0 means "let the
    // AI2U server synthesize the line and send audio back". Our replies never carry that audio, so
    // on 0 the game calls AzureAISpeak with a null speechResult and the NPC just never speaks.
    // Re-assert local synthesis after Awake, which also survives the pref being reset by an update.
    [HarmonyPatch(typeof(Communicator), "Awake")]
    public static class VoicePatch
    {
        static void Postfix()
        {
            try
            {
                // isUsingPersonalTTSAPIKey is private static, so read it reflectively.
                bool personalTts = Traverse.Create(typeof(Communicator))
                    .Field("isUsingPersonalTTSAPIKey").GetValue<bool>();
                if (personalTts)
                {
                    // The player supplied their own Azure Speech key; that path synthesizes from
                    // text rather than replaying server audio, so it already works. Leave it alone.
                    Plugin.Log.LogInfo("Personal TTS key in use; leaving the voice path as configured.");
                    return;
                }

                if (!Communicator.isLocalSpeak)
                {
                    Communicator.isLocalSpeak = true;
                    Plugin.Log.LogInfo("Voice: forced on-device TTS (server audio is unavailable on a custom endpoint).");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not set the voice mode: " + e);
            }
        }
    }

    // Keeps model output inside the value sets the game's behavior trees actually branch on.
    // Anything unrecognized becomes an unhandled string downstream: NPCController.ShowAnimation
    // silently no-ops on an unknown animation, and an unknown angry_level or favorability_change
    // skips the mood and trust updates. Rather than let that happen quietly, snap to a legal value.
    public static class Schema
    {
        // Field -> allowed values, learned from the level's own system prompt.
        static Dictionary<string, List<string>> _allowed;
        static string _learnedFrom;

        // Last-resort list, used only if runtime discovery has not run yet.
        // GameVocab.For() is always preferred because it reads the live scene.
        //
        // These npc_action values are the exact keys of NPCController's
        // m_npcAllActivities dictionary (NPCController.cs:1333). Anything not
        // in that dictionary falls through ShowAction to NPCActivities.Other,
        // so a plausible-sounding invention like "follow_player" is a silent
        // no-op. npc_body_animation mirrors the string switch in
        // ShowAnimation. No location list here: locations are per-level and
        // only exist at runtime.
        static readonly Dictionary<string, string[]> Fallback = new Dictionary<string, string[]>
        {
            { "npc_action", new[] { "other", "standing", "sitting", "sitting_down",
                "walking", "following_player", "following_player_closely", "hugging",
                "kissing", "cooking", "playing_games", "eat", "attack", "idle" } },
            { "npc_body_animation", new[] { "idle", "idling", "idly", "chill_idle",
                "angry_idle", "talk", "nod", "laugh", "shy", "stretch", "cheers",
                "dance", "troublesome" } },
            { "npc_face_expression", new[] { "smile", "slight_smile", "grin", "sad",
                "angry", "angry_face", "surprise", "confused", "shy", "smug",
                "worried", "bored", "tired_face", "scream", "raise_eyebrows" } },
            { "angry_level", new[] { "chill", "annoyed", "furious", "extremely furious" } },
            { "favorability_change", new[] { "very negative", "negative", "neutral",
                "positive", "very positive" } },
        };

        // Values the model reaches for that have an unambiguous in-game counterpart.
        static readonly Dictionary<string, string> Synonyms = new Dictionary<string, string>
        {
            { "gentle_smile", "slight_smile" }, { "soft_smile", "slight_smile" },
            { "small_smile", "slight_smile" }, { "warm_smile", "smile" },
            { "happy", "smile" }, { "laughing", "grin" }, { "excited", "grin" },
            { "concerned", "worried" }, { "concern", "worried" }, { "nervous", "worried" },
            { "anxious", "worried" }, { "relieved", "slight_smile" }, { "sadness", "sad" },
            { "crying_face", "sad" }, { "upset", "sad" }, { "disappointed", "sad" },
            { "shocked", "surprise" }, { "surprised", "surprise" }, { "startled", "surprise" },
            { "curious", "confused" }, { "puzzled", "confused" }, { "thinking", "confused" },
            { "embarrassed", "shy" }, { "blushing", "shy" }, { "flustered", "shy" },
            { "smirk", "smug" }, { "playful", "smug" }, { "teasing", "smug" },
            { "sleepy", "tired_face" }, { "exhausted", "tired_face" }, { "tired", "tired_face" },
            { "mad", "angry" }, { "furious_face", "angry_face" }, { "rage", "angry_face" },
            { "neutral_face", "slight_smile" }, { "neutral", "slight_smile" },
            { "idle_concerned", "idle" }, { "idling", "idle" }, { "idly", "idle" },
            { "standing_idle", "idle" }, { "waiting", "idle" }, { "relaxed", "chill_idle" },
            { "talking", "talk" }, { "speaking", "talk" }, { "nodding", "nod" },
            { "agreeing", "nod" }, { "stretching", "stretch" }, { "dancing", "dance" },
            { "celebrating", "cheers" }, { "cheering", "cheers" }, { "sobbing", "crying" },
            { "cry", "crying" }, { "sit_down", "sitting_down" }, { "sits", "sitting" },
            { "seated", "sitting" }, { "stand", "standing" }, { "stands", "standing" },
            { "walk", "walking" }, { "walks", "walking" }, { "walk_to", "walking" },
            { "move", "walking" }, { "moving", "walking" }, { "go", "walking" },
            { "goes", "walking" }, { "approach", "walking" }, { "approaching", "walking" },
            // The follow family: this is the one the model kept getting wrong.
            { "follow_player", "following_player" }, { "follows_player", "following_player" },
            { "following", "following_player" }, { "follow", "following_player" },
            { "follow_the_player", "following_player" },
            { "following_the_player", "following_player" },
            { "follow_player_closely", "following_player_closely" },
            { "following_closely", "following_player_closely" },
            { "follow_closely", "following_player_closely" },
            { "hug", "hugging" }, { "hugs", "hugging" }, { "cook", "cooking" },
            { "cooks", "cooking" }, { "kiss", "kissing" }, { "none", "other" },
            { "nothing", "other" }, { "n/a", "other" }, { "null", "other" },
            { "eating", "eat" }, { "eats", "eat" }, { "play_games", "playing_games" },
            { "playing_game", "playing_games" }, { "gaming", "playing_games" },
            { "attacking", "attack" }, { "attacks", "attack" },
            { "calm", "chill" }, { "content", "chill" }, { "irritated", "annoyed" },
            { "irritable", "annoyed" }, { "annoyed_level", "annoyed" }, { "angry_level", "annoyed" },
            { "mildly annoyed", "annoyed" }, { "very angry", "furious" }, { "enraged", "furious" },
            { "extremely_furious", "extremely furious" }, { "livid", "extremely furious" },
            { "very_negative", "very negative" }, { "very_positive", "very positive" },
            { "slightly positive", "positive" }, { "slightly negative", "negative" },
            { "no change", "neutral" }, { "none_change", "neutral" }, { "unchanged", "neutral" },
        };

        // Some words mean different things depending on the field: "happy" is a
        // smile on a face but the calm end of angry_level, and "idle" is both a
        // real action and a real animation. These are consulted before the
        // shared table so the field wins.
        static readonly Dictionary<string, Dictionary<string, string>> FieldSynonyms
            = new Dictionary<string, Dictionary<string, string>>
        {
            { "angry_level", new Dictionary<string, string> {
                { "happy", "chill" }, { "normal", "chill" }, { "pleased", "chill" },
                { "cheerful", "chill" }, { "fine", "chill" }, { "ok", "chill" },
                { "okay", "chill" }, { "neutral", "chill" }, { "relaxed", "chill" },
                { "content", "chill" }, { "calm", "chill" }, { "composed", "chill" },
                { "upset", "annoyed" }, { "displeased", "annoyed" },
                { "angry", "furious" }, { "mad", "furious" }, { "rage", "furious" },
                { "livid", "extremely furious" }, { "seething", "extremely furious" },
            } },
            { "npc_action", new Dictionary<string, string> {
                // "idle" is a real body animation but NOT a real action, so an
                // idling character is "standing" as far as the engine cares.
                { "idle", "standing" }, { "idling", "standing" },
                { "waiting", "standing" }, { "wait", "standing" },
                { "follow", "following_player" }, { "follow_player", "following_player" },
                { "following", "following_player" }, { "follow_me", "following_player" },
                { "followplayer", "following_player" },
                { "follow_closely", "following_player_closely" },
                { "attack", "chaseAttacking" }, { "attacking", "chaseAttacking" },
                { "chase", "chaseAttacking" }, { "chasing", "chaseAttacking" },
                { "threaten", "idleThreating" }, { "threatening", "idleThreating" },
                { "eating", "eat" }, { "sit", "sitting" }, { "sit_down", "sitting_down" },
                { "stand", "standing" }, { "walk", "walking" }, { "walk_to", "walking" },
                { "hug", "hugging" }, { "kiss", "kissing" }, { "cook", "cooking" },
                { "play_games", "playing_games" }, { "gaming", "playing_games" },
                { "resting", "sitting" }, { "rest", "sitting" }, { "talking", "other" },
                { "talk", "other" }, { "chatting", "other" }, { "speaking", "other" },
                { "nothing", "other" }, { "stay", "standing" }, { "staying", "standing" },
            } },
            { "npc_target_location", new Dictionary<string, string> {
                { "player", "player_location" }, { "the_player", "player_location" },
                { "player_position", "player_location" }, { "me", "player_location" },
                { "towards_player", "player_location" }, { "to_player", "player_location" },
                { "player_s_location", "player_location" },
            } },
        };

        // Where a numeric answer means something: models love writing angry_level: 0.
        // Only annoyed/furious/extremely furious read as angry to
        // NPCUitility.IsNPCAngry; everything calmer is equivalent, so the calm
        // end of the scale is a single value rather than three shades.
        static readonly string[] AngryScale = { "chill", "annoyed", "furious", "extremely furious" };
        static readonly string[] FavorScale = { "very negative", "negative", "neutral", "positive", "very positive" };

        public static void Learn(string systemPrompt)
        {
            if (string.IsNullOrEmpty(systemPrompt)) return;
            if (_learnedFrom != null && _learnedFrom == systemPrompt) return;

            Dictionary<string, List<string>> found = new Dictionary<string, List<string>>();
            foreach (string field in new[] { "npc_action", "npc_body_animation",
                "npc_face_expression", "angry_level", "favorability_change" })
            {
                // Matches "For <field>, the npc can [ONLY] choose from this list (a, b, c)".
                Match m = Regex.Match(systemPrompt,
                    @"For\s+" + field + @"\b[^(\r\n]*\(([^)]*)\)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;

                List<string> vals = new List<string>();
                foreach (string piece in m.Groups[1].Value.Split(new char[] { ',' }))
                {
                    string v = piece.Trim();
                    // Skip unresolved placeholders like {GeneratedRoom}.
                    if (v.Length == 0 || v.IndexOf('{') >= 0) continue;
                    if (!vals.Contains(v)) vals.Add(v);
                }
                if (vals.Count > 0) found[field] = vals;
            }

            if (found.Count == 0) return;
            _allowed = found;
            _learnedFrom = systemPrompt;

            StringBuilder sb = new StringBuilder("Learned allowed values from the level prompt: ");
            foreach (KeyValuePair<string, List<string>> kv in found)
                sb.Append(kv.Key).Append('=').Append(kv.Value.Count).Append(' ');
            Info(sb.ToString());
        }

        // Priority order matters. The live scene is ground truth; the level
        // prompt is a description of it and is sometimes stale or contains
        // unresolved placeholders; the static table is only a safety net.
        static List<string> AllowedFor(string field)
        {
            List<string> live = GameVocab.For(field);
            if (live != null && live.Count > 0) return live;
            if (_allowed != null && _allowed.ContainsKey(field)) return _allowed[field];
            if (Fallback.ContainsKey(field)) return new List<string>(Fallback[field]);
            return null;
        }

        public static void Clamp(JObject o)
        {
            if (o == null) return;
            foreach (string field in new[] { "npc_action", "npc_target_location",
                "npc_body_animation", "npc_face_expression", "angry_level",
                "favorability_change" })
            {
                List<string> allowed = AllowedFor(field);
                if (allowed == null) continue;

                JToken tok = o[field];
                if (tok == null) continue;

                string raw = tok.Type == JTokenType.Null ? "" : tok.ToString().Trim();

                // An empty location is meaningful: it means "do not walk
                // anywhere". Leave it alone rather than trying to resolve it.
                if (field == "npc_target_location" && raw.Length == 0) continue;

                string fixedVal = Resolve(field, raw, allowed);

                if (fixedVal == null)
                {
                    // Nothing sane to map onto. Substitute a value the engine
                    // definitely understands rather than removing the key: the
                    // game reads these with currentJson["field"].Value and a
                    // deliberate neutral beats whatever a missing node yields.
                    string safe = SafeDefault(field, allowed);
                    if (raw.Length > 0)
                        Warn("Unmappable " + field + "=\"" + Trim(raw, 80)
                            + "\"; using \"" + safe + "\" instead.");
                    o[field] = safe;
                }
                else if (fixedVal != raw)
                {
                    Info("Clamped " + field + ": \"" + Trim(raw, 80) + "\" -> \"" + fixedVal + "\"");
                    o[field] = fixedVal;
                }
            }
        }

        // Words that mean "come with me" in the last thing the player typed.
        static readonly string[] FollowAsks =
        {
            "follow me", "follow us", "come with me", "come with us", "come along",
            "come here", "stay with me", "stick with me", "walk with me",
            "tag along", "let's go", "lets go", "keep up", "behind me",
            "come to me", "over here", "this way"
        };

        static readonly string[] StopAsks =
        {
            "stop following", "stay here", "stay there", "wait here", "don't follow",
            "dont follow", "stop there", "stay put", "leave me alone", "go away"
        };

        // Fields can each be individually legal yet still describe an
        // impossible turn. ShowAction only starts a walk when npc_action is a
        // movement activity AND npc_target_location resolves; a model that
        // sets one without the other produces a character who says "sure!"
        // and never moves. This reconciles the two.
        public static void Coherence(JObject o, List<TextMessage> history)
        {
            if (o == null) return;

            string action = Str(o, "npc_action");
            string target = Str(o, "npc_target_location");
            List<string> actions = AllowedFor("npc_action");
            List<string> places = AllowedFor("npc_target_location");

            bool targetLegal = target.Length > 0 && places != null && Legal(target, places) != null;
            bool wantsFollow = Asked(history, FollowAsks);
            bool wantsStop = Asked(history, StopAsks);

            // The player asked for company and the model did not wire it up.
            if (wantsFollow && !wantsStop && !IsFollow(action)
                && actions != null && Legal("following_player", actions) != null)
            {
                Info("Player asked the character to follow but npc_action was \""
                    + Trim(action, 40) + "\"; setting following_player.");
                o["npc_action"] = "following_player";
                o["npc_target_location"] = "";
                return;
            }

            // The player asked her to stop and the model left her following.
            if (wantsStop && IsFollow(action)
                && actions != null && Legal("standing", actions) != null)
            {
                Info("Player asked the character to stop following; setting standing.");
                o["npc_action"] = "standing";
                o["npc_target_location"] = "";
                return;
            }

            // A destination with no walk order: the engine needs the action too.
            if (targetLegal && !IsMove(action)
                && actions != null && Legal("walking", actions) != null)
            {
                Info("npc_target_location \"" + Trim(target, 40)
                    + "\" was set but npc_action was \"" + Trim(action, 40)
                    + "\"; setting walking so the move actually happens.");
                o["npc_action"] = "walking";
                return;
            }

            // A walk order with nowhere to go: GetTargetAreaTriggerTransform
            // would return null and the character would freeze mid-intent.
            if (action == "walking" && !targetLegal)
            {
                if (places != null && Legal("player_location", places) != null)
                {
                    Info("npc_action walking had no usable destination; "
                        + "defaulting to player_location.");
                    o["npc_target_location"] = "player_location";
                }
                else if (actions != null && Legal("standing", actions) != null)
                {
                    Info("npc_action walking had no usable destination and there is "
                        + "no player_location; falling back to standing.");
                    o["npc_action"] = "standing";
                    o["npc_target_location"] = "";
                }
                return;
            }

            // Following ignores npc_target_location; a leftover value here is
            // harmless but confuses the next turn's context.
            if (IsFollow(action) && target.Length > 0)
                o["npc_target_location"] = "";
        }

        static bool IsFollow(string a)
        {
            return a == "following_player" || a == "following_player_closely";
        }

        static bool IsMove(string a)
        {
            return a == "walking" || IsFollow(a);
        }

        static string Str(JObject o, string field)
        {
            JToken t = o[field];
            if (t == null || t.Type == JTokenType.Null) return "";
            return t.ToString().Trim();
        }

        // Only the most recent player line counts. Scanning further back makes
        // the character re-follow on every turn after a single old request.
        static bool Asked(List<TextMessage> history, string[] phrases)
        {
            if (history == null) return false;
            string last = null;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                TextMessage m = history[i];
                if (m == null || m.content == null) continue;
                string role = m.role == null ? "" : m.role.ToLowerInvariant();
                if (role == "system" || role == "assistant") continue;
                last = m.content.ToLowerInvariant();
                break;
            }
            if (last == null) return false;

            for (int i = 0; i < phrases.Length; i++)
                if (last.IndexOf(phrases[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static string Resolve(string field, string raw, List<string> allowed)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            // 1. Already legal.
            for (int i = 0; i < allowed.Count; i++)
                if (string.Equals(allowed[i], raw, StringComparison.OrdinalIgnoreCase)) return allowed[i];

            string norm = Normalize(raw);

            // 2. Legal once spacing/underscores/case are normalized.
            for (int i = 0; i < allowed.Count; i++)
                if (Normalize(allowed[i]) == norm) return allowed[i];

            // 3. Numeric or boolean answers on the two scaled fields.
            if (field == "angry_level" || field == "favorability_change")
            {
                string scaled = FromNumber(field, raw, allowed);
                if (scaled != null) return scaled;
            }

            // 4a. A synonym scoped to this field beats the shared table.
            Dictionary<string, string> scoped;
            if (FieldSynonyms.TryGetValue(field, out scoped))
            {
                string hit;
                if (scoped.TryGetValue(norm.Replace(' ', '_'), out hit)
                    || scoped.TryGetValue(norm, out hit))
                {
                    string ok = Legal(hit, allowed);
                    if (ok != null) return ok;
                }
            }

            // 4b. Known synonym, but only if the target is legal for this level.
            string syn;
            if (Synonyms.TryGetValue(norm.Replace(' ', '_'), out syn) || Synonyms.TryGetValue(norm, out syn))
                for (int i = 0; i < allowed.Count; i++)
                    if (string.Equals(allowed[i], syn, StringComparison.OrdinalIgnoreCase)) return allowed[i];

            // 5. A legal value appears inside a prose answer ("walks over holding a glass of water").
            //    Prefer the longest match so "angry_face" wins over "angry".
            string best = null;
            int bestPos = int.MaxValue, bestSize = 0;
            for (int i = 0; i < allowed.Count; i++)
            {
                string cand = Normalize(allowed[i]);
                if (cand.Length < 3) continue;
                Match cm = Regex.Match(norm, @"(^|\W)" + Regex.Escape(cand) + @"(\W|$)");
                if (!cm.Success) continue;
                // Earliest wins, then longest, so "angry_face" beats a later bare "angry".
                if (cm.Index < bestPos || (cm.Index == bestPos && cand.Length > bestSize))
                {
                    bestPos = cm.Index; bestSize = cand.Length; best = allowed[i];
                }
            }
            if (best != null) return best;

            // 6. A synonym appears inside the prose. Compound answers such as
            //    "relieved_and_slightly_concerned" contain several, so pick by position rather
            //    than by dictionary order: the leading emotion is the one being reported, and
            //    relying on hash iteration order would make the result arbitrary.
            string bestSyn = null;
            int bestAt = int.MaxValue, bestLen = 0;
            foreach (KeyValuePair<string, string> kv in Synonyms)
            {
                string key = Normalize(kv.Key);
                if (key.Length < 4) continue;
                Match sm = Regex.Match(norm, @"(^|\W)" + Regex.Escape(key) + @"(\W|$)");
                if (!sm.Success) continue;

                string target = Legal(kv.Value, allowed);
                if (target == null) continue;

                int at = sm.Index;
                if (at < bestAt || (at == bestAt && key.Length > bestLen))
                {
                    bestAt = at; bestLen = key.Length; bestSyn = target;
                }
            }
            return bestSyn;
        }

        // Models emit angry_level: 0 or favorability_change: 1 despite being told to use words.
        // Map onto the scale; a 0..1 float is treated as a fraction of the range.
        static string FromNumber(string field, string raw, List<string> allowed)
        {
            bool b;
            if (bool.TryParse(raw, out b))
                return Legal(field == "angry_level" ? (b ? "furious" : "normal")
                                                    : (b ? "positive" : "negative"), allowed);

            double d;
            if (!double.TryParse(raw, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out d))
                return null;

            string[] scale = field == "angry_level" ? AngryScale : FavorScale;

            if (field == "favorability_change")
            {
                // Signed: negative means lost trust, 0 neutral, positive gained.
                int idx = d < -1.0 ? 0 : d < 0 ? 1 : d == 0 ? 2 : d <= 1.0 ? 3 : 4;
                return Legal(scale[idx], allowed);
            }

            // angry_level: 0 = calm. Treat 0..1 as a fraction, larger values as an index.
            double frac = (d > 0 && d <= 1.0) ? d : d / 10.0;
            if (d > 1.0 && d <= scale.Length - 1) frac = d / (scale.Length - 1);
            if (frac < 0) frac = 0;
            if (frac > 1) frac = 1;
            int i2 = (int)Math.Round(frac * (scale.Length - 1));
            // 0 means calm, which is index 0 of this scale.
            if (d == 0) i2 = 0;
            return Legal(scale[i2], allowed);
        }

        // A neutral the engine is guaranteed to accept for each field, used when a
        // reply cannot be mapped at all. Preference order per field, then the
        // first allowed value, then empty as the last resort.
        static string SafeDefault(string field, List<string> allowed)
        {
            string[] prefs;
            switch (field)
            {
                case "npc_action":          prefs = new[] { "other", "idle", "standing" }; break;
                case "npc_body_animation":  prefs = new[] { "idle", "standing" }; break;
                case "angry_level":         prefs = new[] { "chill" }; break;
                case "favorability_change": prefs = new[] { "neutral", "no change" }; break;
                // An empty target means "stay put", which the engine handles cleanly.
                case "npc_target_location": return "";
                // Faces are per-character assets; an unknown id simply leaves the
                // current expression in place, so empty is the honest answer.
                case "npc_face_expression": return "";
                default:                    prefs = new string[0]; break;
            }
            for (int i = 0; i < prefs.Length; i++)
            {
                string ok = Legal(prefs[i], allowed);
                if (ok != null) return ok;
            }
            return allowed.Count > 0 ? allowed[0] : "";
        }

        static string Legal(string want, List<string> allowed)
        {
            for (int i = 0; i < allowed.Count; i++)
                if (string.Equals(allowed[i], want, StringComparison.OrdinalIgnoreCase)) return allowed[i];
            return null;
        }

        static string Normalize(string s)
        {
            if (s == null) return "";
            string t = s.ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
            t = Regex.Replace(t, @"[^a-z0-9 ]", " ");
            return Regex.Replace(t, @"\s+", " ").Trim();
        }

        static string Trim(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        // Clamping must never throw on account of logging: it also runs from the offline test
        // harness, where BepInEx never initializes Plugin.Log.
        static void Info(string msg) { if (Plugin.Log != null) Plugin.Log.LogInfo(msg); }
        static void Warn(string msg) { if (Plugin.Log != null) Plugin.Log.LogWarning(msg); }
    }

    /// <summary>Running totals so a session's real spend is visible in the log.</summary>
    public static class Session
    {
        public static int Requests;
        public static double Cost;
    }

    public static class Bridge
    {
        public static IEnumerator Send(ChatGPTConversation conv, List<TextMessage> history, Action<string, int> errorCallback)
        {
            // Re-read the scene every turn: locations are populated per level,
            // and the cast can change, so a cached list goes stale on a
            // level transition.
            if (Plugin.CfgClampValues.Value)
                GameVocab.Refresh();

            string url = CombineUrl(Plugin.CfgBaseUrl.Value, "chat/completions");
            string body = BuildRequest(history);

            if (Plugin.CfgLogPayloads.Value)
                Plugin.Log.LogInfo("--> " + url + "\n" + body);

            int attempts = Plugin.CfgRetries.Value + 1;
            string lastError = "unknown error";

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                UnityWebRequest req = new UnityWebRequest(url, "POST");
                byte[] payload = Encoding.UTF8.GetBytes(body);
                req.uploadHandler = new UploadHandlerRaw(payload);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + Plugin.CfgApiKey.Value);
                req.SetRequestHeader("HTTP-Referer", "https://github.com/ai2u-custom-ai");
                req.SetRequestHeader("X-Title", "AI2U Custom AI");

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    lastError = req.error + " | " + Safe(req.downloadHandler);
                    Plugin.Log.LogError("HTTP failure (attempt " + attempt + "/" + attempts + "): " + lastError);
                    req.Dispose();
                    continue;
                }

                string raw = req.downloadHandler.text;
                req.Dispose();

                if (Plugin.CfgLogPayloads.Value)
                    Plugin.Log.LogInfo("<-- " + Trim(raw, 4000));

                string content = null;
                int completionTokens = 0;
                int totalTokens = 0;
                try
                {
                    JObject o = JObject.Parse(raw);
                    JToken err = o["error"];
                    if (err != null)
                    {
                        lastError = err.ToString();
                        Plugin.Log.LogError("Endpoint returned an error: " + Trim(lastError, 1000));
                        continue;
                    }
                    content = (string)o["choices"][0]["message"]["content"];
                    JToken usage = o["usage"];
                    if (usage != null)
                    {
                        if (usage["completion_tokens"] != null) completionTokens = (int)usage["completion_tokens"];
                        if (usage["total_tokens"] != null) totalTokens = (int)usage["total_tokens"];

                        int promptTokens = usage["prompt_tokens"] != null ? (int)usage["prompt_tokens"] : 0;
                        double cost = usage["cost"] != null ? (double)usage["cost"] : 0.0;
                        Session.Requests++;
                        Session.Cost += cost;
                        Plugin.Log.LogInfo(string.Format(
                            "billing: prompt={0} completion={1} cost=${2:F6} | session: {3} calls ${4:F4}",
                            promptTokens, completionTokens, cost, Session.Requests, Session.Cost));
                    }
                }
                catch (Exception e)
                {
                    lastError = "could not read the response envelope: " + e.Message;
                    Plugin.Log.LogError(lastError);
                    continue;
                }

                JObject reactions = ExtractReactions(content);
                if (reactions == null)
                {
                    lastError = "model did not return usable NPC JSON";
                    Plugin.Log.LogWarning("Attempt " + attempt + "/" + attempts + ": " + lastError
                        + ". Raw content: " + Trim(content, 600));
                    continue;
                }

                if (Plugin.CfgClampValues.Value)
                {
                    Schema.Clamp(reactions);
                    Schema.Coherence(reactions, history);
                }

                JObject envelope = new JObject();
                envelope["npc_reactions"] = reactions;
                envelope["completion"] = completionTokens;
                envelope["total"] = totalTokens;

                string final = envelope.ToString(Formatting.None);
                if (Plugin.CfgLogPayloads.Value)
                    Plugin.Log.LogInfo("==> handing to game: " + Trim(final, 2000));

                ChatGPTConversation.isInAsync = false;
                Traverse.Create(conv).Method("ResolveChatGPTAzure", new object[] { final }).GetValue();
                yield break;
            }

            ChatGPTConversation.isInAsync = false;
            Plugin.Log.LogError("Giving up after " + attempts + " attempt(s): " + Trim(lastError, 500));
            if (errorCallback != null)
                errorCallback(lastError, 0);
        }

        static string BuildRequest(List<TextMessage> history)
        {
            JArray messages = new JArray();
            for (int i = 0; i < history.Count; i++)
            {
                TextMessage m = history[i];
                if (m == null || m.content == null) continue;
                JObject jm = new JObject();
                string role = NormalizeRole(m.role);
                jm["role"] = role;
                jm["content"] = m.content;
                messages.Add(jm);

                // The level's system prompt enumerates the legal values; learn them from it so
                // clamping tracks whichever level is loaded instead of a hardcoded guess.
                if (role == "system" && Plugin.CfgClampValues.Value)
                    Schema.Learn(m.content);
            }

            // The whitelist read off the live scene. This goes in as its own
            // system message after the level prompt so it wins any conflict:
            // the level prompt describes the world, but only these values
            // survive contact with the engine.
            string contract = GameVocab.Contract();
            if (contract != null)
            {
                JObject engine = new JObject();
                engine["role"] = "system";
                engine["content"] = contract;
                messages.Add(engine);
            }

            // The game's schema is strict; nudge models that like to wrap output in prose or fences.
            JObject reminder = new JObject();
            reminder["role"] = "system";
            reminder["content"] = "Reply with a single raw JSON object only. No markdown fences, no commentary, "
                + "no reasoning. Every value must be copied exactly from the allowed lists given above - do not "
                + "invent new values, do not use numbers where a listed word is required, and do not write prose "
                + "into fields like npc_action or npc_face_expression. Always include npc_reply_to_player. "
                + "When the player asks the character to go somewhere or to come along, you MUST set the "
                + "movement fields, not merely agree in the dialogue text: agreeing in npc_reply_to_player "
                + "while leaving npc_action as \"other\" makes the character stand still and read as ignoring "
                + "the player.";
            messages.Add(reminder);

            JObject root = new JObject();
            root["model"] = Plugin.CfgModel.Value;
            root["messages"] = messages;
            root["temperature"] = Plugin.CfgTemperature.Value;
            root["max_tokens"] = Plugin.CfgMaxTokens.Value;

            if (Plugin.CfgHideReasoning.Value)
            {
                JObject reasoning = new JObject();
                reasoning["exclude"] = true;
                root["reasoning"] = reasoning;
            }
            if (Plugin.CfgJsonMode.Value)
            {
                JObject rf = new JObject();
                rf["type"] = "json_object";
                root["response_format"] = rf;
            }

            // OpenRouter omits token accounting unless it is asked for, which makes a paid session
            // look free. Opting in lets us log the real per-request cost.
            JObject usageOpt = new JObject();
            usageOpt["include"] = true;
            root["usage"] = usageOpt;

            return root.ToString(Formatting.None);
        }

        static string NormalizeRole(string role)
        {
            if (string.IsNullOrEmpty(role)) return "user";
            string r = role.ToLowerInvariant();
            if (r == "system" || r == "assistant" || r == "user") return r;
            if (r == "ai" || r == "bot" || r == "chatgpt") return "assistant";
            return "user";
        }

        // Models wrap JSON in fences or prose; pull out the first balanced object.
        public static JObject ExtractReactions(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;

            string s = content.Trim();
            int fence = s.IndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                int start = s.IndexOf('{', fence);
                if (start >= 0) s = s.Substring(start);
            }

            int open = s.IndexOf('{');
            if (open < 0) return null;

            int depth = 0;
            bool inStr = false;
            bool esc = false;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        string candidate = s.Substring(open, i - open + 1);
                        try
                        {
                            JObject o = JObject.Parse(candidate);
                            // Unwrap if the model echoed the outer envelope shape.
                            if (o["npc_reactions"] != null && o["npc_reactions"].Type == JTokenType.Object)
                                o = (JObject)o["npc_reactions"];
                            if (o["npc_reply_to_player"] == null) return null;
                            return o;
                        }
                        catch { return null; }
                    }
                }
            }
            return null;
        }

        static string CombineUrl(string base_, string path)
        {
            // Share the Test button's logic so a URL that only works in one of
            // the common forms behaves the same in play as it does under test.
            List<string> c = ModUiPatch.ChatUrlCandidates(base_);
            return c.Count > 0 ? c[0] : "https://openrouter.ai/api/v1/chat/completions";
        }

        static string Safe(DownloadHandler dh)
        {
            try { return dh == null ? "" : Trim(dh.text, 800); }
            catch { return ""; }
        }

        static string Trim(string s, int max)
        {
            if (s == null) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "... [" + s.Length + " chars]";
        }
    }
}
