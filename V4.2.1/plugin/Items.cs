// Giving items to the player.
//
// Why nothing happened. The plumbing was never broken: the game parses
// giving_to_player in ChatGPTConversation.ResolveChatGPTAzure, and
// NPCMasterBehavior_MainCharacter.ReceiveItem turns it into a real inventory
// add with a sound and a UI notice. What was missing is that the field is only
// ever documented in the server-side level prompt, and the mod replaces that
// server. ChatGPTConversation._initialPrompt is left at its stock "You are
// ChatGPT, a large language model trained by OpenAI.", so the model was never
// told the field exists. She wrote "here you go" in npc_reply_to_player and
// handed over nothing, on every character, at every level.
//
// Three engine rules this file is built around, all in
// NPCMasterBehavior_MainCharacter (ReceiveItemNameCheck at :646,
// ReceiveItemUINoticeMessage at :667):
//
//   Twenty characters. itemName.Length <= 20 or the gift is dropped without a
//   word. "a handful of cherry tomatoes" is 27 and never arrives, which looks
//   exactly like the bug we started with, so Repair resolves long prose down to
//   the real item name rather than letting the engine bin it.
//
//   Unknown names are legal. When itemLibrary has no match the game builds an
//   ad-hoc Item with IsAiGift = true and a generic sprite, so improvised gifts
//   are supported by design. That is why Repair does not hard-clamp to a
//   whitelist the way Schema does for npc_action - it only repairs what the
//   engine would silently discard.
//
//   Naming her real stock matters anyway. A match against itemLibrary picks up
//   the item's own artwork and, when she is holding it, removes it from her
//   inventory. So the prompt lists what she is actually carrying.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AI2UCustomAI
{
    internal static class Items
    {
        // Words the engine already treats as "no gift". Listed here so the mod
        // normalises them to an empty string before the engine has to, which
        // keeps the log honest about whether a gift was actually attempted.
        static readonly string[] NoneWords =
        {
            "null", "none", "nothing", "n/a", "na", "empty", "no item", "-",
            "没有", "无", "ninguno", "なし", "ничего", "nichts"
        };

        const int MaxNameLength = 20;

        static Communicator _comm;

        static Communicator Comm()
        {
            if (_comm == null) _comm = UnityEngine.Object.FindObjectOfType<Communicator>();
            return _comm;
        }

        // Communicator holds the master behaviour for whichever girl is loaded
        // in a private field. Going through it means we describe HER pockets
        // instead of merging every NPC in the scene - in the hub that would let
        // the catgirl offer the witch's cauldron reagents.
        static object Property()
        {
            try
            {
                Communicator c = Comm();
                if (c == null) return null;

                Traverse t = Traverse.Create(c);

                // npcMasterBehavior FIRST. The preference used to be the other way
                // round, which looks right and is not: ChangeNPC assigns the plain
                // field unconditionally (Communicator.cs:465), while the
                // _MainCharacter field's assignment at :468 sits behind
                // `if (CurrentLevel == 0)`. So off the hub it keeps whatever Awake
                // set at :205 and never moves again.
                //
                // On L99, where five characters share one scene, that meant the
                // gift block described the WRONG girl's pockets - she was told she
                // was carrying items she did not have, and Repair matched names
                // against the wrong pool. Levels 1-4 hid it (one character, both
                // fields identical) and so did the hub (:468 keeps it current),
                // which is exactly why it survived testing.
                object master = t.Field("npcMasterBehavior").GetValue();
                if (master == null) master = t.Field("npcMasterBehavior_MainCharacter").GetValue();
                if (master == null) return null;

                return Traverse.Create(master).Field("npcProperty").GetValue();
            }
            catch { return null; }
        }

        // Raw ItemName, not LocalizedItemName. Item.Equals compares against
        // both, so the English key resolves on any language setting, whereas a
        // localised string only resolves on the one it was read from.
        public static List<string> Carried()
        {
            List<string> names = new List<string>();
            try
            {
                object p = Property();
                if (p == null) return names;

                object inv = Traverse.Create(p).Property("NPCInventory").GetValue();
                if (inv == null) return names;

                IList slots = Traverse.Create(inv).Property("Slots").GetValue() as IList;
                if (slots == null) return names;

                foreach (object slot in slots)
                {
                    if (slot == null) continue;
                    object item = Traverse.Create(slot).Property("Item").GetValue();
                    if (item == null) continue;
                    Add(names, Traverse.Create(item).Property("ItemName").GetValue<string>());
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not read her inventory: " + e.Message);
            }
            return names;
        }

        // Every item this level knows about, used only to resolve a wordy gift
        // back to a real name. Not sent to the model: the libraries run to
        // hundreds of entries and listing them invites her to hand over quest
        // items she has never held.
        public static List<string> Known()
        {
            List<string> names = new List<string>();
            try
            {
                object p = Property();
                if (p == null) return names;

                object lib = Traverse.Create(p).Field("itemLibrary").GetValue();
                if (lib == null) return names;

                IDictionary d = Traverse.Create(lib).Property("Library").GetValue() as IDictionary;
                if (d == null) return names;

                foreach (object key in d.Keys)
                {
                    if (key == null) continue;
                    Add(names, Traverse.Create(key).Property("ItemName").GetValue<string>());
                }
            }
            catch { }
            return names;
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

        static bool IsNoneWord(string s)
        {
            for (int i = 0; i < NoneWords.Length; i++)
                if (string.Equals(s, NoneWords[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        // The system message that teaches the field. Returned even when her
        // inventory is empty, because improvised gifts still work and the field
        // still has to be documented - an empty stock only means she has nothing
        // of her own to offer yet.
        public static string Block()
        {
            if (Property() == null) return null;

            List<string> carried = Carried();

            StringBuilder sb = new StringBuilder();
            sb.Append("### GIVING ITEMS (authoritative)\n");
            sb.Append("You can hand physical objects to the player. To do it, set the ");
            sb.Append("giving_to_player field to the item's name. The engine then puts ");
            sb.Append("that item in the player's inventory for real.\n");
            sb.Append("- Saying you are giving something in npc_reply_to_player does ");
            sb.Append("NOTHING on its own. If you say \"here you go\" and leave ");
            sb.Append("giving_to_player empty, the player receives nothing and it reads ");
            sb.Append("as you refusing them.\n");
            sb.Append("- Set it to \"\" (empty) on every turn where you are not actually ");
            sb.Append("handing something over. Do not fill it just because an item was ");
            sb.Append("mentioned, and do not give the same item twice in a row.\n");
            sb.Append("- The name must be at most ").Append(MaxNameLength);
            sb.Append(" characters, and must be the bare item name only: ");
            sb.Append("\"Tomato\", not \"a fresh tomato from the basket\". ");
            sb.Append("One item per turn, no lists, no quantities, no descriptions.\n");

            if (carried.Count > 0)
            {
                sb.Append("- You are carrying these right now. Prefer these exact names, ");
                sb.Append("copied character for character: ");
                sb.Append(string.Join(", ", carried.ToArray())).Append('\n');
                sb.Append("- Giving one of those removes it from you, so only offer it once.\n");
            }
            else
            {
                sb.Append("- Your own bag is empty at the moment, so anything you give is ");
                sb.Append("something you fetch or improvise. Keep it to objects that make ");
                sb.Append("sense for where you are and who you are.\n");
            }

            return sb.ToString();
        }

        // Repairs the one field the engine accepts free text in. Deliberately
        // narrower than Schema.Clamp: an unrecognised name is legal here (it
        // becomes an IsAiGift item), so this only fixes what the engine would
        // otherwise throw away without telling anyone.
        public static void Repair(JObject o)
        {
            if (o == null) return;

            JToken tok = o["giving_to_player"];
            if (tok == null) return;

            string raw = tok.Type == JTokenType.Null ? "" : tok.ToString();
            string name = Normalize(raw);

            if (name.Length == 0)
            {
                if (raw.Trim().Length > 0)
                    Plugin.Log.LogInfo("Gift field held \"" + Trim(raw) + "\", read as no gift.");
                o["giving_to_player"] = "";
                return;
            }

            // The engine drops anything longer than 20 characters in silence, so
            // a wordy answer has to be resolved to a real name or it is a
            // guaranteed invisible failure.
            if (name.Length > MaxNameLength)
            {
                string resolved = Resolve(name);
                if (resolved != null)
                {
                    Plugin.Log.LogInfo("Gift \"" + Trim(name) + "\" was too long for the engine ("
                        + name.Length + " > " + MaxNameLength + "); resolved to \"" + resolved + "\".");
                    o["giving_to_player"] = resolved;
                    return;
                }

                Plugin.Log.LogWarning("Gift \"" + Trim(name) + "\" is " + name.Length
                    + " characters and matches no known item, so the engine would have"
                    + " dropped it silently. Cleared instead.");
                o["giving_to_player"] = "";
                return;
            }

            // Within the length limit: keep it, but prefer the library's own
            // casing so the item resolves to its real artwork instead of
            // becoming a generic AI gift.
            string canonical = Canonical(name);
            o["giving_to_player"] = canonical ?? name;

            Plugin.Log.LogInfo("She is giving: " + (canonical ?? name)
                + (canonical == null ? " (improvised - no library match)" : ""));
        }

        // Strips the wrappers models reach for, and applies the same two
        // reductions the engine does so the value we log is the value it acts
        // on.
        static string Normalize(string s)
        {
            if (s == null) return "";
            string v = s.Trim().Trim('"', '\'', '[', ']', '{', '}', '(', ')', '.', '!').Trim();

            // The engine splits on "giving" and keeps the far side; do it here
            // so a phrase like "giving_to_player: Tomato" does not survive as
            // half a field name.
            int g = v.IndexOf("giving", StringComparison.OrdinalIgnoreCase);
            if (g >= 0)
            {
                string tail = v.Substring(g + "giving".Length);
                tail = tail.TrimStart('_', ' ', ':', '-');
                if (tail.StartsWith("to_player", StringComparison.OrdinalIgnoreCase))
                    tail = tail.Substring("to_player".Length);
                if (tail.StartsWith("to player", StringComparison.OrdinalIgnoreCase))
                    tail = tail.Substring("to player".Length);
                v = tail.TrimStart('_', ' ', ':', '-').Trim();
            }

            // One item per turn: the engine keeps only the first comma segment.
            int comma = v.IndexOf(',');
            if (comma >= 0) v = v.Substring(0, comma).Trim();

            v = v.Replace('_', ' ').Trim();
            while (v.IndexOf("  ") >= 0) v = v.Replace("  ", " ");

            return IsNoneWord(v) ? "" : v;
        }

        // Finds the real item hiding inside a phrase. Longest match wins, so
        // "Necklace Box Piece C" beats a stray "Box" in the same sentence.
        static string Resolve(string phrase)
        {
            string hay = phrase.ToLowerInvariant();
            string best = null;

            List<string> pool = Carried();
            foreach (string k in Known()) Add(pool, k);

            for (int i = 0; i < pool.Count; i++)
            {
                string cand = pool[i];
                if (cand.Length > MaxNameLength) continue;
                if (hay.IndexOf(cand.ToLowerInvariant(), StringComparison.Ordinal) < 0) continue;
                if (best == null || cand.Length > best.Length) best = cand;
            }
            return best;
        }

        static string Canonical(string name)
        {
            List<string> pool = Carried();
            foreach (string k in Known()) Add(pool, k);

            for (int i = 0; i < pool.Count; i++)
                if (string.Equals(pool[i], name, StringComparison.OrdinalIgnoreCase))
                    return pool[i];
            return null;
        }

        static string Trim(string s)
        {
            if (s == null) return "";
            s = s.Replace("\n", " ").Replace("\r", " ").Trim();
            return s.Length <= 80 ? s : s.Substring(0, 80) + "...";
        }

        // Logged once per stock change so a missing gift can be told apart from
        // a gift she never had to give.
        static string _reported;

        public static void Report()
        {
            if (Property() == null) return;

            List<string> carried = Carried();
            string line = string.Join(", ", carried.ToArray());
            if (line == _reported) return;

            _reported = line;
            Plugin.Log.LogInfo("Her inventory (" + carried.Count + " item(s)): "
                + (carried.Count == 0 ? "(empty - gifts will be improvised)" : line));
        }
    }
}
