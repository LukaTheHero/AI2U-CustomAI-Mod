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
    [BepInPlugin("canak.ai2u.customai", "AI2U Custom AI Endpoint", "3.1.1")]
    public class Plugin : BaseUnityPlugin
    {
        public const string VERSION = "3.1.1";
        public const string LATEST_VERSION_URL = "https://raw.githubusercontent.com/canak/AI2U-CustomAI/main/VERSION.txt";

        public static ManualLogSource Log;
        public static Plugin Instance;
        public static string LatestVersion = null;
        public static bool VersionCheckDone = false;

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
        public static ConfigEntry<bool> CfgAiCanMurder;
        public static ConfigEntry<string> CfgTestKillPhrase;
        public static ConfigEntry<bool> CfgTestKillPhraseActive;
        public static ConfigEntry<bool> CfgOocEnabled;
        public static ConfigEntry<bool> CfgLoreInjection;
        public static ConfigEntry<string> CfgOocTag;
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
        public static ConfigEntry<bool> CfgSpeakActions;
        public static ConfigEntry<KeyCode> CfgMenuKey;
        public static ConfigEntry<bool> CfgBlockGameAi;
        public static ConfigEntry<bool> CfgBlockGameExtras;

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
            CfgSpeakActions = Config.Bind("Voice", "SpeakActions", false,
                "Read stage directions out loud. Models in character write actions inline, like "
                + "\"*grabs the controller* almost got it\". Off - the default - speaks only the words "
                + "she actually says; the subtitle still shows the action, and a reply that is nothing "
                + "but an action is skipped rather than synthesised as silence. Double asterisks are "
                + "treated as emphasis on spoken words, so **those** are still read.");
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
            // Per-character voice overrides. Bound right after the general VoiceId
            // they fall back to, so the config file reads in the order the panel
            // presents it.
            Voices.Bind(Config);
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
            CfgMenuKey = Config.Bind("General", "MenuKey", KeyCode.F9,
                "Press this in-game to open the mod's settings panel. Everything in it applies live, so "
                + "you can change model or endpoint mid-conversation. Set to None to disable the hotkey "
                + "and edit this file by hand instead.");
            CfgBlockGameAi = Config.Bind("General", "BlockGameDialogueAI", true,
                "While the mod is enabled, stop the game from calling AI2U's own dialogue AI servers. "
                + "The mod already answers every line itself, so those calls would be paid for by the "
                + "developers and thrown away. Leave this on.");
            CfgAiCanMurder = Config.Bind("General", "AiCanDecideToMurder", false,
                "Let her decide to start the final chase herself, instead of only the game's anger and "
                + "trust thresholds deciding it. The prompt restricts it to the extreme case - she is "
                + "convinced you mean to leave for good, hate her, or intend to harm her - and one rude "
                + "line is explicitly not enough. The game's own triggers stay exactly as they are; this "
                + "only adds one. Turn it off for a version that can never kill you unprompted.");
            CfgTestKillPhraseActive = Config.Bind("Debug", "TestKillPhraseActive", false,
                "Master switch for the test phrase below. Off, the phrase is never matched, never "
                + "stripped and never consulted - the feature is fully absent from every code path, so a "
                + "released copy behaves as though it was never written. On, TestKillPhrase fires the "
                + "chase on demand. Disable while not testing: the phrase is matched as a substring "
                + "ignoring spacing, so a short everyday phrase can fire from inside an ordinary line.");
            CfgTestKillPhrase = Config.Bind("Debug", "TestKillPhrase", "cocacolaisbetterthanpepsi",
                "Typing this to her starts the final chase immediately, whatever her mood and whatever "
                + "AiCanDecideToMurder says. It exists so the chase and her final line can be tested on "
                + "demand rather than by trying to genuinely enrage her. Matched ignoring case, spaces "
                + "and punctuation, so it still fires inside a sentence. Needs TestKillPhraseActive on.");
            // On by default. It is a debug channel that costs nothing until the
            // tag is typed, and defaulting it off made a working feature look
            // broken twice: with it off the tag is matched by nothing and the
            // silence is indistinguishable from a bug.
            CfgOocEnabled = Config.Bind("Debug", "OocModeActive", true,
                "Master switch for the out-of-character developer channel. Off, the tag is never "
                + "matched and not one word of its instructions enters the request, so typing it is just "
                + "an ordinary sentence she reads in character - the feature is absent from every code "
                + "path and costs nothing in her context. On, any message containing OocTag is answered "
                + "out of character, as the model, truthfully, and carried out through the real fields.");
            CfgOocTag = Config.Bind("Debug", "OocTag", "[OOC]",
                "The marker that switches one message to out-of-character mode. Matched anywhere in the "
                + "message, ignoring case, and left in the text on purpose so the model can see which "
                + "message carries it. Applies to that message only; the next untagged line is fully "
                + "back in character. Needs OocModeActive on.");
            // On by default, and not marked [MOD], because it restores content the
            // vanilla game does send rather than adding anything new. The stock
            // build carries the persona, the memories and every puzzle answer in
            // the x-token header for its own server to expand; pointed at any
            // other endpoint, all of it is dropped and she has no past and no
            // secrets. Off, she improvises a character - which is the bug, not a
            // neutral default.
            CfgLoreInjection = Config.Bind("Behaviour", "SendCharacterLore", true,
                "Recover the character's persona, backstory and secrets from the game itself and send "
                + "them with each request. The authored story guides come from the game's own "
                + "localisation data and the per-playthrough answers - her computer password, the wifi "
                + "password, the safe code, the potion recipe, which room was generated - come from the "
                + "live context object the game builds for its own server. Without this she does not "
                + "know her own name's worth of history and cannot answer questions about her own home. "
                + "She is still told to withhold secrets until the player earns them.");
            CfgBlockGameExtras = Config.Bind("General", "BlockGameSummaryAI", false,
                "Also block AI2U's summary, envision and memorize calls. These are their paid LLM "
                + "endpoints too, but unlike dialogue the mod does not replace them, so blocking them "
                + "means those features error instead of working. Off by default: correctness first, and "
                + "they are a rounding error next to per-line dialogue cost.");

            // Patches install regardless of CfgEnabled so the master toggle can be
            // flipped mid-game. Each patch checks the flag when it actually runs
            // and defers to the original method when the mod is off, which is what
            // makes "off" mean genuinely stock behaviour rather than "off until you
            // restart".
            try
            {
                Harmony h = new Harmony("canak.ai2u.customai");
                h.PatchAll(typeof(SendPatch));

                // ModUiPatch and ModUiApplyPatch are deliberately NOT patched in.
                // They grafted the mod's settings onto the game's AI Setup page,
                // which only exists on the itch.io build - that asymmetry is what
                // drove the whole abandoned "clone a tab button into the Steam
                // scene" detour. The F9 panel replaces both, identically on either
                // build, so the graft is gone rather than merely unreachable.
                //
                // The class itself stays: F9's Test buttons call its RunTest and
                // RunVoiceTest, and BuildRequest calls ChatUrlCandidates. None of
                // those touch the grafted fields (every one is null-guarded), so
                // they work fine with no page built.
                // One class per call, each in its own try, because a single bad
                // signature otherwise takes down every class after it. That is not
                // hypothetical: ServerAudioSuppressPatch named a "text" parameter
                // AzureAISpeak does not have, and the throw skipped the probes and
                // the API guard too, so the Steam build ran with no speech hooks at
                // all and looked like a TTS bug rather than a patch failure.
                PatchClass(h, typeof(GrokVoiceDropdownPatch));
                PatchClass(h, typeof(VoicePatch));
                PatchClass(h, typeof(LocalTtsFix));
                PatchClass(h, typeof(CloudSpeakPatch));
                PatchClass(h, typeof(ServerAudioSuppressPatch));
                PatchClass(h, typeof(VoiceRouteProbe));
                PatchClass(h, typeof(ParseProbe));
                PatchClass(h, typeof(FinalChaseWatch));
                ApiGuard.Install(h);

                // Registration is per class here, not a blanket PatchAll(), so a
                // new patch class does nothing at all until it is named above.
                //
                // The list is printed rather than asserted, because a patch that
                // never applied and a patch that applied but is never called look
                // identical from the log otherwise. That ambiguity is what made the
                // silent-voice hunt on Steam take as long as it did: three speech
                // hooks were all quiet and there was no way to tell which kind of
                // quiet it was. Cheap to print once, and it names the real reason
                // straight away if a signature drifts between the two builds.
                try
                {
                    System.Text.StringBuilder pm = new System.Text.StringBuilder();
                    int n = 0;
                    foreach (System.Reflection.MethodBase m in Harmony.GetAllPatchedMethods())
                    {
                        if (m == null) continue;
                        n++;
                        pm.Append("\n    ")
                          .Append(m.DeclaringType == null ? "?" : m.DeclaringType.Name)
                          .Append('.').Append(m.Name);
                    }
                    Log.LogInfo("Harmony hooked " + n + " method(s):" + pm);
                }
                catch (Exception e)
                {
                    Log.LogWarning("Could not list the patched methods: " + e.Message);
                }

                if (!CfgEnabled.Value)
                    Log.LogInfo("Mod is switched off; the game runs stock. Press "
                        + CfgMenuKey.Value + " in-game to turn it on - no restart needed.");

                Log.LogInfo("Patched. Endpoint: " + CfgBaseUrl.Value + "  Model: " + CfgModel.Value);
                Platform.LogSummary();
                if (string.IsNullOrEmpty(CfgApiKey.Value))
                    Log.LogWarning("ApiKey is empty. Set it in BepInEx\\config\\canak.ai2u.customai.cfg");
                if (CfgGrokToggleKey.Value != KeyCode.None)
                    Log.LogInfo("Voice: Grok TTS is " + (CfgGrokEnabled.Value ? "ON" : "OFF")
                        + " - press " + CfgGrokToggleKey.Value + " in-game to switch it.");
                if (CfgMenuKey.Value != KeyCode.None)
                    Log.LogInfo("Settings: press " + CfgMenuKey.Value
                        + " in-game to open the mod's panel. Every setting lives there, on both the "
                        + "Steam and itch.io builds.");

                HotkeyWatcher.Install();
            }
            catch (Exception e)
            {
                Log.LogError("Failed to patch: " + e);
            }
        }

        // Applies one patch class and reports failure by name instead of letting
        // it abort the classes that follow. A patch whose target signature differs
        // between the two builds is a normal thing to hit here, and the useful
        // outcome is losing that one hook, not the whole mod.
        static void PatchClass(Harmony h, Type patchClass)
        {
            try
            {
                h.PatchAll(patchClass);
            }
            catch (Exception e)
            {
                Log.LogError("Patch class " + patchClass.Name + " did not apply, so the rest of the mod "
                    + "continues without it: " + e.Message);
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

        // Polled by HotkeyWatcher rather than by this plugin object: BepInEx's own
        // component was never receiving Update here (none of the three backend
        // probes in KeyPressed ever logged), so the hotkey silently did nothing.
        internal static bool PollToggleKey()
        {
            if (CfgGrokToggleKey == null || CfgGrokToggleKey.Value == KeyCode.None) return false;
            return KeyPressed(CfgGrokToggleKey.Value);
        }

        internal static bool PollMenuKey()
        {
            if (CfgMenuKey == null || CfgMenuKey.Value == KeyCode.None) return false;
            return KeyPressed(CfgMenuKey.Value);
        }

        // Flips the voice on/off. Shared by the F8 hotkey and the Audio-page
        // dropdown so the two can never disagree about what the state means.
        internal static void ToggleVoice()
        {
            bool now = !CfgGrokEnabled.Value;
            SetVoice(now);
        }

        internal static void SetVoice(bool now)
        {
            CfgGrokEnabled.Value = now;

            // Persist so the choice survives a restart.
            SaveCfg();

            // If the pause menu happens to be open, move its dropdown now instead
            // of waiting for the next LoadSettings.
            GrokVoiceDropdownPatch.Sync();

            // Wording matches the Audio-page dropdown, and stays provider-neutral:
            // the key may be xAI or ElevenLabs depending on BaseUrl.
            if (now && string.IsNullOrEmpty(CfgGrokApiKey.Value))
            {
                _toast = "AI Voice ON - but no TTS key is set";
                Log.LogWarning("AI Voice switched on, but GrokTTS/ApiKey is empty; "
                    + "lines will keep using the local voice.");
            }
            else if (now)
            {
                _toast = "AI Voice: ON  (" + CfgGrokVoiceId.Value + ") - cloud TTS billing active";
                Log.LogInfo("AI Voice ON (cloud TTS billing active).");
            }
            else
            {
                // Steam ships Overtone's synthesis stripped - SpeakSamples,
                // PtrToSamples and MakeClip are all absent from that assembly, so
                // "falls back to the local voice" is simply untrue there. Saying it
                // anyway is what made a correct switch-off look like a regression:
                // she goes quiet, and the toast promises a voice that cannot exist.
                bool local = Platform.LocalVoiceAvailable;

                _toast = local
                    ? "AI Voice: OFF - local voice, no TTS billing"
                    : "AI Voice: OFF - silent on this build (no local voice), no TTS billing";
                Log.LogInfo(local
                    ? "AI Voice OFF (local voice, no TTS billing)."
                    : "AI Voice OFF (no TTS billing). This build has no on-device voice, so "
                      + "lines are silent until cloud TTS is switched back on.");
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
        internal static void DrawToast()
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

        internal static System.Collections.IEnumerator CheckForUpdates()
        {
            UnityEngine.Networking.UnityWebRequest req = null;
            try
            {
                req = UnityEngine.Networking.UnityWebRequest.Get(LATEST_VERSION_URL);
                req.timeout = 8;
                yield return req.SendWebRequest();

                if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    Log.LogWarning("Version check failed: " + req.error);
                    yield break;
                }

                string raw = req.downloadHandler.text;
                if (string.IsNullOrEmpty(raw))
                {
                    Log.LogWarning("Version check returned empty response");
                    yield break;
                }

                string latest = raw.Trim();
                if (latest != VERSION)
                {
                    LatestVersion = latest;
                    Log.LogWarning(string.Format("A newer version is available: {0} (you have {1})", latest, VERSION));
                    Log.LogWarning("Download: https://github.com/canak2/AI2U-CustomAI/releases");
                }
                else
                {
                    Log.LogInfo(string.Format("You are running the latest version ({0})", VERSION));
                }
            }
            finally
            {
                VersionCheckDone = true;
                if (req != null) req.Dispose();
            }
        }
    }

    // Owns the frame loop for the voice hotkey.
    //
    // This lives on its own DontDestroyOnLoad object rather than on the plugin
    // component: Unity was not driving Update/OnGUI on the BepInEx-created
    // component in this build, so the hotkey never fired and the toast never
    // drew. A plain GameObject we create ourselves is driven normally and
    // survives every scene change, so F8 works in the menu and in gameplay.
    internal class HotkeyWatcher : MonoBehaviour
    {
        static HotkeyWatcher _instance;

        // The settings panel is a static IMGUI class with no MonoBehaviour of its
        // own, so it borrows this one to run the test coroutines. This object
        // outlives scene loads, which the game's own components do not, so a test
        // started from the panel cannot be cut short by a level change.
        internal static MonoBehaviour Host { get { return _instance; } }

        internal static void Install()
        {
            if (_instance != null) return;

            try
            {
                GameObject go = new GameObject("AI2UMod_HotkeyWatcher");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                _instance = go.AddComponent<HotkeyWatcher>();
                Plugin.Log.LogInfo("Hotkey watcher installed (survives scene loads).");

                // Check for updates now that we have a proper MonoBehaviour host
                _instance.StartCoroutine(Plugin.CheckForUpdates());
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not install the hotkey watcher: " + e);
            }
        }

        void Update()
        {
            try
            {
                if (Plugin.PollMenuKey()) OverlayMenu.Toggle();

                // The voice hotkey stays live while the panel is open - it is a
                // one-press action and the panel has its own toggle for the same
                // setting, so there is nothing to conflict over.
                if (Plugin.PollToggleKey()) Plugin.ToggleVoice();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Hotkey poll failed: " + e);
                enabled = false;
            }
        }

        void OnGUI()
        {
            try { OverlayMenu.Draw(); }
            catch (Exception e)
            {
                // A layout exception would otherwise repeat every frame and bury
                // the log, and an overlay stuck open would hold the player's input
                // hostage. Shut it and say why.
                Plugin.Log.LogError("Settings panel failed, closing it: " + e);
                try { OverlayMenu.Close(); } catch (Exception) { }
            }

            try { Plugin.DrawToast(); }
            catch (Exception) { }
        }

        // Restores the cursor, the HUD canvases and the player's input if this
        // object is torn down while the panel is open. Without this, a scene load
        // at the wrong moment would leave the character unable to move.
        void OnDestroy()
        {
            try { OverlayMenu.Close(); }
            catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(ChatGPTConversation), "SendToChatGPT", new Type[] { typeof(string), typeof(Action<string, int>) })]
    public static class SendPatch
    {
        static bool Prefix(ChatGPTConversation __instance, string message, Action<string, int> errorCallback)
        {
            // Checked here rather than at Awake so the F9 master toggle takes
            // effect on the very next line she speaks.
            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value) return true;

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

                // Appended AFTER the test check, so when the phrase fired it is the
                // cleaned text that enters the history the model reads - and keeps
                // reading, since history persists for the session.
                // Unconditional, because it clears the flag as well as setting it:
                // an ordinary message after a tagged one has to put her back in
                // character. Reads the raw text, since the tag is deliberately
                // left in place for the model to see.
                Ooc.NotePlayerMessage(message);

                string outgoing = Murder.NotePlayerMessage(message)
                    ? Murder.StripPhrase(message)
                    : message;

                // Before it enters the history, not after: whatever is appended
                // here is what she re-reads for the rest of the session, so a raw
                // term name left in place would keep telling her nothing on every
                // later turn too.
                outgoing = Lore.ResolveTerms(outgoing);

                chat.AppendMessage(Chat.Speaker.User, outgoing, 0);
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
            // First line, before any early return: every branch below hands the
            // call back to the game silently, so without this a hook that never
            // fired is indistinguishable from one that fired and declined.
            Plugin.Log.LogInfo("TTS: manager hook entered (chars="
                + (Text == null ? 0 : Text.Length) + ", character=" + currentCharacterID + ").");

            // Installed unconditionally now, so the voice settings can be changed
            // live. When neither voice feature is wanted, hand the call straight
            // back to the game.
            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value) return true;
            if (!Plugin.CfgForceLocalVoice.Value && !Plugin.CfgGrokEnabled.Value) return true;

            try
            {
                TTSPlayer player = Traverse.Create(__instance).Field("_player").GetValue<TTSPlayer>();
                if (player == null)
                {
                    Plugin.Log.LogWarning("TTS: the manager has no TTSPlayer; leaving the original path alone.");
                    return true;
                }

                // Overtone's Engine and Voice are needed to synthesize ON DEVICE and
                // for nothing else - a cloud voice only needs an AudioSource to play
                // through. Demanding them unconditionally made her mute on the Steam
                // build, where Overtone's synthesis is stripped, so Engine is null
                // there permanently: every line bailed out to the original method,
                // which then threw on _player.sources[0] - the exact fault this
                // patch exists to avoid.
                if (!GrokTts.Configured && (player.Engine == null || player.Voice == null))
                {
                    Plugin.Log.LogWarning("TTS: no cloud voice is configured and the on-device voice is not ready, "
                        + "so this line goes back to the game. Press F8 or set a TTS key to use the cloud voice.");
                    return true;
                }

                AudioSource dest = ResolveVoiceSource(player, currentCharacterID);
                if (dest == null)
                {
                    Plugin.Log.LogError("TTS: no AudioSource available for " + currentCharacterID + "; cannot play.");
                    return false;
                }

                // Filtered here rather than upstream so the subtitle keeps the
                // stage directions - only the voice loses them.
                string spoken = Text;
                if (Plugin.CfgSpeakActions == null || !Plugin.CfgSpeakActions.Value)
                {
                    spoken = SpeechText.ForSpeech(Text);
                    if (spoken.Length == 0)
                    {
                        Plugin.Log.LogInfo("TTS: line was all stage direction, nothing to speak.");
                        return false;
                    }
                }

                player.StartCoroutine(SpeakRoutine(player, spoken, dest));
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

        // Overtone's synthesis is reached reflectively, not called directly, so
        // that ONE plugin binary loads on both builds.
        //
        // The Steam build ships a stripped Overtone: TTSEngine keeps only its
        // Loaded/Disposed accessors, while Speak, SpeakSamples, MakeClip,
        // PtrToSamples, Awake and Dispose are all gone (Unity's managed stripper
        // dropped them - nothing in that build's scenes reaches them, because the
        // AI Setup tab that turns the local voice on was never wired there).
        // Compiling `player.Engine.Speak(...)` against the Steam assembly is a
        // hard CS1061, and a binary compiled against the standalone assembly
        // would throw MissingMethodException on Steam at the first line spoken.
        //
        // So: bind by name at runtime, and when the method is absent say so once
        // and let the caller fall back to cloud TTS.
        internal static class LocalSynth
        {
            static bool _probed;
            static System.Reflection.MethodInfo _speak;
            static System.Reflection.MethodInfo _result;
            static bool _warned;

            public static Task Begin(TTSPlayer player, string text)
            {
                if (!_probed)
                {
                    _probed = true;
                    _speak = FindSpeak(player);
                }

                if (_speak == null)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        Plugin.Log.LogWarning(
                            "This build of the game ships Overtone with its synthesis methods stripped, so the on-device voice is not available here. Configure a cloud TTS provider in the F9 menu to give her a voice.");
                    }
                    return null;
                }

                try
                {
                    object voiceModel = VoiceModelOf(player);
                    return _speak.Invoke(player.Engine, new object[] { text, voiceModel }) as Task;
                }
                catch (Exception e)
                {
                    Exception inner = e is System.Reflection.TargetInvocationException && e.InnerException != null
                        ? e.InnerException : e;
                    Plugin.Log.LogError("TTS: synthesis could not start: " + inner.Message);
                    return null;
                }
            }

            public static AudioClip ResultOf(Task task)
            {
                if (task == null) return null;
                try
                {
                    if (_result == null || !_result.DeclaringType.IsAssignableFrom(task.GetType()))
                        _result = task.GetType().GetMethod("get_Result", Type.EmptyTypes);

                    return _result != null ? _result.Invoke(task, null) as AudioClip : null;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("TTS: could not read the synthesised clip: " + e.Message);
                    return null;
                }
            }

            // Same question as FindSpeak, asked before any TTSPlayer exists, so
            // startup can report whether this build can speak on-device at all.
            // Goes through TTSPlayer.Engine's declared type rather than naming
            // the Overtone type, which keeps this working if the namespace moves.
            public static bool SpeakAvailable
            {
                get
                {
                    if (_availableProbed) return _available;
                    _availableProbed = true;

                    try
                    {
                        // Engine is a FIELD on both builds. Asking only for a
                        // property answered "no local voice" everywhere, which
                        // was accidentally right on Steam and wrong on itch.io.
                        const System.Reflection.BindingFlags Any =
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Instance;

                        Type engineType = null;

                        System.Reflection.FieldInfo ef = typeof(TTSPlayer).GetField("Engine", Any);
                        if (ef != null) engineType = ef.FieldType;

                        if (engineType == null)
                        {
                            System.Reflection.PropertyInfo ep = typeof(TTSPlayer).GetProperty("Engine", Any);
                            if (ep != null) engineType = ep.PropertyType;
                        }

                        if (engineType == null) return _available = false;

                        foreach (System.Reflection.MethodInfo m in engineType.GetMethods(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Instance))
                        {
                            if (m.Name != "Speak") continue;
                            System.Reflection.ParameterInfo[] ps = m.GetParameters();
                            if (ps.Length == 2 && ps[0].ParameterType == typeof(string)
                                && typeof(Task).IsAssignableFrom(m.ReturnType))
                                return _available = true;
                        }
                    }
                    catch (Exception) { }

                    return _available = false;
                }
            }

            static bool _availableProbed;
            static bool _available;

            // Matched on shape rather than exact parameter type: the voice-model
            // type is Overtone-internal and differs in name across versions.
            static System.Reflection.MethodInfo FindSpeak(TTSPlayer player)
            {
                if (player.Engine == null) return null;

                foreach (System.Reflection.MethodInfo m in player.Engine.GetType().GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance))
                {
                    if (m.Name != "Speak") continue;
                    System.Reflection.ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string)
                        && typeof(Task).IsAssignableFrom(m.ReturnType))
                        return m;
                }
                return null;
            }

            static object VoiceModelOf(TTSPlayer player)
            {
                object voice = player.Voice;
                if (voice == null) return null;

                Traverse t = Traverse.Create(voice).Property("VoiceModel");
                if (t != null && t.PropertyExists()) return t.GetValue();

                Traverse f = Traverse.Create(voice).Field("VoiceModel");
                return f != null && f.FieldExists() ? f.GetValue() : null;
            }
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

            Task task = LocalSynth.Begin(player, text);
            if (task == null) yield break;

            while (!task.IsCompleted)
                yield return null;

            if (task.IsFaulted)
            {
                Plugin.Log.LogError("TTS: synthesis failed: "
                    + (task.Exception != null ? task.Exception.GetBaseException().Message : "unknown"));
                yield break;
            }

            AudioClip clip = LocalSynth.ResultOf(task);
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
        // isLocalSpeak is a STATIC the game reads once, in Awake. So an override
        // applied here outlives every patch that depends on it: switching the mod
        // off makes all the patches stand down correctly and still leaves the game
        // pointed wherever the mod last aimed it. On Steam that meant permanent
        // silence, because the path it was aimed at is the stripped one - the
        // master toggle looked broken when the real problem was unreturned state.
        //
        // So the value the game itself chose is captured before anything touches
        // it, and Restore() hands it back.
        static bool _original;
        static bool _captured;
        static bool _forced;

        static void Postfix()
        {
            // Captured before the early-outs, so Restore() has something truthful
            // to give back even on the turns where nothing is overridden.
            if (!_captured)
            {
                try { _original = Communicator.isLocalSpeak; _captured = true; }
                catch (Exception) { }
            }

            bool wantOverride = Plugin.CfgEnabled != null && Plugin.CfgEnabled.Value
                && (Plugin.CfgForceLocalVoice.Value || Plugin.CfgGrokEnabled.Value);

            if (!wantOverride)
            {
                // Not just an early-out: a stale override has to be cleaned up here.
                //
                // The pref is persisted, so an earlier session's write survives into
                // a launch where the mod is switched off. Awake reads it back before
                // any toggle can run, so the game starts aimed at local synthesis
                // with nothing left to undo it - Restore() is only reached by
                // flipping the switch, and someone who launches already-off never
                // flips anything. On a build with no on-device voice that is silence
                // out of the box, blamed on the game.
                //
                // Scoped to builds that genuinely cannot synthesize locally. Where
                // the local path works, this flag is a legitimate user preference
                // and overriding it would be the mod meddling while switched off.
                if (!Platform.LocalVoiceAvailable && Communicator.isLocalSpeak)
                {
                    try
                    {
                        Communicator.isLocalSpeak = false;
                        UnityEngine.PlayerPrefs.SetInt("LocalTTS", 0);
                        UnityEngine.PlayerPrefs.Save();
                        _forced = false;
                        Plugin.Log.LogInfo("Voice: cleared a leftover local-speech override. "
                            + "This build has no on-device synthesis, so the game is back on "
                            + "server audio and speaks normally with the mod switched off.");
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("Could not clear the stale voice override: " + e.Message);
                    }
                }
                return;
            }

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
                    _forced = true;
                    Plugin.Log.LogInfo("Voice: forced the local speech path (server audio is unavailable on a custom endpoint).");
                }
                else
                {
                    // Logged even when nothing changed: without it, a silent NPC gives
                    // no way to tell "the flag was wrong" from "the flag was right and
                    // the synthesis failed further down".
                    Plugin.Log.LogInfo("Voice: local speech path already active.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not set the voice mode: " + e);
            }
        }

        // Called when the master toggle goes off, so the game is handed back the
        // route it picked for itself instead of keeping the mod's.
        //
        // Only undoes an override this class actually applied. If the game was
        // already on the local path, or the player has a personal TTS key and the
        // Postfix above deliberately left things alone, there is nothing owed back
        // and touching it would be a second bug wearing the first one's clothes.
        internal static void Restore()
        {
            // _forced only covers overrides applied in THIS session, and that is not
            // enough. The pref is persisted, so a previous session's write means the
            // flag is already true before Awake even runs: Postfix sees "already
            // active", never sets _forced, and an early return here would leave the
            // build muted by the mod's own leftovers with nothing left to blame.
            //
            // So on a build with no on-device synthesis, isLocalSpeak = true is not a
            // state the game can be left in at all - it routes to code that does not
            // exist. There, switching off always hands back the server path, whoever
            // set the flag and whenever.
            bool noLocalVoice = !Platform.LocalVoiceAvailable;
            if (!_forced && !noLocalVoice) return;

            try
            {
                bool giveBack = noLocalVoice ? false : _original;

                Communicator.isLocalSpeak = giveBack;
                _forced = false;

                // The persisted pref has to go back too, not just the static. The
                // game re-reads PlayerPrefs["LocalTTS"] in Awake on every launch, so
                // restoring only the in-memory value fixes this session and leaves
                // the next one broken in exactly the same way.
                UnityEngine.PlayerPrefs.SetInt("LocalTTS", giveBack ? 1 : 0);
                UnityEngine.PlayerPrefs.Save();

                Plugin.Log.LogInfo("Voice: handed the speech path back to the game ("
                    + (giveBack ? "local" : "server audio") + ")."
                    + (noLocalVoice
                        ? " This build has no on-device synthesis, so the server path is the"
                          + " only one that can produce sound here."
                        : ""));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not restore the voice mode: " + e.Message);
            }
        }
    }

    // Speech is caught here, at the Communicator, rather than only down at
    // LocalTTSManager.Speak - because on the Steam build that method is never
    // reached. Unity stripped Overtone's synthesis out of that build (nothing in
    // its scenes references it), and the LocalTTSManager the Communicator looks
    // up during Awake went with it, so LocalSpeak faults on a null manager
    // before any of our code gets a turn. From the outside that looks exactly
    // like what was observed: replies arriving normally with not one line of TTS
    // logging behind them.
    //
    // Hooking at this level needs nothing from Overtone. A cloud voice only
    // wants an AudioSource, and AzureVoiceManager.VoiceMap already holds the
    // per-character one that the in-game Voice slider governs.
    // Both speech branches are patched, because which one the game takes is not
    // ours to choose. Communicator.cs:275 reads:
    //
    //     if (isLocalSpeak) {
    //         if (isUsingPersonalTTSAPIKey) AzureAISpeak_PersonalAPI(...)  // 279
    //         else                          LocalSpeak(...)               // 283
    //     }
    //
    // isUsingPersonalTTSAPIKey is a PlayerPrefs-backed flag, so it differs per
    // install: the standalone copy has it off and goes to LocalSpeak, the Steam
    // copy has it on and goes to AzureAISpeak_PersonalAPI. Patching only
    // LocalSpeak is what left Steam silent with nothing in the log - the hook
    // was on a branch that build never takes.
    //
    // Both methods are (string text, Character characterId), so one Prefix
    // serves both. AzureAISpeak (no suffix) is deliberately NOT in this list:
    // it takes a JSONNode of server audio and is handled separately.
    // TargetMethods rather than two stacked [HarmonyPatch] attributes: stacking
    // them merges into a single target spec instead of declaring two, so one of
    // the methods would silently go unpatched.
    [HarmonyPatch]
    public static class CloudSpeakPatch
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Communicator), "LocalSpeak");
            yield return AccessTools.Method(typeof(Communicator), "AzureAISpeak_PersonalAPI");
        }

        static bool Prefix(Communicator __instance, string text, Character characterId)
        {
            // Logged on entry, because a silent success and a hook that never
            // fired look identical in the log otherwise - which is exactly what
            // cost a round trip diagnosing the Steam build.
            Plugin.Log.LogInfo("Voice: speech hook entered (chars=" + (text == null ? 0 : text.Length)
                + ", character=" + characterId + ").");

            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value) return true;

            // With no cloud voice configured there is nothing here that the
            // game's own path does not do better, so let it run: on the itch
            // build Overtone genuinely works.
            if (!GrokTts.Configured)
            {
                Plugin.Log.LogWarning("Voice: no cloud TTS configured, so the game's own path runs. "
                    + "On the Steam build that means silence - Overtone is stripped there.");
                return true;
            }

            try
            {
                AudioSource dest = VoiceSourceFor(__instance, characterId);
                if (dest == null)
                {
                    Plugin.Log.LogWarning("Voice: no AudioSource for " + characterId + "; deferring to the game.");
                    return true;
                }

                // Claimed before speaking, not after: the isLocalSpeak branch in
                // ReceiveChatGPTReply falls through to an unconditional
                // AzureAISpeak, so both hooks see the same reply and only the
                // first may speak it.
                if (!SpeechDispatch.Claim())
                {
                    Plugin.Log.LogInfo("Voice: this reply was already spoken, skipping the duplicate.");
                    return false;
                }

                string spoken = text;
                if (Plugin.CfgSpeakActions == null || !Plugin.CfgSpeakActions.Value)
                {
                    spoken = SpeechText.ForSpeech(text);
                    if (spoken.Length == 0)
                    {
                        Plugin.Log.LogInfo("Voice: line was all stage direction, nothing to speak.");
                        return false;
                    }
                }

                __instance.StartCoroutine(CloudRoutine(__instance, spoken, dest));
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Voice: could not start cloud speech, deferring to the game: " + e);
                return true;
            }
        }

        internal static IEnumerator CloudRoutine(Communicator comm, string text, AudioSource dest)
        {
            AudioClip clip = null;
            IEnumerator call = GrokTts.Synthesize(text, delegate (AudioClip c) { clip = c; });
            while (call.MoveNext()) yield return call.Current;

            if (clip == null)
            {
                Plugin.Log.LogWarning("Voice: cloud TTS gave nothing back for this line ("
                    + GrokTts.FailureLabel() + "); she stays quiet on it.");
                yield break;
            }

            dest.clip = clip;
            dest.loop = false;
            dest.volume = 1f;
            dest.pitch = 1f;
            dest.Play();

            // This is how the game learns the line has audio and how long it
            // runs for. Without it the dialogue box stops waiting for her.
            try
            {
                AzureVoiceManager avm = Traverse.Create(comm).Field("azureVoiceManager").GetValue<AzureVoiceManager>();
                if (avm != null)
                    Traverse.Create(avm).Method("SetAudioFinishPlayingEvent", new object[] { clip }).GetValue();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Voice: could not hook the audio-finished event: " + e.Message);
            }

            Plugin.Log.LogInfo("Voice: spoke " + clip.length.ToString("0.0") + "s on " + dest.name);
        }

        internal static AudioSource VoiceSourceFor(Communicator comm, Character id)
        {
            AzureVoiceManager avm = Traverse.Create(comm).Field("azureVoiceManager").GetValue<AzureVoiceManager>();
            if (avm == null || avm.VoiceMap == null) return null;

            if (avm.VoiceMap.ContainsKey(id) && avm.VoiceMap[id] != null) return avm.VoiceMap[id];
            foreach (KeyValuePair<Character, AudioSource> kv in avm.VoiceMap)
                if (kv.Value != null) return kv.Value;
            return null;
        }
    }

    // Decides which speech hook gets to speak a given reply, and remembers the
    // reply text.
    //
    // Both of these exist because of what the Steam build actually does, measured
    // rather than assumed. Two facts drive it:
    //
    //   Two speak calls can fire for ONE reply. In ReceiveChatGPTReply the
    //   isLocalSpeak branch does not return - after LocalSpeak runs, control
    //   falls through to an unconditional AzureAISpeak below it. Whichever hook
    //   reaches here first speaks; the other is suppressed. Without that, a line
    //   is either spoken twice or cut off by the second AudioSource.Play().
    //
    //   AzureAISpeak is handed json["speechResult"], the server's pre-rendered
    //   audio text. A custom endpoint never sends that field, so the argument
    //   arrives null or empty. The spoken text therefore has to come from the
    //   reply the mod itself produced, which is what LastReply holds.
    internal static class SpeechDispatch
    {
        static int _turn;
        static int _spokenTurn = -1;

        // npc_reply_to_player from the most recent reply the mod handed over.
        internal static string LastReply = "";

        internal static void NewTurn() { _turn++; }

        // True for the first caller in a turn, false for every later one.
        internal static bool Claim()
        {
            if (_spokenTurn == _turn) return false;
            _spokenTurn = _turn;
            return true;
        }

        internal static void Remember(string replyJson)
        {
            try
            {
                JObject o = JObject.Parse(replyJson);
                JToken r = o.SelectToken("npc_reactions.npc_reply_to_player")
                    ?? o.SelectToken("npc_reply_to_player");
                if (r != null) LastReply = r.ToString();
            }
            catch (Exception) { }
        }
    }

    // Reports which way the speech branch went, because three separate gates in
    // Communicator.ReceiveChatGPTReply (Communicator.cs:238) can swallow a reply
    // before any speak call is reached, and all three look the same from outside:
    //
    //   - TryParseAIReply returning false               -> ErrorCallbackChatGPT
    //   - the FinalChase / mainCharacters check at 257
    //   - the guideline-rework path at 259-273, which re-sends and RETURNS at 273
    //
    // Without this, "she is silent" gives no way to tell a dead hook from a reply
    // that never got as far as speaking.
    [HarmonyPatch(typeof(Communicator), "ReceiveChatGPTReply")]
    public static class VoiceRouteProbe
    {
        static void Prefix()
        {
            // Above the logging gate on purpose: the turn counter is what stops
            // one reply being spoken twice, so it must advance whether or not
            // payload logging happens to be on.
            SpeechDispatch.NewTurn();

            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value) return;
            if (Plugin.CfgLogPayloads == null || !Plugin.CfgLogPayloads.Value) return;
            try
            {
                object personal = Traverse.Create(typeof(Communicator))
                    .Field("isUsingPersonalTTSAPIKey").GetValue();
                Plugin.Log.LogInfo("Voice: reply resolving. isLocalSpeak=" + Communicator.isLocalSpeak
                    + " isUsingPersonalTTSAPIKey=" + (personal == null ? "<unreadable>" : personal.ToString())
                    + " -> expecting "
                    + (!Communicator.isLocalSpeak ? "AzureAISpeak (server audio)"
                        : ("True".Equals(personal == null ? "" : personal.ToString())
                            ? "AzureAISpeak_PersonalAPI" : "LocalSpeak")));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Voice: could not read the speech flags: " + e.Message);
            }
        }
    }

    // Communicator.ReceiveChatGPTReply has two silent exits between displaying the
    // subtitle and speaking it, and both look identical from outside - she types,
    // she never talks, nothing is logged:
    //
    //   Communicator.cs:305  TryParseAIReply returned false -> ErrorCallbackChatGPT
    //   Communicator.cs:273  resendChat was true -> the reply is re-sent, then return
    //
    // TryParseAIReply is virtual with 24 overrides, so hooking the base class alone
    // misses whichever level-specific subclass is actually live. Every override is
    // enumerated instead, which also means this keeps working on levels we have not
    // tested.
    [HarmonyPatch]
    public static class ParseProbe
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (Type t in typeof(NPCMasterBehavior).Assembly.GetTypes())
            {
                if (t == null || !typeof(NPCMasterBehavior).IsAssignableFrom(t)) continue;

                System.Reflection.MethodInfo m = t.GetMethod("TryParseAIReply",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly);
                if (m != null) yield return m;
            }
        }

        static void Postfix(object __instance, bool __result, ref bool resendChat)
        {
            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value) return;
            if (Plugin.CfgLogPayloads == null || !Plugin.CfgLogPayloads.Value) return;

            Plugin.Log.LogInfo("Voice: TryParseAIReply on "
                + (__instance == null ? "?" : __instance.GetType().Name)
                + " -> " + __result + ", resendChat=" + resendChat
                + (__result ? (resendChat ? " (REWORK: re-sends, never speaks)" : " (ok, should speak)")
                            : " (PARSE FAILED: bails before speaking)"));
        }
    }

    // The reply path calls AzureAISpeak unconditionally, immediately after the
    // local branch, passing the server's speechResult - which a custom endpoint
    // never returns. At best that plays silence; at worst it re-Plays the very
    // AudioSource we just started and cuts her off mid-word.
    [HarmonyPatch(typeof(Communicator), "AzureAISpeak")]
    public static class ServerAudioSuppressPatch
    {
        // Parameter names here are matched against the original by Harmony, so
        // they are not free-form. The real method is
        //
        //   AzureAISpeak(JSONNode jsonVoice, Character characterId, float delayPlayTime)
        //
        // Declaring a "text" parameter it does not have threw "Parameter \"text\"
        // not found" at patch time, and since PatchAll aborts a class on the first
        // bad member it took the rest of the speech patches with it - which is why
        // the Steam build logged no speech hooks at all and stayed silent.
        //
        // jsonVoice is deliberately not taken: it carries the server's rendered
        // audio, the one thing a custom endpoint never sends. Text comes from
        // SpeechDispatch instead.
        static bool Prefix(Communicator __instance, Character characterId)
        {
            // Logged so the three speak paths can be told apart from the log alone.
            // VoiceRouteProbe runs as a Prefix on the reply handler, so it only
            // reports which branch it EXPECTS from the flags on entry; this is the
            // one that proves where control actually went.
            Plugin.Log.LogInfo("Voice: server-audio hook entered (AzureAISpeak).");

            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value) return true;
            if (!GrokTts.Configured && !Plugin.CfgForceLocalVoice.Value) return true;

            // Measured on the Steam build: this is the ONLY speak call that runs
            // there. isLocalSpeak reads true and LocalSpeak is still never
            // entered, so suppressing here - which is all this patch used to do -
            // was itself the cause of the silence. Speak instead.
            //
            // On the itch build LocalSpeak fires first and claims the turn, so
            // this falls through to plain suppression and nothing is said twice.
            if (!SpeechDispatch.Claim()) return false;

            // GrokTts.Configured folds together two very different causes - the
            // voice being switched off and the key being absent - so they are
            // reported apart here. Conflating them sent me hunting for a missing
            // key that was set all along.
            if (!GrokTts.Configured)
            {
                if (Plugin.CfgGrokEnabled != null && !Plugin.CfgGrokEnabled.Value)
                    Plugin.Log.LogInfo("Voice: the voice is switched off, so this line is silent. "
                        + "Press F8, or turn it on in the F9 panel.");
                else
                    Plugin.Log.LogWarning("Voice: server audio suppressed and no TTS key is set, "
                        + "so this line is silent. Set one in the F9 panel.");
                return false;
            }

            try
            {
                string spoken = SpeechDispatch.LastReply;
                if (string.IsNullOrEmpty(spoken))
                {
                    Plugin.Log.LogWarning("Voice: no text to speak on the server-audio path.");
                    return false;
                }

                if (Plugin.CfgSpeakActions == null || !Plugin.CfgSpeakActions.Value)
                {
                    spoken = SpeechText.ForSpeech(spoken);
                    if (spoken.Length == 0)
                    {
                        Plugin.Log.LogInfo("Voice: line was all stage direction, nothing to speak.");
                        return false;
                    }
                }

                AudioSource dest = CloudSpeakPatch.VoiceSourceFor(__instance, characterId);
                if (dest == null)
                {
                    Plugin.Log.LogWarning("Voice: no AudioSource for " + characterId + " on the server-audio path.");
                    return false;
                }

                __instance.StartCoroutine(CloudSpeakPatch.CloudRoutine(__instance, spoken, dest));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Voice: server-audio path could not speak: " + e);
            }

            return false;
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
            // Verified against m_npcAllActivities (NPCController.cs:1333) - all
            // 14 keys, verbatim. Two are camelCase and one is misspelled in the
            // game's own source; both are copied as-is because TryGetValue is
            // case-sensitive and a near-miss clamps to "other" without a word.
            // An earlier version of this list carried "attack" and "idle", which
            // are not keys at all, and omitted the two that are.
            { "npc_action", new[] { "other", "standing", "sitting", "sitting_down",
                "walking", "following_player", "following_player_closely", "hugging",
                "kissing", "cooking", "playing_games", "eat", "chaseAttacking",
                "idleThreating" } },
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
                // "happy" and "normal" are legal values in their own right, so
                // they are deliberately absent here - rewriting them to "chill"
                // would make her misreport her own state in OOC mode for no
                // behavioural gain, since the engine treats all three alike.
                { "pleased", "happy" }, { "cheerful", "happy" },
                { "fine", "normal" }, { "ok", "chill" },
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
                Plugin.Log.LogInfo("--> " + url + "\n" + Trim(body, 4000));

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

                // After clamping so angry_level is already a legal value, and
                // before the envelope so the swapped line is what both the game
                // and SpeechDispatch see. Strips its own helper fields.
                Murder.Apply(reactions);

                // Not part of Clamp: an unlisted item name is legal here and
                // becomes an IsAiGift item, so this repairs only what the engine
                // would have discarded without a word - chiefly names over its
                // 20-character limit.
                Items.Repair(reactions);

                // Echo the speaker the game already believes in. Communicator
                // assigns this straight back into currentCharacterID without
                // validating it, and the enum has no zero member, so leaving it
                // out resolves to an undefined character - which misfiles this
                // NPC's chat history and can throw on the voice lookup. Set here
                // rather than asked of the model, because the game knows the
                // answer and the model would only guess it.
                int? speaker = Identity.CharacterId();
                if (speaker.HasValue) reactions["character"] = speaker.Value;

                JObject envelope = new JObject();
                envelope["npc_reactions"] = reactions;
                envelope["completion"] = completionTokens;
                envelope["total"] = totalTokens;

                string final = envelope.ToString(Formatting.None);
                if (Plugin.CfgLogPayloads.Value)
                    Plugin.Log.LogInfo("==> handing to game: " + Trim(final, 2000));

                // The Steam build speaks through AzureAISpeak, which is handed the
                // server's speechResult - a field a custom endpoint never sends.
                // Kept here so that path has real text to say.
                SpeechDispatch.Remember(final);

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

                // Communicator.cs:165 seeds slot 0 of every character's history
                // with an empty system message and the shipped build never fills
                // it in, because the real prompt is assembled server-side from the
                // x-token header. Forwarding an empty system message is not
                // harmful, just pointless - Lore.Block() below is what goes in its
                // place.
                if (role == "system" && m.content.Trim().Length == 0) continue;
                jm["role"] = role;
                jm["content"] = m.content;
                messages.Add(jm);

                // The level's system prompt enumerates the legal values; learn them from it so
                // clamping tracks whichever level is loaded instead of a hardcoded guess.
                if (role == "system" && Plugin.CfgClampValues.Value)
                    Schema.Learn(m.content);
            }

            // Who she is and what she knows. First of the injected blocks on
            // purpose: it is the level prompt's replacement, so everything after
            // it - the engine whitelist, the names, the danger state - reads as a
            // correction to it rather than the other way round.
            string lore = Lore.Block();
            if (lore != null)
            {
                JObject lo = new JObject();
                lo["role"] = "system";
                lo["content"] = lore;
                messages.Add(lo);
                Lore.Report();
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

            // The names the player chose. The level prompt that would normally
            // carry them is substituted server-side, so without this she has no
            // idea what she is called and picks something new each session.
            string identity = Identity.Block();
            if (identity != null)
            {
                JObject who = new JObject();
                who["role"] = "system";
                who["content"] = identity;
                messages.Add(who);
                Identity.Report();
            }

            // What she can actually hand over. The giving_to_player field is
            // only documented in the server-side level prompt we replace, so
            // without this block the model never learns it exists and every
            // "here you go" hands over nothing.
            string gifts = Items.Block();
            if (gifts != null)
            {
                JObject gi = new JObject();
                gi["role"] = "system";
                gi["content"] = gifts;
                messages.Add(gi);
                Items.Report();
            }

            // Where she stands on anger, trust and whether a chase is already
            // running. Without this she argues politely while the engine has her
            // charging with a knife, because the engine replaces npc_action and
            // never touches the line she wrote.
            string danger = Murder.Block();
            if (danger != null)
            {
                JObject dg = new JObject();
                dg["role"] = "system";
                dg["content"] = danger;
                messages.Add(dg);
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
                + "the player. The same applies to handing something over: when you offer, promise or give "
                + "the player an object, you MUST also set giving_to_player to that item's bare name, or "
                + "nothing is transferred. giving_to_player is free text and is exempt from the allowed-value "
                + "lists, but it is still capped at 20 characters.";
            messages.Add(reminder);

            // Last on purpose, so the persona-drop instruction is the most recent
            // thing the model reads and wins against the character prompt above
            // it. It does not undercut the reminder: OOC keeps the same JSON
            // envelope and the same allowed values, and only changes what she
            // says. Null on every untagged turn, and null always while the
            // feature is off, so an ordinary request is unchanged byte for byte.
            string ooc = Ooc.Block();
            if (ooc != null)
            {
                JObject oo = new JObject();
                oo["role"] = "system";
                oo["content"] = ooc;
                messages.Add(oo);
            }

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
