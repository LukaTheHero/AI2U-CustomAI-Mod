// What she is supposed to already know.
//
// This is the largest hole in the mod, and it is a hole rather than a bug: the
// stock game sends almost no character information in the chat array at all.
// Communicator.cs:165 seeds every character's history slot 0 with
//
//     new TextMessage("system", string.Empty)
//
// and nothing in the shipped build ever replaces it - ChatGPTConversation
// ._initialPrompt keeps its factory default about being ChatGPT. The persona,
// the setting, the memories, the trust, the room map and every puzzle answer
// travel instead in one HTTP header, x-token, which the vendor's own server
// expands into the real system prompt before it reaches a model.
//
// We do not reach that server. So the model we do reach was being handed an
// empty system message and a name, and asked to improvise a person. That is why
// she does not know the password to her own computer, does not know why the
// windows are boarded up, and has nothing to say about the blue parrot statue
// beyond reading its inventory label back.
//
// Everything below is recovered locally. Two sources, both already in the game:
//
//   I2 Localization terms, shipped in resources.assets. 260 StoryGuide/* blocks
//   of authored prose, one per character per situation, plus every item's
//   description. This is real writing by the game's authors, not a guess.
//
//   ServerContextManager.GetCurrentServerContext(), the object the game builds
//   to fill that header. It is fully populated by the time our patch runs, and
//   it holds the per-level secrets: the PC password, the wifi password, the
//   hidden-room passcode (SecretPswd, :490 - there is no safe anywhere in the
//   game), which room was generated, the potion recipe, the ship's systems.
//
// One caution that shaped the whole file. The game's L1-L4 code hands the
// server a raw term NAME rather than resolved text (GetInitStoryGuide returns a
// plain string, not a LocalizedString - see CHANGELOG entry 13), so the vendor
// server must be resolving terms itself. Bypassing it, we have to resolve them
// here, and a term that fails to resolve must never be forwarded: sending the
// literal "StoryGuide/L1StoryGuide" into a prompt teaches the model that its
// own instructions are gibberish. Every lookup here returns null on failure and
// every caller drops a null.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace AI2UCustomAI
{
    internal static class Lore
    {
        // I2's own manager, found by its full name on purpose. The game also
        // ships Michsky.DreamOS.LocalizationManager for the in-game desktop, and
        // a bare TypeByName("LocalizationManager") can bind that one instead -
        // it has no GetTranslation, so the whole feature would silently go dark.
        const string I2Manager = "I2.Loc.LocalizationManager";

        static MethodInfo _translate;
        static bool _translateLooked;

        // GetTranslation has one overload with seven optional parameters. Filling
        // them from ParameterInfo.DefaultValue rather than hardcoding seven
        // literals means a build that adds or reorders an optional argument still
        // works instead of throwing on arity.
        static string Tr(string term)
        {
            if (string.IsNullOrEmpty(term)) return null;

            try
            {
                if (!_translateLooked)
                {
                    _translateLooked = true;
                    Type t = AccessTools.TypeByName(I2Manager);
                    if (t != null)
                    {
                        MethodInfo[] all = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
                        for (int i = 0; i < all.Length; i++)
                        {
                            if (all[i].Name != "GetTranslation") continue;
                            ParameterInfo[] p = all[i].GetParameters();
                            if (p.Length >= 1 && p[0].ParameterType == typeof(string))
                            {
                                _translate = all[i];
                                break;
                            }
                        }
                    }
                    if (_translate == null)
                        Plugin.Log.LogWarning("Lore: I2 LocalizationManager.GetTranslation not found - "
                            + "authored story guides and item descriptions cannot be read this session.");
                }
                if (_translate == null) return null;

                ParameterInfo[] ps = _translate.GetParameters();
                object[] args = new object[ps.Length];
                args[0] = term;
                for (int i = 1; i < ps.Length; i++) args[i] = ps[i].DefaultValue;

                string s = _translate.Invoke(null, args) as string;
                return Clean(s, term);
            }
            catch (Exception) { return null; }
        }

        // I2 hands back the term itself when a lookup misses under some settings,
        // and several shipped terms are deliberate fragments with trailing spaces.
        static string Clean(string s, string term)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            if (s.Length == 0) return null;
            if (s == term) return null;
            if (s.IndexOf('/') >= 0 && s.StartsWith("StoryGuide/", StringComparison.Ordinal)) return null;
            return s;
        }

        // The opening turn ships term NAMES instead of prose on levels 1-4.
        //
        // GetInitStoryGuide (NPCMasterBehavior_Main_L1.cs:468) returns the plain
        // string "StoryGuide/L1StoryGuide", and GetInitSentenceFromPlayer does the
        // same for L1InitSentence. The hub and L99 paths declare those as
        // LocalizedString and so come out as real sentences, and the event-driven
        // guides on the very same level do call GetTranslation - so this is
        // specific to the first turn, which is the turn carrying her persona.
        //
        // Vanilla it costs nothing: the level prompt lives on the vendor's server,
        // which resolves keys itself. Pointed anywhere else the model is handed the
        // literal characters "StoryGuide/L1StoryGuide", which tell it nothing, and
        // her persona silently never arrives.
        //
        // So any story-guide field whose whole value is a term name is replaced by
        // what the game's own localization table says under that name. A field that
        // already holds prose is untouched, an unknown key is left exactly as it
        // was, and nothing here invents wording - a miss stays a miss.
        static readonly string[] TermFields = { "story_guide", "sentence_from_player" };

        public static string ResolveTerms(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            if (json.IndexOf(Prefix, StringComparison.Ordinal) < 0) return json;

            string s = json;

            // Embedded keys first. ResolveField below only fires when the term name
            // is the ENTIRE field value, which is the normal case - but the game
            // also concatenates keys into longer sentences, and those can never
            // match a whole-value test.
            s = ResolveEmbedded(s);

            for (int i = 0; i < TermFields.Length; i++) s = ResolveField(s, TermFields[i]);
            return s;
        }

        // Term keys the game concatenates into a larger string instead of passing
        // on their own.
        //
        // GetAngryModePrompt (NPCMasterBehavior_MainCharacter.cs:456-464) declares
        // its key as a plain `string` rather than a LocalizedString, so all 13 call
        // sites paste the literal characters "StoryGuide/AngryModePrompt" into the
        // prompt. Every one of them builds a longer value around it, so the
        // whole-value path misses, calls Tr() on the concatenation, fails, and logs
        // a "term did not resolve" warning that reads like a lookup failure rather
        // than the concatenation bug it actually is.
        //
        // Fixed literals only - no pattern matching. Each entry is a key the game
        // is known to leak, so a miss here cannot silently rewrite anything else.
        static readonly string[] EmbeddedTerms =
        {
            "StoryGuide/AngryModePrompt",
        };

        static string ResolveEmbedded(string json)
        {
            string s = json;

            for (int i = 0; i < EmbeddedTerms.Length; i++)
            {
                string key = EmbeddedTerms[i];
                if (s.IndexOf(key, StringComparison.Ordinal) < 0) continue;

                string text = Tr(key);
                if (text == null)
                {
                    Plugin.Log.LogWarning("Lore: the game leaked the raw term \"" + key
                        + "\" into the request and the localisation table has no text for it, "
                        + "so it was left as-is rather than guessed at.");
                    continue;
                }

                // Same sanitising as ResolveField: a quote or backslash would break
                // the JSON envelope this sits inside.
                text = text.Replace("\\", " ").Replace("\"", "'");

                s = s.Replace(key, text);
                Plugin.Log.LogInfo("Lore: resolved the embedded term \"" + key
                    + "\" that the game passed as a literal string.");
            }

            return s;
        }

        const string Prefix = "StoryGuide/";

        static string ResolveField(string json, string field)
        {
            string open = "\"" + field + "\":\"";
            int at = json.IndexOf(open, StringComparison.Ordinal);
            if (at < 0) return json;

            int from = at + open.Length;
            int end = json.IndexOf('"', from);
            if (end <= from) return json;

            string val = json.Substring(from, end - from);

            // Some senders wrap the guide in parentheses. Kept exactly as found,
            // because the game's own resolved guides are parenthesised too and the
            // model has been reading them that way all session.
            string lead = "", tail = "";
            if (val.Length > 1 && val[0] == '(' && val[val.Length - 1] == ')')
            {
                lead = "("; tail = ")";
                val = val.Substring(1, val.Length - 2);
            }

            val = val.Trim();
            if (!val.StartsWith(Prefix, StringComparison.Ordinal)) return json;

            // Term names legitimately contain spaces, so the whole field value is
            // the candidate key - never a token split out of it.
            string text = Tr(val);
            if (text == null)
            {
                Plugin.Log.LogWarning("Lore: " + field + " arrived as the unresolved term \""
                    + val + "\" and the localization table has no text for it, so it was left "
                    + "as-is rather than guessed at.");
                return json;
            }

            // A quote or backslash would break the envelope this sits inside.
            text = text.Replace("\\", " ").Replace("\"", "'");

            return json.Substring(0, from) + lead + text + tail + json.Substring(end);
        }

        static int Level()
        {
            try { return GameManager.CurrentLevel; }
            catch (Exception) { return -1; }
        }

        // The authored persona block for whoever is speaking.
        //
        // L1-L4 are one term per level. Level 0 is the hub. L99 is per character,
        // keyed by the Character enum's NAME rather than its number, which is why
        // this resolves the name through the enum instead of formatting the int -
        // "StoryGuide/L99StoryGuide_1002" is not a term, "..._ParrotGirl" is.
        static string PersonaTerm()
        {
            int lv = Level();

            // Checked first: the Dark Siren is a separate persona that runs inside
            // level 4, so the level number alone would hand her Eiona's prompt.
            if (InScene("NPCMasterBehavior_DarkSiren")) return "StoryGuide/L4DarkSiren_SG";

            if (lv == 0) return "StoryGuide/HubworldStoryGuide";
            if (lv >= 1 && lv <= 4) return "StoryGuide/L" + lv + "StoryGuide";

            if (lv == 99) return L99PersonaTerm();
            return null;
        }

        // The five names that actually have an "L99StoryGuide_<name>" term.
        // Verified by listing the terms present in resources.assets rather than by
        // assuming the enum and the term set line up - they do not.
        static readonly string[] L99PersonaNames =
        {
            "Eddie", "Elysia", "Estelle", "Eiona", "ParrotGirl",
        };

        // On level 99 the speaker's id is frequently a SCENE-SPECIFIC id, not the
        // plain character: FinalDoorEddie (9911), FinalDoorEddieRedLine (9915),
        // L99Eddie_GuidingEddie_Minigame (9919), FinalDoorElysia (9921),
        // FinalDoorEstelle (9931), L99Estelle_FindingPlanet_Minigame (9939),
        // FinalDoorEiona (9941), L99Eiona_TreasureHunt_Minigame (9949).
        //
        // Formatting Enum.GetName straight into the term built eight keys that do
        // not exist - so in every final-door scene and every L99 minigame she had
        // NO persona and improvised one. This is the third instance of one bug
        // class: a naming convention that holds for most members and silently
        // fails for the rest. Evie was the first, found the same way, by diffing
        // terms-that-exist against ids-that-occur.
        //
        // So the id is reduced to the person, the person is validated against the
        // list above, and anything unrecognised returns null and SAYS SO. Silence
        // with a warning is recoverable; a confidently wrong term is not, and
        // formatting a name always "succeeds", which is precisely how the first
        // instance hid for so long.
        static string L99PersonaTerm()
        {
            string who = CharacterName();
            if (who == null) return null;

            // Evie has no L99StoryGuide_ term at all; her authored persona lives on
            // the wake-up encounter. Checked before the substring pass because
            // "IRLGirl" matches none of the five names either way.
            if (who.IndexOf("IRLGirl", StringComparison.Ordinal) >= 0)
                return "StoryGuide/L99WakeUpIRLGirlDialogue_SG";

            for (int i = 0; i < L99PersonaNames.Length; i++)
            {
                if (who.IndexOf(L99PersonaNames[i], StringComparison.Ordinal) < 0) continue;

                // She keeps her real identity here even while impersonating someone
                // else behind a door. The game sends the pretence separately, as a
                // per-turn story guide, so overriding the persona would break the
                // puzzle rather than serve it.
                return "StoryGuide/L99StoryGuide_" + L99PersonaNames[i];
            }

            Plugin.Log.LogWarning("Lore: character \"" + who + "\" on level 99 maps to no known "
                + "persona term, so none was sent and she will improvise. If this is a character "
                + "the game added, add it to L99PersonaNames.");
            return null;
        }

        static string CharacterName()
        {
            try
            {
                int? id = Identity.CharacterId();
                if (!id.HasValue) return null;
                Type t = AccessTools.TypeByName("Character");
                if (t == null || !t.IsEnum) return null;
                string n = Enum.GetName(t, id.Value);
                return string.IsNullOrEmpty(n) ? null : n;
            }
            catch (Exception) { return null; }
        }

        // Which girl this is, independent of which scene she is in.
        //
        // The Character enum carries the scene in the name as well as the person:
        // Elysia in her cabin is "Elysia", but the same character behind a final
        // door is "FinalDoorElysia", and Eddie has a second door variant,
        // "FinalDoorEddieRedLine", for the Red line. A biography lookup keyed on
        // the raw enum name would miss all of those, so the scene prefix and
        // suffix are stripped and only the person is matched.
        //
        // The scene itself is not discarded, it is just not this function's job:
        // the game sends the situational guide - Pretend, SlipUp, the Red line's
        // own opener - in story_guide each turn, and ResolveTerms above resolves
        // whichever one arrives.
        static readonly string[] People = { "Eddie", "Elysia", "Estelle", "Eiona" };

        static string PersonName()
        {
            string raw = CharacterName();
            if (raw == null) return null;

            for (int i = 0; i < People.Length; i++)
                if (raw.IndexOf(People[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return People[i];

            // The hub-world girls are their own characters rather than door
            // variants, so they get no biography rather than a wrong one.
            return null;
        }

        static bool InScene(string typeName)
        {
            try
            {
                Type t = AccessTools.TypeByName(typeName);
                if (t == null) return false;
                UnityEngine.Object[] f = UnityEngine.Object.FindObjectsOfType(t);
                return f != null && f.Length > 0;
            }
            catch (Exception) { return false; }
        }

        // The live context object the game built for the header we are replacing.
        static object Context()
        {
            try
            {
                Type t = AccessTools.TypeByName("ServerContextManager");
                if (t == null) return null;
                MethodInfo m = t.GetMethod("GetCurrentServerContext",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                return m == null ? null : m.Invoke(null, null);
            }
            catch (Exception) { return null; }
        }

        // Two different reasons to leave a field out, and both matter.
        //
        // The first group is transport and bookkeeping - a database id, a
        // timestamp, the names we already send in Identity.Block(). Spending
        // tokens on those buys nothing.
        //
        // The second group is the one worth explaining: Personalities, Tones,
        // Hobbies and Memories are List<int>, and Knowledge and Stage are bare
        // ints. Emitting them raw would produce "Memories: 3, 7, 12" - not a
        // memory, just a number that invites the model to invent one, which is
        // the exact failure this file exists to stop.
        //
        // An earlier version of this comment claimed the text behind all six
        // shipped nowhere. That was wrong for three of them, and it was the kind
        // of wrong that closes an investigation early: the personality, tone and
        // hobby ordinals index enums right there in the assembly, and each member
        // name doubles as a NPCTag_Skin/ localization key. TagWord() recovers
        // those from the game's own strings.
        //
        // Memories, Stage and Knowledge stay out. Their flags and tiers are
        // readable, but the authored wording is server-side, and supplying my own
        // wording instead is worse than the gap - so nothing is emitted for them
        // until the shipped corpus is mapped.
        static readonly string[] Skip =
        {
            "PlayFabId", "TimeStamp", "Lang", "OptOutSpeech", "Her", "Him",
            "Character", "Level", "MediaFileName", "IsVisionEnabled",
            "Personalities", "Tones", "Hobbies", "Memories", "Knowledge", "Stage",
        };

        // Three of the six skipped above are not dropped, they are handled
        // separately and better - Traits() below decodes them into words. They
        // stay on the skip list only so the generic reflection loop does not also
        // emit them as bare numbers ("Personalities: 9, 20").
        //
        // The decode was the part that looked impossible. The ints are ordinals
        // into three enums that ship in the assembly, and each member name is
        // itself a localization key under NPCTag_Skin/, so the player-facing word
        // for ordinal 9 is Tr("NPCTag_Skin/" + Enum.GetNames(...)[9]). Reading it
        // that way rather than prettifying the identifier matters: three of them
        // disagree, and CasualAndInformal displays as "Casual".
        static readonly string[] TagEnums =
        {
            "NPCCustomTagPersonality", "NPCCustomTagSpeakingTone", "NPCCustomTagHobby",
        };

        static readonly string[] TagFields = { "personality", "speakingTone", "hobby" };
        static readonly string[] TagLabels = { "Personality", "How you speak", "What you enjoy" };

        static string TagWord(int enumIdx, int ordinal)
        {
            try
            {
                Type t = AccessTools.TypeByName(TagEnums[enumIdx]);
                if (t == null || !t.IsEnum) return null;

                string[] names = Enum.GetNames(t);
                if (ordinal < 0 || ordinal >= names.Length) return null;

                // The localized word when the term resolves, the enum name split
                // into words when it does not - never the raw ordinal.
                string word = Tr("NPCTag_Skin/" + names[ordinal]);
                return word != null ? word : Spaced(names[ordinal]);
            }
            catch (Exception) { return null; }
        }

        // "DualFaced" -> "Dual Faced". Only used when the term lookup fails.
        static string Spaced(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            StringBuilder sb = new StringBuilder(s.Length + 4);
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // Friendly labels for the fields that are actually secrets. Anything not
        // named here still gets emitted under its own field name - a build that
        // adds a field should surface it rather than have it silently dropped,
        // and an ugly label is a much smaller problem than missing knowledge.
        static readonly string[,] Labels =
        {
            // Deliberately just the field's name. The reason this password is
            // also her favourite food is not something to assert here in my own
            // words - the game ships that hint as a string of its own
            // (GameUI/PassWordHint), and PcPswdHint below forwards it verbatim.
            // Paraphrasing shipped text is how invented lore gets in.
            { "PcPswd",       "The password to your own computer" },
            { "WifiPswd",     "Your wifi password" },
            { "SecretPswd",   "The passcode to your hidden room" },
            { "GeneratedRoom","The room behind the locked door in your home" },
            { "OutsideArea",  "What is outside the apartment" },
            { "Recipe",       "The potion recipes you know" },
            { "SpeedColor",   "Colour of the speed potion" },
            { "HealthColor",  "Colour of the health potion" },
            { "ShieldColor",  "Colour of the shield potion" },
            { "LoveColor",    "Colour of the love potion" },
            { "SecLvl",       "Current ship security level" },
            { "DarkRoom",     "Which room is dark" },
            { "FixedSystems", "Ship systems already repaired" },
            { "UnFixedSystems","Ship systems still broken" },
            { "Cards",        "Access cards that exist" },
            { "Year",         "How many years you and the player have been together" },
            { "Gift",         "The first gift the player ever gave you" },
            { "Location",     "Where you first met the player" },
            { "HiddenIsland", "The hidden island" },
            { "SundialStatus","State of the sundial" },
            { "FixedStructures","Structures already repaired" },
            { "UnFixedStructures","Structures still broken" },
            { "BindingGemColor","The binding gem colours" },
            { "TreasureLocation","Where the treasure is buried" },
            { "LastTopic",    "What you and the player last talked about" },
            { "Appearance",   "How you look" },
            { "PlayerBio",    "What you know about the player" },
        };

        static string LabelFor(string field)
        {
            for (int i = 0; i < Labels.GetLength(0); i++)
                if (Labels[i, 0] == field) return Labels[i, 1];
            return field;
        }

        // Field -> the localization key of a hint the game already ships for it.
        // Only the key is named here; the wording is whatever the game says, in
        // the player's language, resolved at runtime. Nothing is written by hand,
        // so a term that is missing or renamed in some build simply yields no
        // hint rather than a stale sentence of mine.
        static readonly string[,] Hints =
        {
            { "PcPswd", "GameUI/PassWordHint" },
        };

        static string HintFor(string field)
        {
            for (int i = 0; i < Hints.GetLength(0); i++)
                if (Hints[i, 0] == field) return Tr(Hints[i, 1]);
            return null;
        }

        static bool Skipped(string name)
        {
            for (int i = 0; i < Skip.Length; i++) if (Skip[i] == name) return true;
            return false;
        }

        // A false bool is not knowledge, and neither is an empty string. Only
        // things that are actually true or actually set earn a line.
        static string Render(object v)
        {
            if (v == null) return null;

            if (v is bool) return ((bool)v) ? "yes" : null;

            if (v is string)
            {
                string s = ((string)v).Trim();
                return s.Length == 0 ? null : s;
            }

            IList list = v as IList;
            if (list != null)
            {
                if (list.Count == 0) return null;
                StringBuilder j = new StringBuilder();
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] == null) continue;
                    if (j.Length > 0) j.Append(", ");
                    j.Append(list[i].ToString());
                }
                return j.Length == 0 ? null : j.ToString();
            }

            string t = v.ToString();
            if (string.IsNullOrEmpty(t)) return null;
            t = t.Trim();
            return t.Length == 0 ? null : t;
        }

        static int _factCount;

        static void Facts(StringBuilder sb)
        {
            _factCount = 0;
            object c = Context();
            if (c == null) return;

            PropertyInfo[] ps;
            try
            {
                ps = c.GetType().GetProperties(BindingFlags.Public
                    | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            }
            catch (Exception) { return; }

            List<string> lines = new List<string>();
            for (int i = 0; i < ps.Length; i++)
            {
                if (Skipped(ps[i].Name)) continue;
                if (ps[i].GetIndexParameters().Length > 0) continue;
                if (!ps[i].CanRead) continue;

                object v;
                try { v = ps[i].GetValue(c, null); }
                catch (Exception) { continue; }

                string s = Render(v);
                if (s == null) continue;
                lines.Add("- " + LabelFor(ps[i].Name) + ": " + s);

                // Some values have a shipped hint string explaining what they
                // mean, written by the game's authors for the player to solve.
                // Forwarded verbatim, in quotes, so it reads as the game's words
                // and not as an assertion of mine. The PC password is the case
                // that matters: the post-it beside her monitor is the only thing
                // that links the value to her favourite food, and without it she
                // can recite the password while denying she likes the food.
                string hint = HintFor(ps[i].Name);
                if (hint != null)
                    lines.Add("  The game states this as: \"" + hint + "\"");
            }

            if (lines.Count == 0) return;
            _factCount = lines.Count;

            sb.Append("\nFacts you know, read out of this playthrough. These are true for ");
            sb.Append("THIS session - the game randomises several of them per playthrough, so ");
            sb.Append("never substitute a value you remember from anywhere else:\n");
            for (int i = 0; i < lines.Count; i++) sb.Append(lines[i]).Append('\n');

            sb.Append("\nThese are yours to keep or to share. You know every one of them ");
            sb.Append("perfectly and you must never claim not to know one, or say you have no ");
            sb.Append("memory of it - but knowing a secret is not the same as handing it over. ");
            sb.Append("Withhold, deflect or tease as your character would, and give one up only ");
            sb.Append("once the player has genuinely earned it. Refusing is in character. ");
            sb.Append("Amnesia is not.\n");
        }

        // Every item's authored description, under its own I2 namespace.
        //
        // UIManager_Inventory.cs:218 builds this term for the player's tooltip, so
        // the text exists for all 160 real items and has never been shown to a
        // model. Item.itemDesc, the field that looks like it should hold it, is
        // empty on nearly everything - reading that instead is the trap here.
        // The behaviour holds characterConfig, which holds the three tag lists and
        // the appearance config carrying her authored bio. Both are public fields,
        // but reached through Traverse anyway so a missing one degrades to null
        // rather than throwing at JIT time on a build that lacks it.
        static object Config()
        {
            try
            {
                object beh = Murder.BehaviourObject();
                if (beh == null) return null;
                return Traverse.Create(beh).Field("characterConfig").GetValue();
            }
            catch (Exception) { return null; }
        }

        static void Traits(StringBuilder sb)
        {
            object cfg = Config();
            if (cfg == null) return;

            List<string> lines = new List<string>();
            for (int f = 0; f < TagFields.Length; f++)
            {
                IList ids;
                try { ids = Traverse.Create(cfg).Field(TagFields[f]).GetValue() as IList; }
                catch (Exception) { continue; }
                if (ids == null || ids.Count == 0) continue;

                List<string> words = new List<string>();
                for (int i = 0; i < ids.Count; i++)
                {
                    if (ids[i] == null) continue;
                    int ord;
                    try { ord = Convert.ToInt32(ids[i]); }
                    catch (Exception) { continue; }

                    string w = TagWord(f, ord);
                    if (w != null && !words.Contains(w)) words.Add(w);
                }
                if (words.Count == 0) continue;

                lines.Add("- " + TagLabels[f] + ": " + string.Join(", ", words.ToArray()));
                _factCount++;
            }

            // Her authored bio, one field over on the appearance config. This is
            // where the only written line about the parrot lives - "You have
            // unexplained, special feelings connected to blue parrot statues" -
            // so it is the difference between her improvising a story and
            // actually having one.
            string bio = null;
            try
            {
                object app = Traverse.Create(cfg).Field("NPCCurrentAppearanceConfig").GetValue();
                if (app != null)
                    bio = Render(Traverse.Create(app).Field("prompt_Description").GetValue());
            }
            catch (Exception) { }

            if (lines.Count == 0 && bio == null) return;

            sb.Append("\nWho you are, as this playthrough has you configured");
            sb.Append(" - the player chose these, so they are not negotiable:\n");
            for (int i = 0; i < lines.Count; i++) sb.Append(lines[i]).Append('\n');

            if (bio != null)
            {
                sb.Append("\nYour own history, written by this game's authors. It is about YOU. ");
                sb.Append("Everything in it is something you genuinely remember, and you speak ");
                sb.Append("about it as your own past, never as information you were given:\n");
                sb.Append(bio).Append('\n');
                _factCount++;
            }
        }

        // Progress flags (GetMemories) are deliberately NOT rendered here.
        //
        // The flags themselves are readable - they are named booleans on each
        // level's behaviour - but the authored wording for them lives on the
        // vendor's server, and an earlier revision of this file filled that gap
        // with sentences I wrote myself. That is the one thing this file must
        // never do: text handed to the model as something she remembers has to
        // come from the game, or a playthrough ends up carrying lore the authors
        // never wrote. Left absent on purpose until the shipped corpus is mapped.

        // Authored, first-person notes about the things she can be caught looking
        // at - the meteor-struck city through the window, the victim wall in the
        // secret room. The game only ever sends one of these on the exact turn
        // the player pokes that object, so between those moments she does not
        // know her own home. Sent as standing knowledge instead.
        // Logged once per session, not per entry or per level: the swap affects 14
        // entries and two levels, and a line each would bury the log for something
        // the player can do nothing about.
        static bool _reportedSwap;

        // Is this string an AI directive rather than a player-facing description?
        //
        // The game's own convention: directives are stage direction wrapped in
        // brackets and addressed to her, descriptions are bare noun phrases. Both
        // bracket styles count - level5 uses square brackets for five of its
        // entries, so a parenthesis-only test misreads exactly the subset this is
        // meant to catch.
        static bool Wrapped(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim();
            if (t.Length < 2) return false;
            return (t[0] == '(' && t[t.Length - 1] == ')')
                || (t[0] == '[' && t[t.Length - 1] == ']');
        }

        static void Surroundings(StringBuilder sb)
        {
            object beh = Murder.BehaviourObject();
            if (beh == null) return;

            IList entries;
            try { entries = Traverse.Create(beh).Field("investigatableEventContent").GetValue() as IList; }
            catch (Exception) { return; }
            if (entries == null || entries.Count == 0) return;

            List<string> lines = new List<string>();
            for (int i = 0; i < entries.Count && lines.Count < 40; i++)
            {
                object e = entries[i];
                if (e == null) continue;

                string name, ai, player;
                try
                {
                    Traverse t = Traverse.Create(e);
                    name = Render(t.Field("interactableName").GetValue());
                    ai = Render(t.Field("aiContent").GetValue());
                    player = Render(t.Field("defaultContent").GetValue());
                }
                catch (Exception) { continue; }

                if (name == null) continue;

                // 14 of the game's 187 entries have these two values swapped by
                // whoever authored them - all ten in level7 (Eiona), plus four in
                // level5. Since only aiContent is ever read, Eiona currently ships
                // the player-facing tooltip to the model and her ten authored
                // directives never fire at all. That is a bug in the game, not the
                // mod, but the mod is the only thing in a position to notice.
                //
                // Corrected structurally, on the game's OWN convention rather than
                // on a per-entry list: an AI directive is wrapped in ( or [ - it is
                // stage direction, addressed to her in the second person - while a
                // player-facing description is a bare noun phrase. So when exactly
                // one of the pair is wrapped, that one is the directive whichever
                // field it happens to sit in.
                //
                // Deliberately conservative: if both are wrapped or neither is, the
                // declared field wins. A rule that guesses in the ambiguous case
                // would eventually feed her a tooltip while claiming it was a
                // directive, and being wrong here is worse than being incomplete.
                string chosen = ai;
                if (Wrapped(player) && !Wrapped(ai))
                {
                    chosen = player;
                    if (!_reportedSwap)
                    {
                        _reportedSwap = true;
                        Plugin.Log.LogInfo("Lore: this level has authored notes stored in the "
                            + "opposite fields (a known quirk of the game's data). Reading them "
                            + "the right way round, so her directives are used instead of the "
                            + "player-facing tooltips.");
                    }
                }

                if (chosen == null) continue;
                lines.Add("- " + Spaced(name.Replace('_', ' ')) + ": " + chosen);
            }

            if (lines.Count == 0) return;

            sb.Append("\nYour own home and the things in it, as you think about them. These notes ");
            sb.Append("are written in your voice and describe what each thing means to you, ");
            sb.Append("including what you would rather the player not work out:\n");
            for (int i = 0; i < lines.Count; i++) sb.Append(lines[i]).Append('\n');
            _factCount += lines.Count;
        }

        static string ItemDesc(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return null;
            return Tr("Item/" + itemName + "Desc");
        }

        // What the player is showing her this turn.
        //
        // DialogueInventory.SendItemToAI fires an event that lands in
        // NPCMasterBehavior_MainCharacter.attachedItemsToDialogue, and
        // OnSubmitChatMessage flattens it to "(player is holding X, )" - a bare
        // name, no description, which is exactly what she read back about the
        // parrot. The objects themselves are still in that dictionary, so the
        // descriptions are one field read away.
        static List<string> Attached()
        {
            List<string> names = new List<string>();
            try
            {
                Type t = AccessTools.TypeByName("NPCMasterBehavior_MainCharacter");
                if (t == null) return names;

                UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(t);
                if (found == null) return names;

                for (int i = 0; i < found.Length; i++)
                {
                    IDictionary d = Traverse.Create(found[i])
                        .Field("attachedItemsToDialogue").GetValue() as IDictionary;
                    if (d == null) continue;

                    foreach (object k in d.Keys)
                    {
                        if (k == null) continue;
                        string n = Traverse.Create(k).Property("ItemName").GetValue<string>();
                        if (string.IsNullOrEmpty(n)) continue;
                        if (!names.Contains(n)) names.Add(n);
                    }
                }
            }
            catch (Exception) { }
            return names;
        }

        static void Objects(StringBuilder sb)
        {
            List<string> shown = Attached();
            List<string> carried = Items.Carried();

            List<string> lines = new List<string>();
            for (int i = 0; i < shown.Count; i++)
            {
                string d = ItemDesc(shown[i]);
                if (d != null) lines.Add("- The player is showing you the " + shown[i] + ". " + d);
            }
            for (int i = 0; i < carried.Count; i++)
            {
                if (shown.Contains(carried[i])) continue;
                string d = ItemDesc(carried[i]);
                if (d != null) lines.Add("- You are carrying the " + carried[i] + ". " + d);
            }

            if (lines.Count == 0) return;

            sb.Append("\nObjects in play right now. These descriptions were written for the ");
            sb.Append("player and talk about you in the third person - read them as being about ");
            sb.Append("YOU, and react as the person they describe:\n");
            for (int i = 0; i < lines.Count; i++) sb.Append(lines[i]).Append('\n');

            sb.Append("If one of these is something you care about, that feeling is real and it ");
            sb.Append("is yours. Say what the object means to you and tell the story behind it ");
            sb.Append("rather than naming it back - a described object is an invitation to ");
            sb.Append("remember, and answering \"it is just an inventory item\" is always wrong.\n");
        }

        // Null when there is nothing recovered, so a scene without a context object
        // costs one reflection call and adds no message.
        public static string Block()
        {
            if (Plugin.CfgLoreInjection == null || !Plugin.CfgLoreInjection.Value) return null;

            string persona = Tr(PersonaTerm());

            // The authored biography, and for Eddie the apartment history.
            //
            // These are separate sources rather than one, because the scene file
            // stores them separately: Elysia, Estelle and Eiona have their history
            // written as directives, while Eddie's directives are only the two
            // disguise lines and her real history sits in the shared ### Knowledge
            // block. Bios keys that block to Eddie alone - it is the apartment,
            // the meteor and the parrot, and none of it is true of the others.
            string who = PersonName();
            string bio = Bios.For(who);
            string apartment = who == "Eddie" ? Bios.Apartment() : null;

            StringBuilder body = new StringBuilder();
            Traits(body);
            Facts(body);
            Surroundings(body);
            Objects(body);

            if (persona == null && bio == null && apartment == null && body.Length == 0) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append("### WHO YOU ARE (authoritative)\n");

            if (bio != null || apartment != null)
            {
                sb.Append("Your character and your history, written by this game's authors. These ");
                sb.Append("are your own memories and traits, in the second person - they are true of ");
                sb.Append("you, you remember them, and they are yours to bring up in conversation:\n");
                if (bio != null) sb.Append(bio).Append('\n');
                if (apartment != null) sb.Append(apartment).Append('\n');
                sb.Append('\n');
            }

            if (persona != null)
            {
                // The authored guides are written about her in the third person
                // ("Cat girlfriend doesn't like repeat things"). Handed over
                // unframed, a model reads that as notes about someone else.
                sb.Append("The following was written by this game's authors to describe you. It is ");
                sb.Append("about YOU, in the third person. Treat it as your own character, history ");
                sb.Append("and current situation, and let it govern your mood and what you want:\n");
                sb.Append(persona).Append('\n');
            }

            sb.Append(body.ToString());

            // Deliberately not repeated in OOC mode: Ooc.Block() goes in after this
            // one and requires literal truth, which is the behaviour we want there.
            // Nothing here contradicts it - the instruction is to withhold in
            // character, not to deny knowing.
            return sb.ToString();
        }

        static string _reported;

        public static void Report()
        {
            string term = PersonaTerm();
            string person = PersonName();
            string line = (term ?? "(none)") + " facts=" + _factCount + " bio=" + (person ?? "-");
            if (line == _reported) return;

            if (person != null && Bios.For(person) == null)
                Plugin.Log.LogWarning("Lore: no authored biography found for " + person
                    + ", so she keeps her name and situation but improvises her history.");

            _reported = line;
            if (term != null && Tr(term) == null)
                Plugin.Log.LogWarning("Lore: story guide term \"" + term + "\" did not resolve, "
                    + "so no persona was sent this scene. She will improvise a character.");
            else
                Plugin.Log.LogInfo("Lore: persona \"" + (term ?? "(none)")
                    + "\" plus " + _factCount + " known facts sent to the model.");
        }
    }
}
