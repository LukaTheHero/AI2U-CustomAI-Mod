using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json.Linq;

namespace AI2UCustomAI
{
    // Speaks NPC lines with xAI's Grok TTS.
    //
    // POST https://api.x.ai/v1/tts
    //   { "text": "...", "voice_id": "iris", "language": "en",
    //     "output_format": { "codec": "mp3", "sample_rate": 24000 } }
    //
    // The schema documents a JSON reply carrying base64 audio, but every
    // official sample treats the default response as raw bytes. Handle both:
    // sniff the payload and decode accordingly.
    public static class GrokTts
    {
        public static bool Configured
        {
            get
            {
                return Plugin.CfgGrokEnabled.Value
                    && !string.IsNullOrEmpty(Plugin.CfgGrokApiKey.Value);
            }
        }

        // Three request shapes cover almost everything in the wild. They differ
        // in URL, body AND auth header, so the shape has to be resolved before
        // anything else is built:
        //
        //   xai         POST <base>/tts
        //               Authorization: Bearer <key>
        //               { text, voice_id, language, output_format:{codec,sample_rate} }
        //
        //   elevenlabs  POST <base>/text-to-speech/<voice_id>?output_format=...
        //               xi-api-key: <key>          <-- not a Bearer token
        //               { text, model_id }          <-- voice lives in the PATH
        //
        // Shape is auto-detected from the host so pasting a provider's URL into
        // the settings page just works; the Provider setting overrides it for
        // self-hosted endpoints whose host name gives nothing away.
        public const string ShapeXai = "xai";
        public const string ShapeEleven = "elevenlabs";
        public const string ShapeOpenAi = "openai";

        // Why the last attempt failed, so the settings page can say something
        // more useful than "Failed". A 402 from ElevenLabs means the voice needs
        // a paid plan, which looks identical to a broken setup without this.
        public static long LastStatus;
        public static string LastError;

        public static string FailureLabel()
        {
            switch (LastStatus)
            {
                case 401: return "Bad key";
                case 402: return "Paid plan";
                case 403: return "Forbidden";
                case 404: return "Bad URL";
                case 422: return "Bad voice";
                case 429: return "Rate limited";
                case 0:   return "No connection";
            }
            if (LastStatus >= 400) return "Failed " + LastStatus;
            return "Failed";
        }

        public static string Shape
        {
            get
            {
                string p = Plugin.CfgTtsProvider.Value;
                p = p == null ? "" : p.Trim().ToLowerInvariant();

                // An explicit, recognised choice always wins.
                if (p.StartsWith("eleven")) return ShapeEleven;
                if (p.StartsWith("openai")) return ShapeOpenAi;
                if (p.StartsWith("xai") || p.StartsWith("grok")) return ShapeXai;

                string url = Plugin.CfgGrokBaseUrl.Value;
                url = url == null ? "" : url.ToLowerInvariant();
                if (url.Contains("elevenlabs")) return ShapeEleven;
                if (url.Contains("api.x.ai")) return ShapeXai;
                return ShapeOpenAi;
            }
        }
        static bool IsOpenAiShape { get { return Shape == ShapeOpenAi; } }

        static string DefaultBase()
        {
            switch (Shape)
            {
                case ShapeEleven: return "https://api.elevenlabs.io/v1";
                case ShapeOpenAi: return "https://api.openai.com/v1";
            }
            return "https://api.x.ai/v1";
        }

        static string BuildUrl()
        {
            string url = Plugin.CfgGrokBaseUrl.Value;
            if (string.IsNullOrEmpty(url)) url = DefaultBase();
            url = url.Trim().TrimEnd('/');

            if (Shape == ShapeEleven)
            {
                // ElevenLabs carries the voice in the path, so a base that
                // already includes it must not get a second copy appended.
                if (url.IndexOf("/text-to-speech/", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    if (!url.EndsWith("/text-to-speech", StringComparison.OrdinalIgnoreCase))
                        url += "/text-to-speech";
                    url += "/" + Voices.Current().Trim();
                }
                return url + "?output_format=mp3_44100_128";
            }

            string tail = IsOpenAiShape ? "/audio/speech" : "/tts";
            if (url.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return url;
            return url + tail;
        }

        // ElevenLabs authenticates with its own header rather than a Bearer token.
        static void ApplyAuth(UnityWebRequest req)
        {
            string key = Plugin.CfgGrokApiKey.Value;
            if (string.IsNullOrEmpty(key)) return;

            if (Shape == ShapeEleven) req.SetRequestHeader("xi-api-key", key);
            else req.SetRequestHeader("Authorization", "Bearer " + key);
        }

        // ElevenLabs names its models eleven_*; an OpenAI-style default like
        // "tts-1" would be rejected, so fall back to their standard model
        // rather than passing something the endpoint cannot honour.
        static string ElevenModel()
        {
            string m = Plugin.CfgTtsModel.Value;
            if (string.IsNullOrEmpty(m)) return "eleven_multilingual_v2";
            m = m.Trim();
            if (m.StartsWith("tts-", StringComparison.OrdinalIgnoreCase))
                return "eleven_multilingual_v2";
            return m;
        }

        static string BuildBody(string text)
        {
            JObject root = new JObject();

            if (Shape == ShapeEleven)
            {
                root["text"] = text;
                root["model_id"] = ElevenModel();
                return root.ToString(Newtonsoft.Json.Formatting.None);
            }

            if (IsOpenAiShape)
            {
                root["model"] = Plugin.CfgTtsModel.Value;
                root["input"] = text;
                root["voice"] = Voices.Current();
                root["response_format"] = "mp3";
                if (Math.Abs(Plugin.CfgGrokSpeed.Value - 1f) > 0.001f)
                    root["speed"] = Plugin.CfgGrokSpeed.Value;
            }
            else
            {
                JObject fmt = new JObject();
                fmt["codec"] = "mp3";
                fmt["sample_rate"] = Plugin.CfgGrokSampleRate.Value;

                root["text"] = text;
                root["voice_id"] = Voices.Current();
                root["language"] = Plugin.CfgGrokLanguage.Value;
                root["output_format"] = fmt;
                root["text_normalization"] = Plugin.CfgGrokNormalize.Value;
                if (Math.Abs(Plugin.CfgGrokSpeed.Value - 1f) > 0.001f)
                    root["speed"] = Plugin.CfgGrokSpeed.Value;
            }

            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        // Providers deliver wildly different loudness for the same text: xAI's
        // output sits well below ElevenLabs', which is jarring when swapping
        // between them and leaves her hard to hear over game audio.
        //
        // Scale by the clip's own peak so every voice arrives at the same level,
        // then apply the user's gain. Peak normalisation rather than true
        // loudness (LUFS) analysis: one pass over the samples, no perceptual
        // model, and speech from a TTS engine is consistent enough that the
        // difference is not worth the cost here.
        static void Level(AudioClip clip)
        {
            if (clip == null) return;

            bool normalize = Plugin.CfgTtsNormalize.Value;
            float gain = Plugin.CfgTtsVolume.Value;
            if (!normalize && Math.Abs(gain - 1f) < 0.001f) return;

            float[] samples;
            try
            {
                samples = new float[clip.samples * clip.channels];
                if (!clip.GetData(samples, 0)) return;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not adjust voice level: " + e.Message);
                return;
            }

            float scale = gain;

            if (normalize)
            {
                float peak = 0f;
                for (int i = 0; i < samples.Length; i++)
                {
                    float a = samples[i] < 0f ? -samples[i] : samples[i];
                    if (a > peak) peak = a;
                }

                // Silence, or already at full scale: nothing useful to do.
                if (peak < 0.0001f) return;

                // Target just under 1.0 so the peak itself cannot clip.
                scale = (0.97f / peak) * gain;
            }

            if (Math.Abs(scale - 1f) < 0.01f) return;

            for (int i = 0; i < samples.Length; i++)
            {
                float v = samples[i] * scale;
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                samples[i] = v;
            }

            try { clip.SetData(samples, 0); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not write the levelled audio back: " + e.Message);
                return;
            }

            if (Plugin.CfgLogPayloads.Value)
                Plugin.Log.LogInfo("Voice level scaled x" + scale.ToString("0.00")
                    + (normalize ? " (normalised)" : " (manual gain)"));
        }

        // Calls back with the finished clip, or null if anything went wrong so
        // the caller can fall back to the on-device voice.
        public static IEnumerator Synthesize(string text, Action<AudioClip> done)
        {
            if (string.IsNullOrEmpty(text)) { done(null); yield break; }

            LastStatus = 0;
            LastError = null;

            string url = BuildUrl();
            byte[] body = Encoding.UTF8.GetBytes(BuildBody(text));

            UnityWebRequest req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyAuth(req);

            float t0 = Time.realtimeSinceStartup;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                string detail = "";
                try { detail = req.downloadHandler.text; } catch { }
                if (detail != null && detail.Length > 400) detail = detail.Substring(0, 400) + "...";
                LastStatus = req.responseCode;
                LastError = detail;

                // A 402 here is a plan limit, not a misconfiguration. Say so, or
                // it reads as "the mod is broken".
                if (req.responseCode == 402 && Shape == ShapeEleven)
                    Plugin.Log.LogWarning("ElevenLabs refused this voice on billing grounds. Voice "
                        + "Library voices need a paid plan; built-in ones such as Sarah "
                        + "(EXAVITQu4vr4xnSDxMaL) or Lily (pFZP5JQG7iQjIQuC4Bku) work on the free tier.");

                Plugin.Log.LogError("TTS request failed [" + Shape + "] " + url
                    + " (" + req.responseCode + "): "
                    + req.error + (string.IsNullOrEmpty(detail) ? "" : " | " + detail));
                req.Dispose();
                done(null);
                yield break;
            }

            byte[] audio = req.downloadHandler.data;
            req.Dispose();

            if (audio == null || audio.Length == 0)
            {
                Plugin.Log.LogWarning("Grok TTS returned an empty body.");
                done(null);
                yield break;
            }

            // A JSON envelope starts with '{' once whitespace is skipped; raw
            // MP3 starts with an ID3 tag or an 0xFF frame sync.
            int probe = 0;
            while (probe < audio.Length && (audio[probe] == (byte)' ' || audio[probe] == (byte)'\n'
                   || audio[probe] == (byte)'\r' || audio[probe] == (byte)'\t')) probe++;

            if (probe < audio.Length && audio[probe] == (byte)'{')
            {
                string asText = Encoding.UTF8.GetString(audio);
                try
                {
                    JObject o = JObject.Parse(asText);
                    JToken err = o["error"];
                    if (err != null)
                    {
                        Plugin.Log.LogError("Grok TTS error: " + err.ToString());
                        done(null);
                        yield break;
                    }
                    JToken b64 = o["audio"];
                    if (b64 == null)
                    {
                        Plugin.Log.LogError("Grok TTS reply had no 'audio' field.");
                        done(null);
                        yield break;
                    }
                    audio = Convert.FromBase64String((string)b64);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Grok TTS reply was not usable JSON: " + e.Message);
                    done(null);
                    yield break;
                }
            }

            float elapsed = Time.realtimeSinceStartup - t0;

            AudioClip clip = null;
            IEnumerator decode = Mp3Decoder.ToClip(audio, delegate(AudioClip c) { clip = c; });
            while (decode.MoveNext()) yield return decode.Current;

            if (clip == null)
            {
                done(null);
                yield break;
            }

            Level(clip);

            if (Plugin.CfgLogPayloads.Value)
                Plugin.Log.LogInfo(string.Format(
                    "Grok TTS: voice={0} chars={1} {2:F0}KB {3:F1}s audio in {4:F1}s",
                    Plugin.CfgGrokVoiceId.Value, text.Length, audio.Length / 1024f,
                    clip.length, elapsed));

            done(clip);
        }
    }

    // Unity can only build an AudioClip from compressed audio via a file URL,
    // so the MP3 lands in a temp file that is reused and overwritten per line.
    public static class Mp3Decoder
    {
        static string _path;

        static string TempPath()
        {
            if (_path == null)
                _path = System.IO.Path.Combine(Application.temporaryCachePath, "ai2u_grok_tts.mp3");
            return _path;
        }

        public static IEnumerator ToClip(byte[] mp3, Action<AudioClip> done)
        {
            string file = TempPath();
            try
            {
                System.IO.File.WriteAllBytes(file, mp3);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Grok TTS: could not stage audio at " + file + ": " + e.Message);
                done(null);
                yield break;
            }

            string uri = "file:///" + file.Replace('\\', '/');
            UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.MPEG);
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Plugin.Log.LogError("Grok TTS: decode failed: " + req.error);
                req.Dispose();
                done(null);
                yield break;
            }

            AudioClip clip = null;
            try { clip = DownloadHandlerAudioClip.GetContent(req); }
            catch (Exception e) { Plugin.Log.LogError("Grok TTS: clip build failed: " + e.Message); }
            req.Dispose();

            done(clip);
        }
    }
}
