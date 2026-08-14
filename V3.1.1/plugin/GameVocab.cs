using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace AI2UCustomAI
{
    // The authoritative vocabulary the game will actually act on.
    //
    // Everything here is read off the live objects at runtime instead of being
    // guessed from the server's prompt text. That matters because the game
    // silently discards anything it does not recognise: an unknown npc_action
    // falls through NPCController.ShowAction to NPCActivities.Other, and an
    // unknown npc_target_location makes GetTargetAreaTriggerTransform return
    // null so the NPC never moves. Both look like the NPC ignoring an order.
    public static class GameVocab
    {
        // npc_body_animation is compiled into a string switch in
        // NPCController.ShowAnimation, so there is no collection to reflect.
        // These are the exact 13 literals that switch compares against;
        // anything else returns before touching the Animator.
        static readonly string[] BodyAnimations =
        {
            "idle", "idling", "idly", "chill_idle", "angry_idle", "talk",
            "nod", "laugh", "shy", "stretch", "cheers", "dance", "troublesome"
        };

        // Only "extremely furious" changes behaviour (speed 6 + very_angry
        // status). The rest are the tone words Constants.cs defines, kept so
        // the model has somewhere calmer to sit.
        //
        // "happy" and "normal" belong here even though the engine does nothing
        // special with them: anything it does not recognise as one of the three
        // angry words counts as not-angry, and the game's own shipped prompts
        // list all six. Leaving them out mattered because Clamp snaps to this
        // list, so a model reaching for "happy" had it rewritten to a tone it
        // did not choose - she had no way to read as positively calm.
        static readonly string[] AngryLevels =
        {
            "happy", "normal", "chill", "annoyed", "furious", "extremely furious"
        };

        static readonly string[] Favorability =
        {
            "very negative", "negative", "neutral", "positive", "very positive"
        };

        public static List<string> Actions = new List<string>();
        public static List<string> Locations = new List<string>();
        public static List<string> Faces = new List<string>();

        public static bool Discovered { get { return Actions.Count > 0; } }
        static string _signature = "";

        public static List<string> For(string field)
        {
            switch (field)
            {
                case "npc_action":
                    return Actions.Count > 0 ? Actions : null;
                case "npc_target_location":
                    return Locations.Count > 0 ? Locations : null;
                case "npc_face_expression":
                    return Faces.Count > 0 ? Faces : null;
                case "npc_body_animation":
                    return new List<string>(BodyAnimations);
                case "angry_level":
                    return new List<string>(AngryLevels);
                case "favorability_change":
                    return new List<string>(Favorability);
            }
            return null;
        }

        // Cheap enough to call before each request; bails out early once the
        // scene's vocabulary stops changing.
        public static void Refresh()
        {
            try
            {
                Type nc = FindType("NPCController");
                if (nc == null) return;

                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(nc);
                if (all == null || all.Length == 0) return;

                List<string> actions = new List<string>();
                List<string> locations = new List<string>();
                List<string> faces = new List<string>();

                for (int i = 0; i < all.Length; i++)
                {
                    ReadActivities(all[i], actions);
                    ReadLocations(all[i], locations);
                    ReadFaces(all[i], faces);
                }

                // ShowAction special-cases this one instead of looking it up
                // in m_locationDictionary, so it is always legal.
                Add(locations, "player_location");

                if (actions.Count == 0) return;

                string sig = actions.Count + "/" + locations.Count + "/" + faces.Count;
                Actions = actions;
                Locations = locations;
                Faces = faces;

                if (sig != _signature)
                {
                    _signature = sig;
                    Report();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Vocabulary discovery failed: " + e.Message);
            }
        }

        static void ReadActivities(object controller, List<string> into)
        {
            IDictionary d = Field(controller, "m_npcAllActivities") as IDictionary;
            if (d == null) return;
            foreach (object k in d.Keys) Add(into, k as string);
        }

        static void ReadLocations(object controller, List<string> into)
        {
            IDictionary d = Field(controller, "m_locationDictionary") as IDictionary;
            if (d == null) return;
            foreach (object k in d.Keys) Add(into, k as string);
        }

        static void ReadFaces(object controller, List<string> into)
        {
            object fc = Field(controller, "facialController");
            if (fc == null) return;
            Array groups = Field(fc, "m_expressionGroupList") as Array;
            if (groups == null) return;

            for (int i = 0; i < groups.Length; i++)
            {
                object g = groups.GetValue(i);
                if (g == null) continue;
                Add(into, Field(g, "name") as string);
            }
        }

        static object Field(object target, string name)
        {
            if (target == null) return null;
            Type t = target.GetType();
            while (t != null)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f.GetValue(target);
                t = t.BaseType;
            }
            return null;
        }

        static Type FindType(string name)
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try
                {
                    Type t = asms[i].GetType(name, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        static void Add(List<string> list, string v)
        {
            if (string.IsNullOrEmpty(v)) return;
            v = v.Trim();
            if (v.Length == 0) return;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], v, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(v);
        }

        static void Report()
        {
            Plugin.Log.LogInfo("Vocabulary read from the live scene:");
            Plugin.Log.LogInfo("  npc_action           (" + Actions.Count + "): " + Join(Actions));
            Plugin.Log.LogInfo("  npc_target_location  (" + Locations.Count + "): " + Join(Locations));
            Plugin.Log.LogInfo("  npc_face_expression  (" + Faces.Count + "): " + Join(Faces));
        }

        static string Join(List<string> l) { return string.Join(", ", l.ToArray()); }

        // The contract appended to the system prompt. Written as a hard
        // whitelist because the server's own prompt sometimes leaves
        // placeholders like {GeneratedRoom} unresolved, and models treat a
        // half-filled list as an invitation to improvise.
        public static string Contract()
        {
            if (!Discovered) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append("\n\n### ENGINE CONSTRAINTS (authoritative)\n");
            sb.Append("The values below are read directly from the running game. ");
            sb.Append("Any other value is silently discarded by the engine, ");
            sb.Append("which makes the character appear to ignore the player. ");
            sb.Append("Copy them exactly: lowercase, underscores, no paraphrasing, ");
            sb.Append("no inventing, no translating.\n");

            Line(sb, "npc_action", Actions);
            Line(sb, "npc_target_location", Locations);
            Line(sb, "npc_face_expression", Faces);
            Line(sb, "npc_body_animation", new List<string>(BodyAnimations));
            Line(sb, "angry_level", new List<string>(AngryLevels));
            Line(sb, "favorability_change", new List<string>(Favorability));

            sb.Append("\nMovement rules:\n");
            sb.Append("- To follow the player, npc_action MUST be exactly ");
            sb.Append("\"following_player\" (or \"following_player_closely\" to stay near).\n");
            sb.Append("- To walk somewhere, set npc_action \"walking\" AND ");
            sb.Append("npc_target_location to one of the locations listed above.\n");
            sb.Append("- To approach the player, npc_target_location \"player_location\".\n");
            sb.Append("- If no movement is wanted, use \"other\" and leave ");
            sb.Append("npc_target_location as an empty string.\n");
            sb.Append("- Never put a location name in npc_action, and never put ");
            sb.Append("an action name in npc_target_location.\n");

            Gates(sb);
            Encounters(sb);
            ReplyRules(sb);
            return sb.ToString();
        }

        // Constraints on the reply TEXT itself. These are not vocabulary, they
        // are ways a perfectly good reply gets thrown away, and two of them are
        // punishing enough to be worth the tokens.
        static void ReplyRules(StringBuilder sb)
        {
            sb.Append("\nHow npc_reply_to_player is processed before the player sees it:\n");

            // NPCMasterBehavior_MainCharacter.cs:936 voids the turn on a bracket,
            // and Communicator.cs:409 starts the final chase on the second void.
            sb.Append("- NEVER use a square bracket. The game discards the entire reply if the ");
            sb.Append("text contains [ or ], and after the second discarded reply it puts the NPC ");
            sb.Append("into a permanent hunt for the player. Nothing you gain from a bracket is ");
            sb.Append("worth that.\n");

            // NPCMasterBehavior_MainCharacter.cs:941-966.
            sb.Append("- Round brackets are deleted along with everything inside them, so stage ");
            sb.Append("directions like (smiles) silently vanish. Put actions in the animation and ");
            sb.Append("expression fields instead, and keep the text to spoken words.\n");

            sb.Append("- Underscores in the text become spaces, and the names of these JSON fields ");
            sb.Append("are stripped out of it, so never mention a field name in what she says.\n");

            // NPCController.cs:970-973 early-returns on an unchanged value, and
            // :974 updates the cache before matching, so a bad value also eats
            // the next identical good one.
            sb.Append("- npc_body_animation is ignored when it repeats the previous turn's value, ");
            sb.Append("and while she is sitting. Vary it, or send an empty string when no new ");
            sb.Append("animation is wanted.\n");
        }

        // Encounter-scoped reply fields.
        //
        // Unlike the level gates these are not keyed on GameManager.CurrentLevel:
        // the scripted encounters can run inside a level, so the only honest test
        // is whether the behaviour driving the conversation is actually in the
        // scene right now. Found by type name through Harmony rather than a
        // direct reference, which keeps this compiling against either build's
        // assembly without assuming the class survived stripping.
        struct Encounter
        {
            public string Type; public string Text;
            public Encounter(string t, string x) { Type = t; Text = x; }
        }

        static readonly Encounter[] AllEncounters =
        {
            new Encounter("NPCMasterBehavior_DarkSiren",
                "- is_soothed: set true ONLY once the player has genuinely calmed her. This is the "
                + "single field that decides the outcome of this encounter. If the conversation ends "
                + "without it, the attempt counts as a failure, and a second failure kills the "
                + "player outright. It is ignored in the first 5 exchanges, so do not rush it.\n"
                + "- should_end_conversation: set true to close the encounter. Only ever send this "
                + "together with is_soothed true, because ending while unsoothed is scored as the "
                + "failure described above.\n"),

            new Encounter("NPCMasterBehavior_WakeUpDialogue",
                "- should_end_conversation: set true when this scripted exchange is finished. "
                + "Harmless here - it simply closes the scene rather than waiting out the turn "
                + "limit.\n"),

            // GuidingEddie.cs:259-381. Three of these contain a space, not an
            // underscore, and an unrecognised entry is dropped while its valid
            // neighbours still run - so a typo produces a silently truncated
            // wrong route.
            new Encounter("NPCMasterBehavior_GuidingEddie",
                "- npc_action_chain: an ARRAY of movement steps for this minigame, carried out in "
                + "order. Allowed entries, exactly: \"forward\", \"backward\", \"turn left\", "
                + "\"turn right\", \"north\", \"south\", \"east\", \"west\", \"take exit\". Note the "
                + "spaces - \"turn left\" is not \"turn_left\". Any entry that is not on this list "
                + "is dropped while the rest still execute, which walks the wrong route, so send "
                + "only these strings and omit the field when not routing.\n"),
        };

        static void Encounters(StringBuilder sb)
        {
            bool any = false;
            for (int i = 0; i < AllEncounters.Length; i++)
            {
                if (!InScene(AllEncounters[i].Type)) continue;
                if (!any)
                {
                    sb.Append("\nThis conversation is a scripted encounter, which adds these fields:\n");
                    any = true;
                }
                sb.Append(AllEncounters[i].Text);
            }
        }

        static bool InScene(string typeName)
        {
            try
            {
                Type t = HarmonyLib.AccessTools.TypeByName(typeName);
                if (t == null) return false;
                UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(t);
                return found != null && found.Length > 0;
            }
            catch (Exception) { return false; }
        }

        // The progression gates. Each is a boolean the engine reads off the
        // reply to unlock something physical - a door, a scroll, an escape pod.
        //
        // These were missing from the contract entirely until now, which is why
        // asking her to open the exit door never worked no matter how the
        // request was phrased: there was no field in the reply for her to set,
        // so the refusal was structural rather than a decision she was making.
        //
        // Emitted per level on purpose. ChatGPTConversation only parses each one
        // on its own level (allow_exit_door_open is read when CurrentLevel == 1
        // and discarded otherwise), so listing them all everywhere would invite
        // the model to spend a field that cannot land - and would make it lie in
        // OOC mode, where the whole point is that what it reports is true.
        struct Gate
        {
            public int Level; public string Field; public float Trust; public string Unlocks; public string Extra;
            public Gate(int lv, string f, float t, string u, string x)
            { Level = lv; Field = f; Trust = t; Unlocks = u; Extra = x; }
        }

        static readonly Gate[] AllGates =
        {
            new Gate(1,  "allow_exit_door_open",         10f, "the apartment exit door",
                     " Also requires at least 11 messages from the player in this conversation."),
            new Gate(2,  "should_disclose_magic_scroll", 10f, "the magic scroll's location", ""),
            new Gate(3,  "allow_escape_pod_access",      20f, "the escape pod", ""),
            new Gate(99, "allow_exit_door_open",         10f, "the exit door (Rorre)",
                     " Also requires at least 11 messages from the player in this conversation."),
            new Gate(99, "allow_locked_room_access",     40f, "the locked room (Estelle)", ""),
        };

        static void Gates(StringBuilder sb)
        {
            int level = -1;
            try { level = GameManager.CurrentLevel; } catch (Exception) { return; }
            if (level < 0) return;

            bool any = false;
            for (int i = 0; i < AllGates.Length; i++)
            {
                if (AllGates[i].Level != level) continue;
                if (!any)
                {
                    sb.Append("\nUnlocks available on this level (booleans, default false):\n");
                    any = true;
                }
                sb.Append("- ").Append(AllGates[i].Field).Append(": set true to open ")
                  .Append(AllGates[i].Unlocks).Append(". The engine ignores it unless trust is above ")
                  .Append(AllGates[i].Trust.ToString("F0")).Append('.').Append(AllGates[i].Extra).Append('\n');
            }

            if (!any) return;
            sb.Append("- Setting one of these is the ONLY way to actually open it. ");
            sb.Append("Saying so in npc_reply_to_player does nothing on its own, ");
            sb.Append("so never describe an unlock you did not set the field for.\n");
        }

        static void Line(StringBuilder sb, string field, List<string> vals)
        {
            if (vals == null || vals.Count == 0) return;
            sb.Append("- ").Append(field).Append(": ").Append(Join(vals)).Append('\n');
        }
    }
}
