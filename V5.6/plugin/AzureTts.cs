// Azure Speech, made to fail out loud and repair itself where it can.
//
// Three reports arrived in the same week and looked unrelated:
//   - "I entered my Azure key and saved it, but she still has the robotic voice."
//   - "Both the local voice and the Azure voice sound robotic."
//   - "The Azure voice field for each girl does nothing."
//
// They are one bug. Azure Speech keys belong to ONE region, and the mod's region
// field defaulted to eastus. A key created anywhere else - in the test that
// found this, northcentralus - is answered by eastus with HTTP 401. The synth
// path treated every Azure failure the same way: log a warning nobody reads,
// then quietly synthesize the line with the offline Overtone voice and hand
// that clip back as if nothing had happened. The Test Voice button reported
// "Works! (game voice)" because a clip did come back. So a player whose Azure
// setup had failed heard the robotic offline voice, was told Azure worked, and
// concluded Azure sounds robotic; and every per-girl voice they typed changed
// nothing, because no request was ever reaching Azure to carry it.
//
// A per-girl voice the key's region does not offer fails the same way (HTTP
// 400 - the Dragon HD voices, for instance, simply do not exist in several
// regions), and took the whole line down to Overtone with it.
//
// So this file does three things the old path did not:
//   1. It says what happened. Every synthesis records whether Azure actually
//      spoke, which voice it used, and if not, why, in words a player can act
//      on. The test buttons and an in-game notice read that record.
//   2. It repairs what it can. A region typed as "North Central US" or pasted
//      as the full endpoint URL is read as the region it names; a 401 triggers
//      a search for the region the key actually belongs to, and the answer is
//      remembered; a per-girl voice the region does not have falls back to HER
//      original voice, not to the offline engine.
//   3. It lets the voices sound like people. Output is 24 kHz instead of 16 kHz
//      (16 kHz cuts everything above 8 kHz, which is most of what makes a
//      neural voice sound present), and voices that support Azure's speaking
//      styles are given one that matches how she currently feels.
//
// Everything here is driven from coroutines hosted on game objects that can be
// destroyed mid-call (a level change, closing the game's settings page). Unity
// does not run a stopped coroutine's finally blocks, so every "in progress"
// marker below carries a start time and simply expires if nobody finishes it.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Security;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace AI2UCustomAI
{
    internal sealed class AzureVoice
    {
        public string ShortName;
        public string LocalName;
        public string Locale;
        public string Gender;
        public string VoiceType;
        public string[] Styles;

        // Lower-cased, punctuation-free forms, computed once when the catalogue
        // loads. The Voice tab checks every typed name against the catalogue on
        // every GUI event; recomputing these per voice per event was tens of
        // thousands of throwaway strings a frame.
        public string SqName;
        public string SqLocal;
        public string SqShort;

        // HD voices are listed as VoiceType "NeuralHD" (the ...MultilingualNeuralHD
        // family) or carry "DragonHD" in the name. They are the most natural voices
        // Azure has, and a player picking a voice for a character almost always
        // wants to see them first.
        public bool IsHD
        {
            get
            {
                return string.Equals(VoiceType, "NeuralHD", StringComparison.OrdinalIgnoreCase)
                    || (ShortName != null && ShortName.IndexOf("DragonHD", StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        public bool HasStyle(string style)
        {
            if (Styles == null || style == null) return false;
            for (int i = 0; i < Styles.Length; i++)
                if (string.Equals(Styles[i], style, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    // What ONE synthesis did. Each call gets its own, because the game keeps
    // running while the settings are open: a reply can be spoken while a test is
    // in flight, and a shared record let a failed test report the other call's
    // success - the exact false "Works!" this whole rewrite exists to remove.
    internal sealed class AzureOutcome
    {
        public bool Ok;
        public bool FellBack;
        public string Voice;
        public string Style;
        public string Region;
        public string Problem;
        // Something worth telling the player that did NOT stop Azure speaking:
        // a region that was corrected, or a chosen voice replaced by her original.
        public string Note;
    }

    internal static class AzureTts
    {
        // Every region that hosts Speech text-to-speech. Probed only after a key
        // has been rejected, all at once, so a sweep costs about as long as the
        // slowest single answer rather than the sum.
        internal static readonly string[] Regions =
        {
            "eastus", "eastus2", "westus", "westus2", "westus3", "centralus",
            "northcentralus", "southcentralus", "westcentralus", "canadacentral",
            "canadaeast", "brazilsouth", "mexicocentral", "northeurope", "westeurope",
            "uksouth", "ukwest", "francecentral", "germanywestcentral", "norwayeast",
            "swedencentral", "switzerlandnorth", "switzerlandwest", "italynorth",
            "polandcentral", "spaincentral", "eastasia", "southeastasia", "japaneast",
            "japanwest", "koreacentral", "koreasouth", "australiaeast",
            "australiasoutheast", "centralindia", "southindia", "westindia",
            "jioindiawest", "uaenorth", "qatarcentral", "southafricanorth",
            "israelcentral",
        };

        internal static bool IsKnownRegion(string r)
        {
            if (string.IsNullOrEmpty(r)) return false;
            for (int i = 0; i < Regions.Length; i++)
                if (Regions[i] == r) return true;
            return false;
        }

        // ---------------------------------------------------------------
        // Key and region input
        // ---------------------------------------------------------------

        // The key the synth path will use: the mod's own field, or failing that
        // the base game's personal-TTS key, which a player may have set through
        // the game's own menu. Every place that asks "is there a key" asks this,
        // so the panel and the voice agree.
        internal static string EffectiveKey()
        {
            string key = Plugin.CfgGameVoiceKey != null ? (Plugin.CfgGameVoiceKey.Value ?? "").Trim() : "";
            if (key.Length > 0) return key;
            try { key = (Communicator.APIKey_UserPersonalTTS ?? "").Trim(); } catch (Exception) { key = ""; }
            return key;
        }

        // The field asks for a region, and the Azure portal shows three different
        // things that look like one: the region code (northcentralus), its display
        // name (North Central US), and the endpoint URL, which the Keys and
        // Endpoint page puts right next to the key. People paste whichever they see
        // first - the test that found this bug began with the full endpoint URL.
        // All three name the same region, so all three are read as it.
        internal static string NormalizeRegion(string raw)
        {
            if (raw == null) return "";
            string s = raw.Trim();
            if (s.Length == 0) return "";

            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s.Substring(scheme + 3);

            int slash = s.IndexOf('/');
            if (slash >= 0) s = s.Substring(0, slash);

            // A host like northcentralus.tts.speech.microsoft.com or
            // northcentralus.api.cognitive.microsoft.com carries the region as its
            // first label.
            int dot = s.IndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);

            return Squash(s);
        }

        internal static string TtsUrl(string region)
        {
            return "https://" + region + ".tts.speech.microsoft.com/cognitiveservices/v1";
        }

        internal static string VoicesUrl(string region)
        {
            return "https://" + region + ".tts.speech.microsoft.com/cognitiveservices/voices/list";
        }

        // ---------------------------------------------------------------
        // The last line's outcome, for the Voice tab's status line only
        // ---------------------------------------------------------------
        //
        // Tests never read these: they read the AzureOutcome of their own call.
        // This is just "what happened to whatever spoke last", for display.
        internal static bool LastOk;
        internal static bool LastFellBack;
        internal static string LastVoice;
        internal static string LastStyle;
        internal static string LastRegion;
        internal static string LastProblem;
        internal static string LastNote;

        internal static void Publish(AzureOutcome o)
        {
            if (o == null) return;
            LastOk = o.Ok;
            LastFellBack = o.FellBack;
            LastVoice = o.Voice;
            LastStyle = o.Style;
            LastRegion = o.Region;
            LastProblem = o.Problem;
            LastNote = o.Note;
        }

        // The on-screen notice appears once per distinct problem per session - the
        // first failure has to be impossible to miss, the fortieth must not bury
        // the game. The log gets every one, so a later failure is never invisible
        // to someone reading LogOutput.log.
        static readonly HashSet<string> _announced = new HashSet<string>();

        internal static void Announce(string problem)
        {
            if (string.IsNullOrEmpty(problem)) return;
            Plugin.Log.LogWarning("Azure TTS: " + problem);
            if (_announced.Add(problem)) Plugin.Toast("Azure voice: " + problem, 8f);
        }

        // Turns an HTTP outcome into the sentence a player needs.
        internal static string Explain(long code, string region, string voice, string error)
        {
            switch (code)
            {
                case 401:
                case 403:
                    return "the key was rejected in region \"" + region + "\". Azure keys belong to ONE "
                        + "region - check the key and the region on the Voice tab.";
                case 400:
                    return "Azure refused the request - usually the voice \"" + voice
                        + "\" does not exist in region \"" + region + "\".";
                case 429:
                    return "Azure's rate limit or monthly free quota is used up for this key.";
                case 0:
                    return IsKnownRegion(region)
                        ? "could not reach Azure (" + (error ?? "no connection") + ")."
                        : "\"" + region + "\" is not an Azure region - use a code like northcentralus.";
            }
            return "Azure answered HTTP " + code + (string.IsNullOrEmpty(error) ? "" : " (" + error + ")") + ".";
        }

        // Caches are per key. A player who fixes a wrong key mid-session must not
        // be held to the failures the old key earned.
        static string _key;

        internal static void UseKey(string key)
        {
            if (key == _key) return;
            _key = key;
            _catalog.Clear();
            _catalogFailed.Clear();
            _catalogFailedAt.Clear();
        }

        // The catalogue failures that mean "right key, wrong region" (or a region
        // that does not exist), as opposed to a network hiccup in a real region.
        internal static bool RegionLooksWrong(long catalogFailure, string region)
        {
            if (catalogFailure == 401 || catalogFailure == 403) return true;
            return catalogFailure == -1 && !IsKnownRegion(region);
        }

        internal static bool RejectsRegion(long httpCode, string region)
        {
            return httpCode == 401 || httpCode == 403 || (httpCode == 0 && !IsKnownRegion(region));
        }

        // ---------------------------------------------------------------
        // Finding the region a key belongs to
        // ---------------------------------------------------------------
        //
        // voices/list is a free GET that answers 200 only in the key's own region
        // (or a region Azure pairs it with) and 401 everywhere else, so it is a
        // cheap and exact probe. All regions are asked at once and the first 200
        // wins - every region that answers 200 accepts the key.
        //
        // The answer is remembered per key for the session. Without that, a
        // second profile still holding the old wrong region (or a region edited
        // back by hand) was refused forever, because the sweep "had already run"
        // and its result had been thrown away.
        static readonly HashSet<string> _swept = new HashSet<string>();
        static readonly Dictionary<string, string> _foundFor = new Dictionary<string, string>();
        static readonly Dictionary<string, float> _sweepingSince = new Dictionary<string, float>();
        // A sweep that found NOTHING is only believed for a minute. A key made
        // moments ago can be refused everywhere while Azure is still activating
        // it, and a slow connection can time a sweep out; caching that answer for
        // the whole session meant the repair never ran again once the key worked.
        static readonly Dictionary<string, float> _sweptEmptyAt = new Dictionary<string, float>();

        internal static string KnownRegion(string key)
        {
            string r;
            return key != null && _foundFor.TryGetValue(key, out r) ? r : null;
        }

        static bool Sweeping(string key)
        {
            float t;
            if (key == null || !_sweepingSince.TryGetValue(key, out t)) return false;
            if (Time.realtimeSinceStartup - t > 15f) { _sweepingSince.Remove(key); return false; }
            return true;
        }

        // The one entry point for "Azure rejected this key here - where should it
        // go instead?". done receives a better region, or null when nothing better
        // is known. A sweep already running for this key is waited for rather than
        // duplicated; a key already swept this session reuses the answer.
        internal static IEnumerator RepairRegion(string key, string region, Action<string> done)
        {
            if (string.IsNullOrEmpty(key)) { done(null); yield break; }

            float until = Time.realtimeSinceStartup + 14f;
            while (Sweeping(key) && Time.realtimeSinceStartup < until) yield return null;

            float emptyAt;
            bool recentlyEmpty = _sweptEmptyAt.TryGetValue(key, out emptyAt)
                && Time.realtimeSinceStartup - emptyAt < 60f;
            if (!_swept.Contains(key) && !recentlyEmpty)
            {
                IEnumerator d = DetectRegion(key);
                while (d.MoveNext()) yield return d.Current;
            }

            string known = KnownRegion(key);
            done(known != null && known != region ? known : null);
        }

        static IEnumerator DetectRegion(string key)
        {
            _sweepingSince[key] = Time.realtimeSinceStartup;

            List<UnityWebRequest> reqs = new List<UnityWebRequest>();
            List<string> regs = new List<string>();
            for (int i = 0; i < Regions.Length; i++)
            {
                try
                {
                    UnityWebRequest r = UnityWebRequest.Get(VoicesUrl(Regions[i]));
                    r.SetRequestHeader("Ocp-Apim-Subscription-Key", key);
                    r.timeout = 8;
                    r.SendWebRequest();
                    reqs.Add(r);
                    regs.Add(Regions[i]);
                }
                catch (Exception) { }
            }

            string found = null;
            float until = Time.realtimeSinceStartup + 10f;
            while (found == null && Time.realtimeSinceStartup < until)
            {
                bool pending = false;
                for (int i = 0; i < reqs.Count; i++)
                {
                    if (!reqs[i].isDone) { pending = true; continue; }
                    if (reqs[i].responseCode == 200) { found = regs[i]; break; }
                }
                if (found != null || !pending) break;
                yield return null;
            }

            for (int i = 0; i < reqs.Count; i++)
            {
                try
                {
                    if (!reqs[i].isDone) reqs[i].Abort();
                    reqs[i].Dispose();
                }
                catch (Exception) { }
            }

            // Marked done only now. Marking it at the start meant a sweep cut short
            // (the level changed, the settings page closed) left the key flagged
            // as swept with no answer, and nothing ever tried again.
            if (found != null)
            {
                _swept.Add(key);
                _foundFor[key] = found;
                _sweptEmptyAt.Remove(key);
            }
            else _sweptEmptyAt[key] = Time.realtimeSinceStartup;
            _sweepingSince.Remove(key);

            if (found != null)
                Plugin.Log.LogInfo("Azure TTS: this key belongs to region \"" + found + "\".");
            else
                Plugin.Log.LogWarning("Azure TTS: no region accepted this key - it is probably mistyped, "
                    + "disabled, or not a Speech key.");
        }

        // ---------------------------------------------------------------
        // The region's voice catalogue
        // ---------------------------------------------------------------
        //
        // Fetched once per region per key. It is what makes a typed voice name
        // checkable before it costs a line, what lets "Jenny" mean
        // en-US-JennyNeural, what fills the picker, and what says which speaking
        // styles a voice actually has.
        static readonly Dictionary<string, List<AzureVoice>> _catalog = new Dictionary<string, List<AzureVoice>>();
        static readonly Dictionary<string, float> _catalogBusy = new Dictionary<string, float>();
        // Whose load a busy mark belongs to. The panel can fetch with a key typed
        // but not saved while a line is being spoken with the saved one, and a
        // waiter must not adopt a result that was earned by the other key.
        static readonly Dictionary<string, string> _catalogBusyKey = new Dictionary<string, string>();
        static readonly Dictionary<string, long> _catalogFailed = new Dictionary<string, long>();
        static readonly Dictionary<string, float> _catalogFailedAt = new Dictionary<string, float>();

        internal static List<AzureVoice> Catalog(string region)
        {
            List<AzureVoice> v;
            return region != null && _catalog.TryGetValue(region, out v) ? v : null;
        }

        internal static bool CatalogBusy(string region)
        {
            float t;
            if (region == null || !_catalogBusy.TryGetValue(region, out t)) return false;
            // A load whose coroutine died never clears its own mark. The request
            // times out at 10 s, so anything older than 20 s is a dead mark.
            if (Time.realtimeSinceStartup - t > 20f)
            {
                _catalogBusy.Remove(region);
                _catalogBusyKey.Remove(region);
                return false;
            }
            return true;
        }

        // The failure on record for a region was a rejection (401/403), but this
        // key has since been SEEN to work there - a key that was still
        // activating, or a failure another key earned. Drop the stale rejection so
        // the voice list is fetched again; without it, per-girl voice names and
        // speaking styles stayed off for the whole session.
        internal static void KeyProvenIn(string region)
        {
            long c;
            if (region == null || _catalog.ContainsKey(region)) return;
            if (_catalogFailed.TryGetValue(region, out c) && (c == 401 || c == 403))
            {
                _catalogFailed.Remove(region);
                _catalogFailedAt.Remove(region);
            }
        }

        // The HTTP code of a failed catalogue fetch (-1 no connection, -2 an
        // unreadable reply), or 0 when none is on record.
        //
        // Failures that are evidence about the key or region stay for the session.
        // Anything that could be a passing hiccup - a dropped connection in a real
        // region, a 429, a 5xx, a slow download - expires after a minute. Before,
        // one bad moment on the first line of a session left the catalogue missing
        // for good, which quietly disabled every per-girl voice name and every
        // speaking style until the game was restarted.
        internal static long CatalogFailure(string region)
        {
            long c;
            if (region == null || !_catalogFailed.TryGetValue(region, out c)) return 0;
            bool sticky = c == 401 || c == 403 || c == -2 || (c == -1 && !IsKnownRegion(region));
            float at;
            if (!sticky && _catalogFailedAt.TryGetValue(region, out at) && Time.realtimeSinceStartup - at > 60f)
            {
                _catalogFailed.Remove(region);
                _catalogFailedAt.Remove(region);
                return 0;
            }
            return c;
        }

        static void CatalogFailed(string region, long code)
        {
            _catalogFailed[region] = code;
            _catalogFailedAt[region] = Time.realtimeSinceStartup;
        }

        internal static IEnumerator LoadCatalog(string region, string key)
        {
            if (string.IsNullOrEmpty(region) || string.IsNullOrEmpty(key)) yield break;
            if (_catalog.ContainsKey(region)) yield break;

            // Somebody else is already fetching it WITH THIS KEY: wait for their
            // answer instead of carrying on as if there were no catalogue. If
            // their load died without an answer, fall through and fetch it here.
            string busyKey;
            if (CatalogBusy(region) && _catalogBusyKey.TryGetValue(region, out busyKey) && busyKey == key)
            {
                float until = Time.realtimeSinceStartup + 22f;
                while (CatalogBusy(region) && Time.realtimeSinceStartup < until) yield return null;
                if (_catalog.ContainsKey(region) || CatalogFailure(region) != 0 || CatalogBusy(region)) yield break;
            }

            _catalogBusy[region] = Time.realtimeSinceStartup;
            _catalogBusyKey[region] = key;

            UnityWebRequest req = UnityWebRequest.Get(VoicesUrl(region));
            req.SetRequestHeader("Ocp-Apim-Subscription-Key", key);
            req.timeout = 10;
            yield return req.SendWebRequest();

            long code = req.responseCode;
            string body = null;
            try { body = req.downloadHandler != null ? req.downloadHandler.text : null; } catch (Exception) { }
            bool ok = req.result == UnityWebRequest.Result.Success && code == 200 && !string.IsNullOrEmpty(body);
            req.Dispose();

            string owner;
            if (_catalogBusyKey.TryGetValue(region, out owner) && owner == key)
            {
                _catalogBusy.Remove(region);
                _catalogBusyKey.Remove(region);
            }

            // The key in use changed while this was in flight (the panel's typed
            // key versus the saved one). This answer belongs to a key nobody is
            // using now; recording it would pin that key's failure on the other.
            if (key != _key) yield break;

            if (!ok)
            {
                CatalogFailed(region, code == 0 ? -1 : code);
                yield break;
            }

            List<AzureVoice> list = new List<AzureVoice>();
            try
            {
                JArray arr = JArray.Parse(body);
                foreach (JToken t in arr)
                {
                    JObject o = t as JObject;
                    if (o == null) continue;
                    AzureVoice v = new AzureVoice();
                    v.ShortName = (string)o["ShortName"];
                    v.LocalName = (string)o["LocalName"];
                    v.Locale = (string)o["Locale"];
                    v.Gender = (string)o["Gender"];
                    v.VoiceType = (string)o["VoiceType"];
                    JArray st = o["StyleList"] as JArray;
                    if (st != null)
                    {
                        v.Styles = new string[st.Count];
                        for (int i = 0; i < st.Count; i++) v.Styles[i] = (string)st[i];
                    }
                    if (string.IsNullOrEmpty(v.ShortName)) continue;
                    v.SqName = Squash(NamePart(v.ShortName));
                    v.SqLocal = Squash(v.LocalName);
                    v.SqShort = Squash(v.ShortName);
                    list.Add(v);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Azure TTS: could not read the voice list (" + e.Message + ").");
                CatalogFailed(region, -2);
                yield break;
            }

            _catalog[region] = list;
            _catalogFailed.Remove(region);
            _catalogFailedAt.Remove(region);
            Plugin.Log.LogInfo("Azure TTS: region " + region + " offers " + list.Count + " voices.");
        }

        internal static AzureVoice Find(string region, string shortName)
        {
            List<AzureVoice> list = Catalog(region);
            if (list == null || string.IsNullOrEmpty(shortName)) return null;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].ShortName, shortName, StringComparison.OrdinalIgnoreCase)) return list[i];
            return null;
        }

        // ---------------------------------------------------------------
        // What a player types, read as a voice
        // ---------------------------------------------------------------
        //
        // The old rule accepted a name only if it contained "Neural". That silently
        // threw away a plain "Jenny" and let through names the region does not
        // offer, which then failed the whole line. Now:
        //   - a name the catalogue lists is used exactly as listed;
        //   - a voice's own display name is matched as typed, so a Japanese player
        //     can type the 七海 the browser shows them;
        //   - a bare name ("Jenny", "JennyNeural", "nova hd") is matched against
        //     the catalogue, English first, so it means what the player meant;
        //   - anything else is rejected BEFORE it costs a line, with a reason, and
        //     the caller keeps her original voice instead.
        // With no catalogue (it could not be fetched), a full locale-prefixed name
        // is trusted and anything else is refused.
        internal static string ResolveName(string raw, string region, out string problem)
        {
            problem = null;
            if (raw == null) return null;
            string want = raw.Trim();
            if (want.Length == 0) return null;

            List<AzureVoice> list = Catalog(region);
            if (list == null)
            {
                if (LooksLikeShortName(want)) return want;
                problem = "\"" + want + "\" is not a full Azure voice name (like en-US-JennyNeural), and "
                    + "the voice list for region " + region + " could not be loaded to look it up.";
                return null;
            }

            AzureVoice exact = Find(region, want);
            if (exact != null) return exact.ShortName;

            AzureVoice best = null;
            int bestScore = -1;

            // Exact display name, compared as typed - the only match that works for
            // names written in a non-Latin script.
            for (int i = 0; i < list.Count; i++)
            {
                AzureVoice v = list[i];
                if (!string.Equals(v.LocalName, want, StringComparison.OrdinalIgnoreCase)) continue;
                int score = Pref(v);
                if (score > bestScore) { best = v; bestScore = score; }
            }
            if (best != null) return best.ShortName;

            // Everything below compares letters and digits only. A name with none
            // (七海 that was not an exact display name, "?", "-") must be refused
            // here: an empty needle is a prefix of every voice, and used to pick
            // whichever voice happened to be first in the list - an Amharic one.
            string needle = Squash(want);
            if (needle.Length == 0)
            {
                problem = "\"" + want + "\" is not a voice region " + region + " offers.";
                return null;
            }
            string needleN = needle + "neural";

            for (int i = 0; i < list.Count; i++)
            {
                AzureVoice v = list[i];
                int score = -1;
                if (v.SqName == needle || v.SqName == needleN) score = 100;
                else if (v.SqLocal == needle) score = 90;
                else if (v.SqName != null && v.SqName.StartsWith(needle, StringComparison.Ordinal)) score = 60;
                if (score < 0) continue;
                score += Pref(v);
                if (score > bestScore) { best = v; bestScore = score; }
            }
            if (best != null) return best.ShortName;

            problem = "\"" + want + "\" is not a voice region " + region + " offers.";
            return null;
        }

        // Tie-break toward English, then American English.
        static int Pref(AzureVoice v)
        {
            if (v.Locale == "en-US") return 8;
            if (v.Locale != null && v.Locale.StartsWith("en-", StringComparison.Ordinal)) return 4;
            return 0;
        }

        internal static bool LooksLikeShortName(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string[] parts = s.Split('-');
            if (parts.Length < 3) return false;
            if (parts[0].Length < 2 || parts[0].Length > 3) return false;
            if (parts[1].Length < 2 || parts[1].Length > 4) return false;
            return parts[2].Length > 0;
        }

        // en-US-JennyNeural -> JennyNeural: everything after the locale.
        static string NamePart(string shortName)
        {
            if (string.IsNullOrEmpty(shortName)) return "";
            int a = shortName.IndexOf('-');
            if (a < 0) return shortName;
            int b = shortName.IndexOf('-', a + 1);
            return b < 0 ? shortName : shortName.Substring(b + 1);
        }

        static string Squash(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            string lower = s.ToLowerInvariant();
            for (int i = 0; i < lower.Length; i++)
            {
                char c = lower[i];
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            }
            return sb.ToString();
        }

        // The xml:lang an SSML document needs for a voice: the catalogue's locale
        // when known, else the voice name's own locale prefix.
        internal static string LocaleOf(string shortName, string region)
        {
            AzureVoice v = Find(region, shortName);
            if (v != null && !string.IsNullOrEmpty(v.Locale)) return v.Locale;
            if (string.IsNullOrEmpty(shortName)) return "en-US";
            int a = shortName.IndexOf('-');
            int b = a < 0 ? -1 : shortName.IndexOf('-', a + 1);
            return b > 0 ? shortName.Substring(0, b) : "en-US";
        }

        // Candidates for the picker, filtered by what is typed, HD voices first,
        // then English, then everything else. A filter with no Latin letters in it
        // is matched as typed, so a display name in any script still narrows it.
        internal static List<AzureVoice> Suggest(string region, string filter, int max)
        {
            List<AzureVoice> list = Catalog(region);
            List<AzureVoice> outp = new List<AzureVoice>();
            if (list == null) return outp;
            string raw = (filter ?? "").Trim();
            string f = Squash(raw);

            List<AzureVoice> hd = new List<AzureVoice>(), us = new List<AzureVoice>(),
                en = new List<AzureVoice>(), rest = new List<AzureVoice>();
            for (int i = 0; i < list.Count; i++)
            {
                AzureVoice v = list[i];
                if (f.Length > 0)
                {
                    if ((v.SqShort == null || v.SqShort.IndexOf(f, StringComparison.Ordinal) < 0)
                        && (v.SqLocal == null || v.SqLocal.IndexOf(f, StringComparison.Ordinal) < 0)) continue;
                }
                else if (raw.Length > 0)
                {
                    if ((v.ShortName == null || v.ShortName.IndexOf(raw, StringComparison.OrdinalIgnoreCase) < 0)
                        && (v.LocalName == null || v.LocalName.IndexOf(raw, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                }
                bool english = v.Locale != null && v.Locale.StartsWith("en-", StringComparison.Ordinal);
                if (v.IsHD && english) hd.Add(v);
                else if (v.Locale == "en-US") us.Add(v);
                else if (english) en.Add(v);
                else rest.Add(v);
            }
            List<AzureVoice>[] tiers = { hd, us, en, rest };
            for (int t = 0; t < tiers.Length && outp.Count < max; t++)
                for (int i = 0; i < tiers[t].Count && outp.Count < max; i++) outp.Add(tiers[t][i]);
            return outp;
        }

        // ---------------------------------------------------------------
        // How she feels, as a speaking style
        // ---------------------------------------------------------------
        //
        // A neural voice reading every line in its default register is a big part
        // of why it sounds recited rather than spoken. Many Azure voices (Jane and
        // Nancy among the original cast) carry speaking styles - angry, cheerful,
        // sad, terrified, shy - and the model already reports her expression and
        // her anger with every reply. This maps one to the other.
        //
        // Each mood lists styles in order of preference and the first one the
        // voice actually has is used; a voice with none of them (Amber, Elysia's
        // original, has no styles at all) simply speaks as before. Azure ignores a
        // style a voice lacks rather than failing, but choosing only real ones
        // means the fallback is the next-closest emotion, not nothing.
        internal static string MoodFace;
        internal static string MoodAngry;
        internal static float MoodAt = -999f;

        internal static void NoteMood(JObject reactions)
        {
            if (reactions == null) return;
            try
            {
                JToken f = reactions["npc_face_expression"];
                JToken a = reactions["angry_level"];
                MoodFace = f != null && f.Type == JTokenType.String ? ((string)f).Trim().ToLowerInvariant() : null;
                MoodAngry = a != null && a.Type == JTokenType.String ? ((string)a).Trim().ToLowerInvariant() : null;
                MoodAt = Time.realtimeSinceStartup;
            }
            catch (Exception) { }
        }

        internal static string StyleFor(AzureVoice voice)
        {
            if (voice == null || voice.Styles == null || voice.Styles.Length == 0) return null;

            // The mood belongs to the reply being spoken. Speech starts moments
            // after the reply lands, so a mood older than a minute belongs to some
            // earlier line and is not reused.
            if (Time.realtimeSinceStartup - MoodAt > 60f) return null;

            string[] chain = null;
            if (MoodAngry == "furious" || MoodAngry == "extremely furious")
                chain = new[] { "angry", "shouting", "unfriendly" };
            else
            {
                switch (MoodFace)
                {
                    case "angry":
                    case "angry_face":
                    case "disgust":
                        chain = new[] { "angry", "unfriendly", "disgruntled" };
                        break;
                    case "scream":
                        chain = new[] { "terrified", "fearful", "shouting" };
                        break;
                    case "worried":
                    case "scared":
                        chain = new[] { "fearful", "terrified", "sad" };
                        break;
                    case "sad":
                    case "tired_face":
                        chain = new[] { "sad", "depressed", "empathetic" };
                        break;
                    case "smile":
                    case "slight_smile":
                    case "grin":
                        chain = new[] { "cheerful", "friendly", "hopeful" };
                        break;
                    case "shy":
                        chain = new[] { "shy", "gentle", "friendly" };
                        break;
                    case "surprise":
                        chain = new[] { "excited", "cheerful" };
                        break;
                }
            }
            if (chain == null) return null;

            for (int i = 0; i < chain.Length; i++)
                if (voice.HasStyle(chain[i])) return chain[i];
            return null;
        }

        // ---------------------------------------------------------------
        // The request itself
        // ---------------------------------------------------------------
        internal static string BuildSsml(string lang, string voice, string pitch, string rate, string style, string text)
        {
            string escaped = SecurityElement.Escape(text ?? "");
            StringBuilder sb = new StringBuilder();
            sb.Append("<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' ");
            sb.Append("xmlns:mstts='https://www.w3.org/2001/mstts' xml:lang='").Append(SecurityElement.Escape(lang)).Append("'>");
            sb.Append("<voice name='").Append(SecurityElement.Escape(voice)).Append("'>");
            if (!string.IsNullOrEmpty(style))
                sb.Append("<mstts:express-as style='").Append(SecurityElement.Escape(style)).Append("'>");

            bool prosody = (pitch != null && pitch != "+0%") || (rate != null && rate != "+0%");
            if (prosody)
                sb.Append("<prosody pitch='").Append(pitch ?? "+0%").Append("' rate='").Append(rate ?? "+0%").Append("'>");
            sb.Append(escaped);
            if (prosody) sb.Append("</prosody>");

            if (!string.IsNullOrEmpty(style)) sb.Append("</mstts:express-as>");
            sb.Append("</voice></speak>");
            return sb.ToString();
        }
    }
}
