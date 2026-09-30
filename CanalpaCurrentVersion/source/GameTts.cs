// The game's original voice engines:
// 1. Local Original: Native on-device Overtone TTS (100% offline, 0 keys needed).
// 2. Cloud Original: Azure Neural Speech with developer-curated casting (Jane, Amber, Nancy, Davis).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using HarmonyLib;

namespace AI2UCustomAI
{
    // Native interop directly to the game's overtone native library (overtone.dll).
    internal static class OvertoneNative
    {
        private const string OvertoneLibrary = "overtone";

        [StructLayout(LayoutKind.Sequential)]
        public struct OvertoneResult
        {
            public uint Channels;
            public uint SampleRate;
            public uint LengthSamples;
            public IntPtr Samples;
        }

        [DllImport(OvertoneLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "overtone_start")]
        public static extern IntPtr OvertoneStart();

        [DllImport(OvertoneLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "overtone_text_2_audio")]
        public static extern OvertoneResult OvertoneText2Audio(IntPtr ctx, IntPtr text, IntPtr voice);

        [DllImport(OvertoneLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "overtone_load_voice")]
        public static extern IntPtr OvertoneLoadVoice(IntPtr configBuffer, uint configBufferSize, IntPtr modelBuffer, uint modelBufferSize);

        [DllImport(OvertoneLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "overtone_set_speaker_id")]
        public static extern void OvertoneSetSpeakerId(IntPtr voice, long speakerId);

        [DllImport(OvertoneLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "overtone_free_voice")]
        public static extern void OvertoneFreeVoice(IntPtr voice);

        [DllImport(OvertoneLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "overtone_free_result")]
        public static extern void OvertoneFreeResult(OvertoneResult result);

        [DllImport(OvertoneLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "overtone_free")]
        public static extern void OvertoneFree(IntPtr ctx);
    }

    public struct AzureVoiceSpec
    {
        public string VoiceName;
        public string Language;
        public string PitchFormatted;
        public string RateFormatted;
        public float Pitch;
        // Which character this is for (a Voices.Names entry, or null), and the
        // voice the player typed for her, still unresolved - it can only be
        // checked against the region's catalogue once the region is known.
        public string Who;
        public string Custom;
        // Her own pitch and speed from the Voice tab (5.7), in percent, or null
        // for the default.
        public int? UserPitch;
        public int? UserSpeed;
    }

    // Handles both Local Overtone (offline) and Cloud Original (Azure) speech engines.
    internal static class GameTts
    {
        private static IntPtr _ctx = IntPtr.Zero;
        private static readonly object _lock = new object();
        private static bool _initAttempted = false;

        private class CachedVoice
        {
            public string VoiceName;
            public int SpeakerId;
            public IntPtr VoicePtr;
            public GCHandle ConfigHandle;
            public GCHandle ModelHandle;
            public bool Valid;
        }

        private static readonly Dictionary<string, CachedVoice> _voiceCache = new Dictionary<string, CachedVoice>();

        public static bool Configured
        {
            get
            {
                string mode = Plugin.CfgVoiceChoice != null ? Plugin.CfgVoiceChoice.Value : "local";
                if (string.Equals(mode, "azure", StringComparison.OrdinalIgnoreCase))
                {
                    return !string.IsNullOrEmpty(Plugin.CfgGameVoiceKey.Value) || HasStockAzureKey();
                }
                return true;
            }
        }

        public static bool WantedButKeyless
        {
            get
            {
                string mode = Plugin.CfgVoiceChoice != null ? Plugin.CfgVoiceChoice.Value : "local";
                if (string.Equals(mode, "azure", StringComparison.OrdinalIgnoreCase))
                {
                    return string.IsNullOrEmpty(Plugin.CfgGameVoiceKey.Value) && !HasStockAzureKey();
                }
                return false;
            }
        }

        private static bool HasStockAzureKey()
        {
            try
            {
                return AzureTts.GameMenuKey().Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool EnsureInitialized()
        {
            if (_ctx != IntPtr.Zero) return true;
            lock (_lock)
            {
                if (_ctx != IntPtr.Zero) return true;
                if (_initAttempted && _ctx == IntPtr.Zero) return false;
                _initAttempted = true;

                try
                {
                    _ctx = OvertoneNative.OvertoneStart();
                    if (_ctx != IntPtr.Zero)
                    {
                        Plugin.Log.LogInfo("GameTts: Native Overtone engine initialized successfully (0 keys needed).");
                        return true;
                    }
                    Plugin.Log.LogError("GameTts: OvertoneStart returned zero pointer.");
                }
                catch (DllNotFoundException)
                {
                    Plugin.Log.LogError("GameTts: overtone.dll native library not found.");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("GameTts: Failed to initialize native Overtone: " + ex.Message);
                }
                return false;
            }
        }

        public struct VoiceSpec
        {
            public string VoiceName;
            public int SpeakerId;
            public float Pitch;
        }

        public static VoiceSpec GetVoiceSpec(int? characterId)
        {
            int langIdx = 1;
            try
            {
                langIdx = PlayerPrefs.GetInt("systemLanguageIndex", 1);
            }
            catch (Exception) { }

            if (langIdx == 0) // Chinese
            {
                return new VoiceSpec { VoiceName = "zh-cn-huayan-medium", SpeakerId = 0, Pitch = 1.0f };
            }
            if (langIdx == 2) // Spanish
            {
                return new VoiceSpec { VoiceName = "es-es-sharvard-medium", SpeakerId = 0, Pitch = 1.0f };
            }

            int cid = characterId.HasValue ? characterId.Value : 0;
            switch (cid)
            {
                case 1:     // Eddie
                case 10:    // Evie
                case 11:    // Evie / Rorre
                case 12:    // Evie
                case 991:   // Hub Eddie
                case 9910:  // Hub Evie
                case 9911:  // Hub Evie
                case 9912:  // Hub Evie
                case 9913:  // Hub Evie
                case 9914:  // Hub Evie
                case 9915:  // Hub Evie
                case 9916:  // Hub Evie
                case 9917:  // Hub Evie
                case 9918:  // Hub Evie
                case 9919:  // Hub Evie
                    return new VoiceSpec { VoiceName = "en-us-amy-medium", SpeakerId = 0, Pitch = 1.15f };

                case 2:     // Elysia
                case 20:    // Elysia
                case 21:    // Elysia
                case 22:    // Elysia
                case 9921:  // FinalDoorElysia
                case 9922:  // Hub Elysia
                    return new VoiceSpec { VoiceName = "en-gb-cori-high", SpeakerId = 0, Pitch = 1.15f };

                case 992:   // MagicCircle
                case 9920:  // Summon
                    return new VoiceSpec { VoiceName = "en-gb-cori-high", SpeakerId = 1, Pitch = 1.0f };

                case 3:     // Estelle
                case 30:    // Estelle
                case 31:    // Estelle
                case 32:    // Estelle
                case 993:   // FinalDoorEstelle
                case 9930:  // Hub Estelle
                case 9931:  // Hub Estelle
                case 9932:  // Hub Estelle
                    return new VoiceSpec { VoiceName = "en-us-hfc_female-medium", SpeakerId = 0, Pitch = 1.05f };

                case 4:     // Eiona
                case 994:   // FinalDoorEiona
                case 9940:  // FinalDoorEiona
                case 9941:  // FinalDoorEiona
                case 9949:  // TreasureHunt minigame
                case 40:    // DarkSiren
                    return new VoiceSpec { VoiceName = "en-us-amy-medium", SpeakerId = 0, Pitch = 1.0f };
            }

            try
            {
                int lvl = GameManager.CurrentLevel;
                if (lvl == 2) return new VoiceSpec { VoiceName = "en-gb-cori-high", SpeakerId = 0, Pitch = 1.15f };
                if (lvl == 3) return new VoiceSpec { VoiceName = "en-us-hfc_female-medium", SpeakerId = 0, Pitch = 1.05f };
                if (lvl == 4) return new VoiceSpec { VoiceName = "en-us-amy-medium", SpeakerId = 0, Pitch = 1.0f };
            }
            catch (Exception) { }

            return new VoiceSpec { VoiceName = "en-us-amy-medium", SpeakerId = 0, Pitch = 1.15f };
        }

        public static AzureVoiceSpec GetAzureVoiceSpec(int? characterId)
        {
            return GetAzureVoiceSpec(characterId, null);
        }

        // forceName voices a specific character (a Voices.Names entry) whoever is
        // on screen - the Voice tab's per-character test buttons, where nobody is
        // actually speaking. Otherwise the speaker is read from the game.
        public static AzureVoiceSpec GetAzureVoiceSpec(int? characterId, string forceName)
        {
            string who = forceName ?? Voices.BaseNameForId(characterId);
            bool summon = forceName != null
                ? (forceName == "MagicCircle" || forceName == "Ghost")
                : Identity.IsSummon();

            int langIdx = 1;
            try { langIdx = PlayerPrefs.GetInt("systemLanguageIndex", 1); }
            catch (Exception) { }

            AzureVoiceSpec spec = DefaultCast(who, summon, langIdx);
            spec.Who = who;
            spec.Custom = Voices.AzureVoiceFor(who);
            spec.UserPitch = Voices.AzurePitchFor(who);
            spec.UserSpeed = Voices.AzureSpeedFor(who);
            return spec;
        }

        // The game's own cast, read out of AzureVoiceManager_PersonalAPI.InitTTSVoice
        // (the path the base game takes when a player supplies their own Azure key).
        // It is organised by LEVEL there; here it is by character, which is how the
        // mod knows who is talking:
        //   Level 1 main, and both hub girls        -> Jane   (pitch 1.2, rate 1.3)
        //   Level 2 main                            -> Amber  (pitch 1.2)
        //   Level 2 magic circle, and the ghost     -> Davis  (pitch 1.3)
        //   Level 3 main, and Level 4 (which reuses -> Nancy  (pitch 1.1)
        //     Level 3's entry verbatim)
        // Until 5.6 the hub girls got Nancy, the fallback; the game gives them the
        // Level 1 entry. The ghost speaks as the magic circle does: in play
        // Identity.IsSummon() is true for her, and the game's own Speak() gives
        // every character id above 10 the level's SECOND voice, which on Level 2
        // is the circle's. The Japanese Nancy cast
        // (Nanami) is tuned 1.05 / 0.9 in the game, not the +15% / +10% the old
        // table carried. The old table also keyed on character ids that do not
        // exist in the enum (10, 991, 9910...) and missed some that do (9915, 9919),
        // so it now keys on the resolved character name instead.
        internal static AzureVoiceSpec DefaultCast(string who, bool summon, int langIdx)
        {
            // The game's raw numbers, copied verbatim from
            // AzureVoiceManager_PersonalAPI.InitTTSVoice, and converted to SSML the
            // way the game's own voice library converts them (RtPitch / RtRate).
            // Until this was checked against the game's code, the table held
            // hand-converted percentages - and every speed-up in it was DOUBLE what
            // the game plays, because the library halves them. Storing the raw
            // values keeps the cast faithful by construction.
            AzureVoiceSpec s;
            if (summon || who == "MagicCircle" || who == "Ghost")
                s = Cast(langIdx, "zh-CN-XiaoxiaoNeural", 0.95f, 0.9f, "es-MX-PelayoNeural", 0.9f, 0.8f,
                    "ja-JP-KeitaNeural", 0.9f, 0.9f, "en-US-DavisNeural", 1.3f, 1.0f);
            else if (who == "Eddie" || who == "IRLGirl" || who == "ParrotGirl")
                s = Cast(langIdx, "zh-CN-XiaoyiNeural", 1.0f, 1.0f, "es-MX-LarissaNeural", 1.1f, 1.2f,
                    "ja-JP-MayuNeural", 1.2f, 1.1f, "en-US-JaneNeural", 1.2f, 1.3f);
            else if (who == "Elysia")
                s = Cast(langIdx, "zh-CN-XiaomengNeural", 1.1f, 1.0f, "es-MX-CarlotaNeural", 1.15f, 1.1f,
                    "ja-JP-ShioriNeural", 1.15f, 1.1f, "en-US-AmberNeural", 1.2f, 1.0f);
            else
                s = Cast(langIdx, "zh-CN-XiaoyanNeural", 0.95f, 1.0f, "es-MX-BeatrizNeural", 1.1f, 1.1f,
                    "ja-JP-NanamiNeural", 1.05f, 0.9f, "en-US-NancyNeural", 1.1f, 1.0f);

            // The game lifts these voices off their natural pitch and pace - Jane
            // is raised 20% and sped up 15%. Faithful, and on by default, but it is
            // part of what sounds synthetic, so it can be switched off to hear the
            // same voices as they were built.
            if (Plugin.CfgAzureOriginalTuning != null && !Plugin.CfgAzureOriginalTuning.Value)
            {
                s.PitchFormatted = "+0%";
                s.RateFormatted = "+0%";
            }
            return s;
        }

        static AzureVoiceSpec Cast(int langIdx,
            string zh, float zhP, float zhR, string es, float esP, float esR,
            string ja, float jaP, float jaR, string en, float enP, float enR)
        {
            string v;
            float p, r;
            switch (langIdx)
            {
                case 0: v = zh; p = zhP; r = zhR; break;
                case 2: v = es; p = esP; r = esR; break;
                case 3: v = ja; p = jaP; r = jaR; break;
                default: v = en; p = enP; r = enR; break;
            }
            AzureVoiceSpec s = new AzureVoiceSpec();
            s.VoiceName = v;
            s.Language = v.Substring(0, 5);
            s.PitchFormatted = RtPitch(p);
            s.RateFormatted = RtRate(r);
            s.Pitch = 1.0f;
            return s;
        }

        // The game's voice library (Crosstales RT-Voice, VoiceProviderAzure.
        // prepareText, read from its IL) turns a pitch into (pitch - 1) as a
        // percentage, but a rate into (rate - 1) * 0.5 when it is above 1 and
        // (rate - 1) when it is below. So the game's "rate 1.3" is +15%, not +30%.
        // The mod sent +30% from 5.3 until 5.6, and with 5.6's speaking styles
        // (which already speak about 15% faster) stacked on top, Eddie's voice ran
        // at 1.5x her natural speed.
        static string RtPitch(float pitch)
        {
            return RtPercent(pitch - 1f);
        }

        static string RtRate(float rate)
        {
            return RtPercent(rate > 1f ? (rate - 1f) * 0.5f : rate - 1f);
        }

        static string RtPercent(float v)
        {
            int pct = (int)Math.Round(v * 100f, MidpointRounding.AwayFromZero);
            return pct >= 0 ? "+" + pct + "%" : pct + "%";
        }

        private static CachedVoice GetOrCreateVoice(string voiceName, int speakerId)
        {
            string key = voiceName + ":" + speakerId;
            lock (_lock)
            {
                CachedVoice existing;
                if (_voiceCache.TryGetValue(key, out existing) && existing.Valid && existing.VoicePtr != IntPtr.Zero)
                {
                    return existing;
                }

                TextAsset modelAsset = Resources.Load<TextAsset>(voiceName ?? "");
                TextAsset configAsset = Resources.Load<TextAsset>((voiceName ?? "") + ".config");

                if (modelAsset == null || configAsset == null)
                {
                    Plugin.Log.LogError("GameTts: Could not find TextAsset for voice " + voiceName + " in Resources.");
                    return null;
                }

                byte[] modelBytes = modelAsset.bytes;
                byte[] configBytes = configAsset.bytes;

                GCHandle configHandle = GCHandle.Alloc(configBytes, GCHandleType.Pinned);
                GCHandle modelHandle = GCHandle.Alloc(modelBytes, GCHandleType.Pinned);

                IntPtr voicePtr = IntPtr.Zero;
                try
                {
                    voicePtr = OvertoneNative.OvertoneLoadVoice(
                        configHandle.AddrOfPinnedObject(), (uint)configBytes.Length,
                        modelHandle.AddrOfPinnedObject(), (uint)modelBytes.Length);
                }
                catch (Exception ex)
                {
                    configHandle.Free();
                    modelHandle.Free();
                    Plugin.Log.LogError("GameTts: Exception loading voice " + voiceName + ": " + ex.Message);
                    return null;
                }

                if (voicePtr == IntPtr.Zero)
                {
                    configHandle.Free();
                    modelHandle.Free();
                    Plugin.Log.LogError("GameTts: OvertoneLoadVoice returned zero pointer for " + voiceName);
                    return null;
                }

                try
                {
                    OvertoneNative.OvertoneSetSpeakerId(voicePtr, (long)speakerId);
                }
                catch (Exception) { }

                CachedVoice cv = new CachedVoice
                {
                    VoiceName = voiceName,
                    SpeakerId = speakerId,
                    VoicePtr = voicePtr,
                    ConfigHandle = configHandle,
                    ModelHandle = modelHandle,
                    Valid = true
                };

                _voiceCache[key] = cv;
                Plugin.Log.LogInfo("GameTts: Loaded voice " + voiceName + " (speaker " + speakerId + ") successfully.");
                return cv;
            }
        }

        public static IEnumerator Synthesize(string text, Action<AudioClip> done)
        {
            string mode = Plugin.CfgVoiceChoice != null ? Plugin.CfgVoiceChoice.Value : "local";
            if (string.Equals(mode, "azure", StringComparison.OrdinalIgnoreCase))
            {
                IEnumerator azCall = SynthesizeAzureCloud(text, done);
                while (azCall.MoveNext()) yield return azCall.Current;
                yield break;
            }

            IEnumerator locCall = SynthesizeLocalOvertone(text, done);
            while (locCall.MoveNext()) yield return locCall.Current;
        }

        public static IEnumerator SynthesizeLocalOvertone(string text, Action<AudioClip> done)
        {
            if (string.IsNullOrEmpty(text))
            {
                done(null);
                yield break;
            }

            if (!EnsureInitialized())
            {
                Plugin.Log.LogError("GameTts: Native engine not initialized; cannot synthesize.");
                done(null);
                yield break;
            }

            int? charId = Identity.CharacterId();
            VoiceSpec spec = GetVoiceSpec(charId);
            CachedVoice cv = GetOrCreateVoice(spec.VoiceName, spec.SpeakerId);
            if (cv == null || cv.VoicePtr == IntPtr.Zero)
            {
                Plugin.Log.LogError("GameTts: Failed to get voice for " + spec.VoiceName);
                done(null);
                yield break;
            }

            float t0 = Time.realtimeSinceStartup;
            float[] samples = null;
            uint channels = 0;
            uint sampleRate = 0;
            bool failed = false;

            Task synthTask = Task.Run(() =>
            {
                lock (_lock)
                {
                    if (_ctx == IntPtr.Zero || cv.VoicePtr == IntPtr.Zero)
                    {
                        failed = true;
                        return;
                    }

                    IntPtr textPtr = IntPtr.Zero;
                    try
                    {
                        textPtr = Marshal.StringToHGlobalAnsi(text);
                        OvertoneNative.OvertoneResult result = OvertoneNative.OvertoneText2Audio(_ctx, textPtr, cv.VoicePtr);
                        if (result.LengthSamples > 0 && result.Samples != IntPtr.Zero)
                        {
                            channels = result.Channels > 0 ? result.Channels : 1;
                            sampleRate = result.SampleRate > 0 ? result.SampleRate : 22050;
                            samples = new float[result.LengthSamples];
                            short[] shortBuf = new short[result.LengthSamples];
                            Marshal.Copy(result.Samples, shortBuf, 0, (int)result.LengthSamples);
                            for (int i = 0; i < result.LengthSamples; i++)
                            {
                                samples[i] = shortBuf[i] / 32767f;
                            }
                            OvertoneNative.OvertoneFreeResult(result);
                        }
                        else
                        {
                            failed = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed = true;
                        Plugin.Log.LogError("GameTts: Native synthesis error: " + ex.Message);
                    }
                    finally
                    {
                        if (textPtr != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(textPtr);
                        }
                    }
                }
            });

            while (!synthTask.IsCompleted)
            {
                yield return null;
            }

            if (failed || samples == null || samples.Length == 0)
            {
                Plugin.Log.LogError("GameTts: Synthesis produced no audio samples.");
                done(null);
                yield break;
            }

            AudioClip clip = AudioClip.Create("overtone_" + spec.VoiceName, samples.Length, (int)channels, (int)sampleRate, false);
            clip.SetData(samples, 0);

            if (Plugin.CfgLogPayloads != null && Plugin.CfgLogPayloads.Value)
            {
                Plugin.Log.LogInfo(string.Format(
                    "GameTts (Local Overtone): {0} chars={1} {2:F1}s audio in {3:F1}s",
                    spec.VoiceName, text.Length, clip.length, Time.realtimeSinceStartup - t0));
            }

            done(clip);
        }

        public static IEnumerator SynthesizeAzureCloud(string text, Action<AudioClip> done)
        {
            IEnumerator e = SynthesizeAzureCloud(text, done, null, false, null);
            while (e.MoveNext()) yield return e.Current;
        }

        // test: a menu test. No speaking style (a test line has no mood), and no
        // in-game notices - the test reports AzureTts's outcome itself.
        //
        // Every exit records what happened in AzureTts.Last*. That record is the
        // whole point of this rewrite: the old version fell back to the offline
        // voice on ANY failure and returned a clip as if Azure had spoken, so the
        // test button said "Works!" over a robotic voice and a player had no way
        // to know Azure had never been reached. See AzureTts.cs for the reports
        // this traced back to.
        public static IEnumerator SynthesizeAzureCloud(string text, Action<AudioClip> done, string forceName, bool test)
        {
            IEnumerator e = SynthesizeAzureCloud(text, done, forceName, test, null);
            while (e.MoveNext()) yield return e.Current;
        }

        // forceName: voice this character (a Voices.Names entry) whoever is on
        // screen - the Voice tab's per-character play buttons. test: a menu test,
        // so no speaking style (a test line has no mood) and no in-game notice.
        // o: this call's own outcome. A caller that needs to know what happened -
        // the tests - passes one and reads it; it is also published to
        // AzureTts.Last* for the Voice tab's status line.
        //
        // The old version fell back to the offline voice on ANY failure and
        // returned that clip as if Azure had spoken, so the test said "Works!"
        // over a robotic voice and nobody could tell Azure had never been
        // reached. See AzureTts.cs for the three reports this traced back to.
        public static IEnumerator SynthesizeAzureCloud(string text, Action<AudioClip> done, string forceName, bool test, AzureOutcome o)
        {
            IEnumerator e = SynthesizeAzureCloud(text, done, forceName, test, o, null);
            while (e.MoveNext()) yield return e.Current;
        }

        // forceVoice: speak with this voice instead of the one saved for her - the
        // voice browser's preview buttons, so a voice can be heard before it is
        // chosen. Her pitch and speed still apply, so the preview is what she
        // would sound like.
        public static IEnumerator SynthesizeAzureCloud(string text, Action<AudioClip> done, string forceName, bool test,
            AzureOutcome o, string forceVoice)
        {
            if (o == null) o = new AzureOutcome();
            if (string.IsNullOrEmpty(text))
            {
                done(null);
                yield break;
            }

            string key = AzureTts.EffectiveKey();

            string rawRegion = Plugin.CfgGameVoiceRegion != null ? (Plugin.CfgGameVoiceRegion.Value ?? "").Trim() : "";
            string region = AzureTts.NormalizeRegion(rawRegion);
            if (region.Length == 0) region = AzureTts.GameMenuRegion();
            if (region.Length == 0) region = "eastus";

            // A region that had to be interpreted - a pasted endpoint URL, or
            // "North Central US" - is written back as the code the mod is actually
            // using, so the Voice tab stops showing something it is not.
            if (rawRegion.Length > 0 && rawRegion != region && Plugin.CfgGameVoiceRegion != null)
            {
                Plugin.Log.LogInfo("Azure TTS: read the region \"" + rawRegion + "\" as \"" + region + "\".");
                Plugin.CfgGameVoiceRegion.Value = region;
                Plugin.SaveCfg();
                OverlayMenu.SyncBuffer("GameVoiceRegion", region);
            }

            if (string.IsNullOrEmpty(key))
            {
                o.Problem = "no Azure Speech key is set.";
                IEnumerator nk = FallBackToLocal(text, done, test, o);
                while (nk.MoveNext()) yield return nk.Current;
                yield break;
            }

            AzureTts.UseKey(key);

            // The region's voice catalogue, fetched once. It doubles as a free test
            // of the key: a 401 here means the region is wrong, and it is repaired
            // before a line is spent on it.
            if (AzureTts.Catalog(region) == null && AzureTts.CatalogFailure(region) == 0)
            {
                IEnumerator cat = AzureTts.LoadCatalog(region, key);
                while (cat.MoveNext()) yield return cat.Current;
            }

            bool regionTried = false;
            if (AzureTts.RegionLooksWrong(AzureTts.CatalogFailure(region), region))
            {
                regionTried = true;
                string better = null;
                IEnumerator rep = AzureTts.RepairRegion(key, region, delegate (string r) { better = r; });
                while (rep.MoveNext()) yield return rep.Current;
                if (better != null)
                {
                    region = AdoptRegion(region, better, test, o);
                    IEnumerator cat2 = AzureTts.LoadCatalog(region, key);
                    while (cat2.MoveNext()) yield return cat2.Current;
                }
                else if (AzureTts.KnownRegion(key) == region)
                {
                    // The key does belong here - the 401 on record is stale.
                    AzureTts.KeyProvenIn(region);
                    IEnumerator cat3 = AzureTts.LoadCatalog(region, key);
                    while (cat3.MoveNext()) yield return cat3.Current;
                }
            }

            AzureVoiceSpec spec = GetAzureVoiceSpec(Identity.CharacterId(), forceName);
            if (!string.IsNullOrEmpty(forceVoice)) spec.Custom = forceVoice;
            string voice = spec.VoiceName, lang = spec.Language, pitch = spec.PitchFormatted, rate = spec.RateFormatted;
            bool custom = false;

            if (!string.IsNullOrEmpty(spec.Custom))
            {
                string problem;
                string resolved = AzureTts.ResolveName(spec.Custom, region, out problem);
                if (resolved != null)
                {
                    // A voice the player chose is heard as it was built. The
                    // original cast's pitch and pace offsets were tuned for those
                    // four voices and would distort any other.
                    voice = resolved;
                    lang = AzureTts.LocaleOf(resolved, region);
                    pitch = "+0%";
                    rate = "+0%";
                    custom = true;
                }
                else
                {
                    o.Note = Voices.LabelFor(spec.Who) + ": " + problem
                        + " Using her original voice (" + spec.VoiceName + ") instead.";
                    if (!test) AzureTts.Announce(o.Note);
                }
            }

            bool userRate = ApplyTuning(spec, ref pitch, ref rate);

            bool expressive = !test && Plugin.CfgAzureExpressive != null && Plugin.CfgAzureExpressive.Value;
            string style = expressive ? AzureTts.StyleFor(AzureTts.Find(region, voice)) : null;

            // Azure's Dragon HD voices take no pitch or speed changes (Microsoft's
            // HD-voice docs). Leaving them out up front costs nothing; finding out
            // by refusal would cost wasted requests on every line.
            bool voiceTried = false, noProsody = IsDragonHd(voice);
            long lastCode = 0;
            string lastErr = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                // A speaking style carries its own pacing - cheerful and frightened
                // speech are about 15% faster on their own, measured - so it
                // REPLACES the cast's speed boost instead of stacking on it. With
                // both, Jane spoke at 1.5x her natural speed; with the style alone
                // she keeps the game's pace (1.15x) and gains the emotion. A speed
                // the player set by hand is theirs, and is sent as set.
                //
                // Her own speed is shown against the same default, so with a style
                // it is sent relative to the boost the style replaced: moving the
                // slider down 5 slows a styled line by 5, as it slows a plain one.
                string sendRate = rate;
                if (style != null)
                    sendRate = userRate && spec.UserSpeed.HasValue
                        ? AzureTts.Percent(spec.UserSpeed.Value - (custom ? 0 : AzureTts.ParsePercent(spec.RateFormatted)))
                        : "+0%";
                string ssml = noProsody
                    ? AzureTts.BuildSsml(lang, voice, "+0%", "+0%", style, text)
                    : AzureTts.BuildSsml(lang, voice, pitch, sendRate, style, text);
                UnityWebRequest req = new UnityWebRequest(AzureTts.TtsUrl(region), "POST");
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(ssml));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/ssml+xml");
                // 24 kHz, not 16: Azure's neural voices are built at 24 kHz, and
                // 16 kHz discards everything above 8 kHz - the breath and
                // brightness that separate a present voice from a telephone one.
                req.SetRequestHeader("X-Microsoft-OutputFormat", "audio-24khz-96kbitrate-mono-mp3");
                req.SetRequestHeader("Ocp-Apim-Subscription-Key", key);
                req.SetRequestHeader("User-Agent", "AI2UCustomAI");
                req.timeout = 25;

                yield return req.SendWebRequest();

                long code = req.responseCode;
                string err = req.error;
                lastCode = code;
                lastErr = err;
                byte[] audio = null;
                if (req.result == UnityWebRequest.Result.Success && req.downloadHandler != null
                    && req.downloadHandler.data != null && req.downloadHandler.data.Length > 1024)
                    audio = req.downloadHandler.data;
                req.Dispose();

                if (audio != null)
                {
                    AudioClip clip = null;
                    IEnumerator dec = DecodeMp3(audio, delegate (AudioClip c) { clip = c; });
                    while (dec.MoveNext()) yield return dec.Current;

                    if (clip != null)
                    {
                        AzureTts.KeyProvenIn(region);
                        o.Ok = true;
                        o.Voice = voice;
                        o.Style = style;
                        o.Region = region;
                        AzureTts.Publish(o);
                        if (Plugin.CfgLogPayloads != null && Plugin.CfgLogPayloads.Value)
                            Plugin.Log.LogInfo(string.Format("GameTts (Azure): {0}{1} @ {2}, {3} chars, {4:F1}s",
                                voice, style != null ? " [" + style + "]" : "", region, text.Length, clip.length));
                        done(clip);
                        yield break;
                    }
                    o.Problem = "Azure sent audio the game could not decode.";
                    break;
                }

                // Rejected here: move to the region this key belongs to - found by
                // a sweep, or already known from one earlier this session - and
                // try again there.
                if (!regionTried && AzureTts.RejectsRegion(code, region))
                {
                    regionTried = true;
                    string better = null;
                    IEnumerator rep2 = AzureTts.RepairRegion(key, region, delegate (string r) { better = r; });
                    while (rep2.MoveNext()) yield return rep2.Current;
                    if (better != null)
                    {
                        region = AdoptRegion(region, better, test, o);
                        continue;
                    }
                }

                // A 400 is Azure refusing something in the request. Drop the
                // extras first, the voice last: until 5.7 a refusal was blamed on
                // her chosen voice straight away, so a mood or a pitch the voice
                // could not take cost her the voice itself - in play only, since
                // tests use no mood. That is one way "the voice I typed works on
                // the play button but not in the game" could happen.
                if (code == 400 && style != null)
                {
                    style = null;
                    continue;
                }

                // Azure's Dragon HD voices take no pitch or speed changes.
                if (code == 400 && !noProsody && HasProsody(pitch, rate))
                {
                    noProsody = true;
                    if (spec.UserPitch.HasValue || spec.UserSpeed.HasValue)
                        o.Note = Voices.LabelFor(spec.Who) + ": " + voice + " does not accept pitch or speed "
                            + "changes, so hers were left out.";
                    continue;
                }

                // Her chosen voice was refused: she keeps HER original voice for
                // this line rather than losing Azure altogether.
                if (code == 400 && custom && !voiceTried)
                {
                    voiceTried = true;
                    o.Note = Voices.LabelFor(spec.Who) + ": " + AzureTts.Where(region) + " refused the voice \""
                        + voice + "\". Using her original voice (" + spec.VoiceName + ") instead.";
                    if (!test) AzureTts.Announce(o.Note);
                    voice = spec.VoiceName;
                    lang = spec.Language;
                    pitch = spec.PitchFormatted;
                    rate = spec.RateFormatted;
                    userRate = ApplyTuning(spec, ref pitch, ref rate);
                    noProsody = IsDragonHd(voice);
                    custom = false;
                    style = expressive ? AzureTts.StyleFor(AzureTts.Find(region, voice)) : null;
                    continue;
                }

                o.Problem = AzureTts.Explain(code, region, voice, err);
                break;
            }

            // Every retry used up without an answer: still say what the last
            // refusal was, or the failure would be silent again.
            if (!o.Ok && o.Problem == null) o.Problem = AzureTts.Explain(lastCode, region, voice, lastErr);

            IEnumerator fb = FallBackToLocal(text, done, test, o);
            while (fb.MoveNext()) yield return fb.Current;
        }

        // Her own pitch and speed, when the player set them, replace the default
        // for whichever voice she is using. Returns whether the speed is the
        // player's.
        static bool ApplyTuning(AzureVoiceSpec spec, ref string pitch, ref string rate)
        {
            if (spec.UserPitch.HasValue) pitch = AzureTts.Percent(spec.UserPitch.Value);
            if (spec.UserSpeed.HasValue) rate = AzureTts.Percent(spec.UserSpeed.Value);
            return spec.UserSpeed.HasValue;
        }

        static bool IsDragonHd(string voice)
        {
            return voice != null && voice.IndexOf("DragonHD", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool HasProsody(string pitch, string rate)
        {
            return (pitch != null && pitch != "+0%") || (rate != null && rate != "+0%");
        }

        // Adopts the region a key turned out to belong to, saves it, and says so.
        static string AdoptRegion(string was, string found, bool test, AzureOutcome o)
        {
            if (Plugin.CfgGameVoiceRegion != null)
            {
                Plugin.CfgGameVoiceRegion.Value = found;
                Plugin.SaveCfg();
            }
            // The panel keeps its own copy of every field until Save; without this
            // the next Save would write the old, rejected region straight back.
            OverlayMenu.SyncBuffer("GameVoiceRegion", found);

            o.Note = "region corrected from \"" + was + "\" to \"" + found
                + "\" - that is where this key belongs. Saved.";
            Plugin.Log.LogWarning("Azure TTS: " + o.Note);
            if (!test) Plugin.Toast("Azure voice: " + o.Note, 8f);
            return found;
        }

        // The offline voice, as a LABELLED fallback. It still speaks the line -
        // silence would be worse - but the outcome says Azure did not, and outside
        // a test the player is told once why.
        static IEnumerator FallBackToLocal(string text, Action<AudioClip> done, bool test, AzureOutcome o)
        {
            o.FellBack = true;
            if (o.Problem != null)
            {
                if (!test) AzureTts.Announce(o.Problem + " Playing the offline voice until it is fixed.");
                else Plugin.Log.LogWarning("Azure TTS (test): " + o.Problem);
            }
            AzureTts.Publish(o);
            IEnumerator loc = SynthesizeLocalOvertone(text, done);
            while (loc.MoveNext()) yield return loc.Current;
        }

        static IEnumerator DecodeMp3(byte[] audio, Action<AudioClip> done)
        {
            string tmpFile = Path.Combine(Application.temporaryCachePath, "azureTTS_" + Guid.NewGuid().ToString("N") + ".mp3");
            bool wrote = false;
            try { File.WriteAllBytes(tmpFile, audio); wrote = true; }
            catch (Exception e) { Plugin.Log.LogWarning("Azure TTS: could not write audio (" + e.Message + ")."); }
            if (!wrote) { done(null); yield break; }

            AudioClip clip = null;
            using (UnityWebRequest clipReq = UnityWebRequestMultimedia.GetAudioClip("file://" + tmpFile.Replace("\\", "/"), AudioType.MPEG))
            {
                yield return clipReq.SendWebRequest();
                if (clipReq.result == UnityWebRequest.Result.Success)
                    clip = DownloadHandlerAudioClip.GetContent(clipReq);
            }

            try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch (Exception) { }
            done(clip);
        }

        public static string FailureLabel()
        {
            string mode = Plugin.CfgVoiceChoice != null ? Plugin.CfgVoiceChoice.Value : "local";
            if (string.Equals(mode, "azure", StringComparison.OrdinalIgnoreCase))
                return "Azure TTS error";
            return "Local Overtone error";
        }
    }

    // Manages speech routing between Cloud TTS, Azure Cloud, and Local Overtone.
    internal static class ModTts
    {
        public static bool IsGameVoice
        {
            get
            {
                string mode = Plugin.CfgVoiceChoice != null ? Plugin.CfgVoiceChoice.Value : "local";
                if (string.Equals(mode, "cloud", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase))
                {
                    if (Plugin.CfgGrokEnabled != null && Plugin.CfgGrokEnabled.Value && GrokTts.Configured)
                        return false;
                }
                return true;
            }
        }

        public static bool Wanted
        {
            get
            {
                string mode = Plugin.CfgVoiceChoice != null ? Plugin.CfgVoiceChoice.Value : "local";
                if (string.Equals(mode, "cloud", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase))
                {
                    return Plugin.CfgGrokEnabled != null && Plugin.CfgGrokEnabled.Value && GrokTts.Configured;
                }
                return GameTts.Configured;
            }
        }

        public static string FailureLabel()
        {
            string mode = Plugin.CfgVoiceChoice != null ? Plugin.CfgVoiceChoice.Value : "local";
            if ((string.Equals(mode, "cloud", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase))
                && Plugin.CfgGrokEnabled != null && Plugin.CfgGrokEnabled.Value && GrokTts.Configured)
            {
                return GrokTts.FailureLabel();
            }
            return GameTts.FailureLabel();
        }

        public static IEnumerator Synthesize(string text, Action<AudioClip> done)
        {
            string mode = Plugin.CfgVoiceChoice != null ? Plugin.CfgVoiceChoice.Value : "local";
            if ((string.Equals(mode, "cloud", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase))
                && Plugin.CfgGrokEnabled != null && Plugin.CfgGrokEnabled.Value && GrokTts.Configured)
            {
                AudioClip cloudClip = null;
                IEnumerator call = GrokTts.Synthesize(text, delegate (AudioClip c) { cloudClip = c; });
                while (call.MoveNext()) yield return call.Current;

                if (cloudClip != null)
                {
                    done(cloudClip);
                    yield break;
                }

                Plugin.Log.LogWarning("Voice: Custom Cloud TTS failed for this line; falling back to original game voice.");
            }

            if (GameTts.Configured)
            {
                IEnumerator call2 = GameTts.Synthesize(text, done);
                while (call2.MoveNext()) yield return call2.Current;
                yield break;
            }

            done(null);
        }
    }
}
