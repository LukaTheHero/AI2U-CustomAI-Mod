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
            // Read-only entries are shown in full and simply cannot be saved
            // over. See the comment above the registry for why these three are.
            public bool ReadOnly;
            public Entry(string k, string t, string n) : this(k, t, n, false) { }
            public Entry(string k, string t, string n, bool ro) { Key = k; Title = t; Note = n; ReadOnly = ro; }
        }

        internal static Entry Find(string key)
        {
            for (int i = 0; i < Registry.Length; i++)
                if (Registry[i].Key == key) return Registry[i];
            return null;
        }

        static bool IsReadOnly(string key)
        {
            Entry e = Find(key);
            return e != null && e.ReadOnly;
        }

        internal static readonly Entry[] Registry =
        {
            new Entry("identity",  "Who she is",
                "Her name, her situation and the voice she answers in. The first thing she is told."),
            new Entry("identity_summon", "Who the summon is",
                "The Magic Circle summon's identity, used only in that scene. Separate from the block above because it is a different character."),
            new Entry("lore",      "Her persona, memories and secrets",
                "Everything the mod knows about this playthrough: her level, her history with you, what she is hiding."),
            new Entry("contract",  "The engine contract",
                "The reply shape the game accepts, how affection moves, and the rules for her written reply. Editable. The live half - the actions and locations this room actually has, this level's unlocks and this scene's encounter fields - is appended after your edit and cannot be changed, because it is the only implementation of those mechanics."),
            new Entry("mechanics", "Game mechanics she understands",
                "How the systems she can touch actually work: doors, items, progression gates."),
            new Entry("feelings",  "Her feelings and how they move",
                "Trust, affection and anger - what raises them and what does not."),
            new Entry("feelings_moments", "Core-memory moments (Hard difficulty)",
                "The taxonomy she uses to decide when something is worth keeping as a core memory. Only present while Hard difficulty is on - it belongs to that toggle."),
            new Entry("difficulty","Difficulty disposition",
                "How hard she is to win over. Empty on Normal, where the request is left exactly as it would be with no difficulty system at all."),
            new Entry("items",     "Gifts and items",
                "What she is carrying and the rules for handing something over. READ-ONLY: this is her live inventory, and a pinned copy makes her offer things she is not holding - the game then hands over a blank placeholder, which is worse than refusing.", true),
            new Entry("murder",    "When she may hurt you",
                "The danger rules. Only present in scenes where she can actually do it. READ-ONLY: it carries her live chase state and the warning-shock field that exists only on Level 3, so a pinned copy can announce a hunt in an ordinary conversation.", true),
            new Entry("doors",     "The four doors",
                "The final trial: which door she is and whether she is impersonating someone. READ-ONLY: all four doors speak in one scene, so a single saved copy would be replayed for every door - which is exactly the bug that made all four claim to be the real girl in 5.4.", true),
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
                "Replaces almost all of the above when Local Model Mode is on, because small local models do better with one short brief than the whole stack above."),
            new Entry("local_ooc", "Local-model [OOC] channel",
                "The OOC rules in their compact form, used only in Local Model Mode."),
        };

        // What the mod generated, per key, on the most recent turn that used it.
        static readonly Dictionary<string, string> _live = new Dictionary<string, string>(StringComparer.Ordinal);
        // What the player wrote. Only keys present here are overridden.
        static readonly Dictionary<string, string> _over = new Dictionary<string, string>(StringComparer.Ordinal);
        static bool _loaded;

        // Kept in the prompts file rather than in a ConfigEntry on purpose: the
        // profile system writes config entries, and a profile switch that
        // silently re-locked this tab - or silently unlocked it - would be a
        // nasty little bug. The unlock is a property of the player, not of a
        // configuration they can swap.
        static bool _unlocked;

        internal static bool Unlocked { get { Load(); return _unlocked; } }

        internal static bool AnyOverrides { get { Load(); return _over.Count > 0; } }
        internal static bool IsOverridden(string key) { Load(); return _over.ContainsKey(StoreKey(key)); }

        // The one call site pattern. Deliberately null-safe and deliberately
        // null-preserving: a block the mod did not produce this turn is not
        // produced by an override either.
        internal static string Apply(string key, string generated)
        {
            return Apply(key, generated, null);
        }

        // Some blocks are not one block. Lore resolves a different biography for
        // every character (and Eddie alone carries the apartment history);
        // Identity is written per character; Mechanics returns a different block
        // per level. Stored under a bare key, an override captured while talking
        // to Eddie was sent verbatim as Eiona's persona - which reads as the mod
        // confusing two characters rather than as an override doing what it was
        // told.
        //
        // So those keys carry a scope, and the editor reads and writes the same
        // scoped entry it would send. An edit is therefore per character, or per
        // level, which is also what a player editing "her persona" actually
        // means.
        internal static string Apply(string key, string generated, string scope)
        {
            Load();
            if (generated == null) return null;
            _live[key] = generated;
            if (scope != null) _scope[key] = scope;

            string ov;
            if (_over.TryGetValue(StoreKey(key), out ov) && ov != null && ov.Length > 0)
                return ov;
            return generated;
        }

        // The scope last seen for a key, so the panel edits the entry that the
        // next turn will actually read.
        static readonly Dictionary<string, string> _scope = new Dictionary<string, string>(StringComparer.Ordinal);

        // Keys whose override is deliberately narrow, and the wording shown to
        // the player so a per-character edit never looks like a global one.
        internal static string ScopeNote(string key)
        {
            if (key == "lore" || key == "identity" || key == "identity_summon")
                return "Saved for THIS CHARACTER only - each girl has her own.";
            if (key == "mechanics")
                return "Saved for THIS LEVEL only - each level has its own.";
            if (key == "local_compact")
                return "Saved for THIS CHARACTER only - it names her and you in the text.";
            if (key == "difficulty")
                return "Saved for THIS DIFFICULTY only - each tier has its own.";
            return null;
        }

        internal static string ScopeOf(string key)
        {
            string v;
            return _scope.TryGetValue(key, out v) ? v : null;
        }

        static string StoreKey(string key)
        {
            string sc;
            if (_scope.TryGetValue(key, out sc) && !string.IsNullOrEmpty(sc)) return key + "@" + sc;
            return key;
        }

        // Fill the panel WITHOUT waiting for a conversation turn.
        //
        // Capturing only on send was technically honest and practically
        // useless: open the tab before speaking to her and every block
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

            // Scoped keys are only captured once the game can say WHICH
            // character or level they belong to; otherwise the panel would offer
            // an edit box wired to an entry no turn will read.
            string cs = CharScope();
            string ls = LevelScope();
            if (cs != null)
            {
                Grab("identity", cs, delegate { return Identity.Block(); });
                Grab("lore",     cs, delegate { return Lore.Block(); });
            }
            if (ls != null)
                Grab("mechanics", ls, delegate { return Mechanics.Block(); });
            if (cs != null)
                Grab("local_compact", cs, delegate { return Bridge.CompactLocalPromptPreview(); });

            string ds = DifficultyScope();
            if (ds != null)
                Grab("difficulty", ds, delegate { return Difficulty.Block(); });

            Grab("feelings",  delegate { return Feelings.Block(); });
            Grab("feelings_moments", delegate { return Feelings.HardBlock(); });

            Grab("items",     delegate { return Items.Block(); });
            Grab("murder",    delegate { return Murder.Block(); });
            Grab("doors",     delegate { return FinalDoors.Block(); });
            Grab("roleplay_actions",  delegate { return Roleplay.ActionsBlock(); });
            Grab("roleplay_timeskip", delegate { return Roleplay.TimeskipBlock(); });
            Grab("ooc",       delegate { return Ooc.Block(); });
            Grab("local_ooc", delegate { return Ooc.Block(); });

#if CANALPA
            Grab("canalpa",   delegate { return Canalpa.Block(); });
#endif
        }

        delegate string Gen();

        // Scope tags.
        //
        // The character id is used rather than her display name because the name
        // is localised and can be replaced in-scene, and a scope that shifted
        // with the player's language would silently orphan every edit they had
        // saved. Both return null when the game cannot answer yet, and a null
        // scope means "do not capture" rather than "capture unscoped" - showing
        // an unscoped box for a scoped key would let the player edit an entry
        // that no turn will ever read.
        internal static string CharScope()
        {
            try
            {
                int? id = Identity.CharacterId();
                return id.HasValue ? "c" + id.Value : null;
            }
            catch (Exception) { return null; }
        }

        internal static string DifficultyScope()
        {
            try
            {
                return Plugin.CfgDifficulty != null && !string.IsNullOrEmpty(Plugin.CfgDifficulty.Value)
                    ? "d" + Plugin.CfgDifficulty.Value : null;
            }
            catch (Exception) { return null; }
        }

        internal static string LevelScope()
        {
            try { return "L" + GameManager.CurrentLevel; }
            catch (Exception) { return null; }
        }

        static void Grab(string key, Gen f)
        {
            Grab(key, null, f);
        }

        static void Grab(string key, string scope, Gen f)
        {
            try
            {
                string v = f();
                if (!string.IsNullOrEmpty(v))
                {
                    _live[key] = v;
                    if (scope != null) _scope[key] = scope;
                }
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
            return _over.TryGetValue(StoreKey(key), out v) ? v : null;
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

            // A block that is the mechanism cannot be pinned. Guarded here as
            // well as in the panel, because the panel is not the only caller a
            // future version might add.
            if (IsReadOnly(key)) return;

            // Refuse rather than write an entry nothing will read. A scoped key
            // whose scope is not resolved yet (the panel opened on the main
            // menu, say) would otherwise be stored under its bare name while
            // every turn looks it up under name@scope - so the player would see
            // "Saved" and get nothing, forever, with no way to revert it.
            if (ScopeNote(key) != null && string.IsNullOrEmpty(ScopeOf(key))) return;

            // Saving an empty box means "stop overriding this", which is the
            // same thing the revert button does and is what a player who
            // selected everything and pressed delete expects.
            if (text.Trim().Length == 0)
            {
                _over.Remove(StoreKey(key));
                Save();
                return;
            }

            // Saving text the mod would have produced anyway is ALSO a revert.
            // An override is a frozen snapshot and several of these blocks are
            // rebuilt per scene, so "Reload from last sent" followed by Save -
            // a change of nothing at all - used to pin one scene's text forever
            // and was the most destructive thing on this panel. Storing nothing
            // is strictly better than storing a copy: the block keeps tracking
            // the game, and the player sees exactly the same text either way.
            string current = Live(key);
            if (current != null && string.Equals(current, text, StringComparison.Ordinal))
            {
                _over.Remove(StoreKey(key));
                Save();
                return;
            }

            _over[StoreKey(key)] = text;
            Save();
        }

        internal static void Revert(string key)
        {
            Load();
            if (_over.Remove(StoreKey(key))) Save();
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

                if (root["unlocked"] != null)
                    _unlocked = root["unlocked"].Type == JTokenType.Boolean && (bool)root["unlocked"];

                // "blocks" is the current shape. A root that is just the map
                // itself is still read, so an early file is not thrown away.
                JObject blocks = root["blocks"] as JObject;
                if (blocks == null && root["unlocked"] == null) blocks = root;
                if (blocks != null)
                {
                    foreach (KeyValuePair<string, JToken> kv in blocks)
                    {
                        if (kv.Value == null || kv.Value.Type == JTokenType.Null) continue;
                        string v = kv.Value.ToString();
                        if (v.Length > 0) _over[kv.Key] = v;
                    }
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
                JObject blocks = new JObject();
                foreach (KeyValuePair<string, string> kv in _over) blocks[kv.Key] = kv.Value;
                JObject root = new JObject();
                root["unlocked"] = _unlocked;
                root["blocks"] = blocks;
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
        static string _bufScope = "";
        static bool _confirmRestore;
        static string _flash;
        static float _flashAt;

        // Self-contained on purpose: the overlay's own Header/Label helpers are
        // private to it, and a whole editor should not need four of its
        // internals exported to draw itself.
        // The ritual. Three screens and a ten-second hold, once, ever.
        //
        // This is not theatre for its own sake. The tab shows the level's own
        // secrets - who is really behind each door, when she is allowed to kill
        // you, what she is hiding - so opening it casually spoils the game it
        // belongs to. And the damage runs the other way too: these blocks are
        // load-bearing, the ordering between them is load-bearing, and someone
        // rewriting them because one reply annoyed them will quietly dismantle
        // the thing they were enjoying and then report it as a bug.
        //
        // So the gate is deliberately tedious, and deliberately one-time: it
        // costs a determined power user fifteen seconds once, and costs a
        // curious clicker the thing they were about to break.
        const float HOLD_SECONDS = 10f;
        static int _gate;
        static float _holdStart = -1f;
        static bool _holding;

        internal static void Draw()
        {
            if (!Unlocked) { DrawGate(); return; }

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

            if (sel.ReadOnly)
            {
                GUIStyle ro = new GUIStyle(GUI.skin.label);
                ro.fontStyle = FontStyle.Bold;
                ro.wordWrap = true;
                ro.normal.textColor = new Color(1f, 0.72f, 0.25f);
                GUILayout.Label("READ-ONLY - shown in full, but it cannot be saved over. This block is "
                    + "not advice to the model, it IS the mechanism, and a pinned copy would delete it.", ro);
            }
            else if (ScopeNote(sel.Key) != null && string.IsNullOrEmpty(ScopeOf(sel.Key)))
            {
                GUIStyle ro = new GUIStyle(GUI.skin.label);
                ro.wordWrap = true;
                ro.normal.textColor = new Color(1f, 0.72f, 0.25f);
                GUILayout.Label("Saving is off until the game tells the mod which character or level "
                    + "this belongs to. Load a save and come back.", ro);
            }

            string scopeNote = ScopeNote(sel.Key);
            if (scopeNote != null)
            {
                GUIStyle sc = new GUIStyle(GUI.skin.label);
                sc.fontStyle = FontStyle.Italic;
                sc.normal.textColor = OverlayMenu.PromptPink;
                GUILayout.Label(scopeNote, sc);
            }

            if (Live(sel.Key) == null && !IsOverridden(sel.Key))
                GUILayout.Label("Empty right now - this block is not part of the current scene, or the game "
                    + "has not loaded far enough to build it. Load a save and come back.",
                    GUI.skin.label);

            // Also keyed on the scope, not just the selection: leaving the panel
            // open across a character change would otherwise keep the previous
            // girl's text in the box and save it under the new girl's scope.
            string scopeNow = ScopeOf(sel.Key) ?? "";
            if (_bufFor != _sel || _bufScope != scopeNow)
            {
                _buf = Editable(sel.Key);
                _bufFor = _sel;
                _bufScope = scopeNow;
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

            bool readOnly = sel.ReadOnly;
            bool scopeMissing = ScopeNote(sel.Key) != null && string.IsNullOrEmpty(ScopeOf(sel.Key));

            GUI.enabled = !readOnly && !scopeMissing;
            if (GUILayout.Button("Save this block", GUILayout.Height(26f)))
            {
                Set(sel.Key, _buf);
                _bufFor = -1;
                Flash(IsOverridden(sel.Key)
                    ? "Saved. She reads your version from the next message on."
                    : "Cleared - back to the mod's own text for this block.");
            }

            GUI.enabled = IsOverridden(sel.Key) && !readOnly;
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

        static void DrawGate()
        {
            GUIStyle head = new GUIStyle(GUI.skin.label);
            head.fontStyle = FontStyle.Bold;
            head.fontSize = 15;
            head.wordWrap = true;

            GUIStyle body = new GUIStyle(GUI.skin.label);
            body.wordWrap = true;
            body.fontSize = 12;

            GUILayout.Space(8f);

            if (_gate == 0)
            {
                head.normal.textColor = new Color(1f, 0.35f, 0.35f);
                GUILayout.Label("\u26A0  STOP - THIS TAB CONTAINS HEAVY SPOILERS  \u26A0", head);
                GUILayout.Space(6f);
                GUILayout.Label(
                    "Everything the mod tells her is written out in here in plain words: who is "
                    + "really behind each of the four doors, what she is hiding from you on every "
                    + "level, how the endings are chosen, when she is allowed to hurt you, and "
                    + "the secrets she is under instruction never to volunteer.\n\n"
                    + "If you have not finished the game, reading this tab will spoil it "
                    + "permanently. There is no way to un-read it.\n\n"
                    + "Do not continue unless you know ABSOLUTELY what you are doing, and have "
                    + "the knowledge and the skill to edit and mess with raw model prompts.",
                    body);
                GUILayout.Space(10f);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("I understand the spoilers. Continue.", GUILayout.Height(30f), GUILayout.Width(330f)))
                    _gate = 1;
                if (GUILayout.Button("Take me back", GUILayout.Height(30f), GUILayout.Width(160f)))
                    OverlayMenu.GoToTab(0);
                GUILayout.EndHorizontal();
                return;
            }

            if (_gate == 1)
            {
                head.normal.textColor = new Color(1f, 0.72f, 0.25f);
                GUILayout.Label("\u26A0  SECOND WARNING - YOU ALMOST CERTAINLY DO NOT WANT THIS  \u26A0", head);
                GUILayout.Space(6f);
                GUILayout.Label(
                    "Realistically, the chances are that you are not a master prompt engineer, "
                    + "and that editing these blocks will ruin your own immersion and your own "
                    + "experience of the game. That is not a warning about breaking the mod. It "
                    + "is a warning about spending an evening making her worse and not "
                    + "understanding why.\n\n"
                    + "This is for people with a thousand-plus hours in SillyTavern and genuine "
                    + "AI prompt-engineering experience. That is a very small percentage of "
                    + "players. If you are not certain that describes you - just don't.\n\n"
                    + "Almost everything people arrive here wanting is already a supported "
                    + "setting somewhere safer:",
                    body);
                GUILayout.Label(
                    "     - Trying to make a small local model behave?  Use LOCAL MODEL MODE on "
                    + "the Model tab. It already replaces these blocks with one short brief "
                    + "written for exactly that.\n"
                    + "     - Want her written differently, or the game's systems changed?  The "
                    + "Setup, Voice, Model, She knows and Extra content tabs cover it, safely, "
                    + "with defaults you can return to.\n"
                    + "     - Just want a longer character bio?  Edit ONE block - \"Her persona, "
                    + "memories and secrets\" - and leave every other block alone.",
                    body);
                GUILayout.Space(10f);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Go back - I will use the normal settings", GUILayout.Height(30f), GUILayout.Width(330f)))
                { _gate = 0; OverlayMenu.GoToTab(0); }
                if (GUILayout.Button("I am that user. Continue.", GUILayout.Height(30f), GUILayout.Width(240f)))
                    _gate = 2;
                GUILayout.EndHorizontal();
                return;
            }

            head.normal.textColor = OverlayMenu.PromptPink;
            GUILayout.Label("\u26A0  LAST STEP - CLICK AND HOLD  \u26A0", head);
            GUILayout.Space(6f);
            GUILayout.Label(
                "Press and hold the button below for ten seconds. Let go at any point and it "
                + "starts again.\n\n"
                + "You only ever have to do this once. After it unlocks, the Prompts tab stays "
                + "open for good - including after you restart the game.",
                body);
            GUILayout.Space(12f);

            GUIStyle hold = new GUIStyle(GUI.skin.button);
            hold.fontSize = 14;
            hold.fontStyle = FontStyle.Bold;
            hold.wordWrap = true;

            float remaining = HOLD_SECONDS;
            if (_holding && _holdStart > 0f)
                remaining = HOLD_SECONDS - (Time.realtimeSinceStartup - _holdStart);
            if (remaining < 0f) remaining = 0f;

            string label = _holding
                ? "KEEP HOLDING...  " + remaining.ToString("0.0") + "s"
                : "CLICK AND HOLD FOR 10 SECONDS TO UNLOCK";

            Rect r = GUILayoutUtility.GetRect(new GUIContent(label), hold,
                GUILayout.Height(52f), GUILayout.Width(460f));

            // Tracked from raw events rather than through a RepeatButton, which
            // only reports held-ness on the events it happens to receive. A gate
            // whose timer silently stalls because the mouse was not moving would
            // be worse than no gate at all.
            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
            {
                _holding = true;
                _holdStart = Time.realtimeSinceStartup;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && _holding)
            {
                _holding = false;
                _holdStart = -1f;
            }
            if (_holding && e.type == EventType.Repaint && !r.Contains(e.mousePosition))
            {
                // Dragging off the button counts as letting go.
                _holding = false;
                _holdStart = -1f;
            }

            GUI.Button(r, label, hold);

            if (_holding)
            {
                // The filled bar is the only sign the hold is being counted.
                float done = 1f - (remaining / HOLD_SECONDS);
                Rect bar = new Rect(r.x + 3f, r.yMax - 7f, (r.width - 6f) * done, 4f);
                Color prev = GUI.color;
                GUI.color = OverlayMenu.PromptPink;
                GUI.DrawTexture(bar, Texture2D.whiteTexture);
                GUI.color = prev;

                if (remaining <= 0f)
                {
                    _holding = false;
                    _holdStart = -1f;
                    Load();
                    _unlocked = true;
                    Save();
                    _gate = 0;
                    Plugin.Log.LogWarning("Prompts: the editor was unlocked by the player. Prompt "
                        + "blocks can be edited from here on; expect that in any later bug report.");
                    Flash("Unlocked. This tab will not ask again.");
                }
            }

            GUILayout.Space(10f);
            if (GUILayout.Button("Actually, take me back", GUILayout.Height(26f), GUILayout.Width(240f)))
            { _gate = 0; _holding = false; _holdStart = -1f; OverlayMenu.GoToTab(0); }
        }

        static void Flash(string m)
        {
            _flash = m;
            _flashAt = Time.realtimeSinceStartup;
        }
    }
}
