using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Newtonsoft.Json.Linq;

namespace AI2UCustomAI
{
    // Shared helpers for grafting controls onto the game's existing settings
    // pages by cloning widgets that are already there.
    //
    // Two things bite when doing this:
    //
    //  1. I2 Localization. A LocalizeDropdown re-applies its localised option
    //     list every time the object is enabled, so any label written from a
    //     patch is silently overwritten a frame later. The component has to go.
    //
    //  2. Positioning. Instantiating a UI object copies its RectTransform
    //     exactly, so a clone lands precisely on top of its source and looks
    //     like nothing happened. Unless a LayoutGroup is managing the parent,
    //     the clone has to be moved by hand.
    public static class UiGraft
    {
        public static void StripLocalizers(GameObject go)
        {
            if (go == null) return;

            I2.Loc.LocalizeDropdown ld = go.GetComponent<I2.Loc.LocalizeDropdown>();
            if (ld != null) UnityEngine.Object.Destroy(ld);

            foreach (I2.Loc.Localize l in go.GetComponentsInChildren<I2.Loc.Localize>(true))
                UnityEngine.Object.Destroy(l);
        }

        public static bool ParentIsAutoLaidOut(Transform parent)
        {
            if (parent == null) return false;
            return parent.GetComponent<LayoutGroup>() != null;
        }

        // Clone `source`, name it, and offset it from the original unless a
        // LayoutGroup is already handling placement.
        public static GameObject Clone(GameObject source, string name, Vector2 offset)
        {
            if (source == null) return null;
            Transform parent = source.transform.parent;
            if (parent == null) return null;

            Transform found = parent.Find(name);
            if (found != null) return found.gameObject;

            GameObject clone = UnityEngine.Object.Instantiate(source, parent);
            clone.name = name;
            clone.SetActive(true);

            if (!ParentIsAutoLaidOut(parent))
            {
                RectTransform src = source.GetComponent<RectTransform>();
                RectTransform dst = clone.GetComponent<RectTransform>();
                if (src != null && dst != null)
                    dst.anchoredPosition = src.anchoredPosition + offset;
            }
            else
            {
                clone.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
            }

            return clone;
        }

        public static void SetPlaceholder(TMP_InputField field, string text)
        {
            if (field == null) return;
            TMP_Text p = field.placeholder as TMP_Text;
            if (p != null) p.text = text;
        }

        // Logs the real positions of a page's widgets. Cloning blind is what
        // produced the overlapping first attempt; this makes the actual layout
        // visible so offsets can be derived instead of guessed.
        public static void Dump(Transform root, string title)
        {
            if (!Plugin.CfgLogPayloads.Value || root == null) return;
            StringBuilder sb = new StringBuilder();
            sb.Append("layout dump - ").Append(title).Append('\n');
            Walk(root, sb, 0);
            Plugin.Log.LogInfo(sb.ToString());
        }

        static void Walk(Transform t, StringBuilder sb, int depth)
        {
            if (depth > 7) return;
            RectTransform r = t as RectTransform;
            sb.Append(' ', depth * 2).Append(t.name);
            if (r != null)
                sb.Append("  pos=").Append(r.anchoredPosition.ToString("F0"))
                  .Append(" size=").Append(r.rect.size.ToString("F0"));
            if (!t.gameObject.activeSelf) sb.Append("  [inactive]");
            sb.Append('\n');
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), sb, depth + 1);
        }
    }

    // Takes over the game's "AI Setup" page: relabels the provider dropdown to
    // name the model actually answering, and adds editable Base URL / key /
    // model fields plus a Test button that reports green or red.
    [HarmonyPatch(typeof(UIManager_APIKeyPage), "SetUpPage")]
    public static class ModUiPatch
    {
        const string UrlName = "AI2UMod_UrlInput";
        const string ModelName = "AI2UMod_ModelInput";
        const string TestName = "AI2UMod_TestButton";

        static TMP_InputField _url, _key, _model;
        static Button _test;
        static TMP_Text _testLabel;
        static bool _movedKey, _movedVoice;

        // The page's "OpenAI API Key" caption is a sibling of the key field and
        // is wrong once three differently-purposed fields share the row.
        static void HideStaleLabel(Transform parent)
        {
            if (parent == null) return;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c.name.StartsWith("AI2UMod_")) continue;
                if (c.GetComponent<TMP_InputField>() != null) continue;

                TMP_Text txt = c.GetComponent<TMP_Text>();
                if (txt == null) continue;
                string s = txt.text == null ? "" : txt.text.ToLowerInvariant();
                if (s.Contains("api key") || s.Contains("openai"))
                    c.gameObject.SetActive(false);
            }
        }

        static void Postfix(UIManager_APIKeyPage __instance)
        {
            // Mod off means the game's own AI Setup page is left exactly as the
            // game built it. This page only exists on the itch build; the F9
            // panel is the real settings UI on both.
            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value) return;

            try { Build(__instance); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not customise the AI Setup page: " + e);
            }
        }

        static void Build(UIManager_APIKeyPage page)
        {
            Traverse t = Traverse.Create(page);

            TMP_Dropdown ddText = t.Field("dropdown_Text").GetValue<TMP_Dropdown>();
            TMP_InputField ifKey = t.Field("iF_Text").GetValue<TMP_InputField>();
            GameObject textSection = t.Field("m_GOTextInputSection").GetValue<GameObject>();
            Button apply = t.Field("button_Apply").GetValue<Button>();

            // Name the real provider. The localiser has to be removed first or
            // it restores "Default (Use Game Server)" on the next enable.
            if (ddText != null && ddText.options != null && ddText.options.Count > 0)
            {
                UiGraft.StripLocalizers(ddText.gameObject);

                string model = Plugin.CfgModel.Value;
                if (string.IsNullOrEmpty(model)) model = "not set";
                ddText.options[0].text = "MODDED: " + model;
                ddText.SetValueWithoutNotify(0);
                ddText.RefreshShownValue();
                if (ddText.captionText != null) ddText.captionText.text = "MODDED: " + model;
            }

            if (textSection != null) textSection.SetActive(true);
            if (ifKey == null) return;

            UiGraft.Dump(page.transform, "AI Setup page");

            // Row pitch derived from the field's own height so it tracks the
            // game's scaling rather than a hardcoded pixel guess.
            RectTransform keyRect = ifKey.GetComponent<RectTransform>();
            float rowH = keyRect != null ? keyRect.rect.height * 1.25f : 52f;
            if (rowH < 34f) rowH = 52f;

            // The stock key field sits directly under the provider dropdown,
            // leaving no room above it. Drop it one row so the URL field has
            // somewhere to go, then stack the model field beneath.
            if (keyRect != null && !UiGraft.ParentIsAutoLaidOut(ifKey.transform.parent))
            {
                if (!_movedKey)
                {
                    keyRect.anchoredPosition = keyRect.anchoredPosition + new Vector2(0f, -rowH * 0.35f);
                    _movedKey = true;
                }
            }

            ifKey.text = Plugin.CfgApiKey.Value;
            UiGraft.SetPlaceholder(ifKey, "API key");

            GameObject urlGo = UiGraft.Clone(ifKey.gameObject, UrlName, new Vector2(0f, rowH));
            GameObject modelGo = UiGraft.Clone(ifKey.gameObject, ModelName, new Vector2(0f, -rowH));

            // The stock "OpenAI API Key" caption now sits between our rows and
            // names the wrong thing, so retire it.
            HideStaleLabel(ifKey.transform.parent);

            // Give the voice half of the page room to breathe.
            TMP_Dropdown ddVoice = t.Field("dropdown_Voice").GetValue<TMP_Dropdown>();
            if (ddVoice != null && !_movedVoice
                && !UiGraft.ParentIsAutoLaidOut(ddVoice.transform.parent))
            {
                RectTransform vr = ddVoice.GetComponent<RectTransform>();
                if (vr != null) vr.anchoredPosition = vr.anchoredPosition + new Vector2(0f, -rowH * 0.9f);
                _movedVoice = true;
            }

            _key = ifKey;
            _url = urlGo != null ? urlGo.GetComponent<TMP_InputField>() : null;
            _model = modelGo != null ? modelGo.GetComponent<TMP_InputField>() : null;

            if (_url != null)
            {
                _url.onValueChanged.RemoveAllListeners();
                _url.onEndEdit.RemoveAllListeners();
                _url.text = Plugin.CfgBaseUrl.Value;
                UiGraft.SetPlaceholder(_url, "Base URL");
            }
            if (_model != null)
            {
                _model.onValueChanged.RemoveAllListeners();
                _model.onEndEdit.RemoveAllListeners();
                _model.text = Plugin.CfgModel.Value;
                UiGraft.SetPlaceholder(_model, "Model");
            }

            BuildTestButton(page, apply);
            BuildVoiceSection(page, apply);
            Plugin.Log.LogInfo("AI Setup page customised (url/key/model fields + Test).");
        }

        const string VoiceUrlName = "AI2UMod_VoiceUrl";
        const string VoiceModelName = "AI2UMod_VoiceModel";
        const string VoiceTestName = "AI2UMod_VoiceTest";

        static TMP_InputField _vUrl, _vKey, _vModel, _vVoice;
        static Button _vTest;
        static TMP_Text _vTestLabel;

        // The page already owns an "InputSection_Voice_APIKey" block, hidden
        // unless the stock Azure-key mode is picked. It sits directly under the
        // voice dropdown and its two fields are already positioned by the game,
        // so switching it on and repurposing them beats inventing coordinates.
        static void BuildVoiceSection(UIManager_APIKeyPage page, Button apply)
        {
            Traverse t = Traverse.Create(page);

            GameObject voiceSection = t.Field("m_GOVoiceInputSection").GetValue<GameObject>();
            TMP_InputField ifVoiceKey = t.Field("iF_Voice").GetValue<TMP_InputField>();
            TMP_InputField ifVoiceRegion = t.Field("iF_Voice_Region").GetValue<TMP_InputField>();
            if (voiceSection == null || ifVoiceKey == null) return;

            voiceSection.SetActive(true);


            // "Azure TTS API Key" names the wrong thing now and sits on top of
            // the URL row; "Region Code" labels what is really the voice name.
            Transform secT = voiceSection.transform;
            Transform azureLabel = secT.Find("API Key Text");
            if (azureLabel != null) azureLabel.gameObject.SetActive(false);

            Transform regionLabel = secT.Find("API Key Text_Region");
            if (regionLabel != null)
            {
                UiGraft.StripLocalizers(regionLabel.gameObject);
                TMP_Text rt = regionLabel.GetComponent<TMP_Text>();
                if (rt != null) rt.text = "Voice";
            }

            // Say what is actually speaking - and make the dropdown switch it,
            // rather than leaving a stock control that no longer does anything.
            TMP_Dropdown ddVoice2 = t.Field("dropdown_Voice").GetValue<TMP_Dropdown>();
            if (ddVoice2 != null)
            {
                UiGraft.StripLocalizers(ddVoice2.gameObject);
                ddVoice2.onValueChanged.RemoveAllListeners();
                ddVoice2.ClearOptions();
                ddVoice2.AddOptions(new List<string> {
                    "MODDED: local voice (free)",
                    "MODDED: " + Plugin.CfgGrokVoiceId.Value + " (cloud)"
                });
                ddVoice2.SetValueWithoutNotify(Plugin.CfgGrokEnabled.Value ? 1 : 0);
                ddVoice2.RefreshShownValue();

                ddVoice2.onValueChanged.AddListener(delegate(int v)
                {
                    Plugin.CfgGrokEnabled.Value = v == 1;
                    Plugin.SaveCfg();
                    Plugin.Log.LogInfo(v == 1
                        ? "AI Voice ON (cloud TTS billing active)."
                        : "AI Voice OFF (local voice, no TTS billing).");
                });
            }

            _vKey = ifVoiceKey;
            _vKey.onValueChanged.RemoveAllListeners();
            _vKey.onEndEdit.RemoveAllListeners();
            _vKey.text = Plugin.CfgGrokApiKey.Value;
            UiGraft.SetPlaceholder(_vKey, "Voice API key");

            // The stock second field is the Azure region; it becomes the voice name.
            if (ifVoiceRegion != null)
            {
                _vVoice = ifVoiceRegion;
                _vVoice.onValueChanged.RemoveAllListeners();
                _vVoice.onEndEdit.RemoveAllListeners();
                _vVoice.text = Plugin.CfgGrokVoiceId.Value;
                UiGraft.SetPlaceholder(_vVoice, "Voice (iris)");
            }

            RectTransform kr = _vKey.GetComponent<RectTransform>();
            float rowH = kr != null ? kr.rect.height * 1.25f : 52f;
            if (rowH < 34f) rowH = 52f;

            GameObject vUrlGo = UiGraft.Clone(_vKey.gameObject, VoiceUrlName, new Vector2(0f, rowH));
            GameObject vModelGo = UiGraft.Clone(_vKey.gameObject, VoiceModelName, new Vector2(0f, -rowH));

            _vUrl = vUrlGo != null ? vUrlGo.GetComponent<TMP_InputField>() : null;
            _vModel = vModelGo != null ? vModelGo.GetComponent<TMP_InputField>() : null;

            if (_vUrl != null)
            {
                _vUrl.onValueChanged.RemoveAllListeners();
                _vUrl.onEndEdit.RemoveAllListeners();
                _vUrl.text = Plugin.CfgGrokBaseUrl.Value;
                UiGraft.SetPlaceholder(_vUrl, "Voice API URL");
            }
            if (_vModel != null)
            {
                _vModel.onValueChanged.RemoveAllListeners();
                _vModel.onEndEdit.RemoveAllListeners();
                _vModel.text = Plugin.CfgTtsModel.Value;
                UiGraft.SetPlaceholder(_vModel, "Voice model (openai shape only)");
            }

            // Voice Test sits one row below the text Test button.
            if (apply != null)
            {
                float dx = 0f;
                RectTransform ar = apply.GetComponent<RectTransform>();
                if (ar != null) dx = -(ar.rect.width + 20f);
                if (dx > -60f) dx = -170f;

                GameObject go = UiGraft.Clone(apply.gameObject, VoiceTestName,
                                              new Vector2(dx, -(ar != null ? ar.rect.height + 14f : 70f)));
                if (go != null)
                {
                    _vTest = go.GetComponent<Button>();
                    _vTestLabel = go.GetComponentInChildren<TMP_Text>();
                    UiGraft.StripLocalizers(go);
                    if (_vTest != null)
                    {
                        _vTest.onClick.RemoveAllListeners();
                        _vTest.interactable = true;
                        _vTest.onClick.AddListener(delegate { page.StartCoroutine(RunVoiceTest(null)); });
                    }
                    SetVoiceLabel("Test Voice", Color.white);
                }
            }

            _vVoiceLabel = regionLabel;
            AlignVoiceRows(t.Field("dropdown_Text").GetValue<TMP_Dropdown>(), ddVoice2);

            Plugin.Log.LogInfo("AI Setup page: voice fields added (url/key/model/voice + Test).");
        }

        static Transform _vVoiceLabel;

        // Put the voice rows the same distance below the voice dropdown as the
        // text rows sit below the text dropdown, measured live rather than
        // hand-tuned. Two guessed constants in a row (70 then 150) came from
        // treating this as a number to nudge instead of a relationship to copy.
        //
        // Works in world space: for an overlay canvas that is screen pixels, so
        // the two sections stay consistent whatever the UI scale. Self-
        // correcting, so running it again is a no-op rather than a second shift.
        static void AlignVoiceRows(TMP_Dropdown textDd, TMP_Dropdown voiceDd)
        {
            if (textDd == null || voiceDd == null || _url == null || _vUrl == null) return;

            float gap = textDd.transform.position.y - _url.transform.position.y;
            if (gap <= 0f) return;

            float dy = (voiceDd.transform.position.y - gap) - _vUrl.transform.position.y;
            if (Mathf.Abs(dy) < 0.5f) return;

            ShiftWorld(_vUrl, dy);
            ShiftWorld(_vKey, dy);
            ShiftWorld(_vModel, dy);
            ShiftWorld(_vVoice, dy);
            if (_vVoiceLabel != null) ShiftWorld(_vVoiceLabel, dy);

            Plugin.Log.LogInfo("voice rows aligned to the text section's spacing (dy="
                + dy.ToString("F0") + ", gap=" + gap.ToString("F0") + ").");
        }

        static void ShiftWorld(Component c, float dy)
        {
            if (c == null) return;
            ShiftWorld(c.transform, dy);
        }

        static void ShiftWorld(Transform tr, float dy)
        {
            if (tr == null) return;
            tr.position = tr.position + new Vector3(0f, dy, 0f);
        }

        static void SetVoiceLabel(string text, Color c)
        {
            if (_vTestLabel == null) return;
            _vTestLabel.text = text;
            _vTestLabel.color = c;
        }

        static void ReportVoice(Action<string, Color> report, string text, Color c)
        {
            SetVoiceLabel(text, c);
            if (report != null) report(text, c);
        }

        // Synthesizes a short line and plays it, so the button proves the whole
        // path rather than just that the endpoint answered.
        //
        // Tests whichever provider would actually speak: the original-game-voices
        // feature when its toggle is on, the cloud provider otherwise. Testing
        // the cloud shape while the game voices are what will really run would
        // report a green "Works!" about a path she never takes.
        internal static IEnumerator RunVoiceTest(Action<string, Color> report)
        {
            if (_vTest != null) _vTest.interactable = false;
            ReportVoice(report, "Testing...", Color.white);
            SaveVoiceFields();

            bool gameVoice = Plugin.CfgGameVoice != null && Plugin.CfgGameVoice.Value;

            if (!gameVoice && string.IsNullOrEmpty(Plugin.CfgGrokApiKey.Value))
            {
                ReportVoice(report, "No key", Color.red);
                if (_vTest != null) _vTest.interactable = true;
                yield break;
            }

            AudioClip clip = null;
            IEnumerator call = gameVoice
                ? GameTts.Synthesize("Hey. This is how I will sound.",
                    delegate(AudioClip c) { clip = c; })
                : GrokTts.Synthesize("Hey. This is how I will sound.",
                    delegate(AudioClip c) { clip = c; });
            while (call.MoveNext()) yield return call.Current;

            if (clip == null)
            {
                ReportVoice(report, gameVoice ? GameTts.FailureLabel() : GrokTts.FailureLabel(),
                    Color.red);
                Plugin.Log.LogWarning("Voice test failed - see the lines above for the endpoint's reply.");
            }
            else
            {
                AudioSource src = UnityEngine.Object.FindObjectOfType<AudioSource>();
                if (src != null) src.PlayOneShot(clip);
                ReportVoice(report, gameVoice ? "Works! (game voice)" : "Works!", Color.green);
                Plugin.Log.LogInfo("Voice test succeeded (" + clip.length.ToString("0.0") + "s, "
                    + (gameVoice ? "game voices" : "cloud provider") + ").");
            }

            if (_vTest != null) _vTest.interactable = true;
        }

        public static void SaveVoiceFields()
        {
            try
            {
                if (_vUrl != null && !string.IsNullOrEmpty(_vUrl.text))
                    Plugin.CfgGrokBaseUrl.Value = _vUrl.text.Trim();
                if (_vKey != null)
                    Plugin.CfgGrokApiKey.Value = _vKey.text.Trim();
                if (_vModel != null && !string.IsNullOrEmpty(_vModel.text))
                    Plugin.CfgTtsModel.Value = _vModel.text.Trim();
                if (_vVoice != null && !string.IsNullOrEmpty(_vVoice.text))
                    Plugin.CfgGrokVoiceId.Value = _vVoice.text.Trim();

                // The game's Apply writes PlayerPrefs["LocalTTS"] from the voice
                // dropdown's index. Since that dropdown now means something else,
                // re-assert local synthesis or the NPC falls back to waiting for
                // server audio that a custom endpoint never sends.
                //
                // Only while the mod is actually on, though. This is a PERSISTED
                // pref, not mod state: Communicator.Awake reads it back on every
                // launch to pick the speech route. Writing it unconditionally left
                // the game aimed at local synthesis even with the mod switched off,
                // and on Steam that path is the stripped one - so a player who
                // turned the mod off got permanent silence and nothing in the mod's
                // own settings explained why.
                if (Plugin.CfgEnabled != null && Plugin.CfgEnabled.Value)
                    PlayerPrefs.SetInt("LocalTTS", 1);

                Plugin.SaveCfg();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not save voice fields: " + e.Message);
            }
        }

        static void BuildTestButton(UIManager_APIKeyPage page, Button apply)
        {
            if (apply == null) return;

            // Sit to the left of Apply so the two do not overlap.
            float dx = 0f;
            RectTransform ar = apply.GetComponent<RectTransform>();
            if (ar != null) dx = -(ar.rect.width + 20f);
            if (dx > -60f) dx = -170f;

            GameObject go = UiGraft.Clone(apply.gameObject, TestName, new Vector2(dx, 0f));
            if (go == null) return;

            _test = go.GetComponent<Button>();
            _testLabel = go.GetComponentInChildren<TMP_Text>();

            UiGraft.StripLocalizers(go);
            if (_test != null)
            {
                _test.onClick.RemoveAllListeners();
                _test.interactable = true;
                _test.onClick.AddListener(delegate { page.StartCoroutine(RunTest(null)); });
            }
            SetLabel("Test", Color.white);
        }

        static void SetLabel(string text, Color color)
        {
            if (_testLabel == null) return;
            _testLabel.text = text;
            _testLabel.color = color;
        }

        // The F9 panel runs this same coroutine, so progress has to reach two
        // places at once: the grafted button's own label, which is absent on the
        // Steam build, and whatever reporter the caller passed in.
        static void Report(Action<string, Color> report, string text, Color color)
        {
            SetLabel(text, color);
            if (report != null) report(text, color);
        }

        // Providers disagree about where chat/completions lives: some want the
        // base to already include /v1, some do not, and some hand out a URL
        // with the full path baked in. Rather than reject a URL that is merely
        // shaped differently, try the plausible forms and keep whichever works.
        public static List<string> ChatUrlCandidates(string baseUrl)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(baseUrl)) baseUrl = "https://openrouter.ai/api/v1";

            string b = baseUrl.Trim().TrimEnd('/');
            if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(b);
                return list;
            }

            list.Add(b + "/chat/completions");
            if (!b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                list.Add(b + "/v1/chat/completions");

            // A base ending in /api/v1 that 404s is often really /v1.
            if (b.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
                list.Add(b.Substring(0, b.Length - "/api/v1".Length) + "/v1/chat/completions");

            return list;
        }

        internal static IEnumerator RunTest(Action<string, Color> report)
        {
            if (_test != null) _test.interactable = false;
            Report(report, "Testing...", Color.white);
            SaveFields();

            List<string> candidates = ChatUrlCandidates(Plugin.CfgBaseUrl.Value);
            string lastLabel = "Failed";
            string lastDetail = "";
            bool success = false;

            for (int i = 0; i < candidates.Count && !success; i++)
            {
                string url = candidates[i];

                JArray msgs = new JArray();
                JObject m = new JObject();
                m["role"] = "user";
                m["content"] = "Reply with the single word: OK";
                msgs.Add(m);

                JObject root = new JObject();
                root["model"] = Plugin.CfgModel.Value;
                root["messages"] = msgs;
                root["max_tokens"] = 2000;
                Plugin.ApplyOpenRouterParams(root);

                UnityWebRequest req = new UnityWebRequest(url, "POST");
                req.uploadHandler = new UploadHandlerRaw(
                    Encoding.UTF8.GetBytes(root.ToString(Newtonsoft.Json.Formatting.None)));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(Plugin.CfgApiKey.Value))
                    req.SetRequestHeader("Authorization", "Bearer " + Plugin.CfgApiKey.Value);
                req.timeout = 30;

                yield return req.SendWebRequest();

                long code = req.responseCode;
                bool ok = req.result == UnityWebRequest.Result.Success;
                string raw = "";
                try { raw = req.downloadHandler.text; } catch { }
                req.Dispose();

                if (ok)
                {
                    string err = null;
                    try
                    {
                        JObject o = JObject.Parse(raw);
                        if (o["error"] != null) err = o["error"].ToString();
                        else if (o["choices"] == null) err = "unexpected reply shape";
                    }
                    catch (Exception e) { err = e.Message; }

                    if (err == null)
                    {
                        success = true;

                        // Remember the form that actually worked.
                        string keep = url;
                        if (keep.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                            keep = keep.Substring(0, keep.Length - "/chat/completions".Length);
                        if (keep != Plugin.CfgBaseUrl.Value)
                        {
                            Plugin.CfgBaseUrl.Value = keep;
                            Plugin.SaveCfg();
                            if (_url != null) _url.text = keep;
                            Plugin.Log.LogInfo("Base URL corrected to " + keep);
                        }
                        break;
                    }

                    lastLabel = "Bad model?";
                    lastDetail = err;
                }
                else
                {
                    lastLabel = Describe(code);
                    lastDetail = code + " " + Trim(raw, 300);
                }

                Plugin.Log.LogInfo("Test tried " + url + " -> " + lastLabel
                    + (string.IsNullOrEmpty(lastDetail) ? "" : " | " + Trim(lastDetail, 300)));
            }

            if (success)
            {
                Report(report, "Works!", Color.green);
                Plugin.Log.LogInfo("AI Setup test succeeded for " + Plugin.CfgModel.Value);
            }
            else
            {
                Report(report, lastLabel, Color.red);
                Plugin.Log.LogWarning("AI Setup test failed: " + Trim(lastDetail, 500));
            }

            if (_test != null) _test.interactable = true;
        }

        static string Describe(long code)
        {
            switch (code)
            {
                case 401: return "Bad key";
                case 402: return "No credit";
                case 403: return "Forbidden";
                case 404: return "Bad URL";
                case 429: return "Rate limited";
                case 0:   return "No connection";
            }
            return "Failed " + code;
        }

        public static void SaveFields()
        {
            try
            {
                if (_url != null && !string.IsNullOrEmpty(_url.text))
                    Plugin.CfgBaseUrl.Value = _url.text.Trim();
                if (_key != null)
                    Plugin.CfgApiKey.Value = _key.text.Trim();
                if (_model != null && !string.IsNullOrEmpty(_model.text))
                    Plugin.CfgModel.Value = _model.text.Trim();
                Plugin.SaveCfg();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not save AI Setup fields: " + e.Message);
            }
        }

        static string Trim(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }

    [HarmonyPatch(typeof(UIManager_APIKeyPage), "ButtonPressed_Apply")]
    public static class ModUiApplyPatch
    {
        static void Postfix() { ModUiPatch.SaveFields(); ModUiPatch.SaveVoiceFields(); }
    }

    // Adds a "Grok Voice" selector to the Audio page, directly under the
    // game's own "NPC Voice Model" dropdown. Switching it off stops xAI
    // billing immediately; she keeps talking with the free local voice.
    [HarmonyPatch(typeof(UIManager_Audio), "LoadSettings")]
    public static class GrokVoiceDropdownPatch
    {
        const string Name = "AI2UMod_GrokVoice";
        static TMP_Dropdown _dd;

        static void Postfix(UIManager_Audio __instance)
        {
            // With the mod off the Audio page has to look untouched, same as
            // every other patch. This one grafts a control into the game's own
            // menu, so leaving it behind would be a visible remnant of a mod
            // that is supposed to be inert.
            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value)
            {
                Remove(__instance);
                return;
            }

            try { Build(__instance); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not add the Grok Voice control: " + e);
            }
        }

        // The page is rebuilt on every open, but the graft can outlive a toggle
        // if the menu was already open when the mod was switched off.
        static void Remove(UIManager_Audio page)
        {
            try
            {
                _dd = null;
                if (page == null) return;

                foreach (Transform t in page.GetComponentsInChildren<Transform>(true))
                {
                    if (t != null && t.name == Name)
                    {
                        UnityEngine.Object.Destroy(t.gameObject);
                        Plugin.Log.LogInfo("Mod is off: removed the grafted Grok Voice control.");
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not remove the Grok Voice control: " + e.Message);
            }
        }

        static void Build(UIManager_Audio page)
        {
            TMP_Dropdown ddTts = Traverse.Create(page).Field("dd_tts").GetValue<TMP_Dropdown>();
            if (ddTts == null) return;

            float dy = 0f;
            RectTransform r = ddTts.GetComponent<RectTransform>();
            if (r != null) dy = -(r.rect.height + 18f);
            if (dy > -20f) dy = -52f;

            GameObject go = UiGraft.Clone(ddTts.gameObject, Name, new Vector2(0f, dy));
            if (go == null) return;

            _dd = go.GetComponent<TMP_Dropdown>();
            if (_dd == null) return;

            // Without this, I2 restores the TTS option list on the next enable.
            UiGraft.StripLocalizers(go);

            _dd.onValueChanged.RemoveAllListeners();
            _dd.ClearOptions();
            _dd.AddOptions(new List<string> {
                "AI Voice: OFF (local, free)",
                "AI Voice: ON (" + Plugin.CfgGrokVoiceId.Value + ")"
            });
            _dd.SetValueWithoutNotify(Plugin.CfgGrokEnabled.Value ? 1 : 0);
            _dd.RefreshShownValue();

            // Same path as the F8 hotkey, so saving, logging and the toast behave
            // identically no matter which one the player used.
            _dd.onValueChanged.AddListener(delegate(int v)
            {
                Plugin.SetVoice(v == 1);
            });

            Plugin.Log.LogInfo("Audio page: Grok Voice control added.");
        }

        // Pushes the current config value into the live dropdown. Called after a
        // hotkey toggle so an open pause menu updates immediately; a no-op when
        // the Audio page has not been built yet.
        internal static void Sync()
        {
            try
            {
                if (_dd == null) return;

                _dd.SetValueWithoutNotify(Plugin.CfgGrokEnabled.Value ? 1 : 0);
                _dd.RefreshShownValue();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not refresh the AI Voice dropdown: " + e.Message);
            }
        }
    }
}
