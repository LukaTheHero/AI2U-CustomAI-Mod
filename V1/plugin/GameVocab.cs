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
        static readonly string[] AngryLevels =
        {
            "chill", "annoyed", "furious", "extremely furious"
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
            return sb.ToString();
        }

        static void Line(StringBuilder sb, string field, List<string> vals)
        {
            if (vals == null || vals.Count == 0) return;
            sb.Append("- ").Append(field).Append(": ").Append(Join(vals)).Append('\n');
        }
    }
}
