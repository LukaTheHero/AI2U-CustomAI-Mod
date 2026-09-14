// Every instruction the mod puts in front of the model, shown and editable.
//
// Asked for on Nexus, and it is a fair ask. This mod writes a great deal on the
// player's behalf: who she is, what she remembers, what the engine will accept
// back, how difficulty bends her, when she is allowed to kill. All of it was
// invisible. You could change the model and the temperature and her voice, but
// not one word of what she is actually told - which is the part that decides
// who she is.
//
// HOW IT WORKS
//
// The mod keeps building every block exactly as it always has. On the way into
// the request each one passes through Apply(), which does two things: it
// remembers the text that was generated, so the panel can show the real thing
// rather than a documentation copy of it, and it substitutes the player's
// version when they have written one. Nothing else in the mod changes, and a
// player who never opens the tab gets byte-identical requests.
//
// TWO RULES WORTH KNOWING, BOTH DELIBERATE
//
// 1. An override replaces a block; it cannot create one. Blocks come and go by
//    scene - the four-doors block only exists in the final trial, the murder
//    block only where she can actually kill you - and a block that is not
//    active this turn stays inactive no matter what is saved for it. Otherwise
//    editing the doors text would inject four-doors rules into a kitchen
//    conversation, and the resulting bug would look like the model losing its
//    mind rather than like an override doing exactly what it was told.
//
// 2. What you see is the LAST text that was really sent. These blocks are
//    assembled live from the save - her trust, your history, the room she is
//    standing in - so most of them are empty until you have played a turn with
//    the mod running. That is honest, and it is the only way to show a player
//    what the model is actually reading rather than a template of it.
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AI2UCustomAI
{
    internal static class Prompts
    {
        // The registry is written out by hand rather than discovered, because
        // the order here is the order they reach the model, and that order is
        // load-bearing (see the comment above the OOC block in BuildRequest -
        // a shipped bug came from moving one of these). Reading the list top to
        // bottom tells you what she is told, in the sequence she is told it.
        internal sealed class Entry
        {
            public string Key;
            public string Title;
            public string Note;
            public Entry(string k, string t, string n) { Key = k; Title = t; Note = n; }
        }

        internal static readonly Entry[] Registry =
        {
            new Entry("identity",  "Who she is",
                "Her name, her situation and the voice she answers in. The first thing she is told."),
            new Entry("lore",      "Her persona, memories and secrets",
                "Everything the mod knows about this playthrough: her level, her history with you, what she is hiding."),
            new Entry("contract",  "The engine contract",
                "The JSON shape the game will accept back, and the list of actions she is allowed to take. Edit with care - the game rejects replies that do not match it."),
            new Entry("mechanics", "Game mechanics she understands",
                "How the systems she can touch actually work: doors, items, progression gates."),
            new Entry("feelings",  "Her feelings and how they move",
                "Trust, affection and anger - what raises them and what does not."),
            new Entry("difficulty","Difficulty disposition",
                "How hard she is to win over. Empty on Normal, where the request is left exactly as it would be with no difficulty system at all."),
            new Entry("items",     "Gifts and items",
                "What she is carrying and the rules for handing something over."),
            new Entry("murder",    "When she may hurt you",
                "The danger rules. Only present in scenes where she can actually do it."),
            new Entry("doors",     "The four doors",
                "The final trial: which door she is and whether she is impersonating someone. Only present in that scene."),
            new Entry("roleplay_actions", "*Actions* in asterisks",
                "How she reads and writes physical action text."),
            new Entry("roleplay_timeskip", "&Timeskips&",
                "How she handles a jump forward in time."),
            new Entry("canalpa",   "Canalpa mode",
                "Only present when the mode is on AND its gates have passed. Usually absent."),
            new Entry("ooc",       "The [OOC] channel",
                "Developer mode: what she must answer truthfully and out of character. Deliberately the LAST thing she reads, so it outranks everything above."),
            new Entry("reminder",  "The reply-format reminder",
                "The short final nudge that keeps the reply a single raw JSON object."),
            new Entry("local_compact", "Local-model compact prompt",
                "Replaces almost all of the above when Local Model Mode is on, because small local models do better with one short brief than fourteen long ones."),
            new Entry("local_ooc", "Local-model [OOC] channel",
                "The OOC rules in their compact form, used only in Local Model Mode."),
        };

        // What the mod generated, per key, on the most recent turn that used it.
        static readonly Dictionary<string, string> _live = new Dictionary<string, string>(StringComparer.Ordinal);
        // What the player wrote. Only keys present here are overridden.
        static readonly Dictionary<string, string> _over = new Dictionary<string, string>(StringComparer.Ordinal);
        static bool _loaded;

        internal static bool AnyOverrides { get { Load(); return _over.Count > 0; } }
        internal static bool IsOverridden(string key) { Load(); return _over.ContainsKey(key); }

        // The one call site pattern. Deliberately null-safe and deliberately
        // null-preserving: a block the mod did not produce this turn is not
        // produced by an override either.
        internal static string Apply(string key, string generated)
        {
            Load();
            if (generated == null) return null;
            _live[key] = generated;

            string ov;
            if (_over.TryGetValue(key, out ov) && ov != null && ov.Length > 0)
                return ov;
            return generated;
        }

        // Fill the panel WITHOUT waiting for a conversation turn.
        //
        // Capturing only on send was technically honest and practically
        // useless: open the tab before speaking to her and all sixteen blocks
        // read "not sent yet", which looks exactly like a broken feature. These
        // generators are the same ones BuildRequest calls and they are pure
        // reads of the live save, so running them here produces precisely what
        // the next turn would send.
        //
        // Every call is isolated. Outside a conversation some of these walk
        // scene objects that do not exist yet, and one throwing generator must
        // cost its own block and nothing else - certainly not the panel.
        static float _previewAt = -99f;

        internal static void CapturePreview(bool force)
        {
            if (!force && Time.realtimeSinceStartup - _previewAt < 5f) return;
            _previewAt = Time.realtimeSinceStartup;

            // The contract is assembled from a vocabulary the send path
            // refreshes first; without this it reports the previous scene's.
            Grab("contract", delegate { GameVocab.Refresh(); return GameVocab.Contract(); });

            Grab("identity",  delegate { return Identity.Block(); });
            Grab("lore",      delegate { return Lore.Block(); });
            Grab("mechanics", delegate { return Mechanics.Block(); });
            Grab("feelings",  delegate { return Feelings.Block(); });
            Grab("difficulty",delegate { return Difficulty.Block(); });
            Grab("items",     delegate { return Items.Block(); });
            Grab("murder",    delegate { return Murder.Block(); });
            Grab("doors",     delegate { return FinalDoors.Block(); });
            Grab("roleplay_actions",  delegate { return Roleplay.ActionsBlock(); });
            Grab("roleplay_timeskip", delegate { return Roleplay.TimeskipBlock(); });
            Grab("ooc",       delegate { return Ooc.Block(); });
            Grab("local_ooc", delegate { return Ooc.Block(); });
            Grab("local_compact", delegate { return Bridge.CompactLocalPromptPreview(); });
#if CANALPA
            Grab("canalpa",   delegate { return Canalpa.Block(); });
#endif
        }

        delegate string Gen();

        static void Grab(string key, Gen f)
        {
            try
            {
                string v = f();
                if (!string.IsNullOrEmpty(v)) _live[key] = v;
            }
            catch (Exception)
            {
                // Not loggable per-frame without spamming; a block that cannot
                // be built right now simply stays as it was.
            }
        }

        internal static string Live(string key)
        {
            string v;
            return _live.TryGetValue(key, out v) ? v : null;
        }

        internal static string Override(string key)
        {
            Load();
            string v;
            return _over.TryGetValue(key, out v) ? v : null;
        }

        // The text the editor should show: the player's version if there is
        // one, otherwise the real generated text, otherwise nothing.
        internal static string Editable(string key)
        {
            string o = Override(key);
            if (o != null) return o;
            return Live(key) ?? "";
        }

        internal static void Set(string key, string text)
        {
            Load();
            if (text == null) text = "";
            // Saving an empty box means "stop overriding this", which is the
            // same thing the revert button does and is what a player who
            // selected everything and pressed delete expects.
            if (text.Trim().Length == 0) _over.Remove(key);
            else _over[key] = text;
            Save();
        }

        internal static void Revert(string key)
        {
            Load();
            if (_over.Remove(key)) Save();
        }

        internal static void RevertAll()
        {
            Load();
            _over.Clear();
            Save();
        }

        // ---------------------------------------------------------------
        // Persistence. Same directory and the same fallback ProfileManager
        // uses, so all of the mod's saved state lives in one place.
        // ---------------------------------------------------------------
        static string PathFor()
        {
            string dir;
            try { dir = Paths.ConfigPath; }
            catch (Exception)
            {
                try { dir = Path.Combine(Application.dataPath, "..", "BepInEx", "config"); }
                catch (Exception) { dir = "."; }
            }
            return Path.Combine(dir, "AI2UCustomAI_prompts.json");
        }

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                string p = PathFor();
                if (!File.Exists(p)) return;
                JObject root = JObject.Parse(File.ReadAllText(p));
                foreach (KeyValuePair<string, JToken> kv in root)
                {
                    if (kv.Value == null || kv.Value.Type == JTokenType.Null) continue;
                    string v = kv.Value.ToString();
                    if (v.Length > 0) _over[kv.Key] = v;
                }
                if (_over.Count > 0)
                    Plugin.Log.LogWarning("Prompts: " + _over.Count + " of the mod's prompt blocks are "
                        + "player-edited. Restore defaults on the Prompts tab if she starts behaving oddly.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Prompts: could not read saved prompts (" + e.Message
                    + "). Falling back to the mod's own.");
            }
        }

        static void Save()
        {
            try
            {
                JObject root = new JObject();
                foreach (KeyValuePair<string, string> kv in _over) root[kv.Key] = kv.Value;
                File.WriteAllText(PathFor(), root.ToString(Formatting.Indented));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Prompts: could not save (" + e.Message + ")");
            }
        }

        // ---------------------------------------------------------------
        // The panel. Kept here rather than in OverlayMenu so that adding a
        // whole editor does not grow a file that is already 150KB, and so the
        // feature can be read in one place.
        // ---------------------------------------------------------------
        static int _sel;
        static Vector2 _listScroll;
        static Vector2 _textScroll;
        static string _buf;
        static int _bufFor = -1;
        static bool _confirmRestore;
        static string _flash;
        static float _flashAt;

        // Self-contained on purpose: the overlay's own Header/Label helpers are
        // private to it, and a whole editor should not need four of its
        // internals exported to draw itself.
        internal static void Draw()
        {
            // Cheap because it is throttled: the generators are the same ones a
            // turn runs, and the panel is only drawn while the tab is open.
            CapturePreview(false);

            GUIStyle head = new GUIStyle(GUI.skin.label);
            head.fontStyle = FontStyle.Bold;
            head.fontSize = 14;
            GUILayout.Label("Everything she is told, and your chance to change it", head);

            GUILayout.Label(
                "The mod writes these blocks and sends them with every message. Pick one on the left to "
                + "read exactly what was sent last turn, edit it, and press Save. An edited block replaces "
                + "the mod's version until you restore it.\n"
                + "These are read live from your current save, so what you see is what the next message "
                + "would send. A block that is not part of the scene you are in right now stays empty - "
                + "and stays out of the request no matter what you write in it.",
                GUI.skin.label);

            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();

            // ---- the list ----
            GUILayout.BeginVertical(GUILayout.Width(286f));
            _listScroll = GUILayout.BeginScrollView(_listScroll, false, true,
                GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(400f));
            for (int i = 0; i < Registry.Length; i++)
            {
                Entry e = Registry[i];
                bool edited = IsOverridden(e.Key);
                bool live = Live(e.Key) != null;

                string mark = edited ? "✎ " : (live ? "● " : "○ ");
                GUIStyle st = new GUIStyle(GUI.skin.button);
                st.alignment = TextAnchor.MiddleLeft;
                st.fontSize = 11;
                st.wordWrap = false;
                st.clipping = TextClipping.Clip;
                st.padding = new RectOffset(6, 4, 2, 2);
                if (i == _sel) st.fontStyle = FontStyle.Bold;

                if (GUILayout.Button(mark + e.Title, st, GUILayout.Height(24f), GUILayout.Width(252f)))
                {
                    _sel = i;
                    _bufFor = -1;
                    _confirmRestore = false;
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Label("✎ edited   ● active now   ○ not in this scene", GUI.skin.label);
            if (GUILayout.Button("↻ Refresh from game", GUILayout.Height(22f), GUILayout.Width(252f)))
            {
                CapturePreview(true);
                _bufFor = -1;
                Flash("Re-read every block from the save as it stands now.");
            }
            GUILayout.EndVertical();

            GUILayout.Space(8f);

            // ---- the editor ----
            GUILayout.BeginVertical();
            Entry sel = Registry[Mathf.Clamp(_sel, 0, Registry.Length - 1)];

            GUIStyle title = new GUIStyle(GUI.skin.label);
            title.fontStyle = FontStyle.Bold;
            GUILayout.Label(sel.Title, title);
            GUILayout.Label(sel.Note, GUI.skin.label);

            if (Live(sel.Key) == null && !IsOverridden(sel.Key))
                GUILayout.Label("Empty right now - this block is not part of the current scene, or the game "
                    + "has not loaded far enough to build it. Load a save and come back.",
                    GUI.skin.label);

            if (_bufFor != _sel)
            {
                _buf = Editable(sel.Key);
                _bufFor = _sel;
            }

            GUIStyle area = new GUIStyle(GUI.skin.textArea);
            area.wordWrap = true;
            area.alignment = TextAnchor.UpperLeft;
            area.fontSize = 12;
            area.padding = new RectOffset(6, 6, 6, 6);

            // Horizontal scrollbar suppressed outright (GUIStyle.none): the text
            // wraps, so a horizontal bar can only ever appear by accident and it
            // steals a row of height when it does.
            _textScroll = GUILayout.BeginScrollView(_textScroll, false, true,
                GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(400f));
            _buf = GUILayout.TextArea(_buf ?? "", area,
                GUILayout.ExpandWidth(true), GUILayout.MinHeight(384f));
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Save this block", GUILayout.Height(26f)))
            {
                Set(sel.Key, _buf);
                _bufFor = -1;
                Flash(IsOverridden(sel.Key)
                    ? "Saved. She reads your version from the next message on."
                    : "Cleared - back to the mod's own text for this block.");
            }

            GUI.enabled = IsOverridden(sel.Key);
            if (GUILayout.Button("Revert this block", GUILayout.Height(26f)))
            {
                Revert(sel.Key);
                _bufFor = -1;
                Flash("Reverted this block to the mod's default.");
            }
            GUI.enabled = true;

            if (GUILayout.Button("Reload from last sent", GUILayout.Height(26f)))
            {
                _buf = Live(sel.Key) ?? "";
                Flash("Loaded the text the mod generated last turn. Not saved yet.");
            }

            GUILayout.EndHorizontal();

            GUILayout.Space(10f);

            // ---- restore everything ----
            if (!_confirmRestore)
            {
                GUI.enabled = AnyOverrides;
                if (GUILayout.Button("↺  Restore ALL default prompts", GUILayout.Height(28f)))
                    _confirmRestore = true;
                GUI.enabled = true;
            }
            else
            {
                GUILayout.Label("Are you sure you want to go back to the default prompts that come with the mod?",
                    GUI.skin.label);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Yes, restore the mod's prompts", GUILayout.Height(28f)))
                {
                    RevertAll();
                    _bufFor = -1;
                    _confirmRestore = false;
                    Flash("Every block is back to the mod's own text.");
                }
                if (GUILayout.Button("No, keep my edits", GUILayout.Height(28f)))
                    _confirmRestore = false;
                GUILayout.EndHorizontal();
            }

            if (_flash != null && Time.realtimeSinceStartup - _flashAt < 6f)
                GUILayout.Label(_flash, GUI.skin.label);

            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        static void Flash(string m)
        {
            _flash = m;
            _flashAt = Time.realtimeSinceStartup;
        }
    }
}
