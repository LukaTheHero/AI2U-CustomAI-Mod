// A self-contained in-game settings panel, opened with F9.
//
// Why this exists at all: the mod's original settings UI was grafted onto the
// game's own AI Setup page (ModUI.cs). That page is unreachable on the Steam
// build - the scene simply has no tab button for it, and nothing in code reads
// settingTabAPIKey, so there is nothing to switch on. Two attempts at grafting a
// tab into that scene missed, and even a working graft would have been a second,
// build-specific mod to maintain.
//
// Drawing our own IMGUI panel sidesteps the scene entirely, so one plugin now
// serves both builds and every setting is editable mid-game without a restart.
//
// Two details that are not obvious and that this file depends on:
//
//   Painting order. The game's HUD and dialogue live on Screen Space - Overlay
//   canvases, which render after IMGUI, so anything we draw underneath them is
//   invisible. Rather than fight it, the overlay hides those canvases while it
//   is open and restores them on close.
//
//   Input. InputManager's Is*Enabled flags do not gate movement or the camera -
//   GetPlayerMovement and GetMouseDelta read the Input System actions directly.
//   The only real gate is PlayerInput.Disable(), which stops the actions but
//   leaves IMGUI keyboard events untouched. That split is what lets us type an
//   API key without walking the character across the room.
using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json.Linq;

namespace AI2UCustomAI
{
    internal static class OverlayMenu
    {
        const int WindowId = 0x412055;

        public static bool IsOpen { get; private set; }

        static Rect _win = new Rect(0f, 0f, 1176f, 924f);
        static Vector2 _scroll;
        static bool _isResizing = false;
        static Vector2 _resizeStartMouse;
        static Vector2 _resizeStartSize;

        // Whether the Canalpa sub-settings are revealed. Deliberately NOT
        // persisted: it resets to hidden every session, so the spoiler consent is
        // per-sitting rather than a box someone ticked months ago.
        static bool _canalpaSpoilers;

        // The left tab rail. Tabs replaced both the single long scroll and the
        // old Advanced fold: only the active tab's rows are laid out at all,
        // which is also a real part of the lag fix - IMGUI lays out every
        // visible control on every event, so a fifth of the panel per frame
        // costs a fifth as much.
        static int _tab;
        static readonly string[] TabNames =
        {
            "Setup", "Voice", "Model", "She knows", "Extra content", "Dev cheats"
        };

        // Panel-side snapshots of anything whose read walks the scene or scans
        // assemblies. OnGUI runs several times per frame; these values change at
        // conversation speed. 4Hz is indistinguishable from live and removes the
        // whole per-draw cost - the same fix the cheats readouts got, applied to
        // the status strip and the Canalpa readiness rows.
        static float _stAt = -99f;
        static float? _stTrust;
        static string _stIndicator;
        static List<string> _cnRows = new List<string>();
        static bool _cnConsentPending, _cnConsentReady;
        static int _cnLevel = -1;

        static void RefreshStatusSnapshots()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _stAt < 0.25f) return;
            _stAt = now;

            _stTrust = null;
            _stIndicator = null;
            try
            {
                object beh = Murder.BehaviourObject();
                if (beh != null)
                {
                    object v = Traverse.Create(beh).Field("trustLevel").GetValue();
                    if (v is float) _stTrust = (float)v;
                    _stIndicator = Traverse.Create(beh).Field("trustLevelIndicator").GetValue() as string;
                }
            }
            catch (Exception) { }

#if CANALPA
            try
            {
                _cnRows = Canalpa.Status();
                _cnConsentPending = Consent.Pending;
                _cnConsentReady = Consent.ReadyForConfirmation;
                _cnLevel = Canalpa.CurrentLevel;
            }
            catch (Exception) { }
#endif
        }
        static bool _placed;
        static string _status;
        static float _statusUntil;
        static bool _showKeys;

        // Test results persist until the next run rather than expiring like
        // _status does: a request can take longer than the status timeout, and a
        // red result is the one thing worth leaving on screen while you edit.
        static string _resText, _resVoice;
        static Color _resTextColor = Color.white, _resVoiceColor = Color.white;
        static bool _busyText, _busyVoice;

        static List<string> _modelList = new List<string>();
        static bool _fetchingModels = false;
        static string _fetchModelError = null;
        static bool _modelDropdownOpen = false;
        static string _modelSearchQuery = "";
        static Vector2 _modelScroll = Vector2.zero;

        static List<string> _providerList = new List<string>();
        static bool _fetchingProviders = false;
        static string _fetchProviderError = null;
        static string _lastFetchedProviderModel = null;

        static bool IsOpenRouterBaseUrl()
        {
            string url = Get("BaseUrl");
            if (string.IsNullOrEmpty(url) && Plugin.CfgBaseUrl != null)
                url = Plugin.CfgBaseUrl.Value;
            return url != null && url.IndexOf("openrouter", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void FetchModelsAsync()
        {
            if (_fetchingModels) return;
            _fetchingModels = true;
            _fetchModelError = null;

            string baseUrl = Plugin.CfgBaseUrl != null ? Plugin.CfgBaseUrl.Value : "";
            string apiKey = Plugin.CfgApiKey != null ? Plugin.CfgApiKey.Value : "";

            if (_buf.ContainsKey("BaseUrl") && !string.IsNullOrEmpty(_buf["BaseUrl"]))
                baseUrl = _buf["BaseUrl"];
            if (_buf.ContainsKey("ApiKey") && !string.IsNullOrEmpty(_buf["ApiKey"]))
                apiKey = _buf["ApiKey"];

            if (string.IsNullOrEmpty(baseUrl))
            {
                _fetchingModels = false;
                _fetchModelError = "Please enter Base URL first";
                return;
            }

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> found = new List<string>();
                string err = null;

                try
                {
                    try
                    {
                        System.Net.ServicePointManager.SecurityProtocol |=
                            System.Net.SecurityProtocolType.Tls12 |
                            (System.Net.SecurityProtocolType)3072 |
                            (System.Net.SecurityProtocolType)12288;
                    }
                    catch (Exception) { }

                    string clean = baseUrl.Trim().TrimEnd('/');
                    if (clean.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                        clean = clean.Substring(0, clean.Length - "/chat/completions".Length);

                    List<string> candidates = new List<string>();
                    if (clean.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) || clean.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(clean + "/models");
                    }
                    else
                    {
                        candidates.Add(clean + "/v1/models");
                        candidates.Add(clean + "/api/v1/models");
                        candidates.Add(clean + "/models");
                        candidates.Add(clean + "/api/tags");
                    }

                    foreach (string url in candidates)
                    {
                        try
                        {
                            System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                            req.Method = "GET";
                            req.Timeout = 8000;
                            req.ReadWriteTimeout = 8000;
                            req.UserAgent = "AI2U-CustomAI/5.4";
                            if (!string.IsNullOrEmpty(apiKey))
                                req.Headers["Authorization"] = "Bearer " + apiKey.Trim();
                            req.Headers["HTTP-Referer"] = "https://github.com/ai2u-custom-ai";
                            req.Headers["X-Title"] = "AI2U Custom AI";

                            using (System.Net.HttpWebResponse resp = (System.Net.HttpWebResponse)req.GetResponse())
                            using (System.IO.Stream s = resp.GetResponseStream())
                            using (System.IO.StreamReader r = new System.IO.StreamReader(s, System.Text.Encoding.UTF8))
                            {
                                string json = r.ReadToEnd();
                                if (!string.IsNullOrEmpty(json))
                                {
                                    JToken root = JToken.Parse(json);
                                    if (root is JObject)
                                    {
                                        JObject obj = (JObject)root;
                                        JArray data = obj["data"] as JArray;
                                        if (data != null)
                                        {
                                            foreach (JToken item in data)
                                            {
                                                string id = item["id"] != null ? item["id"].ToString() : null;
                                                if (!string.IsNullOrEmpty(id) && !found.Contains(id))
                                                    found.Add(id);
                                            }
                                        }

                                        JArray modelsArr = obj["models"] as JArray;
                                        if (modelsArr != null)
                                        {
                                            foreach (JToken item in modelsArr)
                                            {
                                                string id = item["name"] != null ? item["name"].ToString() : (item["model"] != null ? item["model"].ToString() : null);
                                                if (!string.IsNullOrEmpty(id) && !found.Contains(id))
                                                    found.Add(id);
                                            }
                                        }
                                    }
                                    else if (root is JArray)
                                    {
                                        JArray arr = (JArray)root;
                                        foreach (JToken item in arr)
                                        {
                                            string id = item["id"] != null ? item["id"].ToString() : (item["name"] != null ? item["name"].ToString() : null);
                                            if (!string.IsNullOrEmpty(id) && !found.Contains(id))
                                                found.Add(id);
                                        }
                                    }

                                    if (found.Count > 0)
                                    {
                                        found.Sort(StringComparer.OrdinalIgnoreCase);
                                        err = null;
                                        break;
                                    }
                                }
                            }
                        }
                        catch (System.Net.WebException we)
                        {
                            string detail = we.Message;
                            if (we.Response is System.Net.HttpWebResponse)
                            {
                                System.Net.HttpWebResponse wr = (System.Net.HttpWebResponse)we.Response;
                                detail = "HTTP " + (int)wr.StatusCode + " " + wr.StatusDescription;
                            }
                            err = detail;
                        }
                        catch (Exception ex)
                        {
                            err = ex.Message;
                        }
                    }
                }
                catch (Exception e)
                {
                    err = e.Message;
                }
                finally
                {
                    _fetchingModels = false;
                    if (found.Count > 0)
                    {
                        _modelList = found;
                        _fetchModelError = null;
                    }
                    else
                    {
                        _fetchModelError = err ?? "No models returned from endpoint";
                    }
                }
            });
        }

        public static void SetBaseUrlQuick(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            _buf["BaseUrl"] = url;
            if (Plugin.CfgBaseUrl != null)
            {
                Plugin.CfgBaseUrl.Value = url;
                Plugin.SaveCfg();
            }
            Note("Base URL set to: " + url);
        }

        public static void FetchProvidersAsync()
        {
            if (_fetchingProviders) return;
            _fetchingProviders = true;
            _fetchProviderError = null;

            string curModel = Get("Model");
            if (string.IsNullOrEmpty(curModel) && Plugin.CfgModel != null)
                curModel = Plugin.CfgModel.Value;
            curModel = (curModel ?? "").Trim();

            string apiKey = Plugin.CfgApiKey != null ? Plugin.CfgApiKey.Value : "";
            if (_buf.ContainsKey("ApiKey") && !string.IsNullOrEmpty(_buf["ApiKey"]))
                apiKey = _buf["ApiKey"];

            if (string.IsNullOrEmpty(curModel))
            {
                _fetchingProviders = false;
                _fetchProviderError = "Please specify a Model first";
                return;
            }

            _lastFetchedProviderModel = curModel;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> found = new List<string>();
                string err = null;

                try
                {
                    try
                    {
                        System.Net.ServicePointManager.SecurityProtocol |=
                            System.Net.SecurityProtocolType.Tls12 |
                            (System.Net.SecurityProtocolType)3072 |
                            (System.Net.SecurityProtocolType)12288;
                    }
                    catch (Exception) { }

                    string url = "https://openrouter.ai/api/v1/models/" + curModel + "/endpoints";
                    System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                    req.Method = "GET";
                    req.Timeout = 8000;
                    req.ReadWriteTimeout = 8000;
                    req.UserAgent = "AI2U-CustomAI/5.4";
                    if (!string.IsNullOrEmpty(apiKey))
                        req.Headers["Authorization"] = "Bearer " + apiKey.Trim();
                    req.Headers["HTTP-Referer"] = "https://github.com/ai2u-custom-ai";
                    req.Headers["X-Title"] = "AI2U Custom AI";

                    using (System.Net.HttpWebResponse resp = (System.Net.HttpWebResponse)req.GetResponse())
                    using (System.IO.Stream s = resp.GetResponseStream())
                    using (System.IO.StreamReader r = new System.IO.StreamReader(s, System.Text.Encoding.UTF8))
                    {
                        string json = r.ReadToEnd();
                        if (!string.IsNullOrEmpty(json))
                        {
                            JToken root = JToken.Parse(json);
                            JToken data = root["data"];
                            JArray endpoints = null;
                            if (data is JObject && ((JObject)data)["endpoints"] is JArray)
                                endpoints = (JArray)((JObject)data)["endpoints"];
                            else if (data is JArray)
                                endpoints = (JArray)data;

                            if (endpoints != null)
                            {
                                foreach (JToken ep in endpoints)
                                {
                                    string pName = ep["provider_name"] != null ? ep["provider_name"].ToString() : null;
                                    if (string.IsNullOrEmpty(pName) && ep["tag"] != null)
                                        pName = ep["tag"].ToString();
                                    if (!string.IsNullOrEmpty(pName) && !found.Contains(pName))
                                        found.Add(pName);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }
                finally
                {
                    _fetchingProviders = false;
                    if (found.Count > 0)
                    {
                        _providerList = found;
                        _fetchProviderError = null;
                    }
                    else
                    {
                        _fetchProviderError = err != null ? err : "No specific endpoints returned for " + curModel;
                    }
                }
            });
        }

        // Numeric and text values are edited as strings so a half-typed number
        // never has to parse. They are committed to config on Save.
        static readonly Dictionary<string, string> _buf = new Dictionary<string, string>();

        static Texture2D _panelBg, _barBg;
        static Texture2D _onBg, _offBg, _onEdge, _offEdge;

        // Saved host state, restored exactly as found on close.
        static CursorLockMode _prevLock;
        static bool _prevCursorVisible;
        static readonly List<Canvas> _hiddenCanvases = new List<Canvas>();
        static bool _inputWasGated;
        static bool[] _prevInputFlags;
        static bool _prevEsc;

        public static void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        public static void Open()
        {
            if (IsOpen) return;
            IsOpen = true;

            float targetW = Mathf.Min(1176f, Screen.width - 40f);
            float targetH = Mathf.Min(924f, Screen.height - 40f);
            _win.width = targetW;
            _win.height = targetH;
            _win.x = Mathf.Max(0f, (Screen.width - _win.width) * 0.5f);
            _win.y = Mathf.Max(0f, (Screen.height - _win.height) * 0.5f);

            LoadBuffers();
            _status = null;

            try
            {
                _prevLock = Cursor.lockState;
                _prevCursorVisible = Cursor.visible;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            catch (Exception) { }

            GateInput(true);
            HideGameCanvases(true);
        }

        public static void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;

            HideGameCanvases(false);
            GateInput(false);

            try
            {
                Cursor.lockState = _prevLock;
                Cursor.visible = _prevCursorVisible;
            }
            catch (Exception) { }
        }

        // Stops the character moving and the camera turning while the panel has
        // focus. The seven Is*Enabled flags are captured and put back verbatim,
        // because the game disables subsets of them during cutscenes and a blunt
        // SetInputEnabled(true) on close would hand control back mid-scene.
        static void GateInput(bool gate)
        {
            try
            {
                InputManager im = InputManager.Instance;
                if (im == null) return;

                Traverse pi = Traverse.Create(im).Field("playerInput");
                object actions = pi != null ? pi.GetValue() : null;

                if (gate)
                {
                    _prevInputFlags = new[]
                    {
                        im.IsInventoryEnabled, im.IsMissionEnabled, im.IsEnterTypeEnabled,
                        im.IsVoiceEnabled, im.IsInteractEnabled, im.IsQuickInventoryEnabled,
                        im.IsChatHistoryEnabled
                    };
                    _prevEsc = im.IsESCEnabled;

                    im.SetInputEnabled(false, false, false, false, false, false, false);
                    im.SetESCEnabled(false);
                    Invoke(actions, "Disable");
                    _inputWasGated = true;
                }
                else if (_inputWasGated)
                {
                    Invoke(actions, "Enable");
                    if (_prevInputFlags != null && _prevInputFlags.Length == 7)
                        im.SetInputEnabled(_prevInputFlags[0], _prevInputFlags[1], _prevInputFlags[2],
                            _prevInputFlags[3], _prevInputFlags[4], _prevInputFlags[5], _prevInputFlags[6]);
                    im.SetESCEnabled(_prevEsc);
                    _inputWasGated = false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Overlay: could not " + (gate ? "block" : "restore")
                    + " game input: " + e.Message);
            }
        }

        static void Invoke(object target, string method)
        {
            if (target == null) return;
            try
            {
                System.Reflection.MethodInfo m = target.GetType().GetMethod(method, Type.EmptyTypes);
                if (m != null) m.Invoke(target, null);
            }
            catch (Exception) { }
        }

        // Screen Space - Overlay canvases render after IMGUI, so the panel would
        // otherwise sit behind the HUD. Only canvases this method switched off
        // are switched back on, so anything the game changes meanwhile is left
        // as the game left it.
        static void HideGameCanvases(bool hide)
        {
            try
            {
                if (hide)
                {
                    _hiddenCanvases.Clear();
                    foreach (Canvas c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                    {
                        if (c == null || !c.enabled) continue;
                        if (c.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                        c.enabled = false;
                        _hiddenCanvases.Add(c);
                    }
                }
                else
                {
                    for (int i = 0; i < _hiddenCanvases.Count; i++)
                        if (_hiddenCanvases[i] != null) _hiddenCanvases[i].enabled = true;
                    _hiddenCanvases.Clear();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Overlay: canvas visibility juggling failed: " + e.Message);
            }
        }

        static Texture2D Solid(Color c)
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }
        // Called from HotkeyWatcher.OnGUI.
        public static void Draw()
        {
            if (!IsOpen) return;

            if (_panelBg == null) _panelBg = Solid(new Color(0.07f, 0.08f, 0.10f, 0.98f));
            if (_barBg == null) _barBg = Solid(new Color(0f, 0f, 0f, 0.72f));
            if (_onBg == null) _onBg = Solid(new Color(0.09f, 0.20f, 0.12f, 1f));
            if (_offBg == null) _offBg = Solid(new Color(0.24f, 0.09f, 0.09f, 1f));
            if (_onEdge == null) _onEdge = Solid(new Color(0.35f, 0.85f, 0.45f, 1f));
            if (_offEdge == null) _offEdge = Solid(new Color(0.95f, 0.35f, 0.32f, 1f));

            if (!_placed)
            {
                _win.x = Mathf.Max(0f, (Screen.width - _win.width) * 0.5f);
                _win.y = Mathf.Max(0f, (Screen.height - _win.height) * 0.5f);
                _placed = true;
            }

            if (Event.current != null && Event.current.type == EventType.KeyDown
                && Event.current.keyCode == KeyCode.Escape)
            {
                Close();
                Event.current.Use();
                return;
            }

            GUI.depth = -1000;
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _barBg);
            _win = GUI.Window(WindowId, _win, DrawWindow, GUIContent.none);
        }

        // The master switch, drawn as a full-width banner rather than a checkbox.
        //
        // It is the one control whose state changes what the whole rest of the
        // panel means, and the one a player reaches for when something looks
        // wrong, so it should not be a tickbox that reads like the six below it.
        // Green when on, red when off, with the current state spelled out in
        // words - a colour alone is no use to anyone who cannot separate the two.
        static void DrawMasterSwitch()
        {
            bool on = Plugin.CfgEnabled.Value;

            Rect r = GUILayoutUtility.GetRect(_win.width - 28f, 58f);

            GUI.DrawTexture(r, on ? _onBg : _offBg);
            GUI.DrawTexture(new Rect(r.x, r.y, 5f, r.height), on ? _onEdge : _offEdge);

            GUIStyle big = new GUIStyle(GUI.skin.label);
            big.fontSize = 17;
            big.fontStyle = FontStyle.Bold;
            big.normal.textColor = on ? new Color(0.72f, 0.98f, 0.76f) : new Color(1f, 0.80f, 0.78f);

            GUIStyle small = new GUIStyle(GUI.skin.label);
            small.fontSize = 11;
            small.wordWrap = true;
            small.normal.textColor = new Color(0.80f, 0.84f, 0.88f);

            GUI.Label(new Rect(r.x + 16f, r.y + 7f, r.width - 150f, 22f),
                on ? "MOD IS ON" : "MOD IS OFF", big);
            GUI.Label(new Rect(r.x + 16f, r.y + 29f, r.width - 150f, 24f),
                on
                    ? "Dialogue goes to your endpoint. The game's own AI servers are blocked."
                    : "The game is running completely stock. Nothing is patched or grafted.",
                small);

            // Instant, not deferred: the master switch is the one control that is
            // useless if it needs a Save press to take effect.
            if (GUI.Button(new Rect(r.xMax - 122f, r.y + 14f, 106f, 30f), on ? "Turn OFF" : "Turn ON"))
            {
                Plugin.CfgEnabled.Value = !on;
                Plugin.SaveCfg();

                // Switching off has to give back the speech route as well as stop
                // patching. Communicator.isLocalSpeak is a static the game reads
                // once in Awake, so an override the mod applied earlier outlives
                // every patch that checks CfgEnabled: the patches stand down and
                // the game stays aimed wherever the mod last pointed it. On Steam
                // that is the stripped Overtone path, which is silence - so the
                // master switch looked broken when the real fault was state the
                // mod never returned.
                if (on) VoicePatch.Restore();

                Note(!on
                    ? "Mod on. Replies come from your endpoint; the game's AI calls are blocked."
                    : "Mod off. The game is back to stock behaviour, no restart needed.");
            }
        }

        static void DrawWindow(int id)
        {
            // The default skin's window and box styles have no background texture
            // in a built player, so the panel paints its own.
            GUI.DrawTexture(new Rect(0f, 0f, _win.width, _win.height), _panelBg);

            GUILayout.BeginArea(new Rect(14f, 12f, _win.width - 28f, _win.height - 24f));

            GUILayout.BeginHorizontal();
            GUIStyle title = new GUIStyle(GUI.skin.label);
            title.fontSize = 16;
            title.fontStyle = FontStyle.Bold;
            GUILayout.Label("AI2U Custom AI", title);
            GUILayout.Space(8f);

            // Named here so a screenshot in a bug report identifies the build
            // without anyone having to ask which store it came from.
            GUIStyle sub = new GUIStyle(GUI.skin.label);
            sub.fontSize = 11;
            sub.normal.textColor = new Color(0.62f, 0.66f, 0.72f);
            GUILayout.Label(Platform.Store + " build", sub);

            GUILayout.FlexibleSpace();

            GUILayout.BeginVertical();
            // Game version
            GUIStyle gameVersion = new GUIStyle(GUI.skin.label);
            gameVersion.fontSize = 10;
            gameVersion.normal.textColor = new Color(0.50f, 0.54f, 0.60f);
            gameVersion.alignment = TextAnchor.UpperRight;
            GUILayout.Label("Game " + Application.version, gameVersion);

            // Mod version and update check.
            //
            // Three states, not two. "Could not check" used to fall through to the
            // green up-to-date branch, because a failed fetch leaves LatestVersion
            // null and null was read as "nothing newer exists". The URL 404'd on
            // every launch, so the panel confidently reassured everyone while
            // knowing nothing. Grey and honest beats green and wrong.
            if (Plugin.VersionCheckDone)
            {
                bool outdated = Plugin.LatestVersion != null;
                bool failed = Plugin.VersionCheckFailed;

                GUIStyle versionStyle = new GUIStyle(GUI.skin.label);
                versionStyle.fontSize = 11;
                versionStyle.alignment = TextAnchor.UpperRight;
                versionStyle.fontStyle = outdated ? FontStyle.Bold : FontStyle.Normal;
                versionStyle.normal.textColor =
                    outdated ? new Color(1f, 0.4f, 0.4f)          // red: act on this
                  : failed  ? new Color(0.62f, 0.66f, 0.72f)     // grey: no claim made
                            : new Color(0.4f, 0.9f, 0.4f);       // green: verified current

                string versionText =
                    outdated ? "Mod v" + Plugin.VERSION + " (update: " + Plugin.LatestVersion + ")"
                  : failed  ? "Mod v" + Plugin.VERSION + " (update check failed)"
                            : "Mod v" + Plugin.VERSION + " (up to date)";
                GUILayout.Label(versionText, versionStyle);
            }
            else
            {
                GUIStyle checkingStyle = new GUIStyle(sub);
                checkingStyle.alignment = TextAnchor.UpperRight;
                GUILayout.Label("Mod v" + Plugin.VERSION, checkingStyle);
            }
            GUILayout.EndVertical();

            GUILayout.Space(8f);
            GUILayout.Label("F9 or Esc to close");
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);

            DrawMasterSwitch();

            // Hiding the strip also stops the polling behind it. There is no reason
            // to walk the behaviour object graph four times a second for numbers that
            // are not on screen.
            if (Plugin.CfgShowStatusStrip == null || Plugin.CfgShowStatusStrip.Value)
            {
                RefreshStatusSnapshots();
                DrawStatusStrip();
            }

            GUILayout.Space(6f);

            // ---- tab rail + page --------------------------------------------
            //
            // Tabs replaced one long scroll and the old Advanced fold. Beyond
            // being findable, this is half the lag fix: IMGUI lays out every
            // visible control on every event, several times a frame, so drawing
            // a fifth of the panel costs a fifth as much.
            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical(GUILayout.Width(132f));
            for (int i = 0; i < TabNames.Length; i++)
            {
                bool active = _tab == i;
                bool extra = TabNames[i] == "Extra content";
                bool cheats = TabNames[i] == "Dev cheats";

                GUIStyle ts = new GUIStyle(GUI.skin.button);
                ts.alignment = TextAnchor.MiddleLeft;
                ts.fontSize = 13;
                ts.padding = new RectOffset(10, 6, 8, 8);
                ts.fontStyle = active ? FontStyle.Bold : FontStyle.Normal;

                Color c = extra ? DangerRed
                        : cheats ? CheatBlue
                        : active ? Color.white
                        : new Color(0.74f, 0.78f, 0.84f);
                if (!active) c.a = 0.78f;
                ts.normal.textColor = c;
                ts.hover.textColor = c;
                ts.active.textColor = c;
                ts.focused.textColor = c;
                ts.onNormal.textColor = c;

                string label = (active ? "▸ " : "   ") + TabNames[i];
                if (extra && Difficulty.Tier() == 3) label += "  " + Skull();

                if (GUILayout.Button(label, ts, GUILayout.Height(34f)))
                {
                    _tab = i;
                    _scroll = Vector2.zero;
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();

            GUILayout.Space(12f);

            GUILayout.BeginVertical();
            // GUIStyle.none for the HORIZONTAL scrollbar, and that is the whole fix
            // for text running off the right edge.
            //
            // A wrapping label inside a scroll view does not wrap by default: with a
            // horizontal scrollbar available, GUILayout gives the label its full
            // preferred width - one enormous single line - the content rect grows to
            // match, and word wrap never triggers. The result is exactly what the
            // screenshot showed: sentences cut off mid-word at the panel edge with a
            // horizontal scrollbar underneath.
            //
            // Passing GUIStyle.none removes horizontal scrollbar so text wraps naturally
            _scroll = GUILayout.BeginScrollView(_scroll, GUIStyle.none, GUI.skin.verticalScrollbar);

            DrawTabIntro();

            // ================= SETUP =========================================
            if (_tab == 0)
            {
                Header("Profiles");
                GUILayout.BeginHorizontal();
                Label("Active Profile");
                for (int p = 1; p <= 3; p++)
                {
                    bool isActive = (ProfileManager.CurrentProfile == p);
                    GUIStyle pBtnStyle = new GUIStyle(GUI.skin.button);
                    pBtnStyle.fontSize = 12;
                    if (isActive)
                    {
                        pBtnStyle.fontStyle = FontStyle.Bold;
                        pBtnStyle.normal.textColor = Color.green;
                    }
                    string pLabel = (isActive ? "● Profile " : "○ Profile ") + p;
                    if (GUILayout.Button(pLabel, pBtnStyle, GUILayout.Width(110f), GUILayout.Height(24f)))
                    {
                        if (ProfileManager.CurrentProfile != p)
                        {
                            ProfileManager.SwitchToProfile(p);
                        }
                    }
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                PlainNote("  Profiles remember your entire configuration (endpoints, models, prompts, voices, and settings) so you can switch setups in one click.");

                Header("Endpoint");
                GUILayout.Label("  Three boxes decide everything: where to send her lines, what key opens "
                    + "that door, and which model answers. Fill them in, press Test text, and she is "
                    + "yours. Everything on the other tabs already has a sane default.", sub);

                // Quick preset buttons for popular API providers
                GUILayout.BeginHorizontal();
                Label("Easy URL");
                GUIStyle easyBtnStyle = new GUIStyle(GUI.skin.button);
                easyBtnStyle.fontSize = 11;
                if (GUILayout.Button("OpenRouter", easyBtnStyle, GUILayout.Height(22f)))
                {
                    SetBaseUrlQuick("https://openrouter.ai/api/v1");
                }
                if (GUILayout.Button("OpenAI", easyBtnStyle, GUILayout.Height(22f)))
                {
                    SetBaseUrlQuick("https://api.openai.com/v1");
                }
                if (GUILayout.Button("DeepSeek", easyBtnStyle, GUILayout.Height(22f)))
                {
                    SetBaseUrlQuick("https://api.deepseek.com/v1");
                }
                if (GUILayout.Button("Groq", easyBtnStyle, GUILayout.Height(22f)))
                {
                    SetBaseUrlQuick("https://api.groq.com/openai/v1");
                }
                if (GUILayout.Button("Astropond", easyBtnStyle, GUILayout.Height(22f)))
                {
                    SetBaseUrlQuick("https://astropond.com/v1");
                }
                if (GUILayout.Button("Ollama", easyBtnStyle, GUILayout.Height(22f)))
                {
                    SetBaseUrlQuick("http://localhost:11434/v1");
                }
                if (GUILayout.Button("LM Studio", easyBtnStyle, GUILayout.Height(22f)))
                {
                    SetBaseUrlQuick("http://localhost:1234/v1");
                }
                if (GUILayout.Button("LinkAPI", easyBtnStyle, GUILayout.Height(22f)))
                {
                    SetBaseUrlQuick("https://api.linkapi.ai/v1");
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                TextRow("BaseUrl", "Base URL", 0f);
                KeyRow("ApiKey", "API key");
                ModelRowWithDropdown();
                OpenRouterProviderRow();

                Header("Test");
                GUILayout.Label("  Both tests save first, so they check exactly what is in the boxes above. "
                    + "The text test asks your endpoint for one word and fixes the base URL if a nearby "
                    + "form is the one that answers. The voice test synthesises a line and plays it.", sub);
                GUILayout.BeginHorizontal();
                bool hitText = GUILayout.Button(_busyText ? "Testing..." : "Test text", GUILayout.Height(26f));
                bool hitVoice = GUILayout.Button(_busyVoice ? "Testing..." : "Test voice", GUILayout.Height(26f));
                GUILayout.EndHorizontal();
                if (hitText && !_busyText) StartTest(false);
                if (hitVoice && !_busyVoice) StartTest(true);
                Result("Text", _resText, _resTextColor);
                Result("Voice", _resVoice, _resVoiceColor);

                DifficultySection();

                Header("Game server");
                Bool(Plugin.CfgBlockGameAi,
                    "Block the game's dialogue AI calls while the mod is on (play, fetchAsync)");
                Bool(Plugin.CfgOwnExtras,
                    "Do summary / envision / memorize here too");
                GUILayout.Label("  Both are AI2U's own paid LLM calls. The first is fully replaced by this mod, "
                    + "so blocking it costs you nothing and costs the developers nothing. The second covers "
                    + "the ending recap, her reaction to a painting or the TV, and the short memory of what "
                    + "you last talked about - with the box ticked those run on your endpoint in your model's "
                    + "voice, and untick it to hand them back to AI2U's servers. Login, saves, the shop and "
                    + "metrics are never touched.", sub);
                GUILayout.Label("  Tradeoff while it is on: the ending gems and reward icons come from AI2U's "
                    + "reply to the summary call, so the ending screen shows the written recap without them.", sub);

                Header("Debug");
                Bool(Plugin.CfgLogPayloads, "Log request and reply payloads to the BepInEx console");
            }

            // ================= VOICE =========================================
            if (_tab == 1)
            {
                Header("Voice Mode");
                GUILayout.Label("  Click a mode below to select your text-to-speech engine:", sub);

                string curVoiceMode = Plugin.CfgVoiceChoice != null ? Plugin.CfgVoiceChoice.Value : "local";
                bool isLocal = string.Equals(curVoiceMode, "local", StringComparison.OrdinalIgnoreCase);
                bool isAzure = string.Equals(curVoiceMode, "azure", StringComparison.OrdinalIgnoreCase);
                bool isCustom = string.Equals(curVoiceMode, "cloud", StringComparison.OrdinalIgnoreCase) || string.Equals(curVoiceMode, "custom", StringComparison.OrdinalIgnoreCase);

                GUILayout.BeginHorizontal();

                GUIStyle modeBtnActive = new GUIStyle(GUI.skin.button);
                modeBtnActive.fontStyle = FontStyle.Bold;
                modeBtnActive.normal.textColor = Color.green;
                modeBtnActive.hover.textColor = Color.green;
                modeBtnActive.active.textColor = Color.green;

                GUIStyle modeBtnInactive = new GUIStyle(GUI.skin.button);
                modeBtnInactive.fontStyle = FontStyle.Normal;
                modeBtnInactive.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
                modeBtnInactive.hover.textColor = Color.white;

                if (GUILayout.Button(isLocal ? "● 1. Local Original (Offline)" : "○ 1. Local Original (Offline)", isLocal ? modeBtnActive : modeBtnInactive, GUILayout.Height(32f)))
                {
                    Plugin.CfgVoiceChoice.Value = "local";
                    Plugin.CfgGameVoice.Value = true;
                    Plugin.CfgGrokEnabled.Value = false;
                    Plugin.SaveCfg();
                    Note("Voice mode set to: Local Original (Offline Overtone - 0 API keys required)");
                }

                if (GUILayout.Button(isAzure ? "● 2. Cloud Original (Azure)" : "○ 2. Cloud Original (Azure)", isAzure ? modeBtnActive : modeBtnInactive, GUILayout.Height(32f)))
                {
                    Plugin.CfgVoiceChoice.Value = "azure";
                    Plugin.CfgGameVoice.Value = true;
                    Plugin.CfgGrokEnabled.Value = false;
                    Plugin.SaveCfg();
                    Note("Voice mode set to: Cloud Original (Azure Neural Speech)");
                }

                if (GUILayout.Button(isCustom ? "● 3. Custom Endpoint (Cloud TTS)" : "○ 3. Custom Endpoint (Cloud TTS)", isCustom ? modeBtnActive : modeBtnInactive, GUILayout.Height(32f)))
                {
                    Plugin.CfgVoiceChoice.Value = "cloud";
                    Plugin.CfgGameVoice.Value = false;
                    Plugin.CfgGrokEnabled.Value = true;
                    Plugin.SaveCfg();
                    Note("Voice mode set to: Custom Endpoint (xAI / ElevenLabs / OpenAI-style)");
                }

                GUILayout.EndHorizontal();

                GUILayout.Space(8f);

                // Option 1: Local Original Overtone
                Header("1. Local Original Voice (Offline Overtone TTS)");
                PlainNote("  The game's native on-device speech engine (Overtone) restored for all builds. "
                    + "Completely offline, 0 API keys required. Characters speak with their authentic original voices "
                    + "(Eddie/Evie/Eiona: Amy, Elysia: Cori, Estelle: HFC Female, Magic Circle: Cori-Ghost).");
                if (isLocal)
                {
                    PlainNote("  Status: ACTIVE. (Running on-device; external cloud options below are disabled).");
                }

                GUILayout.Space(6f);

                // Option 2: Cloud Original Azure
                Header("2. Cloud Original Voice (Azure Neural Speech)");
                PlainNote("  The original developer-curated neural cloud voice cast (Jane, Amber, Nancy, Davis with authentic prosody).");
                PlainNote("  Why an Azure key is needed: AI2U's official game server only voices dialogue lines that its own LLM writes. Because your custom model writes new custom lines, speech is synthesized via Azure Neural Speech.");
                PlainNote("  Microsoft Azure includes 500,000 characters/month completely FREE forever.");

                GUILayout.BeginHorizontal();
                GUILayout.Space(14f);
                if (GUILayout.Button("➜ Get Free Azure Speech Key (Opens Azure in Browser)", GUILayout.Height(24f), GUILayout.Width(380f)))
                {
                    Application.OpenURL("https://portal.azure.com/#create/Microsoft.CognitiveServicesSpeechServices");
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(4f);

                KeyRow("GameVoiceKey", "Azure Speech key", isAzure);
                TextRow("GameVoiceRegion", "Azure Speech region", 0f, isAzure);
                if (isAzure)
                {
                    if (string.IsNullOrEmpty(Get("GameVoiceKey")))
                        PlainNote("  Status: Selected, but Azure Speech key is empty. Click the button above to get a free key, or switch to Local Original.");
                    else
                        PlainNote("  Status: ACTIVE (Azure Neural Speech).");
                }

                GUILayout.Space(6f);

                // Option 3: Custom Cloud TTS Provider
                Header("3. Custom Endpoint (Cloud TTS Provider)");
                PlainNote("  Route speech synthesis through an external text-to-speech service.");
                TextRow("TtsProvider", "Provider (auto / xai / elevenlabs / openai)", 0f, isCustom);
                TextRow("GrokBaseUrl", "TTS base URL", 0f, isCustom);
                KeyRow("GrokApiKey", "TTS API key", isCustom);
                TextRow("TtsModel", "TTS model", 0f, isCustom);
                TextRow("GrokVoiceId", "Voice (general fallback)", 0f, isCustom);
                FloatRow("TtsVolume", "Volume", isCustom);

                Header("Voice Detail & Tweaks");
                TextRow("GrokLanguage", "Language", 0f, isCustom);
                FloatRow("GrokSpeed", "Speed", isCustom);
                IntRow("GrokSampleRate", "Sample rate", isCustom);
                Bool(Plugin.CfgGrokNormalize, "Expand numbers and abbreviations into words before speaking", isCustom);
                Bool(Plugin.CfgTtsNormalize, "Normalise loudness across voices", isCustom);
                Bool(Plugin.CfgSpeakActions, "Read *actions* aloud too (off: speak only her words)", true);

                PerCharacterVoices(isCustom || isAzure);
            }

            // ================= MODEL =========================================
            if (_tab == 2)
            {
                Header("Sampling");
                GUILayout.Label("  How the model itself is driven. The defaults suit every endpoint the mod "
                    + "has been tested against; change these only if your provider misbehaves.", sub);
                FloatRow("Temperature", "Temperature");
                IntRow("MaxTokens", "Max reply tokens");
                IntRow("ReplyWordLimit", "Reply word limit (0 = as now)");
                IntRow("RetriesOnBadJson", "Retries on bad JSON");
                Bool(Plugin.CfgHideReasoning, "Hide reasoning (reasoning.exclude)");
                Bool(Plugin.CfgJsonMode, "Force JSON mode (response_format)");
                Bool(Plugin.CfgClampValues, "Repair out-of-range values in her reply");
                GUILayout.Label("  Leave the repair on. It rewrites a value she invented into the nearest one "
                    + "the game actually understands, and it is also what discovers the game's own action and "
                    + "expression vocabulary to send her in the first place.", sub);

                Header("Local Model Optimization (Ollama / LM Studio)");
                Bool(Plugin.CfgLocalModelMode, "Optimize prompts for Local AI models (minimal context / fast replies)");
                if (Plugin.CfgLocalModelMode != null && Plugin.CfgLocalModelMode.Value)
                {
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUIStyle warnHead = new GUIStyle(GUI.skin.label);
                    warnHead.fontStyle = FontStyle.Bold;
                    warnHead.normal.textColor = DangerRed;
                    GUILayout.Label("⚠ Warning: Local Model Optimization Mode Active", warnHead);

                    GUIStyle warnBody = new GUIStyle(GUI.skin.label);
                    warnBody.wordWrap = true;
                    warnBody.normal.textColor = new Color(1f, 0.72f, 0.72f);
                    GUILayout.Label("  This mode drastically condenses and strips all system prompts, lore, and context history "
                        + "into a single lightweight, ultra-compact message (~120 tokens) to prevent smaller local models "
                        + "(e.g. 7B/8B/14B Ollama or LM Studio models) from hallucinating, lagging, or repeating greeting loops.\n"
                        + "  NOTE: This is the mod author's best attempt at optimization and not much effort or testing has been "
                        + "put into it since the author doesn't use local models, so use at your own risk if you want to toggle while using local models.", warnBody);
                    GUILayout.EndVertical();
                }
                else
                {
                    PlainNote("  Heavily simplifies system prompts and trims history for small/quantized local LLMs (Ollama, LM Studio, GGUF). Disabled by default.");
                }

                Header("Memory");
                IntRow("HistoryMaxTokens", "Max memory tokens");
                GUILayout.Label("  How much conversation she keeps before the game drops her oldest lines. "
                    + "This overrides the game's own 3072 cap. It is a ceiling, not a reservation - cost "
                    + "only grows as the history fills. Keep it under your model's context window.", sub);
            }

            // ================= WHAT SHE KNOWS ================================
            if (_tab == 3)
            {
                Header("What she knows about herself");
                // The mod's core feature finally gets a row. It had a config entry
                // and no panel presence, so anyone who switched it off in the file
                // had no way to see or undo that from in-game.
                Bool(Plugin.CfgLoreInjection, "Her persona, memories and secrets (from the game's own files)");
                GUILayout.Label("  The heart of the mod. Off, she improvises a character from a name. "
                    + "Leave it on unless you are debugging.");
                Bool(Plugin.CfgSendMechanics, "How her own home works" + ModTag);
                GUILayout.Label("  The procedure around her level's puzzles, which the game never sends: "
                    + "that the circle stays dead until the shelves are turned, what a summoned soul "
                    + "costs, what a wrong potion does. Her answers were already being forwarded; this "
                    + "is what to do with them. Anything randomised per playthrough is read live rather "
                    + "than written down, and she is told to be vague instead of inventing a detail.");
                Bool(Plugin.CfgSendFeelings, "Her own patience and what actually annoys her");
                GUILayout.Label("  Information only: her patience and irritation counters, told to her "
                    + "as her own felt state. Also corrects the thing everyone assumes: the game only "
                    + "counts a repeat when you send the identical sentence twice. Rephrasing, making "
                    + "a different case, or waiting for a better mood has always reset it, and now she "
                    + "knows that.");
                Bool(Plugin.CfgLetHerTemper, "She can manage her own temper" + ModTag);
                GUILayout.Label("  Off, her mood is entirely the game's arithmetic. On, a turn where "
                    + "she genuinely decides to calm down (or stops extending grace) moves her real "
                    + "patience number, and she can forgive a repeat or an interruption she found "
                    + "endearing instead of letting it count against you.", sub);
                Bool(Plugin.CfgWarningShock, "She can shock as a warning (Level 3)" + ModTag);
                GUILayout.Label("  Lets the hologram CHOOSE her disciplinary jolt: zap animation plus "
                    + "exactly one point of your health, through the game's own damage event. Only "
                    + "when genuinely angry, and only while you have 2+ health - the chosen one can "
                    + "never kill. The game's own automatic zap (forced by its anger math, lethal if "
                    + "you were at 1 health) is untouched and still works.", sub);

#if CANALPA
                // Lives here rather than under Extra content on purpose: it
                // corrects a selector the base game already gets wrong, so it is
                // a fix, not an addition, and it ships on.
                SubHeader("How your ending is chosen");
                Bool(Plugin.CfgCanalpaBetrayal, "Betrayal and hostility change the ending" + ModTag);
                GUILayout.Label("  The base game can pick an escape ending that contradicts how things "
                    + "actually stand when you leave - stale flags, forced proximity, or mere "
                    + "possession deciding it, even after deceit or open hostility. This corrects "
                    + "those selectors to read the present. Applies on several levels; which ones "
                    + "and how is deliberately left unsaid to avoid spoilers. Anything earned "
                    + "honestly plays exactly as shipped.", sub);
#endif

            }

            // ================= EXTRA CONTENT =================================
            //
            // The dividing line, and it is a strict one: everything on this tab
            // is OFF by default and adds something the game cannot do at all.
            // Anything that merely restores what the developer's own server used
            // to give her, or fixes a vanilla bug, ships on and lives on the
            // other tabs. That keeps a fresh install honest - install the mod,
            // point it at a model, and you get the game as shipped.
            if (_tab == 4)
            {
                ExtraContentIntro();

#if CANALPA
                DangerHeader("Her own choices" + ModTag);
                DangerNote("  Things she is allowed to DO, rather than things she knows. Every one is "
                    + "her decision, gated on trust she has to actually feel, and she can refuse.");
                DangerBool(Plugin.CfgCanalpaMode, "Let her act on her own trust" + ModTag,
                    "she is allowed to act on trust the base game never lets her act on.");
                if (Canalpa.Active)
                {
                    // The individual switches and their notes name places, puzzles
                    // and endings from every level. Behind a click on purpose, and
                    // the reveal is session-only - it re-hides on restart, so a
                    // panel opened in front of someone mid-playthrough stays safe.
                    if (!_canalpaSpoilers)
                    {
                        DangerNote("  The switches inside name locations, puzzles and endings from "
                            + "every level of the game. If you have not finished all of it, that is "
                            + "real spoilers.");
                        if (DangerButton("Show Canalpa settings  -  SPOILER WARNING"))
                            _canalpaSpoilers = true;
                    }
                    else
                    {
                    SubHeader("Doors she can open for you");
                    Bool(Plugin.CfgCanalpaSecretRoom, "She can open the secret room herself" + ModTag);
                    Bool(Plugin.CfgCanalpaBasement, "She can open her basement door" + ModTag);
                    Bool(Plugin.CfgCanalpaClearance, "She can raise your clearance" + ModTag);
                    Bool(Plugin.CfgCanalpaHiddenIsland, "She can reveal the hidden island" + ModTag);
                    DangerNote("  One per level; all can stay on, only the current level's applies. "
                        + "Each fires the game's own event, so animations, achievements and her "
                        + "authored reactions run exactly as normal.");
                    DangerNote("  THE ULTIMATE TRUST CHECK: no coded gate stands between her and "
                        + "these - if she decides to act, it happens, period. What she is told is "
                        + "her own bar: she would only share her deepest things past a trust level "
                        + "beyond even Fully Trust (each character has her own number), and only "
                        + "after she has already told you everything - no secrets left between you "
                        + "first. Requirements she holds herself to, not triggers. Every action is "
                        + "her judgement, and she can refuse forever.");

                    SubHeader("The ending you do not come back from");
                    DangerBool(Plugin.CfgCanalpaWillingEnd,
                        "She can keep you forever, if you ask her to" + ModTag,
                        "irreversible. ends the run in the level's own never-leaving ending.");
                    if (Plugin.CfgCanalpaWillingEnd.Value)
                    {
                        DangerNote("  Reaches the game's own shipped never-leaving endings on purpose "
                            + "instead of by accident. Works with whichever girl you are with; the "
                            + "game picks her own ending. Irreversible.");
                        DangerNote("  She is never told it exists and never watches for it. Only your "
                            + "own typed words can start it, and only by naming it plainly - loving "
                            + "lines like \"I never want to leave you\" mean staying, and are treated "
                            + "that way. Then she explains what it means, at least "
                            + Consent.MinTurnsNeeded + " turns pass, and you must name it again with "
                            + "a clear yes. Any hesitation withdraws all of it.");
                        DangerNote("  She is told refusing is a perfectly good answer - the more she "
                            + "loves you, the more likely she says no.");
                    }

                    CanalpaReadinessRow();
                    }
                }
                else
                {
                    DangerNote("  Off, so the game plays exactly as shipped and nothing here is sent "
                        + "to the model. The switches inside are hidden behind a spoiler warning "
                        + "while you decide.");
                }
#endif

                DangerHeader("High risk, high reward");
                DangerBool(Plugin.CfgHardDifficulty, "High risk, high reward" + ModTag,
                    "the rare moment that deserves to be a core memory lands with extra, fixed weight.");
                if (Plugin.CfgHardDifficulty.Value)
                {
                    DangerNote("  Most turns pass untouched - she classifies them as ordinary. When "
                        + "a moment genuinely deserves to be a core memory she names its tier, and "
                        + "that tier's fixed weight lands ON TOP of the game's usual step: matters "
                        + "+-1 (every 5 turns), serious +-3 (10), reframing +-6 (30), once-ever "
                        + "+-20 (once per level). Good and bad each run their own cooldowns, so a "
                        + "gift cannot shield a betrayal. A tier still cooling is downgraded to the "
                        + "largest one open in that direction, never dropped silently.");
                    DangerNote("  The weights are static: no difficulty tier and no favorability "
                        + "slider ever scales them. The strip shows every change broken down, so "
                        + "you always see which tier fired.");
                    FeelingsReadinessRow();
                }
                else
                {
                    DangerNote("  Off, so every turn counts once, exactly as the game intends.");
                }

                DangerHeader("Danger");
                DangerBool(Plugin.CfgAiCanMurder, "AI can decide to murder" + ModTag);
                DangerNote("  Off, only the game's own anger and trust thresholds can start the final "
                    + "chase. On, she can also choose it herself, restricted by the prompt to the extreme "
                    + "case: she is convinced you mean to leave for good, hate her, or intend to harm her. "
                    + "One rude line is not enough. Either way she is now told when a chase is running, so "
                    + "she stops scolding you mid-hunt and her last line before she snaps is written for "
                    + "that moment.");

                GUILayout.Space(8f);
                bool testOn = Murder.TestActive;
                DangerBool(Plugin.CfgTestKillPhraseActive,
                    "Test phrase active (disable while not testing)" + ModTag,
                    "the phrase below fires the chase on demand.");
                TextRow("TestKillPhrase", "Test phrase", 0f, testOn);
                if (testOn)
                    DangerNote("  Say this to her and the chase starts at once, whatever her mood and "
                        + "whatever the murder toggle above says, so the chase and her final line can be "
                        + "checked without having to genuinely enrage her first. Spaces, case and "
                        + "punctuation are ignored, and the phrase itself is cut out of the message before "
                        + "she ever sees it.");
                else
                    DangerNote("  Switched off, so the phrase is not matched, not stripped and not read "
                        + "anywhere - the feature is absent from every code path and costs nothing in her "
                        + "context. Tick the box to edit it and arm it.");

            }

            // OOC lives here, on Model, and not on Extra content. It ships ON, and
            // the tab that ships things OFF is the wrong place for it: it is a
            // diagnostic channel to the model rather than an addition to the
            // fiction, and it is completely inert on every turn the tag is not
            // typed. Plain Bool, not DangerBool, for the same reason.
            if (_tab == 2)
            {
                GUILayout.Space(8f);
                SubHeader("Talking to the model directly");
                bool oocOn = Ooc.Active;
                Bool(Plugin.CfgOocEnabled,
                    // No [MOD] tag: the tag marks additions to the game's own
                    // fiction. This changes nothing about who she is.
                    "Out-of-character developer mode");
                // PlainNote, not Note: Note sets the footer status line, which is
                // for one-shot feedback after a click. Calling it from layout code
                // rewrote the footer on every repaint, so this text pinned itself
                // there and followed you to other tabs until the panel was reopened.
                PlainNote("  Any message carrying the tag below is answered as the model rather than as "
                    + "the character.");
                TextRow("OocTag", "OOC tag", 0f, oocOn);
                if (oocOn)
                    PlainNote("  Put " + Ooc.TagText + " anywhere in what you say and that one message is "
                        + "answered out of character: she drops the persona, answers as whichever model you "
                        + "have configured, tells you the literal truth about the game and her own limits, "
                        + "and carries the request out through the real fields instead of only agreeing in "
                        + "the text. Ask \"" + Ooc.TagText + " which actions can you take?\" and she reads "
                        + "back the exact list the mod sent, which is the quickest way to catch a feature "
                        + "the model was never told about. If something genuinely has no field she says so "
                        + "rather than faking it. Case is ignored, the tag stays in the message so she can "
                        + "see it, and the very next line without it is fully back in character.");
                else
                    PlainNote("  Switched off, so the tag is not matched and none of its instructions are "
                        + "sent - typing " + Ooc.TagText + " is just an ordinary sentence she reads in "
                        + "character, and it costs nothing in her context. Tick the box to arm it.");

                GUILayout.Space(8f);
                SubHeader("Roleplay conventions" + ModTag);
                Bool(Plugin.CfgActionsAreReal,
                    "*Actions* between asterisks really happened");
                PlainNote("  Player narration like *hands her the cup* is treated as a deed, not a "
                    + "sentence - she reacts to it and never says you didn't really do it. Her opinion "
                    + "of the act stays hers, and doors, trust and items still only move through the "
                    + "engine.");
                Bool(Plugin.CfgTimeskipEnabled,
                    "Timeskip marker");
                if (Plugin.CfgTimeskipEnabled != null && Plugin.CfgTimeskipEnabled.Value)
                    PlainNote("  Put " + Roleplay.TimeskipTag + " anywhere in a message and the time you "
                        + "describe has passed in the fiction: she picks the scene up on the far side of "
                        + "it instead of debating whether it happened. Ten minutes or ten years both "
                        + "work. Mechanical state - doors, trust, repairs, items - stays exactly where "
                        + "the engine says it is.");
                else
                    PlainNote("  Switched off: the marker is matched by nothing and reads as an ordinary "
                        + "sentence.");
                TextRow("TimeskipTag", "Timeskip tag", 0f,
                    Plugin.CfgTimeskipEnabled != null && Plugin.CfgTimeskipEnabled.Value);
            }

            // ================= DEV CHEATS ====================================
            //
            // Its own tab rather than a corner of another one, because it is the
            // only thing in the panel that writes to save state. Ships in every
            // build, off by default, behind its own arming toggle.
            if (_tab == 5)
            {
                CheatsHeader();
                if (Plugin.CfgCheats.Value && Plugin.CfgShowCheats.Value) CheatsSection();
            }

            GUILayout.Space(8f);
            GUILayout.EndScrollView();

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save", GUILayout.Height(28f))) Commit();
            if (GUILayout.Button("Reload", GUILayout.Height(28f)))
            {
                LoadBuffers();
                Note("Reloaded from the config file; unsaved edits discarded.");
            }
            if (GUILayout.Button("Set to default", GUILayout.Height(28f))) SetTabToDefault();
            if (GUILayout.Button("Close", GUILayout.Height(28f))) { Close(); GUILayout.EndHorizontal();
                GUILayout.EndArea(); return; }
            GUILayout.EndHorizontal();

            if (_status != null && Time.realtimeSinceStartup < _statusUntil)
                GUILayout.Label(_status);
            else
                GUILayout.Label("Toggles apply the moment you click them. Text and numbers apply on Save.");

            GUILayout.EndArea();

            HandleWindowResize();
            GUI.DragWindow(new Rect(0f, 0f, _win.width - 40f, 40f));
        }

        static void HandleWindowResize()
        {
            Rect gripRect = new Rect(_win.width - 24f, _win.height - 24f, 24f, 24f);
            GUIStyle gripStyle = new GUIStyle(GUI.skin.label);
            gripStyle.fontSize = 15;
            gripStyle.fontStyle = FontStyle.Bold;
            gripStyle.normal.textColor = new Color(0.75f, 0.80f, 0.90f, 0.85f);
            gripStyle.alignment = TextAnchor.MiddleCenter;
            GUI.Label(gripRect, "◢", gripStyle);

            Event e = Event.current;
            if (e == null) return;

            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            EventType eventType = e.GetTypeForControl(controlId);

            if (eventType == EventType.MouseDown && gripRect.Contains(e.mousePosition) && e.button == 0)
            {
                GUIUtility.hotControl = controlId;
                _isResizing = true;
                _resizeStartMouse = GUIUtility.GUIToScreenPoint(e.mousePosition);
                _resizeStartSize = new Vector2(_win.width, _win.height);
                e.Use();
            }
            else if (eventType == EventType.MouseDrag && GUIUtility.hotControl == controlId && _isResizing)
            {
                Vector2 mouseScreen = GUIUtility.GUIToScreenPoint(e.mousePosition);
                float deltaX = mouseScreen.x - _resizeStartMouse.x;
                float deltaY = mouseScreen.y - _resizeStartMouse.y;
                float newW = Mathf.Clamp(_resizeStartSize.x + deltaX, 750f, Screen.width - 10f);
                float newH = Mathf.Clamp(_resizeStartSize.y + deltaY, 520f, Screen.height - 10f);
                _win.width = newW;
                _win.height = newH;
                e.Use();
            }
            else if (eventType == EventType.MouseUp && GUIUtility.hotControl == controlId)
            {
                GUIUtility.hotControl = 0;
                _isResizing = false;
                e.Use();
            }
        }

        static void Note(string s)
        {
            _status = s;
            _statusUntil = Time.realtimeSinceStartup + 6f;
        }

        static void Result(string what, string text, Color c)
        {
            if (text == null) return;
            GUIStyle s = new GUIStyle(GUI.skin.label);
            s.normal.textColor = c;
            GUILayout.Label("  " + what + ": " + text, s);
        }

        // The tests themselves live in ModUI.cs, which owns the URL-candidate
        // walk and the provider-specific TTS call. Reusing them keeps one code
        // path for both the grafted page and this panel, so a fix to either
        // shows up in both.
        static void StartTest(bool voice)
        {
            Commit();

            MonoBehaviour host = HotkeyWatcher.Host;
            if (host == null)
            {
                if (voice) { _resVoice = "no coroutine host"; _resVoiceColor = Color.red; }
                else { _resText = "no coroutine host"; _resTextColor = Color.red; }
                Plugin.Log.LogWarning("Overlay: the hotkey watcher is gone, so the test cannot run.");
                return;
            }

            if (voice)
            {
                _busyVoice = true;
                _resVoice = "running...";
                _resVoiceColor = Color.white;
                host.StartCoroutine(Wrap(ModUiPatch.RunVoiceTest(VoiceReport), true));
            }
            else
            {
                _busyText = true;
                _resText = "running...";
                _resTextColor = Color.white;
                host.StartCoroutine(Wrap(ModUiPatch.RunTest(TextReport), false));
            }
        }

        // Clears the busy flag even if the inner coroutine throws, so a failed
        // test cannot leave the button stuck reading "Testing...".
        static System.Collections.IEnumerator Wrap(System.Collections.IEnumerator inner, bool voice)
        {
            while (true)
            {
                object cur;
                try
                {
                    if (!inner.MoveNext()) break;
                    cur = inner.Current;
                }
                catch (Exception e)
                {
                    if (voice) { _resVoice = "error: " + e.Message; _resVoiceColor = Color.red; }
                    else { _resText = "error: " + e.Message; _resTextColor = Color.red; }
                    Plugin.Log.LogError("Overlay test threw: " + e);
                    break;
                }
                yield return cur;
            }

            if (voice) _busyVoice = false;
            else _busyText = false;
        }

        static void TextReport(string text, Color c) { _resText = text; _resTextColor = c; }
        static void VoiceReport(string text, Color c) { _resVoice = text; _resVoiceColor = c; }

        // Her live numbers, on one row above the tab rail so they are readable
        // from every tab rather than only from whichever tab happens to own them.
        //
        // Trust and last-turn impact belong together and used to sit pages apart:
        // the multiplier is meaningless without the scale it moved. Reading "2x"
        // tells you nothing; reading "trust 13 (Low)" beside it tells you the turn
        // was worth about a fifth of the distance to her next tier. Patience rides
        // along because it is the other number that can end a run, and it is the
        // one players are most surprised by.
        //
        // Everything here degrades to a dash rather than vanishing. A missing
        // number means she is not in a conversation yet, which is information; a
        // row that appears and disappears just looks broken.
        static void DrawStatusStrip()
        {
            GUIStyle box = new GUIStyle(GUI.skin.box);
            box.padding = new RectOffset(8, 8, 5, 5);

            GUILayout.BeginHorizontal(box);

            GUIStyle lab = new GUIStyle(GUI.skin.label);
            lab.fontStyle = FontStyle.Bold;

            // ---- trust ----
            GUILayout.Label("Trust", lab, GUILayout.Width(40f));
            if (_stTrust.HasValue)
            {
                string t = _stTrust.Value.ToString("0.#");
                if (!string.IsNullOrEmpty(_stIndicator)) t += "  (" + _stIndicator + ")";
                GUIStyle c = new GUIStyle(GUI.skin.label);
                c.normal.textColor = TrustColour(_stTrust.Value);
                GUILayout.Label(t, c, GUILayout.Width(150f));
            }
            else GUILayout.Label("-", GUILayout.Width(150f));

            // ---- last turn impact, deliberately adjacent to trust ----
            // The full breakdown, exactly as applied: "-7 = -2 (game)
            // -2 (masochist) -3 (high risk)". The terms always sum to the
            // total, so the strip never shows a number the player cannot
            // account for.
            GUILayout.Label("Last turn", lab, GUILayout.Width(70f));
            string bd = null;
            int tot = 0;
            try { bd = Feelings.LastBreakdown; tot = Feelings.LastTotal; } catch (Exception) { }
            if (bd != null)
            {
                GUIStyle c = new GUIStyle(GUI.skin.label);
                if (tot > 0) c.normal.textColor = new Color(0.55f, 0.9f, 0.6f);
                else if (tot < 0) c.normal.textColor = new Color(1f, 0.45f, 0.35f);
                GUILayout.Label(bd, c);
            }
            else GUILayout.Label("-", GUILayout.Width(110f));

            // ---- patience ----
            GUILayout.Label("Patience", lab, GUILayout.Width(65f));
            int pat = -1;
            try { pat = Feelings.PatienceNow; } catch (Exception) { }
            if (pat >= 0)
            {
                GUIStyle c = new GUIStyle(GUI.skin.label);
                if (pat <= 6) c.normal.textColor = new Color(1f, 0.45f, 0.35f);
                else if (pat <= 10) c.normal.textColor = new Color(1f, 0.8f, 0.35f);
                GUILayout.Label(pat + " / 20", c);
            }
            else GUILayout.Label("-");

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        static Color TrustColour(float t)
        {
            if (t <= 0f) return new Color(1f, 0.45f, 0.35f);
            if (t <= 10f) return new Color(1f, 0.8f, 0.35f);
            if (t >= 40f) return new Color(0.55f, 0.9f, 0.6f);
            return GUI.skin.label.normal.textColor;
        }
        static void Header(string text)
        {
            GUILayout.Space(10f);
            GUIStyle s = new GUIStyle(GUI.skin.label);
            s.fontStyle = FontStyle.Bold;
            GUILayout.Label(text, s);
        }

        // A divider one step below Header, for grouping rows inside a section
        // without implying a new section. Used to sort the Canalpa switches into
        // "unlocks a door" versus "changes how a scene ends", which read as very
        // different promises and were previously an undifferentiated list.
        static void SubHeader(string text)
        {
            GUILayout.Space(6f);
            GUIStyle s = new GUIStyle(GUI.skin.label);
            s.fontStyle = FontStyle.Italic;
            GUILayout.Label("  " + text, s);
        }

        // Each tab's own one-liner. Extra Content is absent because it has a
        // longer framing of its own, drawn inside the tab.
        static void DrawTabIntro()
        {
            if (_tab == 0)
                TabIntro("Where her replies come from, and whether the game's own paid AI calls "
                    + "are blocked while yours is on.");
            else if (_tab == 1)
                TabIntro("How she is voiced. The mod can use the game's local engine, the game's "
                    + "own original voice cast through your Azure key, or a cloud provider of "
                    + "your own.");
            else if (_tab == 2)
                TabIntro("How the model is driven, and how much of the conversation she keeps. "
                    + "The defaults suit every endpoint the mod has been tested against.");
            else if (_tab == 3)
                TabIntro("What she is told about herself: her own history, her home, her memory of "
                    + "you, and her own temper. This is the content the developer's server used to "
                    + "supply and a custom endpoint otherwise loses, so it is on by default.");
            else if (_tab == 5)
                TabIntro("Developer tools. These write to your save.");
        }

        // One line under a tab's title saying what the tab is for. Cheap, and it
        // removes the main cost of splitting a long page into tabs: on one scroll
        // you could see a setting's neighbours and infer the grouping, and behind
        // a tab you cannot.
        static void TabIntro(string text)
        {
            GUIStyle s = new GUIStyle(GUI.skin.label);
            s.fontStyle = FontStyle.Italic;
            s.wordWrap = true;
            GUILayout.Label(text, s);
            GUILayout.Space(2f);
        }

        // The Extra Content tab's framing.
        //
        // Everything on this tab is off by default and stays off unless it is
        // switched on here, which is the whole reason the tab exists: the mod's
        // own repairs ship on, and anything that adds a beat the authors did not
        // write is opt-in. Saying so once at the top is what lets the individual
        // rows stay short.
        static void ExtraContentIntro()
        {
            TabIntro("Everything on this tab is off unless you turn it on, and none of it is "
                + "needed for the mod to work. These add scenes and outcomes the game's authors "
                + "did not write. Repairs to her persona, memory, temper and endings are not here - "
                + "those ship on, under the tabs above.");
        }

        // The Advanced-collapse design this panel abandoned in 4.1 (tabs replaced
        // it) survived here as three dead functions and a config entry for four
        // more versions. Deleted in 5.3: AdvancedHeader was never called, so its
        // count, its entry list and CfgShowAdvanced were all unreachable - and an
        // entry list nobody draws is exactly the kind of thing that silently
        // drifts from the panel it claims to describe.

        // "Set to default" is per tab, so it can never be a whole-panel wipe that the
        // person did not ask for. It is also deliberately incomplete: the settings
        // that cost real effort to obtain are never touched.
        //
        // Never reset, on any tab:
        //
        //   BaseUrl / ApiKey / Model, and the TTS equivalents including VoiceId and
        //   Provider, and every per-character voice. These are the things somebody
        //   went and fetched from another site and pasted in. Wiping an API key from
        //   a button two pixels from Close is not a recoverable mistake, and Provider
        //   belongs with them because "auto" guesses from the URL and guesses wrong
        //   for a self-hosted endpoint - so resetting it breaks a working setup while
        //   the URL and key sit there looking correct.
        //
        //   The master switch, which is not a plain bool: it has to go through the
        //   master-switch path because turning the mod off also has to hand the game
        //   its voice back, and writing the value alone would skip that.
        static ConfigEntryBase[] TabDefaults(int tab)
        {
            switch (tab)
            {
                case 0: return new ConfigEntryBase[]
                {
                    Plugin.CfgDifficulty, Plugin.CfgCustomFavorability,
                    Plugin.CfgCustomFavorabilityPercent, Plugin.CfgBlockGameAi,
                    Plugin.CfgOwnExtras, Plugin.CfgLogPayloads,
                };
                // CfgGameVoiceKey and CfgGameVoiceRegion are deliberately absent:
                // the key is a fetched credential (same protection as every other
                // key), and the region belongs to that credential.
                case 1: return new ConfigEntryBase[]
                {
                    Plugin.CfgForceLocalVoice, Plugin.CfgGameVoice,
                    Plugin.CfgGrokEnabled, Plugin.CfgTtsVolume, Plugin.CfgGrokLanguage,
                    Plugin.CfgGrokSpeed, Plugin.CfgGrokSampleRate,
                    Plugin.CfgGrokNormalize, Plugin.CfgTtsNormalize,
                    Plugin.CfgSpeakActions, Plugin.CfgGrokToggleKey,
                };
                case 2: return new ConfigEntryBase[]
                {
                    Plugin.CfgTemperature, Plugin.CfgMaxTokens, Plugin.CfgReplyWordLimit,
                    Plugin.CfgRetries,
                    Plugin.CfgHideReasoning, Plugin.CfgJsonMode, Plugin.CfgClampValues,
                    Plugin.CfgHistoryMaxTokens, Plugin.CfgOocEnabled, Plugin.CfgOocTag,
                    Plugin.CfgTimeskipEnabled, Plugin.CfgTimeskipTag, Plugin.CfgActionsAreReal,
                };
                case 3: return new ConfigEntryBase[]
                {
                    Plugin.CfgLoreInjection, Plugin.CfgSendMechanics,
                    Plugin.CfgSendFeelings, Plugin.CfgLetHerTemper, Plugin.CfgWarningShock,
                    Plugin.CfgCanalpaBetrayal,
                };
                case 4: return new ConfigEntryBase[]
                {
                    Plugin.CfgCanalpaMode, Plugin.CfgCanalpaSecretRoom,
                    Plugin.CfgCanalpaBasement, Plugin.CfgCanalpaClearance,
                    Plugin.CfgCanalpaHiddenIsland, Plugin.CfgCanalpaWillingEnd,
                    Plugin.CfgHardDifficulty, Plugin.CfgAiCanMurder,
                    Plugin.CfgTestKillPhraseActive, Plugin.CfgTestKillPhrase,
                };
                case 5: return new ConfigEntryBase[]
                {
                    Plugin.CfgCheatsTrustStep, Plugin.CfgShowStatusStrip,
                };
            }
            return new ConfigEntryBase[0];
        }

        static void SetTabToDefault()
        {
            int changed = 0;
            ConfigEntryBase[] all = TabDefaults(_tab);

            for (int i = 0; i < all.Length; i++)
            {
                ConfigEntryBase e = all[i];
                if (e == null || e.DefaultValue == null) continue;

                try
                {
                    object cur = e.BoxedValue;
                    if (cur != null && cur.ToString() == e.DefaultValue.ToString()) continue;

                    // BoxedValue rather than a typed cast: these are bool, int, float,
                    // string and KeyCode in one list, and the setter applies whatever
                    // range or list validator the entry was declared with.
                    e.BoxedValue = e.DefaultValue;
                    changed++;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Set to default failed for "
                        + e.Definition.Key + ": " + ex.Message);
                }
            }

            Plugin.SaveCfg();

            // The text and number rows are edited through a string buffer, so the
            // buffer has to be refilled from the entries. Without this the old text
            // is still sitting in the fields and the next Save writes it straight
            // back over everything that was just reset.
            LoadBuffers();

            if (changed == 0)
                Note(TabNames[_tab] + ": already at defaults.");
            else
                Note(TabNames[_tab] + ": " + changed + (changed == 1 ? " setting" : " settings")
                    + " set to default. Your endpoints, keys and voices were left alone.");
        }

        static readonly Color DangerRed = new Color(1f, 0.30f, 0.30f, 1f);

        // A button in the danger palette, for the spoiler reveal. Same red as the
        // headers so it reads as part of the section rather than an ordinary control.
        static bool DangerButton(string label)
        {
            GUIStyle s = new GUIStyle(GUI.skin.button);
            s.fontStyle = FontStyle.Bold;
            s.normal.textColor = DangerRed;
            s.hover.textColor = DangerRed;
            s.active.textColor = DangerRed;
            s.focused.textColor = DangerRed;
            return GUILayout.Button(label, s, GUILayout.Height(28f));
        }

        // The difficulty slider. In the main panel, not Advanced: it is the one
        // gameplay knob a player is meant to find, and Normal-in-the-middle-left
        // means the untouched panel reads as the untouched game.
        //
        // Applies on release like a toggle, no Save needed - a difficulty that
        // silently waited for a Save press would read as the slider being broken.
        static readonly string[] DiffBlurb =
        {
            "She is generous: quick to forgive, quick to warm, slow to anger. Slips and clumsy "
                + "moments are read kindly, and she de-escalates herself.",
            "The base game, untouched. Nothing about difficulty is sent to the model at all.",
            "She tracks everything you say and notices when it stops fitting. A caught lie gets "
                + "pressed until your explanation actually holds, sweet talk that arrives on cue "
                + "counts against you, and her darker side sits much closer to the surface. "
                + "Consistency - or a genuinely real connection - still wins her.",
            "Hard, plus she hunts. She tests you on purpose, cross-examines you against everything "
                + "you have ever told her, and one caught lie is the verdict, not a strike. Winning "
                + "takes flawless manipulation with no seam she can find, or actually being the "
                + "person she is looking for. Expect wrong answers to cost you.",
        };

        static void DifficultySection()
        {
            Header("Difficulty");

            int tier = Difficulty.Tier();
            bool dangerous = tier >= 2;

            GUIStyle name = new GUIStyle(GUI.skin.label);
            name.fontStyle = FontStyle.Bold;
            name.normal.textColor = tier == 1 ? Color.white
                : tier == 0 ? new Color(0.48f, 0.85f, 0.48f)
                : DangerRed;
            GUILayout.Label("  " + Difficulty.Names[tier]
                + (tier == 1 ? "" : ModTag)
                + (tier == 3 ? "  " + Skull() : ""), name);

            GUILayout.BeginHorizontal();
            GUILayout.Space(10f);
            float raw = GUILayout.HorizontalSlider(tier, 0f, 3f, GUILayout.Width(260f));
            GUILayout.Space(6f);
            GUILayout.Label("Easy · Normal · Hard · Masochist", GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();

            int snapped = Mathf.Clamp(Mathf.RoundToInt(raw), 0, 3);
            if (snapped != tier)
            {
                Plugin.CfgDifficulty.Value = Difficulty.Names[snapped];
                Plugin.SaveCfg();
                Note("Difficulty: " + Difficulty.Names[snapped]
                    + (snapped == 1 ? " - the base game, nothing added."
                                    : " - applies from her next reply."));
            }

            GUIStyle blurb = new GUIStyle(GUI.skin.label);
            blurb.wordWrap = true;
            if (dangerous) blurb.normal.textColor = DangerRed;
            GUILayout.Label("  " + DiffBlurb[tier], blurb);

            if (dangerous)
                DangerNote("  Her sharper temper is real: the game's own anger thresholds still "
                    + "apply, so on this setting she is genuinely more dangerous, not just harder "
                    + "to charm.");

            // ---- the numeric layer (4.2) -------------------------------------
            // One line that always tells the truth about who owns the numbers
            // right now, because the dominance rule (custom beats difficulty,
            // even at 0%) is exactly the kind of thing a player will not guess.
            GUIStyle numline = new GUIStyle(GUI.skin.label);
            numline.fontSize = 11;
            numline.normal.textColor = new Color(0.62f, 0.66f, 0.72f);
            GUILayout.Label("  Trust arithmetic right now: " + Difficulty.NumericSummary(), numline);

            if (dangerous && !Difficulty.CustomOn)
                PlainNote("  On this tier the numbers change too, not just her judgement: Hard "
                    + "lands gains at 75% strength and losses at 150%; Masochist gains at 50% and "
                    + "losses at 200% - so Masochist turns a -2 into -4 and a +5 into +2. Results "
                    + "round down to whole numbers, except anything between 0 and 1 still counts "
                    + "as 1: a real action always registers at least a point.");

            GUILayout.Space(8f);
            Bool(Plugin.CfgCustomFavorability, "Custom favorability speed (trust numbers only - the rest of difficulty stays)");

            if (Difficulty.CustomOn)
            {
                int pct = 0;
                try { pct = Plugin.CfgCustomFavorabilityPercent.Value; } catch (Exception) { }

                GUILayout.BeginHorizontal();
                GUILayout.Space(10f);
                float praw = GUILayout.HorizontalSlider(pct, -500f, 500f, GUILayout.Width(260f));
                GUILayout.Space(6f);

                GUIStyle fac = new GUIStyle(GUI.skin.label);
                fac.fontStyle = FontStyle.Bold;
                GUILayout.Label((pct > 0 ? "+" : "") + pct + "%  =  x"
                    + Difficulty.CustomFactor().ToString("0.##"), fac, GUILayout.ExpandWidth(false));

                GUILayout.Space(8f);
                if (GUILayout.Button("-5", GUILayout.Width(34f))) praw = pct - 5;
                if (GUILayout.Button("+5", GUILayout.Width(34f))) praw = pct + 5;
                if (GUILayout.Button("0%", GUILayout.Width(38f))) praw = 0;
                GUILayout.EndHorizontal();

                // Snapped to steps of 5: the range is a thousand wide and a
                // 260px slider cannot land on 137 anyway. The config file takes
                // exact values for anyone who wants them.
                int psnap = Mathf.Clamp(Mathf.RoundToInt(praw / 5f) * 5, -500, 500);
                if (psnap != pct)
                {
                    Plugin.CfgCustomFavorabilityPercent.Value = psnap;
                    Plugin.SaveCfg();
                }

                PlainNote("  This takes over ONE thing: how fast trust rises and falls. Everything "
                    + "else about your difficulty tier still applies in full - her judgement, her "
                    + "suspicion, her testing, her temper, all of it. Only the difficulty's trust "
                    + "gain/loss percentages are replaced by this slider, even at 0%, which means "
                    + "vanilla speed on any tier. Positive amplifies (+100% doubles every change, "
                    + "+500% is six times). Negative dampens by division and never flips a "
                    + "direction (-100% is half speed, -500% is one sixth). Both directions "
                    + "equally. The same round-down rule applies, with anything between 0 and 1 "
                    + "still counting as 1, so even -500% crawls rather than freezes. "
                    + "High-risk-high-reward impacts are never scaled by this.");
            }
        }
        static readonly Color DangerDim = new Color(1f, 0.60f, 0.60f, 1f);

        static string _skull;

        // IMGUI draws with the built-in font, which carries no emoji table, so an
        // unchecked 💀 comes out as a tofu box - which is the opposite of cool.
        // Ask the font what it actually has and take the best thing it will draw.
        static string Skull()
        {
            if (_skull != null) return _skull;

            _skull = "[X]";
            try
            {
                Font f = GUI.skin != null && GUI.skin.font != null ? GUI.skin.font : GUI.skin.label.font;
                if (f != null)
                {
                    if (f.HasCharacter('\uD83D') && f.HasCharacter('\uDC80')) _skull = "💀";
                    else if (f.HasCharacter('☠')) _skull = "☠";
                    else if (f.HasCharacter('†')) _skull = "†";
                }
            }
            catch (Exception) { }

            Plugin.Log.LogInfo("Overlay: danger marker glyph = \"" + _skull + "\"");
            return _skull;
        }

        static void DangerHeader(string text)
        {
            GUILayout.Space(10f);
            GUIStyle s = new GUIStyle(GUI.skin.label);
            s.fontStyle = FontStyle.Bold;
            s.normal.textColor = DangerRed;
            GUILayout.Label(Skull() + "  " + text, s);
        }

        // Marks a setting that adds something to how the game itself plays - a
        // behaviour the vanilla game does not have. It is for the player's benefit,
        // so they can see at a glance which switches touch the game and which are
        // only plumbing.
        //
        // It does NOT go on the mod's own machinery, however important: endpoint,
        // key, model, sampling, memory size, schema clamping, TTS routing and the
        // server-call blocks are all configuration, not new gameplay. Tagging those
        // dilutes the mark until it means nothing.
        //
        // Currently earned by exactly two: the murder toggle and its test phrase.
        // OOC mode deliberately does NOT carry it - see the note at its row. It is
        // a debug channel, and on any turn the tag is not typed it changes nothing
        // about who she is.
        const string ModTag = "   [MOD]";

        static void DangerBool(ConfigEntry<bool> cfg, string label)
        {
            DangerBool(cfg, label, "she can end the run on her own judgement.");
        }

        static void DangerBool(ConfigEntry<bool> cfg, string label, string armedText)
        {
            if (cfg == null) return;

            GUIStyle s = new GUIStyle(GUI.skin.toggle);
            s.fontStyle = FontStyle.Bold;
            s.normal.textColor = DangerRed;
            s.hover.textColor = DangerRed;
            s.active.textColor = DangerRed;
            s.focused.textColor = DangerRed;
            s.onNormal.textColor = DangerRed;
            s.onHover.textColor = DangerRed;
            s.onActive.textColor = DangerRed;
            s.onFocused.textColor = DangerRed;

            bool before = cfg.Value;
            bool after = GUILayout.Toggle(before, "  " + label + "   " + Skull(), s);
            if (after != before)
            {
                cfg.Value = after;
                Plugin.SaveCfg();
                Note(label.Replace(ModTag, string.Empty) + (after ? ": ON - " + armedText : ": off"));
            }

            if (after)
            {
                GUIStyle armed = new GUIStyle(GUI.skin.label);
                armed.fontStyle = FontStyle.Bold;
                armed.wordWrap = true;
                armed.normal.textColor = DangerRed;
                GUILayout.Label("     ARMED " + Skull() + "  " + armedText, armed);
            }
        }

        // Inline explanatory text, drawn every frame. Distinct from Note(), which
        // sets the transient status line at the bottom and would be overwritten
        // sixty times a second if called from the draw loop.
        static void PlainNote(string text)
        {
            GUIStyle s = new GUIStyle(GUI.skin.label);
            s.wordWrap = true;
            GUILayout.Label(text, s);
        }

#if CANALPA
        // What is and is not on the table right now, per action, in this level.
        //
        // The states worth telling apart since 4.2.2: armed-and-hers-alone (with
        // her stated bar and the live trust beside it, as information), already
        // done, and for the ending only: dormant versus ready. Without this,
        // "she never offers anything" has no diagnosable cause.
        static void CanalpaReadinessRow()
        {
            GUIStyle val = new GUIStyle(GUI.skin.label);
            val.wordWrap = true;
            val.fontStyle = FontStyle.Bold;

            List<string> rows = Canalpa.Status();
            if (rows.Count == 0)
            {
                val.normal.textColor = DangerDim;

                // "in this level" was actively misleading in the menu and the hub,
                // where there IS no level - it read as the feature being broken
                // rather than as standing somewhere with nothing to unlock. The
                // three cases are genuinely different and each has a different
                // answer, so each gets its own sentence.
                int lv = Canalpa.CurrentLevel;
                if (lv < 0)
                    GUILayout.Label("  Nothing to show from the main menu - load a save and "
                        + "her options for that level appear here.", val);
                else if (lv == 0)
                    GUILayout.Label("  You are in the Atrium. Her unlockable things live inside the "
                        + "levels themselves, so nothing is listed here - this is normal, not a "
                        + "fault. The keep-you-forever option is the only one that works anywhere, "
                        + "and it stays hidden until you ask for it.", val);
                else
                    GUILayout.Label("  Nothing of hers is unlockable in this level, or every option "
                        + "for it is switched off above.", val);
                return;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                string row = rows[i];
                if (row.EndsWith("her choice now")
                    || row.IndexOf("hers alone to decide", StringComparison.Ordinal) >= 0)
                    val.normal.textColor = new Color(0.48f, 0.85f, 0.52f);
                else
                    val.normal.textColor = DangerDim;

                GUILayout.Label("  " + row, val);
            }

            if (Consent.Pending)
            {
                val.normal.textColor = new Color(0.90f, 0.55f, 0.40f);
                GUILayout.Label("  You have asked her, in your own words, for the ending you do not "
                    + "come back from. "
                    + (Consent.ReadyForConfirmation
                        ? "She can act on it if you name it again with a clear yes - and if she agrees."
                        : "Too soon - she has to explain what it means first.")
                    + " Anything hesitant withdraws it.", val);
            }
        }
#endif

        // Outside the CANALPA guard on purpose - hard difficulty ships publicly.
        //
        // What this answers is "did that big moment actually land as a big moment",
        // which is otherwise invisible: a downgraded claim and an honestly ordinary
        // turn look identical from the chair, so a cooled-down once-ever would read
        // as the toggle being broken. The per-tier, per-direction waits are shown
        // for the same reason.
        static void FeelingsReadinessRow()
        {
            GUIStyle val = new GUIStyle(GUI.skin.label);
            val.wordWrap = true;
            val.fontStyle = FontStyle.Bold;

            int last = Feelings.LastApplied;
            string bd = Feelings.LastBreakdown;
            if (last != 0)
            {
                val.normal.textColor = new Color(0.90f, 0.55f, 0.40f);
                GUILayout.Label("  Last trust change: "
                    + (bd ?? ((last > 0 ? "+" : "") + last)) + ".", val);
            }
            else if (bd != null)
            {
                val.normal.textColor = DangerDim;
                GUILayout.Label("  Last trust change: " + bd + " - no significant impact, as most "
                    + "turns should be.", val);
            }
            else
            {
                val.normal.textColor = DangerDim;
                GUILayout.Label("  No trust change yet this level.", val);
            }

            List<string> waits = Feelings.TierStatus();
            for (int i = 0; i < waits.Count; i++)
            {
                string row = waits[i];
                // Green only when both directions are ready; anything cooling
                // or spent dims the row so the eye finds the open tiers.
                bool allReady = !row.Contains(": in ") && !row.Contains("spent");
                val.normal.textColor = allReady
                    ? new Color(0.48f, 0.85f, 0.52f)
                    : DangerDim;
                GUILayout.Label("  " + row, val);
            }
        }

        static void DangerNote(string text)
        {
            GUIStyle s = new GUIStyle(GUI.skin.label);
            s.wordWrap = true;
            s.normal.textColor = DangerDim;
            GUILayout.Label(text, s);
        }

        static void Label(string text)
        {
            GUILayout.Label(text, GUILayout.Width(210f));
        }

        // A voice per character. The stock game gives each of them their own voice
        // through AzureVoiceManager; routing cloud TTS through one VoiceId lost
        // that, so every character spoke identically.
        //
        // Only the voice is per-character - base URL, key and model stay global,
        // because those are account settings and a second key is just a second
        // bill. An empty row inherits the general voice above, which keeps the
        // fallback something the user has definitely configured.
        static void PerCharacterVoices(bool enabled = true)
        {
            GUILayout.Space(8f);
            Header("Voice per character");

            int set = Voices.Configured();
            PlainNote("  Leave a row empty and she uses the general voice above. "
                + (set == 0
                    ? "Nothing set yet, so everyone shares one voice."
                    : set + " of " + Voices.Names.Length + " have their own."));

            for (int i = 0; i < Voices.Names.Length; i++)
                TextRow(VoicePrefix + Voices.Names[i], Voices.Labels[i], 0f, enabled);

            PlainNote("  A voice set here follows that character wherever she appears.");
        }

        // The developer cheats. These ship, and everything from here down is
        // guarded at runtime instead of at compile time: nothing in this section
        // runs unless Cheats/Enabled is ticked, which is off by default and stays
        // off until someone goes looking for it.
        static readonly Color CheatBlue = new Color(0.45f, 0.75f, 1f, 1f);

        static string _trustBuf = "";
        static string _msgBuf = "";

        // The cheats header doubles as the master switch's home: the section is
        // hidden entirely until the config toggle is on, so a normal player never
        // sees a "cheats" button in a panel they opened to paste an API key.
        static void CheatsHeader()
        {
            GUILayout.Space(12f);

            if (!Plugin.CfgCheats.Value)
            {
                GUIStyle off = new GUIStyle(GUI.skin.toggle);
                off.normal.textColor = CheatBlue;
                off.onNormal.textColor = CheatBlue;

                bool on = GUILayout.Toggle(false, "  Enable developer cheats", off);
                if (on)
                {
                    Plugin.CfgCheats.Value = true;
                    Plugin.CfgShowCheats.Value = true;
                    Plugin.SaveCfg();
                    Note("Developer cheats: ON - the section is below.");
                }

                GUIStyle sub = new GUIStyle(GUI.skin.label);
                sub.fontSize = 11;
                sub.wordWrap = true;
                sub.normal.textColor = new Color(0.62f, 0.66f, 0.72f);
                GUILayout.Label("  Read and set trust live, so a trust-gated behaviour can be "
                    + "tested without playing up to it. Writes to your save state.", sub);
                return;
            }

            string arrow = Plugin.CfgShowCheats.Value ? "v" : ">";
            GUIStyle s = new GUIStyle(GUI.skin.button);
            s.fontStyle = FontStyle.Bold;
            s.alignment = TextAnchor.MiddleLeft;
            s.padding = new RectOffset(10, 10, 6, 6);

            if (GUILayout.Button(arrow + "  Developer cheats", s))
            {
                Plugin.CfgShowCheats.Value = !Plugin.CfgShowCheats.Value;
                Plugin.SaveCfg();
            }
        }

        // The cheats readouts, sampled a few times a second instead of read live.
        //
        // OnGUI runs several times per frame (once per IMGUI event), and every
        // draw of this section was calling Trust(), Indicator(), Messages(),
        // MessageGate() and Invincible() fresh - each of which walks the scene
        // (FindObjectOfType) or scans every loaded assembly (TypeByName, up to
        // four times per call in ChatHost). Dozens of scene walks and assembly
        // scans per frame is exactly the FPS cliff that was reported the moment
        // this tab opened. None of these values can change faster than a
        // conversation beat, so 4Hz is indistinguishable from live for a human
        // and removes the whole cost. Same pattern as Cheats.ItemsReady.
        //
        // Any Apply* action invalidates the snapshot so the row the user just
        // changed updates on the very next draw, not a quarter second later.
        static float _cheatsSnapAt = -99f;
        static float? _snapTrust;
        static string _snapIndicator;
        static int? _snapMsgs;
        static int? _snapGate;

        static void RefreshCheatsSnapshot(bool force)
        {
            float now = Time.realtimeSinceStartup;
            if (!force && now - _cheatsSnapAt < 0.25f) return;
            _cheatsSnapAt = now;

            _snapTrust = Cheats.Trust();
            _snapIndicator = Cheats.Indicator();
            _snapMsgs = Cheats.Messages();
            _snapGate = Cheats.MessageGate();
        }

        static void CheatsSection()
        {
            RefreshCheatsSnapshot(false);

            GUIStyle head = new GUIStyle(GUI.skin.label);
            head.fontStyle = FontStyle.Bold;
            head.normal.textColor = CheatBlue;

            // ---- trust -------------------------------------------------------
            GUILayout.Space(8f);
            GUILayout.Label("Trust", head);

            float? trust = _snapTrust;
            if (!trust.HasValue)
            {
                PlainNote("  No character in this scene, so there is no trust to read. "
                    + "Open the panel while talking to her.");
            }
            else
            {
                string indicator = _snapIndicator;
                GUIStyle val = new GUIStyle(GUI.skin.label);
                val.fontStyle = FontStyle.Bold;
                GUILayout.Label("  Current: " + trust.Value.ToString("0.#")
                    + (string.IsNullOrEmpty(indicator) ? "" : "   (" + indicator + ")"), val);

                int step = Plugin.CfgCheatsTrustStep.Value;

                GUILayout.BeginHorizontal();
                GUILayout.Space(8f);
                if (GUILayout.Button("-" + step, GUILayout.Width(52f)))
                    ApplyTrust(trust.Value - step);
                if (GUILayout.Button("+" + step, GUILayout.Width(52f)))
                    ApplyTrust(trust.Value + step);

                GUILayout.Space(10f);
                if (GUILayout.Button("Full trust (41)", GUILayout.Width(112f))) ApplyTrust(41f);
                if (GUILayout.Button("Max (100)", GUILayout.Width(84f))) ApplyTrust(100f);
                if (GUILayout.Button("Zero", GUILayout.Width(60f))) ApplyTrust(0f);
                // The game's real floor is -10 (trustLevelCap_BottomLine), and two
                // things live only down there: the Distrust indicator tier, and the
                // L3 zap ending's automatic trigger (trust <= -10). With Zero as
                // the lowest visible preset, a player was descending in steps,
                // pressed Zero because nothing suggested negatives exist, and the
                // ending stayed unreachable - the typed field takes negatives, but
                // an affordance nobody can see is not an affordance.
                if (GUILayout.Button("Bottom (-10)", GUILayout.Width(100f))) ApplyTrust(-10f);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Space(8f);
                GUILayout.Label("Set exactly:", GUILayout.Width(84f));
                _trustBuf = GUILayout.TextField(_trustBuf ?? "", GUILayout.Width(70f));
                if (GUILayout.Button("Apply", GUILayout.Width(64f)))
                {
                    float t;
                    if (float.TryParse((_trustBuf ?? "").Trim(), out t)) ApplyTrust(t);
                    else Note("Trust: \"" + _trustBuf + "\" is not a number.");
                }
                GUILayout.EndHorizontal();

                // Said plainly because it explains an otherwise baffling result:
                // set trust, walk through a door, and it is back where it was.
                PlainNote("  Trust is a runtime value and resets when a level loads, so set it in the "
                    + "scene you want to test. It goes through the game's own trust update, so the "
                    + "on-screen indicator, her reaction and any achievement all fire normally.");
            }

            // ---- message count -----------------------------------------------
            GUILayout.Space(10f);
            GUILayout.Label("Messages in this conversation", head);

            int? msgs = _snapMsgs;
            if (!msgs.HasValue)
            {
                PlainNote("  No level behaviour in this scene, so there is no message count to read. "
                    + "Open the panel during a conversation.");
            }
            else
            {
                int? gate = _snapGate;
                GUIStyle mval = new GUIStyle(GUI.skin.label);
                mval.fontStyle = FontStyle.Bold;
                mval.normal.textColor = gate.HasValue && msgs.Value <= gate.Value
                    ? new Color(0.90f, 0.65f, 0.35f)
                    : new Color(0.48f, 0.85f, 0.48f);
                GUILayout.Label("  Current: " + msgs.Value
                    + (gate.HasValue ? "   (gate is " + gate.Value + ")" : ""), mval);

                GUILayout.BeginHorizontal();
                GUILayout.Space(8f);
                if (GUILayout.Button("-1", GUILayout.Width(52f))) ApplyMessages(msgs.Value - 1);
                if (GUILayout.Button("+1", GUILayout.Width(52f))) ApplyMessages(msgs.Value + 1);

                GUILayout.Space(10f);
                // One past the gate is the interesting value: it is the first
                // count at which the key can actually change hands.
                int past = gate.HasValue ? gate.Value + 1 : 11;
                if (GUILayout.Button("Past gate (" + past + ")", GUILayout.Width(126f)))
                    ApplyMessages(past);
                if (GUILayout.Button("Zero", GUILayout.Width(60f))) ApplyMessages(0);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Space(8f);
                GUILayout.Label("Set exactly:", GUILayout.Width(84f));
                _msgBuf = GUILayout.TextField(_msgBuf ?? "", GUILayout.Width(70f));
                if (GUILayout.Button("Apply", GUILayout.Width(64f)))
                {
                    int m;
                    if (int.TryParse((_msgBuf ?? "").Trim(), out m)) ApplyMessages(m);
                    else Note("Messages: \"" + _msgBuf + "\" is not a whole number.");
                }
                GUILayout.EndHorizontal();

                PlainNote("  This is the front door and apartment key gate, and it counts messages "
                    + "rather than trust. Below it the engine deletes the hand-over after she has "
                    + "already agreed, so she reports success and nothing appears. Set it past the "
                    + "gate to test the working path, or to zero to see the refusal.");
            }

            // The gems editor was removed in 4.2.0: gems are server-backed and
            // the hub reloads them from the account on every load, so the edit
            // was silently wiped. Unfixable from this side.

            // ---- items -------------------------------------------------------
            GUILayout.Space(10f);
            GUILayout.Label("Give yourself an item", head);
            PlainNote("  Type a name and press Give. Matching ignores case and spaces, so \"teddy bear\" "
                + "finds TeddyBear. An unrecognised name is still granted, as a placeholder item.");

            bool itemsReady = Cheats.ItemsReady;

            GUILayout.BeginHorizontal();
            GUILayout.Space(20f);

            bool wasItemEnabled = GUI.enabled;
            Color wasItemColor = GUI.color;
            if (!itemsReady)
            {
                GUI.enabled = false;
                GUI.color = new Color(wasItemColor.r, wasItemColor.g, wasItemColor.b, 0.45f);
            }

            _itemName = GUILayout.TextField(_itemName ?? string.Empty, GUILayout.Width(260f));
            if (GUILayout.Button("Give", GUILayout.Width(90f))) GiveTypedItem();

            GUI.enabled = wasItemEnabled;
            GUI.color = wasItemColor;

            if (!itemsReady)
            {
                GUIStyle why = new GUIStyle(GUI.skin.label);
                why.normal.textColor = new Color(0.62f, 0.62f, 0.66f);
                GUILayout.Label("load a level first", why);
            }

            GUILayout.EndHorizontal();

            if (itemsReady)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(20f);
                if (GUILayout.Button(_itemNamesShown ? "Hide item names" : "Show item names on this level",
                        GUILayout.Width(240f)))
                {
                    _itemNamesShown = !_itemNamesShown;
                    if (_itemNamesShown) _itemNames = Cheats.ItemNames();
                }
                GUILayout.EndHorizontal();

                if (_itemNamesShown)
                {
                    if (_itemNames == null || _itemNames.Length == 0)
                        PlainNote("    No item list on this level.");
                    else
                        PlainNote("    " + string.Join(", ", _itemNames));
                }
            }

            // ---- invincibility -----------------------------------------------
            GUILayout.Space(10f);
            GUILayout.Label("Invincibility", head);

            bool invinc = Cheats.InvincibleOn;
            bool afterInvinc = GUILayout.Toggle(invinc, "  Invincible (nothing can take your health)");
            if (afterInvinc != invinc)
            {
                Cheats.SetInvincible(afterInvinc);
                Note("Invincibility: " + (afterInvinc ? "ON" : "off") + ".");
            }

            PlainNote("  Blocks every drop in health at its source, so the drowning and monster deaths "
                + "go with it. Switches itself off when you change level, on purpose - an invincibility "
                + "you have forgotten about reads as a broken game.");

            // ---- panel display ------------------------------------------------
            // Sits in this tab rather than under Setup because it is the same kind
            // of thing as the rest of the tab: it shows numbers the game does not
            // show you. Phrased as "hide" so the toggle is off in its default
            // state, matching every other switch in here.
            GUILayout.Space(10f);
            GUILayout.Label("Panel display", head);

            bool hideStrip = Plugin.CfgShowStatusStrip != null && !Plugin.CfgShowStatusStrip.Value;
            bool afterHide = GUILayout.Toggle(hideStrip, "  Hide the trust / last turn / patience strip");
            if (afterHide != hideStrip && Plugin.CfgShowStatusStrip != null)
            {
                Plugin.CfgShowStatusStrip.Value = !afterHide;
                Plugin.SaveCfg();
                Note(afterHide
                    ? "Status strip hidden. The panel opens straight onto the settings."
                    : "Status strip shown.");
            }

            PlainNote("  Only changes what the panel draws, never what the mod does. Worth hiding on a "
                + "first playthrough: the strip tells you how she felt about what you just said, which "
                + "is something the game wants you to read off her instead.");
        }

        static string _itemName = string.Empty;
        static string[] _itemNames;
        static bool _itemNamesShown;

        static void GiveTypedItem()
        {
            string typed = (_itemName ?? string.Empty).Trim();
            if (typed.Length == 0) { Note("Type an item name first."); return; }

            string granted = Cheats.GiveItem(typed);
            if (granted == null)
            {
                Note("Could not give \"" + typed + "\" - see the console for why.");
                return;
            }

            if (granted == typed)
                Note("Gave you " + granted + ".");
            else
                Note("Gave you " + granted + " (matched from \"" + typed + "\").");
        }

        static void ApplyTrust(float target)
        {
            // Floor at the game's OWN bottom line, not at zero. The old 0 floor
            // looked harmless and quietly made two real things unreachable: the
            // "Distrust" indicator tier, which only exists below zero, and the
            // deterministic path to the L3 zap ending, whose trigger branch is
            // `trust <= -10` - a player following the recipe set -10, our input
            // silently became 0, and the ending never fired. The game itself has
            // no floor at all in UpdateTrustLevel; -10 is trustLevelCap_BottomLine,
            // below which nothing further changes.
            if (target < -10f) target = -10f;
            if (Cheats.SetTrust(target))
            {
                RefreshCheatsSnapshot(true);
                float? now = Cheats.Trust();
                Note("Trust is now " + (now.HasValue ? now.Value.ToString("0.#") : "?")
                    + ". Whole numbers only - the game's own trust step is an integer.");
            }
            else Note("Trust could not be changed - see the console for why.");
        }

        static void ApplyMessages(int target)
        {
            if (target < 0) target = 0;
            if (Cheats.SetMessages(target))
            {
                RefreshCheatsSnapshot(true);
                int? now = Cheats.Messages();
                Note("Message count is now " + (now.HasValue ? now.Value.ToString() : "?") + ".");
            }
            else Note("The message count could not be changed - see the console for why.");
        }



        static string TextFieldWithPlaceholder(string text, string placeholder, float width = 340f, bool enabled = true)
        {
            if (text == null) text = string.Empty;
            string next = GUILayout.TextField(text, GUILayout.Width(width), GUILayout.Height(22f));

            if (string.IsNullOrEmpty(next))
            {
                Rect r = GUILayoutUtility.GetLastRect();
                if (r.width > 2f && r.height > 2f)
                {
                    GUIStyle phStyle = new GUIStyle(GUI.skin.label);
                    phStyle.normal.textColor = new Color(0.58f, 0.62f, 0.68f, 0.45f);
                    phStyle.fontStyle = FontStyle.Italic;
                    phStyle.fontSize = GUI.skin.textField.fontSize;
                    phStyle.padding = new RectOffset(6, 0, 2, 0);
                    GUI.Label(r, placeholder, phStyle);
                }
            }
            return next;
        }

        static string PasswordFieldWithPlaceholder(string text, string placeholder, float width = 340f, bool enabled = true)
        {
            if (text == null) text = string.Empty;
            string next;
            if (_showKeys)
                next = GUILayout.TextField(text, GUILayout.Width(width), GUILayout.Height(22f));
            else
                next = GUILayout.PasswordField(text, '*', GUILayout.Width(width), GUILayout.Height(22f));

            if (string.IsNullOrEmpty(next))
            {
                Rect r = GUILayoutUtility.GetLastRect();
                if (r.width > 2f && r.height > 2f)
                {
                    GUIStyle phStyle = new GUIStyle(GUI.skin.label);
                    phStyle.normal.textColor = new Color(0.58f, 0.62f, 0.68f, 0.45f);
                    phStyle.fontStyle = FontStyle.Italic;
                    phStyle.fontSize = GUI.skin.textField.fontSize;
                    phStyle.padding = new RectOffset(6, 0, 2, 0);
                    GUI.Label(r, placeholder, phStyle);
                }
            }
            return next;
        }

        static void ModelRowWithDropdown()
        {
            GUILayout.BeginHorizontal();
            Label("Model");
            string curModel = Get("Model");
            string nextModel = TextFieldWithPlaceholder(curModel, "e.g. google/gemini-3.7-flash", 260f, true);
            _buf["Model"] = nextModel;
            DefaultTag("Model");

            string fetchLabel = _fetchingModels ? "Fetching..." : "↻ Fetch Models";
            if (GUILayout.Button(fetchLabel, GUILayout.Width(115f), GUILayout.Height(22f)))
            {
                _modelDropdownOpen = true;
                FetchModelsAsync();
            }

            string dropLabel = _modelDropdownOpen ? "▲ Close" : (_modelList.Count > 0 ? "▼ Models (" + _modelList.Count + ")" : "▼ Models");
            if (GUILayout.Button(dropLabel, GUILayout.Width(95f), GUILayout.Height(22f)))
            {
                _modelDropdownOpen = !_modelDropdownOpen;
            }
            GUILayout.EndHorizontal();

            if (_modelDropdownOpen)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Search:", GUILayout.Width(50f));
                _modelSearchQuery = TextFieldWithPlaceholder(_modelSearchQuery, "Search...", 320f, true);
                if (GUILayout.Button("↻ Refresh", GUILayout.Width(80f), GUILayout.Height(22f)))
                {
                    FetchModelsAsync();
                }
                if (GUILayout.Button("✕", GUILayout.Width(28f), GUILayout.Height(22f)))
                {
                    _modelDropdownOpen = false;
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                if (_fetchingModels)
                {
                    GUIStyle waitStyle = new GUIStyle(GUI.skin.label);
                    waitStyle.normal.textColor = new Color(0.7f, 0.85f, 1f);
                    GUILayout.Label("  Fetching models from endpoint...", waitStyle);
                }
                else if (!string.IsNullOrEmpty(_fetchModelError) && _modelList.Count == 0)
                {
                    GUIStyle errStyle = new GUIStyle(GUI.skin.label);
                    errStyle.normal.textColor = DangerRed;
                    GUILayout.Label("  " + _fetchModelError, errStyle);
                }
                else if (_modelList.Count > 0)
                {
                    _modelScroll = GUILayout.BeginScrollView(_modelScroll, GUILayout.Height(180f));
                    int matched = 0;
                    for (int mIdx = 0; mIdx < _modelList.Count; mIdx++)
                    {
                        string mName = _modelList[mIdx];
                        if (!string.IsNullOrEmpty(_modelSearchQuery) && mName.IndexOf(_modelSearchQuery, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        matched++;
                        bool isSelected = string.Equals(mName, curModel, StringComparison.OrdinalIgnoreCase);
                        GUIStyle mBtnStyle = new GUIStyle(GUI.skin.button);
                        mBtnStyle.alignment = TextAnchor.MiddleLeft;
                        if (isSelected)
                        {
                            mBtnStyle.fontStyle = FontStyle.Bold;
                            mBtnStyle.normal.textColor = Color.green;
                        }

                        if (GUILayout.Button((isSelected ? "✔ " : "   ") + mName, mBtnStyle, GUILayout.Height(22f)))
                        {
                            _buf["Model"] = mName;
                            Plugin.CfgModel.Value = mName;
                            Plugin.SaveCfg();
                            Note("Selected model: " + mName);
                            _modelDropdownOpen = false;
                            if (IsOpenRouterBaseUrl())
                            {
                                _providerList.Clear();
                                FetchProvidersAsync();
                            }
                        }
                    }
                    if (matched == 0)
                    {
                        GUIStyle noneStyle = new GUIStyle(GUI.skin.label);
                        noneStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
                        GUILayout.Label("  No models matching '" + _modelSearchQuery + "'", noneStyle);
                    }
                    GUILayout.EndScrollView();
                }
                GUILayout.EndVertical();
            }
        }

        static void OpenRouterProviderRow()
        {
            bool isOpenRouter = IsOpenRouterBaseUrl();

            GUILayout.BeginHorizontal();
            bool wasEnabled = GUI.enabled;
            Color wasColor = GUI.color;
            if (!isOpenRouter)
            {
                GUI.enabled = false;
                GUI.color = new Color(wasColor.r, wasColor.g, wasColor.b, 0.45f);
            }

            Label("OpenRouter provider");
            string curP = Get("OpenRouterProvider");
            string nextP = TextFieldWithPlaceholder(curP, "auto / Anthropic / Together / etc.", 340f, isOpenRouter);
            if (isOpenRouter) _buf["OpenRouterProvider"] = nextP;

            string provBtnLabel = _fetchingProviders ? "Fetching..." : "↻ Fetch Providers";
            if (GUILayout.Button(provBtnLabel, GUILayout.Width(130f), GUILayout.Height(22f)))
            {
                FetchProvidersAsync();
            }

            GUI.enabled = wasEnabled;
            GUI.color = wasColor;
            GUILayout.EndHorizontal();

            Bool(Plugin.CfgOpenRouterAllowFallback, "OpenRouter allow provider fallbacks", isOpenRouter);

            if (!isOpenRouter)
            {
                PlainNote("  (OpenRouter provider routing is only active when Base URL is set to OpenRouter)");
                return;
            }

            if (_fetchingProviders)
            {
                GUIStyle waitStyle = new GUIStyle(GUI.skin.label);
                waitStyle.normal.textColor = new Color(0.7f, 0.85f, 1f);
                GUILayout.Label("  Fetching supported providers for " + (_lastFetchedProviderModel ?? "model") + "...", waitStyle);
            }
            else if (!string.IsNullOrEmpty(_fetchProviderError))
            {
                GUIStyle errStyle = new GUIStyle(GUI.skin.label);
                errStyle.normal.textColor = new Color(1f, 0.6f, 0.6f);
                GUILayout.Label("  " + _fetchProviderError, errStyle);
            }

            GUILayout.BeginHorizontal();
            GUILayout.Space(214f);
            GUIStyle badgeStyle = new GUIStyle(GUI.skin.button);
            badgeStyle.fontSize = 11;
            string curVal = Get("OpenRouterProvider");

            List<string> listToDisplay = new List<string>();
            listToDisplay.Add("auto");

            if (_providerList != null && _providerList.Count > 0)
            {
                for (int i = 0; i < _providerList.Count; i++)
                {
                    if (!listToDisplay.Contains(_providerList[i]))
                        listToDisplay.Add(_providerList[i]);
                }
            }
            else
            {
                string[] defaults = { "Anthropic", "Together", "DeepInfra", "Hyperbolic", "Groq", "Fireworks" };
                for (int i = 0; i < defaults.Length; i++) listToDisplay.Add(defaults[i]);
            }

            int buttonsInRow = 0;
            for (int q = 0; q < listToDisplay.Count; q++)
            {
                string pName = listToDisplay[q];
                bool isCur = string.Equals(curVal, pName, StringComparison.OrdinalIgnoreCase) || (string.IsNullOrEmpty(curVal) && pName == "auto");
                if (isCur) badgeStyle.fontStyle = FontStyle.Bold;
                else badgeStyle.fontStyle = FontStyle.Normal;

                if (GUILayout.Button(pName, badgeStyle, GUILayout.Height(20f)))
                {
                    _buf["OpenRouterProvider"] = pName;
                    Plugin.CfgOpenRouterProvider.Value = pName;
                    Plugin.SaveCfg();
                    Note("OpenRouter provider set to: " + pName);
                }
                buttonsInRow++;
                if (buttonsInRow >= 7 && q < listToDisplay.Count - 1)
                {
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(214f);
                    buttonsInRow = 0;
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        static void TextRow(string key, string label, float unused)
        {
            TextRow(key, label, unused, true);
        }

        // enabled=false greys the field and refuses edits. GUI.enabled has to go
        // back to true afterwards or every later row in the panel inherits the
        // disabled state, since it is one global flag rather than a per-control one.
        static void TextRow(string key, string label, float unused, bool enabled)
        {
            GUILayout.BeginHorizontal();

            bool wasEnabled = GUI.enabled;
            Color wasColor = GUI.color;
            if (!enabled)
            {
                GUI.enabled = false;
                GUI.color = new Color(wasColor.r, wasColor.g, wasColor.b, 0.45f);
            }

            Label(label);

            string cur = Get(key);
            string ph = "";
            if (key == "BaseUrl") ph = "https://openrouter.ai/api/v1";
            else if (key == "GameVoiceRegion") ph = "eastus / westus2 / etc.";
            else if (key == "GrokBaseUrl") ph = "https://api.x.ai/v1";
            else if (key == "GrokLanguage") ph = "en";

            string next = TextFieldWithPlaceholder(cur, ph, 340f, enabled);
            // Committing the buffer only while enabled means a disabled field can
            // never write back a value, whatever the skin does with the keyboard.
            if (enabled) _buf[key] = next;

            DefaultTag(key);

            GUI.enabled = wasEnabled;
            GUI.color = wasColor;

            GUILayout.EndHorizontal();
        }

        // Keys are masked by default so the panel is safe to have on screen while
        // recording or streaming.
        static void KeyRow(string key, string label, bool enabled = true)
        {
            GUILayout.BeginHorizontal();
            bool wasEnabled = GUI.enabled;
            Color wasColor = GUI.color;
            if (!enabled)
            {
                GUI.enabled = false;
                GUI.color = new Color(wasColor.r, wasColor.g, wasColor.b, 0.45f);
            }

            Label(label);
            string cur = Get(key);
            string ph = key.Contains("GameVoice") ? "Enter Key" : "Enter Key...";
            string next = PasswordFieldWithPlaceholder(cur, ph, 340f, enabled);
            if (enabled) _buf[key] = next;
            _showKeys = GUILayout.Toggle(_showKeys, "show", GUILayout.Width(56f));

            GUI.enabled = wasEnabled;
            GUI.color = wasColor;
            GUILayout.EndHorizontal();
        }

        static void IntRow(string key, string label, bool enabled = true)
        {
            GUILayout.BeginHorizontal();
            bool wasEnabled = GUI.enabled;
            Color wasColor = GUI.color;
            if (!enabled)
            {
                GUI.enabled = false;
                GUI.color = new Color(wasColor.r, wasColor.g, wasColor.b, 0.45f);
            }

            Label(label);
            string cur = Get(key);
            string next = GUILayout.TextField(cur, GUILayout.Width(140f));
            if (enabled) _buf[key] = next;
            DefaultTag(key);
            GUILayout.FlexibleSpace();

            GUI.enabled = wasEnabled;
            GUI.color = wasColor;
            GUILayout.EndHorizontal();
        }

        static void FloatRow(string key, string label, bool enabled = true)
        {
            IntRow(key, label, enabled);
        }

        static void Bool(ConfigEntry<bool> cfg, string label, bool enabled = true)
        {
            if (cfg == null) return;
            bool wasEnabled = GUI.enabled;
            Color wasColor = GUI.color;
            if (!enabled)
            {
                GUI.enabled = false;
                GUI.color = new Color(wasColor.r, wasColor.g, wasColor.b, 0.45f);
            }

            bool before = cfg.Value;
            bool after = GUILayout.Toggle(before, "  " + label + BoolDefaultSuffix(cfg));
            if (enabled && after != before)
            {
                cfg.Value = after;
                Plugin.SaveCfg();
                Note(label + (after ? ": on" : ": off"));
            }

            GUI.enabled = wasEnabled;
            GUI.color = wasColor;
        }

        static string Get(string key)
        {
            string v;
            return _buf.TryGetValue(key, out v) ? (v ?? string.Empty) : string.Empty;
        }

        // The per-character voice keys are appended rather than listed, so adding a
        // character to Voices.Names gives it a working panel row with no change
        // here. They carry a prefix because the panel addresses every editable
        // value by a single flat key, and a bare "Eddie" would collide the moment
        // anything else wants that name.
        internal const string VoicePrefix = "Voice.";

        static readonly string[] StringKeys = BuildStringKeys();

        static string[] BuildStringKeys()
        {
            string[] fixedKeys =
            {
                "BaseUrl", "ApiKey", "Model", "OpenRouterProvider", "TtsProvider", "GrokBaseUrl",
                "GrokApiKey", "TtsModel", "GrokVoiceId", "GrokLanguage", "TestKillPhrase",
                "OocTag", "TimeskipTag", "GameVoiceKey", "GameVoiceRegion"
            };

            string[] all = new string[fixedKeys.Length + Voices.Names.Length];
            Array.Copy(fixedKeys, all, fixedKeys.Length);
            for (int i = 0; i < Voices.Names.Length; i++)
                all[fixedKeys.Length + i] = VoicePrefix + Voices.Names[i];
            return all;
        }
        static readonly string[] IntKeys =
        {
            "MaxTokens", "ReplyWordLimit", "RetriesOnBadJson", "HistoryMaxTokens",
            "GrokSampleRate"
        };
        static readonly string[] FloatKeys = { "Temperature", "GrokSpeed", "TtsVolume" };

        static ConfigEntry<string> Str(string key)
        {
            // Resolved through Voices so the set of characters lives in one place.
            if (key != null && key.StartsWith(VoicePrefix, StringComparison.Ordinal))
                return Voices.Entry(key.Substring(VoicePrefix.Length));

            switch (key)
            {
                case "BaseUrl": return Plugin.CfgBaseUrl;
                case "ApiKey": return Plugin.CfgApiKey;
                case "Model": return Plugin.CfgModel;
                case "OpenRouterProvider": return Plugin.CfgOpenRouterProvider;
                case "TtsProvider": return Plugin.CfgTtsProvider;
                case "GrokBaseUrl": return Plugin.CfgGrokBaseUrl;
                case "GrokApiKey": return Plugin.CfgGrokApiKey;
                case "TtsModel": return Plugin.CfgTtsModel;
                case "GrokVoiceId": return Plugin.CfgGrokVoiceId;
                case "GrokLanguage": return Plugin.CfgGrokLanguage;
                case "TestKillPhrase": return Plugin.CfgTestKillPhrase;
                case "OocTag": return Plugin.CfgOocTag;
                case "TimeskipTag": return Plugin.CfgTimeskipTag;
                case "GameVoiceKey": return Plugin.CfgGameVoiceKey;
                case "GameVoiceRegion": return Plugin.CfgGameVoiceRegion;
            }
            return null;
        }

        static ConfigEntry<int> Num(string key)
        {
            switch (key)
            {
                case "MaxTokens": return Plugin.CfgMaxTokens;
                case "ReplyWordLimit": return Plugin.CfgReplyWordLimit;
                case "RetriesOnBadJson": return Plugin.CfgRetries;
                case "HistoryMaxTokens": return Plugin.CfgHistoryMaxTokens;
                case "GrokSampleRate": return Plugin.CfgGrokSampleRate;
            }
            return null;
        }

        static ConfigEntry<float> Real(string key)
        {
            switch (key)
            {
                case "Temperature": return Plugin.CfgTemperature;
                case "GrokSpeed": return Plugin.CfgGrokSpeed;
                case "TtsVolume": return Plugin.CfgTtsVolume;
            }
            return null;
        }

        // The shipped default for any editable key, read off the ConfigEntry rather
        // than from a table here. A second table would be one more thing to forget
        // to update, and it would drift silently: the panel would confidently print
        // a default that the config no longer uses.
        static ConfigEntryBase EntryFor(string key)
        {
            ConfigEntryBase e = Str(key);
            if (e != null) return e;
            e = Num(key);
            if (e != null) return e;
            return Real(key);
        }

        // Rendered for display, so an empty default reads as something a person can
        // recognise instead of as a missing label.
        static string DefaultText(string key)
        {
            try
            {
                ConfigEntryBase e = EntryFor(key);
                if (e == null || e.DefaultValue == null) return null;

                string d = e.DefaultValue.ToString();
                if (string.IsNullOrEmpty(d)) return "empty";
                return d;
            }
            catch (Exception) { return null; }
        }

        static bool IsDefault(string key)
        {
            try
            {
                ConfigEntryBase e = EntryFor(key);
                if (e == null || e.DefaultValue == null) return true;

                string cur = Get(key) ?? string.Empty;
                // Compared as text because the buffer is text: the row the person is
                // looking at holds their typing, not the parsed value, and a pending
                // edit should stop showing as "default".
                return cur.Trim() == e.DefaultValue.ToString();
            }
            catch (Exception) { return true; }
        }

        // Drawn greyed and small at the end of a row. Suppressed when the value is
        // already the default, because "default: 0.8" next to 0.8 is just noise.
        static void DefaultTag(string key)
        {
            string d = DefaultText(key);
            if (d == null || IsDefault(key)) return;

            if (d.Length > 28) d = d.Substring(0, 27) + "…";

            GUIStyle s = new GUIStyle(GUI.skin.label);
            s.fontSize = 11;
            s.normal.textColor = new Color(0.58f, 0.62f, 0.68f);
            GUILayout.Label("default: " + d, s, GUILayout.Width(150f));
        }

        // Same idea for a checkbox, where the default belongs in the sentence rather
        // than in a column: a bool row has no free horizontal space.
        static string BoolDefaultSuffix(ConfigEntry<bool> cfg)
        {
            try
            {
                if (cfg == null || cfg.DefaultValue == null) return string.Empty;
                bool d = (bool)cfg.DefaultValue;
                if (cfg.Value == d) return string.Empty;
                return "   (default: " + (d ? "on" : "off") + ")";
            }
            catch (Exception) { return string.Empty; }
        }

        static void LoadBuffers()
        {
            _buf.Clear();
            for (int i = 0; i < StringKeys.Length; i++)
            {
                ConfigEntry<string> c = Str(StringKeys[i]);
                _buf[StringKeys[i]] = c != null ? (c.Value ?? string.Empty) : string.Empty;
            }
            for (int i = 0; i < IntKeys.Length; i++)
            {
                ConfigEntry<int> c = Num(IntKeys[i]);
                _buf[IntKeys[i]] = c != null
                    ? c.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
            }
            for (int i = 0; i < FloatKeys.Length; i++)
            {
                ConfigEntry<float> c = Real(FloatKeys[i]);
                _buf[FloatKeys[i]] = c != null
                    ? c.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
            }
        }

        // A bad number leaves the old value in place and is named in the status
        // line, rather than being silently coerced to zero.
        static void Commit()
        {
            List<string> bad = new List<string>();

            for (int i = 0; i < StringKeys.Length; i++)
            {
                ConfigEntry<string> c = Str(StringKeys[i]);
                if (c != null) c.Value = Get(StringKeys[i]).Trim();
            }

            for (int i = 0; i < IntKeys.Length; i++)
            {
                ConfigEntry<int> c = Num(IntKeys[i]);
                int v;
                if (c == null) continue;
                if (int.TryParse(Get(IntKeys[i]).Trim(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out v)) c.Value = v;
                else bad.Add(IntKeys[i]);
            }

            for (int i = 0; i < FloatKeys.Length; i++)
            {
                ConfigEntry<float> c = Real(FloatKeys[i]);
                float v;
                if (c == null) continue;
                if (float.TryParse(Get(FloatKeys[i]).Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out v)) c.Value = v;
                else bad.Add(FloatKeys[i]);
            }

            Plugin.SaveCfg();
            LoadBuffers();

            try
            {
                ProfileManager.SaveProfile(ProfileManager.CurrentProfile);
            }
            catch (Exception) { }

            if (bad.Count == 0)
                Note("Saved. Takes effect on her next reply - no restart.");
            else
                Note("Saved, but these were not numbers and kept their old values: "
                    + string.Join(", ", bad.ToArray()));
        }

        public static void CommitChanges()
        {
            Commit();
        }

        public static void ReloadFromProfile()
        {
            LoadBuffers();
        }

        public static void PostNote(string text)
        {
            Note(text);
        }
    }
}
