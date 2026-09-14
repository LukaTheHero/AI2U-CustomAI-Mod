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
                return !string.IsNullOrEmpty(Communicator.APIKey_UserPersonalTTS);
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
            string overrideVoice = Voices.Resolve(characterId);
            if (!string.IsNullOrEmpty(overrideVoice) && overrideVoice.IndexOf("Neural", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new AzureVoiceSpec
                {
                    VoiceName = overrideVoice,
                    Language = overrideVoice.Length >= 5 ? overrideVoice.Substring(0, 5) : "en-US",
                    PitchFormatted = "+0%",
                    RateFormatted = "+0%",
                    Pitch = 1.0f
                };
            }

            int langIdx = 1;
            try
            {
                langIdx = PlayerPrefs.GetInt("systemLanguageIndex", 1);
            }
            catch (Exception) { }

            bool isSummon = Identity.IsSummon();
            int cid = characterId.HasValue ? characterId.Value : 0;

            if (isSummon || cid == 992 || cid == 9920)
            {
                switch (langIdx)
                {
                    case 0: return new AzureVoiceSpec { VoiceName = "zh-CN-XiaoxiaoNeural", Language = "zh-CN", PitchFormatted = "-5%", RateFormatted = "-10%", Pitch = 1.0f };
                    case 2: return new AzureVoiceSpec { VoiceName = "es-MX-PelayoNeural", Language = "es-MX", PitchFormatted = "-10%", RateFormatted = "-20%", Pitch = 1.0f };
                    case 3: return new AzureVoiceSpec { VoiceName = "ja-JP-KeitaNeural", Language = "ja-JP", PitchFormatted = "-10%", RateFormatted = "-10%", Pitch = 1.0f };
                    default: return new AzureVoiceSpec { VoiceName = "en-US-DavisNeural", Language = "en-US", PitchFormatted = "+30%", RateFormatted = "+0%", Pitch = 1.0f };
                }
            }

            if (cid == 1 || cid == 10 || cid == 11 || cid == 12 || cid == 991 || cid == 9910 || cid == 9911 || cid == 9912)
            {
                switch (langIdx)
                {
                    case 0: return new AzureVoiceSpec { VoiceName = "zh-CN-XiaoyiNeural", Language = "zh-CN", PitchFormatted = "+0%", RateFormatted = "+0%", Pitch = 1.0f };
                    case 2: return new AzureVoiceSpec { VoiceName = "es-MX-LarissaNeural", Language = "es-MX", PitchFormatted = "+10%", RateFormatted = "+20%", Pitch = 1.0f };
                    case 3: return new AzureVoiceSpec { VoiceName = "ja-JP-MayuNeural", Language = "ja-JP", PitchFormatted = "+20%", RateFormatted = "+10%", Pitch = 1.0f };
                    default: return new AzureVoiceSpec { VoiceName = "en-US-JaneNeural", Language = "en-US", PitchFormatted = "+20%", RateFormatted = "+30%", Pitch = 1.0f };
                }
            }

            if (cid == 2 || cid == 20 || cid == 21 || cid == 22 || cid == 9921 || cid == 9922)
            {
                switch (langIdx)
                {
                    case 0: return new AzureVoiceSpec { VoiceName = "zh-CN-XiaomengNeural", Language = "zh-CN", PitchFormatted = "+10%", RateFormatted = "+0%", Pitch = 1.0f };
                    case 2: return new AzureVoiceSpec { VoiceName = "es-MX-CarlotaNeural", Language = "es-MX", PitchFormatted = "+15%", RateFormatted = "+10%", Pitch = 1.0f };
                    case 3: return new AzureVoiceSpec { VoiceName = "ja-JP-ShioriNeural", Language = "ja-JP", PitchFormatted = "+15%", RateFormatted = "+10%", Pitch = 1.0f };
                    default: return new AzureVoiceSpec { VoiceName = "en-US-AmberNeural", Language = "en-US", PitchFormatted = "+20%", RateFormatted = "+0%", Pitch = 1.0f };
                }
            }

            if (cid == 3 || cid == 30 || cid == 31 || cid == 32 || cid == 993 || cid == 9930 || cid == 9931 || cid == 9932)
            {
                switch (langIdx)
                {
                    case 0: return new AzureVoiceSpec { VoiceName = "zh-CN-XiaoyanNeural", Language = "zh-CN", PitchFormatted = "-5%", RateFormatted = "+0%", Pitch = 1.0f };
                    case 2: return new AzureVoiceSpec { VoiceName = "es-MX-BeatrizNeural", Language = "es-MX", PitchFormatted = "+10%", RateFormatted = "+10%", Pitch = 1.0f };
                    case 3: return new AzureVoiceSpec { VoiceName = "ja-JP-NanamiNeural", Language = "ja-JP", PitchFormatted = "+15%", RateFormatted = "+10%", Pitch = 1.0f };
                    default: return new AzureVoiceSpec { VoiceName = "en-US-NancyNeural", Language = "en-US", PitchFormatted = "+10%", RateFormatted = "+0%", Pitch = 1.0f };
                }
            }

            switch (langIdx)
            {
                case 0: return new AzureVoiceSpec { VoiceName = "zh-CN-XiaoyanNeural", Language = "zh-CN", PitchFormatted = "-5%", RateFormatted = "+0%", Pitch = 1.0f };
                case 2: return new AzureVoiceSpec { VoiceName = "es-MX-BeatrizNeural", Language = "es-MX", PitchFormatted = "+10%", RateFormatted = "+10%", Pitch = 1.0f };
                case 3: return new AzureVoiceSpec { VoiceName = "ja-JP-NanamiNeural", Language = "ja-JP", PitchFormatted = "+15%", RateFormatted = "+10%", Pitch = 1.0f };
                default: return new AzureVoiceSpec { VoiceName = "en-US-NancyNeural", Language = "en-US", PitchFormatted = "+10%", RateFormatted = "+0%", Pitch = 1.0f };
            }
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
            if (string.IsNullOrEmpty(text))
            {
                done(null);
                yield break;
            }

            string key = Plugin.CfgGameVoiceKey != null ? Plugin.CfgGameVoiceKey.Value.Trim() : "";
            string region = Plugin.CfgGameVoiceRegion != null ? Plugin.CfgGameVoiceRegion.Value.Trim() : "";

            if (string.IsNullOrEmpty(key))
            {
                try { key = Communicator.APIKey_UserPersonalTTS; } catch (Exception) { }
            }
            if (string.IsNullOrEmpty(region))
            {
                try { region = Communicator.APIKey_UserPersonalTTS_Region; } catch (Exception) { }
            }
            if (string.IsNullOrEmpty(region)) region = "eastus";

            if (string.IsNullOrEmpty(key))
            {
                Plugin.Log.LogWarning("Azure TTS: No Azure Speech key provided; falling back to local Overtone voice.");
                IEnumerator fb = SynthesizeLocalOvertone(text, done);
                while (fb.MoveNext()) yield return fb.Current;
                yield break;
            }

            int? charId = Identity.CharacterId();
            AzureVoiceSpec spec = GetAzureVoiceSpec(charId);

            string escaped = SecurityElement.Escape(text);
            string ssml = string.Format(
                "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{0}'>"
                + "<voice name='{1}'>"
                + "<prosody pitch='{2}' rate='{3}'>{4}</prosody>"
                + "</voice></speak>",
                spec.Language, spec.VoiceName, spec.PitchFormatted, spec.RateFormatted, escaped);

            string url = "https://" + region + ".tts.speech.microsoft.com/cognitiveservices/v1";
            UnityWebRequest req = new UnityWebRequest(url, "POST");
            byte[] payload = Encoding.UTF8.GetBytes(ssml);
            req.uploadHandler = new UploadHandlerRaw(payload);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/ssml+xml");
            req.SetRequestHeader("X-Microsoft-OutputFormat", "audio-16khz-128kbitrate-mono-mp3");
            req.SetRequestHeader("Ocp-Apim-Subscription-Key", key);
            req.SetRequestHeader("User-Agent", "AI2UCustomAI");

            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success && req.downloadHandler != null && req.downloadHandler.data != null && req.downloadHandler.data.Length > 1024)
            {
                byte[] audioBytes = req.downloadHandler.data;
                req.Dispose();

                string tmpFile = Path.Combine(Application.temporaryCachePath, "azureTTS_" + Guid.NewGuid().ToString("N") + ".mp3");
                File.WriteAllBytes(tmpFile, audioBytes);

                AudioClip clip = null;
                using (UnityWebRequest clipReq = UnityWebRequestMultimedia.GetAudioClip("file://" + tmpFile.Replace("\\", "/"), AudioType.MPEG))
                {
                    yield return clipReq.SendWebRequest();
                    if (clipReq.result == UnityWebRequest.Result.Success)
                    {
                        clip = DownloadHandlerAudioClip.GetContent(clipReq);
                    }
                }

                try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch (Exception) { }

                if (clip != null)
                {
                    if (Plugin.CfgLogPayloads != null && Plugin.CfgLogPayloads.Value)
                    {
                        Plugin.Log.LogInfo(string.Format("GameTts (Azure Cloud): {0} chars={1} {2:F1}s audio", spec.VoiceName, text.Length, clip.length));
                    }
                    done(clip);
                    yield break;
                }
            }
            else
            {
                Plugin.Log.LogWarning("Azure TTS error: " + req.error + "; falling back to local Overtone voice.");
                req.Dispose();
            }

            // Fallback to local overtone
            IEnumerator fallback = SynthesizeLocalOvertone(text, done);
            while (fallback.MoveNext()) yield return fallback.Current;
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
