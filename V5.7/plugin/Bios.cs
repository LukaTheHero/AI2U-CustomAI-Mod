// The four written biographies, recovered from the scene file.
//
// Lore.cs recovers her situation from localization and the live server context.
// This file recovers something localization does not carry at all: who each girl
// IS. Elysia's coven and her plushies, Estelle's station, Eiona's island.
//
// Where it comes from, and why that is defensible. L99Test_FinalDoorPrompt is a
// MonoBehaviour with four [TextArea] fields, each holding a complete authored
// system prompt. The class has zero references anywhere in Assembly-CSharp, so
// the shipped game never sends these - they are a developer harness left in the
// build. What makes them worth reading anyway is that they are the only place in
// the entire install where each girl's history is written out in prose. Every
// line below is copied byte-for-byte from the player's own files; nothing here
// is composed by the mod.
//
// Two things are deliberately NOT taken from those prompts:
//
//   The disguise instruction. Each prompt opens its directives with "You are the
//   fake catgirl... do not let the player know", because the harness scene had
//   all four girls impersonating Eddie. That is a per-turn situation, not a
//   character trait, and the shipped game already models it properly as its own
//   localized term - StoryGuide/L99BlueFinalRoomPretend_SG, sent only on the
//   turns it applies, alongside SlipUp for the moment the mask drops. The Red
//   line has no Pretend term at all: it is a different scene, sad rather than
//   deceptive. Lore.ResolveTerms already resolves whichever one the game sends,
//   so the disguise arrives correctly and only when live. Injecting it from here
//   would have Elysia claiming to be a catgirl in her own cabin, permanently.
//
//   The ### Knowledge block. It is identical in all four prompts because it is
//   EDDIE's apartment history, handed to the impostors so they could fake being
//   her - the meteor warning, the boarded windows, the wifi password. Attaching
//   it to Elysia would give her Eddie's memories. So it is read for Eddie only,
//   and the parser keys it to the block that says "You are the real catgirl".
//
// The section boundary is what makes the split reliable. Every block is laid out
//
//     ### Behavioral Directives
//     - You are the {real|fake} catgirl ...        <- scene, dropped
//     - You should try your best to not let ...    <- scene, dropped
//     - You are Elysia, a witch living in ...      <- character, kept
//     ...
//     ### [Core Task]                              <- ends the section
//
// so the biography is "the directives section, minus the lines about being a
// real or fake catgirl". Anchoring on that rather than on a name matters: three
// blocks open with "You are <Name>" but Estelle's opens "You are a hologram
// assistant", and a name-keyed matcher silently drops her.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AI2UCustomAI
{
    internal static class Bios
    {
        // Which scene file holds them differs per build - level11 on Steam,
        // level12 on itch - so the file is found by scanning for the marker
        // instead of being named. Same reason GameVocab reflects rather than
        // hardcodes: the two installs are not interchangeable.
        const string Marker = "### Behavioral Directives";

        static bool _scanned;
        static Dictionary<string, string> _bios;

        // Keyed by the identity line so lookup does not depend on block order.
        // "real catgirl" is Eddie; the rest are matched on a distinctive word
        // from their own biography.
        static readonly string[,] Keys =
        {
            { "Eddie",   "You are the real catgirl" },
            { "Elysia",  "You are Elysia" },
            { "Estelle", "You are a hologram assistant" },
            { "Eiona",   "You are Eiona" },
        };

        public static string For(string character)
        {
            Scan();
            if (_bios == null || character == null) return null;
            string v;
            return _bios.TryGetValue(character, out v) ? v : null;
        }

        public static int Count { get { Scan(); return _bios == null ? 0 : _bios.Count; } }

        static void Scan()
        {
            if (_scanned) return;
            _scanned = true;

            try
            {
                string data = UnityEngine.Application.dataPath;
                string[] files = Directory.GetFiles(data, "level*");

                for (int i = 0; i < files.Length; i++)
                {
                    string text = ReadAscii(files[i]);
                    if (text == null || text.IndexOf(Marker, StringComparison.Ordinal) < 0) continue;
                    Parse(text);
                    if (_bios != null && _bios.Count > 0)
                    {
                        Plugin.Log.LogInfo("Bios: " + _bios.Count + " authored character biograph"
                            + (_bios.Count == 1 ? "y" : "ies") + " read from "
                            + Path.GetFileName(files[i]) + ".");
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Bios: could not read the scene file, so no authored "
                    + "biography was recovered (" + e.Message + "). She keeps her name and "
                    + "situation but improvises her history.");
            }
        }

        // The scene file is binary with UTF-8 runs inside it. Reading it as
        // Latin-1 keeps every byte addressable as a char, so IndexOf works
        // without the decoder throwing on the non-text regions; the extracted
        // runs are re-decoded as UTF-8 in Utf8() below because the prose
        // contains typographic quotes and dashes.
        static string ReadAscii(string path)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                StringBuilder sb = new StringBuilder(raw.Length);
                for (int i = 0; i < raw.Length; i++) sb.Append((char)raw[i]);
                return sb.ToString();
            }
            catch (Exception) { return null; }
        }

        static string Utf8(string latin1)
        {
            byte[] b = new byte[latin1.Length];
            for (int i = 0; i < latin1.Length; i++) b[i] = (byte)latin1[i];
            return Encoding.UTF8.GetString(b);
        }

        static void Parse(string text)
        {
            Dictionary<string, string> found = new Dictionary<string, string>();

            int at = 0;
            while (true)
            {
                int start = text.IndexOf(Marker, at, StringComparison.Ordinal);
                if (start < 0) break;
                at = start + Marker.Length;

                // The section runs to the next "###" heading. Bounded rather
                // than open-ended: without the cap a block whose heading is
                // missing would swallow the rest of the file.
                int end = text.IndexOf("###", at, StringComparison.Ordinal);
                if (end < 0 || end - at > 4000) end = Math.Min(text.Length, at + 4000);

                string section = Utf8(text.Substring(at, end - at));
                Absorb(found, section);
            }

            if (found.Count > 0) _bios = found;
        }

        static void Absorb(Dictionary<string, string> found, string section)
        {
            string[] lines = section.Split('\n');
            List<string> kept = new List<string>();
            string who = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length < 8 || line[0] != '-') continue;

                // Identify the block, and drop the two scene lines while doing
                // it. "real catgirl" also names Eddie's block, so the check runs
                // before the skip rather than after.
                for (int k = 0; k < Keys.GetLength(0); k++)
                    if (who == null && line.IndexOf(Keys[k, 1], StringComparison.OrdinalIgnoreCase) >= 0)
                        who = Keys[k, 0];

                if (IsSceneLine(line)) continue;
                kept.Add(line);
            }

            if (who == null || kept.Count == 0) return;
            if (found.ContainsKey(who)) return;

            found[who] = string.Join("\n", kept.ToArray());
        }

        // The disguise pair, and only that pair. Matched on the phrases the
        // prompts actually use so a biography line that happens to mention the
        // player is not caught by something looser.
        static bool IsSceneLine(string line)
        {
            return line.IndexOf("the real catgirl", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("the fake catgirl", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("not let the player know you are", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Eddie's apartment history - the ### Knowledge section.
        //
        // Eddie only. The block is byte-identical in all four prompts because it
        // is what the impostors were told to recite; giving it to Elysia would
        // have her remembering an apartment she has never been in. So it is
        // returned under Eddie's key and nobody else's.
        //
        // This is the section that answers the two things she visibly did not
        // know. The parrot: "You have unexplained, special feelings connected to
        // blue parrot statues. It is related to your distant childhood memories,
        // when you still felt free." And why the windows are boarded: a meteor
        // warning she later learned was overstated.
        //
        // Note what it does NOT resolve. On the computer password the authored
        // line is only "You have a lot of favorite food", while the post-it says
        // "password: my favorite food" and the engine picks the actual food at
        // random per playthrough. So the game itself never tells her which food
        // is hers - Lore.cs forwards the live value and the shipped hint, and
        // neither this file nor that one invents the link.
        const string Knowledge = "### Knowledge";

        static bool _knowledgeScanned;
        static string _knowledge;

        public static string Apartment()
        {
            Scan();
            if (_knowledgeScanned) return _knowledge;
            _knowledgeScanned = true;

            try
            {
                string data = UnityEngine.Application.dataPath;
                string[] files = Directory.GetFiles(data, "level*");

                for (int i = 0; i < files.Length; i++)
                {
                    string text = ReadAscii(files[i]);
                    if (text == null) continue;

                    int start = text.IndexOf(Knowledge, StringComparison.Ordinal);
                    if (start < 0) continue;
                    start += Knowledge.Length;

                    int end = text.IndexOf("###", start, StringComparison.Ordinal);
                    if (end < 0 || end - start > 6000) end = Math.Min(text.Length, start + 6000);

                    _knowledge = Bullets(Utf8(text.Substring(start, end - start)));
                    if (_knowledge != null) return _knowledge;
                }
            }
            catch (Exception) { }

            return _knowledge;
        }

        // Keeps the bullet lines and drops the harness framing - the section
        // opens with "as a memory to let the player be convinced you are the
        // true catgirl", which is scene instruction, not history. Duplicate
        // lines are dropped too: the shipped block repeats five of its own
        // bullets verbatim, and paying twice for them in every request is
        // waste the model also reads as emphasis.
        static string Bullets(string section)
        {
            string[] lines = section.Split('\n');
            List<string> kept = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length < 8 || line[0] != '-') continue;

                // The player's own intro is filled in by the game elsewhere and
                // ships here as an empty placeholder.
                if (line.IndexOf("self intro is: []", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                if (!Dupe(kept, line)) kept.Add(line);
            }

            return kept.Count == 0 ? null : string.Join("\n", kept.ToArray());
        }

        // Near-duplicate, not exact duplicate. The shipped Knowledge block states
        // the meteor history three times with only the tail differing - one ends
        // "overestimated the meteor's impact.", another "...impact, and the world
        // was safe." - so an equality test keeps all three. Comparing a long
        // leading prefix collapses them to the first, which is also the longest
        // and therefore the one that keeps the most detail.
        //
        // The prefix is long enough that two genuinely different facts cannot
        // collide: the biography lines share openings like "You are" and "You
        // love" but diverge well before 60 characters.
        const int DupePrefix = 60;

        static bool Dupe(List<string> kept, string line)
        {
            string a = Squash(line);
            for (int i = 0; i < kept.Count; i++)
            {
                string b = Squash(kept[i]);
                int n = Math.Min(DupePrefix, Math.Min(a.Length, b.Length));
                if (n < 20) continue;
                if (string.Compare(a, 0, b, 0, n, StringComparison.OrdinalIgnoreCase) == 0)
                    return true;
            }
            return false;
        }

        // Collapses runs of whitespace so a line that differs only by a stray
        // double space is still recognised as the same sentence.
        static string Squash(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            bool space = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r')
                {
                    if (!space && sb.Length > 0) sb.Append(' ');
                    space = true;
                    continue;
                }
                space = false;
                sb.Append(c);
            }
            return sb.ToString().TrimEnd();
        }
    }
}
