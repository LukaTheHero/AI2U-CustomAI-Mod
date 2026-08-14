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
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AI2UCustomAI
{
    internal static class OverlayMenu
    {
        const int WindowId = 0x412055;

        public static bool IsOpen { get; private set; }

        static Rect _win = new Rect(0f, 0f, 720f, 640f);
        static Vector2 _scroll;
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

            GUILayout.Space(4f);
            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label("[MOD] marks a setting that changes how the game itself plays, rather than "
                + "only pointing the mod at a different endpoint or voice.", sub);
            GUILayout.Space(4f);

            Header("Endpoint");
            TextRow("BaseUrl", "Base URL", 0f);
            KeyRow("ApiKey", "API key");
            TextRow("Model", "Model", 0f);

            Header("Voice");
            Bool(Plugin.CfgForceLocalVoice, "Force local Overtone voice (needed with a custom endpoint)");

            // Unity stripped Overtone's synthesis methods from the Steam
            // assembly, so on that build the toggle above cannot produce sound
            // however it is set. Say so here rather than letting it look broken.
            if (!Platform.LocalVoiceAvailable)
                GUILayout.Label("  This build has no local voice engine - Unity stripped Overtone's "
                    + "synthesis code from it, so the toggle above cannot make sound. Use a cloud TTS "
                    + "provider below for her to have a voice.");

            Bool(Plugin.CfgGrokEnabled, "Use a cloud TTS provider instead");
            TextRow("TtsProvider", "Provider (xai / elevenlabs / openai)", 0f);
            TextRow("GrokBaseUrl", "TTS base URL", 0f);
            KeyRow("GrokApiKey", "TTS API key");
            TextRow("TtsModel", "TTS model", 0f);
            TextRow("GrokVoiceId", "Voice (used by anyone without her own below)", 0f);
            FloatRow("TtsVolume", "Volume");

            Header("Test");
            GUILayout.Label("  Both tests save first, so they check exactly what is in the boxes above. "
                + "The text test asks your endpoint for one word and fixes the base URL if a nearby "
                + "form is the one that answers. The voice test synthesises a line and plays it.");
            GUILayout.BeginHorizontal();
            bool hitText = GUILayout.Button(_busyText ? "Testing..." : "Test text", GUILayout.Height(26f));
            bool hitVoice = GUILayout.Button(_busyVoice ? "Testing..." : "Test voice", GUILayout.Height(26f));
            GUILayout.EndHorizontal();
            if (hitText && !_busyText) StartTest(false);
            if (hitVoice && !_busyVoice) StartTest(true);
            Result("Text", _resText, _resTextColor);
            Result("Voice", _resVoice, _resVoiceColor);

            // ---- Advanced ----------------------------------------------------
            //
            // Everything below is either off by default or already correct for
            // almost everyone, and it was drowning the three settings that
            // actually have to be filled in. Collapsed by default, and the count
            // in the label means someone who changed something months ago can
            // still find it without opening every section.
            AdvancedHeader();

            if (Plugin.CfgShowAdvanced.Value)
            {
#if CANALPA
                DangerHeader("Canalpa mode");
                DangerBool(Plugin.CfgCanalpaMode, "Canalpa mode" + ModTag,
                    "she is allowed to act on trust the base game never lets her act on.");
                if (Canalpa.Active)
                {
                    Bool(Plugin.CfgCanalpaSecretRoom, "She can open the secret room herself" + ModTag);
                    DangerNote("  In the base game that door only ever opens by you typing the code "
                        + "into the keypad - however close you get, she has no way to offer it. With "
                        + "this on she can decide to open it for you once she fully trusts you, above "
                        + "the game's own FullyTrust mark of 40. She is told it is her choice, not an "
                        + "instruction, and she is free to refuse.");
                    DangerNote("  The trust requirement is deliberately not adjustable: below it the "
                        + "game's own code answers an opening door by starting the final chase, so a "
                        + "lower threshold would not be a harder unlock, it would be a death.");
                    DangerNote("  Trust alone is not enough. Past the trust mark she first spends a "
                        + "while testing how you take the darker things about her - hypotheticals she "
                        + "can pass off as teasing. She needs " + Canalpa.ProbeTarget + " warm answers "
                        + "before the door becomes something she can offer at all.");
                    CanalpaReadinessRow();
                }
                else
                {
                    DangerNote("  Off, so the game plays exactly as shipped and nothing here is sent "
                        + "to the model. Everything inside is additionally gated on trust, so turning "
                        + "it on changes nothing until you have actually earned it.");
                }
#endif

                Header("Sampling");
                FloatRow("Temperature", "Temperature");
                IntRow("MaxTokens", "Max reply tokens");
                IntRow("RetriesOnBadJson", "Retries on bad JSON");
                Bool(Plugin.CfgHideReasoning, "Hide reasoning (reasoning.exclude)");
                Bool(Plugin.CfgJsonMode, "Force JSON mode (response_format)");
                Bool(Plugin.CfgClampValues, "Repair out-of-range values in her reply");

                Header("Memory");
                IntRow("HistoryMaxTokens", "Max memory tokens");
                GUILayout.Label("  How much conversation she keeps before the game drops her oldest lines. "
                    + "This overrides the game's own 3072 cap. It is a ceiling, not a reservation - cost "
                    + "only grows as the history fills. Keep it under your model's context window.");

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

                GUILayout.Space(8f);
                bool oocOn = Ooc.Active;
                DangerBool(Plugin.CfgOocEnabled,
                    // No [MOD] tag: the tag marks additions to the game's own fiction,
                    // like the murder toggle above. This is a debug channel - it changes
                    // nothing about who she is on any turn the tag is not typed.
                    "Out-of-character developer mode",
                    "any message with the tag below is answered as the model, not the character.");
                TextRow("OocTag", "OOC tag", 0f, oocOn);
                if (oocOn)
                    DangerNote("  Put " + Ooc.TagText + " anywhere in what you say and that one message is "
                        + "answered out of character: she drops the persona, answers as whichever model you "
                        + "have configured, tells you the literal truth about the game and her own limits, "
                        + "and carries the request out through the real fields instead of only agreeing in "
                        + "the text. Ask \"" + Ooc.TagText + " which actions can you take?\" and she reads "
                        + "back the exact list the mod sent, which is the quickest way to catch a feature "
                        + "the model was never told about. If something genuinely has no field she says so "
                        + "rather than faking it. Case is ignored, the tag stays in the message so she can "
                        + "see it, and the very next line without it is fully back in character.");
                else
                    DangerNote("  Switched off, so the tag is not matched and none of its instructions are "
                        + "sent - typing " + Ooc.TagText + " is just an ordinary sentence she reads in "
                        + "character, and it costs nothing in her context. Tick the box to arm it.");

                Header("Game server");
                Bool(Plugin.CfgBlockGameAi,
                    "Block the game's dialogue AI calls while the mod is on (play, fetchAsync)");
                Bool(Plugin.CfgBlockGameExtras,
                    "Also block summary / envision / memorize");
                GUILayout.Label("  Both are AI2U's own paid LLM calls. The first is fully replaced by this mod, "
                    + "so blocking it costs you nothing and costs the developers nothing. The second backs "
                    + "her recap and memory features - blocking it keeps their bill at zero but those "
                    + "features will error instead of running. Login, saves, the shop and metrics are never "
                    + "touched.");

                Header("Voice detail");
                TextRow("GrokLanguage", "Language", 0f);
                FloatRow("GrokSpeed", "Speed");
                IntRow("GrokSampleRate", "Sample rate");
                Bool(Plugin.CfgGrokNormalize, "Normalise loudness");
                Bool(Plugin.CfgTtsNormalize, "Normalise text before speaking");
                Bool(Plugin.CfgSpeakActions, "Read *actions* aloud too (off: speak only her words)");

                PerCharacterVoices();

                Header("Debug");
                Bool(Plugin.CfgLogPayloads, "Log request and reply payloads to the BepInEx console");
            }

            // ---- Developer cheats --------------------------------------------
            //
            // Its own collapsed section rather than a corner of Advanced, because
            // it is the only thing in the panel that writes to save state. Kept
            // last so nothing below it can be reached by accident.
#if CHEATS
            CheatsHeader();
            if (Plugin.CfgCheats.Value && Plugin.CfgShowCheats.Value) CheatsSection();
#endif

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
            if (GUILayout.Button("Close", GUILayout.Height(28f))) { Close(); GUILayout.EndHorizontal();
                GUILayout.EndArea(); return; }
            GUILayout.EndHorizontal();

            if (_status != null && Time.realtimeSinceStartup < _statusUntil)
                GUILayout.Label(_status);
            else
                GUILayout.Label("Toggles apply the moment you click them. Text and numbers apply on Save.");

            GUILayout.EndArea();
            GUI.DragWindow(new Rect(0f, 0f, _win.width, 40f));
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
        static void Header(string text)
        {
            GUILayout.Space(10f);
            GUIStyle s = new GUIStyle(GUI.skin.label);
            s.fontStyle = FontStyle.Bold;
            GUILayout.Label(text, s);
        }

        // The collapsed Advanced section's own header, which is a button.
        //
        // Carries a count of how many settings inside differ from their defaults.
        // Without it, collapsing is a trap: someone who changed a retry count
        // months ago has no way to know the section is hiding it, and "the mod is
        // behaving oddly" becomes unanswerable. The count makes the section
        // honest about concealing state.
        static void AdvancedHeader()
        {
            GUILayout.Space(12f);

            int changed = ChangedAdvancedCount();
            string arrow = Plugin.CfgShowAdvanced.Value ? "v" : ">";
            string label = arrow + "  Advanced settings";
            if (changed > 0)
                label += "   (" + changed + " changed from default)";

            GUIStyle s = new GUIStyle(GUI.skin.button);
            s.fontStyle = FontStyle.Bold;
            s.alignment = TextAnchor.MiddleLeft;
            s.padding = new RectOffset(10, 10, 6, 6);

            if (GUILayout.Button(label, s))
            {
                Plugin.CfgShowAdvanced.Value = !Plugin.CfgShowAdvanced.Value;
                Plugin.SaveCfg();
            }

            if (!Plugin.CfgShowAdvanced.Value)
            {
                GUIStyle sub = new GUIStyle(GUI.skin.label);
                sub.fontSize = 11;
                sub.wordWrap = true;
                sub.normal.textColor = new Color(0.62f, 0.66f, 0.72f);
                GUILayout.Label("  "
#if CANALPA
                    + "Canalpa mode, "
#endif
                    + "sampling, memory, the danger toggles, the developer "
                    + "channel, per-character voices and logging. All of it is either off by default "
                    + "or already right for most people.", sub);
            }
        }

        // Every setting that lives inside the Advanced section, so the count above
        // reflects exactly what is being hidden - no more, no less.
        static ConfigEntryBase[] AdvancedEntries()
        {
            return new ConfigEntryBase[]
            {
#if CANALPA
                Plugin.CfgCanalpaMode, Plugin.CfgCanalpaSecretRoom,
#endif
                Plugin.CfgTemperature, Plugin.CfgMaxTokens, Plugin.CfgRetries,
                Plugin.CfgHideReasoning, Plugin.CfgJsonMode, Plugin.CfgClampValues,
                Plugin.CfgHistoryMaxTokens,
                Plugin.CfgAiCanMurder, Plugin.CfgTestKillPhraseActive, Plugin.CfgTestKillPhrase,
                Plugin.CfgOocEnabled, Plugin.CfgOocTag,
                Plugin.CfgBlockGameAi, Plugin.CfgBlockGameExtras,
                Plugin.CfgGrokLanguage, Plugin.CfgGrokSpeed, Plugin.CfgGrokSampleRate,
                Plugin.CfgGrokNormalize, Plugin.CfgTtsNormalize, Plugin.CfgSpeakActions,
                Plugin.CfgLogPayloads,
            };
        }

        static int ChangedAdvancedCount()
        {
            int n = 0;
            try
            {
                ConfigEntryBase[] all = AdvancedEntries();
                for (int i = 0; i < all.Length; i++)
                {
                    ConfigEntryBase e = all[i];
                    if (e == null || e.DefaultValue == null) continue;

                    object cur = e.BoxedValue;
                    if (cur == null) continue;

                    // ToString rather than Equals: the boxed values are a mix of
                    // bool, int, float and string, and boxed floats do not compare
                    // reliably by reference or by Equals across a config round-trip.
                    if (cur.ToString() != e.DefaultValue.ToString()) n++;
                }

                // Per-character voices are dynamic, so they are counted by walking
                // the same table the panel builds its rows from.
                for (int i = 0; i < Voices.Names.Length; i++)
                {
                    ConfigEntry<string> v = Voices.Entry(Voices.Names[i]);
                    if (v != null && !string.IsNullOrEmpty(v.Value)) n++;
                }
            }
            catch (Exception) { return n; }
            return n;
        }

        static readonly Color DangerRed = new Color(1f, 0.30f, 0.30f, 1f);
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
        // Why the door is or is not on the table right now.
        //
        // Three states worth telling apart: trust too low, trust fine but she is
        // still gauging you, and ready. Without this the second and third look the
        // same from the player's chair.
        static void CanalpaReadinessRow()
        {
            GUIStyle val = new GUIStyle(GUI.skin.label);
            val.wordWrap = true;
            val.fontStyle = FontStyle.Bold;

            string state;
            if (Canalpa.SecretRoomAvailable())
            {
                val.normal.textColor = new Color(0.48f, 0.85f, 0.52f);
                state = "  She could offer it now - waiting on her, not on you.";
            }
            else if (Canalpa.ProbingPhase())
            {
                val.normal.textColor = new Color(0.90f, 0.78f, 0.36f);
                state = "  She is still gauging you: " + Canalpa.ProbesPassed + " of "
                    + Canalpa.ProbeTarget + " warm answers"
                    + (Canalpa.ProbeRaised ? ", and she has just asked you one." : ".");
            }
            else
            {
                val.normal.textColor = DangerDim;
                state = "  Not yet - trust is still below the mark, or the door is already open.";
            }

            GUILayout.Label(state, val);
        }
#endif

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
        static void PerCharacterVoices()
        {
            GUILayout.Space(8f);
            Header("Voice per character");

            int set = Voices.Configured();
            PlainNote("  Leave a row empty and she uses the general voice above. "
                + (set == 0
                    ? "Nothing set yet, so everyone shares one voice."
                    : set + " of " + Voices.Names.Length + " have their own."));

            for (int i = 0; i < Voices.Names.Length; i++)
                TextRow(VoicePrefix + Voices.Names[i], Voices.Labels[i], 0f);

            // Deliberately says nothing about which scenes exist or what happens
            // in them. An earlier version of this note explained the routing by
            // describing a late-game puzzle, which spoiled it for anyone who read
            // the panel before playing that far. The routing needs no explanation
            // to be used: a voice follows the character it is set for, everywhere.
            PlainNote("  A voice set here follows that character wherever she appears.");
        }

        // Everything from here to the matching #endif is local-only and is
        // compiled out of the released binary (build.sh --release). The guard is
        // deliberately around the code rather than around a runtime check: a
        // toggle would still leave trust and gem editing sitting in the DLL for
        // anyone who looked, which is not what "local only" means.
#if CHEATS
        static readonly Color CheatBlue = new Color(0.45f, 0.75f, 1f, 1f);

        static string _trustBuf = "";
        static string _gemBuf = "";
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
                GUILayout.Label("  Read and set trust and gems live, so a trust-gated behaviour can be "
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

        static void CheatsSection()
        {
            GUIStyle head = new GUIStyle(GUI.skin.label);
            head.fontStyle = FontStyle.Bold;
            head.normal.textColor = CheatBlue;

            // ---- trust -------------------------------------------------------
            GUILayout.Space(8f);
            GUILayout.Label("Trust", head);

            float? trust = Cheats.Trust();
            if (!trust.HasValue)
            {
                PlainNote("  No character in this scene, so there is no trust to read. "
                    + "Open the panel while talking to her.");
            }
            else
            {
                string indicator = Cheats.Indicator();
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

            int? msgs = Cheats.Messages();
            if (!msgs.HasValue)
            {
                PlainNote("  No level behaviour in this scene, so there is no message count to read. "
                    + "Open the panel during a conversation.");
            }
            else
            {
                int? gate = Cheats.MessageGate();
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

            // ---- gems --------------------------------------------------------
            GUILayout.Space(10f);
            GUILayout.Label("Gems", head);

            GUIStyle gv = new GUIStyle(GUI.skin.label);
            gv.fontStyle = FontStyle.Bold;
            GUILayout.Label("  Current: " + Cheats.Gems(), gv);

            GUILayout.BeginHorizontal();
            GUILayout.Space(8f);
            if (GUILayout.Button("+100", GUILayout.Width(56f))) ApplyGems(Cheats.Gems() + 100);
            if (GUILayout.Button("+1000", GUILayout.Width(62f))) ApplyGems(Cheats.Gems() + 1000);
            GUILayout.Space(10f);
            GUILayout.Label("Set exactly:", GUILayout.Width(84f));
            _gemBuf = GUILayout.TextField(_gemBuf ?? "", GUILayout.Width(70f));
            if (GUILayout.Button("Apply", GUILayout.Width(64f)))
            {
                int g;
                if (int.TryParse((_gemBuf ?? "").Trim(), out g)) ApplyGems(g);
                else Note("Gems: \"" + _gemBuf + "\" is not a whole number.");
            }
            GUILayout.EndHorizontal();

            PlainNote("  Gems are stored locally, and the hub reloads them from your account when it "
                + "next loads - so treat a cheated balance as good for this session.");

            // ---- companion mods ----------------------------------------------
            GUILayout.Space(10f);
            GUILayout.Label("Companion mods", head);
            PlainNote("  Three separate mods by Luigirocks900. This panel only switches them on and off; "
                + "they are not part of this mod and are not bundled with it.");

            CompanionRow(Cheats.InvincInstalled, "Invincibility",
                "InvincibilityMod", InvincRow);

            CompanionRow(Cheats.GiftInstalled, "Gift yourself anything",
                "GiftYourselfAnything", GiftRow);

            CompanionRow(Cheats.AtriumInstalled, "Atrium gifting restored",
                "RestoreAtriumGifts", AtriumRow);
        }

        // Nothing to toggle: it is a Harmony patch with no state and no keybind.
        // The row exists so that "she would not hand it over in the hub" can be
        // told apart from "the mod that allows that is not loaded".
        static void AtriumRow()
        {
            PlainNote("    Loaded. She can hand over items in the Atrium, which vanilla does not allow. "
                + "Nothing to configure.");
        }

        static void ApplyTrust(float target)
        {
            if (target < 0f) target = 0f;
            if (Cheats.SetTrust(target))
            {
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
                int? now = Cheats.Messages();
                Note("Message count is now " + (now.HasValue ? now.Value.ToString() : "?") + ".");
            }
            else Note("The message count could not be changed - see the console for why.");
        }

        static void ApplyGems(int target)
        {
            if (Cheats.SetGems(target)) Note("Gems are now " + Cheats.Gems() + ".");
            else Note("Gems could not be changed - see the console for why.");
        }

        // One row per companion mod, with the not-installed case handled once.
        // Absent is the common case and should read as information, not an error.
        static void CompanionRow(bool installed, string label, string dll, Action body)
        {
            GUILayout.Space(6f);
            if (!installed)
            {
                GUIStyle miss = new GUIStyle(GUI.skin.label);
                miss.wordWrap = true;
                miss.normal.textColor = new Color(0.62f, 0.66f, 0.72f);
                GUILayout.Label("  " + label + ": not installed. Drop " + dll
                    + ".dll into BepInEx/plugins and restart the game to use it here.", miss);
                return;
            }
            body();
        }

        static void InvincRow()
        {
            bool? state = Cheats.Invincible();
            if (!state.HasValue)
            {
                PlainNote("  Invincibility: installed, but its switch could not be read. Set Keybind "
                    + "back to F2 in BepInEx/config/com.luigirocks900.InvincibilityMod.cfg to use it "
                    + "directly.");
                return;
            }

            bool after = GUILayout.Toggle(state.Value, "  Invincibility (you cannot be killed)");
            if (after != state.Value)
            {
                if (Cheats.SetInvincible(after))
                    Note("Invincibility: " + (after ? "ON" : "off") + ".");
                else
                    Note("Invincibility could not be toggled - see the console for why.");
            }

            // Its own F2 is deliberately turned off in its config so there is one
            // switch for this rather than two that can disagree. Said here because
            // its readme documents F2 and the difference would otherwise look like
            // a broken install.
            PlainNote("    Its own F2 hotkey is switched off in its config, so this row is the only "
                + "switch. The health icon in the corner follows it.");
        }

        static void GiftRow()
        {
            PlainNote("  Gift yourself anything: installed. Type an item name in the chat box, then hold "
                + "either Ctrl and press Enter to receive it instead of sending it. F3 adds one of "
                + "everything you already carry.");
        }
#endif

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
            string next = GUILayout.TextField(cur);
            // Committing the buffer only while enabled means a disabled field can
            // never write back a value, whatever the skin does with the keyboard.
            if (enabled) _buf[key] = next;

            GUI.enabled = wasEnabled;
            GUI.color = wasColor;

            GUILayout.EndHorizontal();
        }

        // Keys are masked by default so the panel is safe to have on screen while
        // recording or streaming.
        static void KeyRow(string key, string label)
        {
            GUILayout.BeginHorizontal();
            Label(label);
            string cur = Get(key);
            if (_showKeys) _buf[key] = GUILayout.TextField(cur);
            else _buf[key] = GUILayout.PasswordField(cur, '*');
            _showKeys = GUILayout.Toggle(_showKeys, "show", GUILayout.Width(56f));
            GUILayout.EndHorizontal();
        }

        static void IntRow(string key, string label)
        {
            GUILayout.BeginHorizontal();
            Label(label);
            _buf[key] = GUILayout.TextField(Get(key), GUILayout.Width(140f));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        static void FloatRow(string key, string label)
        {
            IntRow(key, label);
        }

        static void Bool(ConfigEntry<bool> cfg, string label)
        {
            if (cfg == null) return;
            bool before = cfg.Value;
            bool after = GUILayout.Toggle(before, "  " + label);
            if (after != before)
            {
                cfg.Value = after;
                Plugin.SaveCfg();
                Note(label + (after ? ": on" : ": off"));
            }
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
                "BaseUrl", "ApiKey", "Model", "TtsProvider", "GrokBaseUrl",
                "GrokApiKey", "TtsModel", "GrokVoiceId", "GrokLanguage", "TestKillPhrase",
                "OocTag"
            };

            string[] all = new string[fixedKeys.Length + Voices.Names.Length];
            Array.Copy(fixedKeys, all, fixedKeys.Length);
            for (int i = 0; i < Voices.Names.Length; i++)
                all[fixedKeys.Length + i] = VoicePrefix + Voices.Names[i];
            return all;
        }
        static readonly string[] IntKeys =
        {
            "MaxTokens", "RetriesOnBadJson", "HistoryMaxTokens", "GrokSampleRate"
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
                case "TtsProvider": return Plugin.CfgTtsProvider;
                case "GrokBaseUrl": return Plugin.CfgGrokBaseUrl;
                case "GrokApiKey": return Plugin.CfgGrokApiKey;
                case "TtsModel": return Plugin.CfgTtsModel;
                case "GrokVoiceId": return Plugin.CfgGrokVoiceId;
                case "GrokLanguage": return Plugin.CfgGrokLanguage;
                case "TestKillPhrase": return Plugin.CfgTestKillPhrase;
                case "OocTag": return Plugin.CfgOocTag;
            }
            return null;
        }

        static ConfigEntry<int> Num(string key)
        {
            switch (key)
            {
                case "MaxTokens": return Plugin.CfgMaxTokens;
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

            if (bad.Count == 0)
                Note("Saved. Takes effect on her next reply - no restart.");
            else
                Note("Saved, but these were not numbers and kept their old values: "
                    + string.Join(", ", bad.ToArray()));
        }
    }
}
