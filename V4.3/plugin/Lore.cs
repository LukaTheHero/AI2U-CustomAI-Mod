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
        internal static string Tr(string term)
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

                string s = Clean(_translate.Invoke(null, args) as string, term);
                if (s != null) return s;

                // One retry, for authored text the game files under a key its
                // own code never asks for. See Alt() for both cases.
                //
                // Item.cs:385/:413 build "StoryGuide/{item}PromptL{n}", but the four
                // parrot-feather entries ship as "Parrot FeatherPropmptL1..L4" -
                // Prompt with the m and p transposed. Verified in resources.assets:
                // the typo'd spelling is the ONLY one present, four occurrences, so
                // the key the code asks for cannot resolve and this authored text
                // has never once reached a model in any build of the game. It is
                // her reaction to being handed the feather back ("you tell the
                // player they may regret it later").
                //
                // Fixed single substitution, applied only after a genuine miss, so
                // a key that resolves normally is never rewritten. Nothing is
                // invented here - this reads the game's own text from the game's
                // own table, using the spelling the table actually uses.
                string alt = Alt(term);
                if (alt == null) return null;

                args[0] = alt;
                s = Clean(_translate.Invoke(null, args) as string, alt);
                if (s != null && !_reportedTypo)
                {
                    _reportedTypo = true;
                    Plugin.Log.LogInfo("Lore: recovered authored text the game stores under "
                        + "a key its own code never requests (\"" + alt + "\").");
                }
                return s;
            }
            catch (Exception) { return null; }
        }

        static bool _reportedTypo;

        // Null when the term has no known mismatched counterpart, which is the
        // overwhelmingly common case and costs one IndexOf.
        //
        // Two separate defects, both in the game's own table:
        //
        // 1. The hubworld girls' USE reactions. Item.cs:382 builds
        //    "StoryGuide/{item}PromptUseL{n}" where n is CurrentLevel, or in
        //    the hubworld the Character index - IRLGirl = 1001 and ParrotGirl
        //    = 1002 (Character.cs:21-23). So it asks for PromptUseL1001 and
        //    PromptUseL1002. The shipped table has neither: those eight
        //    reactions each are authored as "PromptUseEvie" and
        //    "PromptUseParrot". Counted in resources.assets - 8 and 8, with
        //    zero PromptUseL1001 or PromptUseL1002 anywhere. The GIFT path is
        //    fine and needs no rewrite (PromptL1001/PromptL1002, 15 each,
        //    present and matching), which is what makes this look like an
        //    oversight in one getter rather than a table-wide naming change.
        //
        // 2. "Propmpt" - Prompt with the m and p transposed - on the four
        //    parrot-feather gift entries, described above.
        //
        // PromptUseL is tested first because it is the narrower match; the two
        // patterns cannot both hit, since "PromptUseL1001" does not contain
        // the substring "PromptL".
        static string Alt(string term)
        {
            int i = term.IndexOf("PromptUseL", StringComparison.Ordinal);
            if (i >= 0)
            {
                string tail = term.Substring(i + "PromptUseL".Length);
                string who = tail == "1001" ? "Evie" : (tail == "1002" ? "Parrot" : null);
                if (who == null) return null;
                return term.Substring(0, i) + "PromptUse" + who;
            }

            i = term.IndexOf("PromptL", StringComparison.Ordinal);
            if (i < 0) return null;
            return term.Substring(0, i) + "PropmptL" + term.Substring(i + "PromptL".Length);
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
            if (!HasTermKey(json)) return json;

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

        // StoryGuide is not the only namespace the game leaks as a raw key.
        //
        // NPCMasterBehavior_Main_L2.cs:608 assigns the literal string
        // "TalklinePlaceholder/L2SavedByElysiaTalkLine" and line 774 hands it to
        // SendToChatGPT as a PlayerMessage without ever localizing it, so the model
        // receives the key itself where a sentence should be. 31 terms live in that
        // namespace and the one above is authored and present in the shipped table.
        //
        // A fixed list of namespaces rather than a pattern: a wildcard "anything
        // with a slash" test would start rewriting file paths and URLs.
        static readonly string[] Prefixes =
        {
            "StoryGuide/", "TalklinePlaceholder/",
        };

        static bool IsTermKey(string val)
        {
            if (string.IsNullOrEmpty(val)) return false;
            for (int i = 0; i < Prefixes.Length; i++)
                if (val.StartsWith(Prefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        static bool HasTermKey(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            for (int i = 0; i < Prefixes.Length; i++)
                if (json.IndexOf(Prefixes[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

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
            if (!IsTermKey(val)) return json;

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

        // Whether the character speaking right now is a particular one of the four.
        //
        // Written because asset residency is not a gate. Resources.FindObjectsOfTypeAll
        // returns every LOADED object of a type, assets included - not only what the
        // current scene references - so a ScriptableObject that any earlier scene
        // pulled in is still found later. That is how another level's crew records
        // reached the catgirl on level 1: "the reader comes back empty everywhere
        // else" was an assumption, and an OOC dump disproved it.
        //
        // Two clauses, because these characters each appear in two places: their own
        // level, and level 99 under a scene-specific id that CharacterName() already
        // reduces to the person. Matching on the person rather than the level keeps
        // her own material with her in both.
        //
        // Summons are excluded outright. A magic circle summon runs INSIDE level 2,
        // so a bare level test would hand a sacrificed toy the witch's diary and her
        // recipes - the same bleed already fixed for her persona blocks.
        static bool SpeakerIs(int ownLevel, string person)
        {
            if (Identity.IsSummon()) return false;

            int lv = Level();
            if (lv == ownLevel) return true;
            if (lv != 99) return false;

            string who = CharacterName();
            return who != null && who.IndexOf(person, StringComparison.Ordinal) >= 0;
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

            // Checked first: the alternate speaker on level 4 is a separate persona
            // that runs inside that level, so the level number alone would hand her
            // the main character's prompt.
            //
            // This tests WHO IS SPEAKING, not what exists in the scene. It used to
            // call InScene(...), which is FindObjectsOfType(...).Length > 0 -
            // presence, not speech. Both behaviours live in the level 4 scene at the
            // same time (the main behaviour resolves the other one by reference), so
            // presence was true while the main character was the one talking, and
            // she was handed the alternate persona plus her own bio on top of it.
            // The id comes from Communicator.currentCharacterID, which ChangeNPC
            // assigns before the request is built, so it is already correct here.
            if (Identity.CharacterId() == 40) return "StoryGuide/L4DarkSiren_SG";

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

            // One id where the id's SPELLING and the game's own name assignment
            // disagree, and the spelling loses.
            //
            // FinalDoorEddieRedLine (9915) is named from npcName_L99_IRLGirl by
            // Communicator.UpdateNPCName - the game considers this speaker to be
            // Evie. The substring pass below reads "Eddie" out of the id name and
            // returned Eddie's persona, and PersonName() went on to hand over
            // Eddie's biography too. The result was one request whose IDENTITY
            // section and WHO YOU ARE section named two different people.
            //
            // Resolved in favour of the name the game assigns, because that is the
            // part the player actually sees and the part UpdateNPCName is the
            // authority on. Keyed by id rather than by spelling so a future rename
            // of the enum member cannot quietly reintroduce the mismatch.
            if (Identity.CharacterId() == 9915)
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

            // Same exception as in L99PersonaTerm, and it has to be repeated here
            // because this is a second, independent substring pass over the same id
            // name. The Red line variant is named from npcName_L99_IRLGirl by the
            // game, so it is not one of the four and must not be given one of their
            // biographies - matching "Eddie" out of the id spelling is exactly how
            // it was getting his. Returning null means no biography rather than the
            // wrong one, which is the same trade this function's last line makes for
            // the hub-world girls.
            if (Identity.CharacterId() == 9915) return null;

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

            // Recipe is skipped here and rebuilt by Recipes() below, which is the
            // one case where the game's own string is worse than what we can make
            // from the same data. The vanilla builder walks the formula list and
            // appends only the INGREDIENTS - "[2 ingredients needed]Rose & Ash,
            // [3 ingredients needed]..." - and never appends the potion each group
            // produces (NPCMasterBehavior_Main_L2.cs:88-95, where .result is simply
            // never read). The pairing survives as list position and nothing else.
            //
            // She therefore receives four real ingredient groups and no way to tell
            // which makes which, so she guesses, and the guess is wrong three times
            // in four. Worse, the formulas are re-rolled on EVERY scene load by
            // PotionFormula.RandomizeFormula (PotionFormula.cs:34-37, seeded from
            // the clock and never saved), so a remembered answer is stale too.
            "Recipe",
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

        // What she and the player last talked about, read straight out of the save.
        //
        // The game writes this itself: MemorizeProcessor.cs:138 saves the memory
        // under "{PlayFabId}_lastTopic_{Character}" in savedata/global.yags after
        // every scene change. The hubworld then loads it back at
        // NPCMasterBehavior_Main_Config.cs:165 - and immediately throws it away:
        //
        //     string text5 = ((this.m_isFirstSentence && !IsNullOrEmpty(text4))
        //         ? text4 : null);
        //
        // followed by m_isFirstSentence = false (:183). So ServerContextHubworld
        // .LastTopic is populated for exactly one line and is null for every turn
        // after it. She greets the player with the memory, then spends the rest of
        // the conversation with no idea they have ever met.
        //
        // Reading the same key ourselves puts it back on every turn. Both sides
        // interpolate a Character enum, so both render its NAME and the keys match
        // without any formatting of my own. Nothing here is authored - it is the
        // game's own saved string, handed back to the character who said it.
        static string SavedLastTopic()
        {
            try
            {
                int? id = Identity.CharacterId();
                if (id == null) return null;

                Type ch = AccessTools.TypeByName("Character");
                if (ch == null) return null;
                string name = Enum.GetName(ch, id.Value);
                if (string.IsNullOrEmpty(name)) return null;

                Type prefs = AccessTools.TypeByName("wAIfuBackend.Prefs");
                if (prefs == null) return null;
                string pf = Traverse.Create(prefs).Property("PlayFabId").GetValue<string>();
                if (string.IsNullOrEmpty(pf)) return null;

                string v = ES3.Load<string>(pf + "_lastTopic_" + name,
                    "savedata/global.yags", "");
                if (!string.IsNullOrEmpty(v)) return v.Trim();

                // Nothing filed under this speaker's own name. For the scene
                // variants that is not a gap in the save, it is a key that is
                // never written at all.
                //
                // GameManager.cs:328 derives the character to memorise from the
                // level number:
                //
                //     Character currentLevel = (Character)GameManager.CurrentLevel;
                //
                // which only works because enum members 1-4 happen to coincide
                // with those level numbers. A scene-specific speaker's id is
                // nowhere near its level number, so the lookup on the next line
                // misses and no memory is ever saved under a variant name.
                //
                // But a door variant is not a different woman - the persona term
                // and the biography both already resolve her through PersonName(),
                // so the memory resolves the same way. She is handed what she
                // herself last talked about, in whichever room that was. Without
                // this she meets the player as a stranger in exactly the scenes
                // where remembering matters most.
                //
                // PersonName() returns null for the hub-world girls, the summon and
                // the level-4 alternate, so none of them can borrow one of the
                // four's memories. The hub girls do not need the fallback anyway:
                // their ids carry their own names, so the direct read above already
                // finds the keys the hub-world memorise pass writes for them.
                string person = PersonName();
                if (person == null || person == name) return null;

                v = ES3.Load<string>(pf + "_lastTopic_" + person,
                    "savedata/global.yags", "");
                return string.IsNullOrEmpty(v) ? null : v.Trim();
            }
            catch (Exception) { return null; }
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

            // Only when the game's own field came through empty, so the first line
            // of a hubworld conversation still uses the value the game chose and
            // this never competes with it. See SavedLastTopic for why it is empty
            // on every turn but the first.
            bool hasTopic = false;
            string topicLabel = LabelFor("LastTopic");
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].StartsWith("- " + topicLabel + ": ")) { hasTopic = true; break; }

            if (!hasTopic)
            {
                string topic = SavedLastTopic();
                if (topic != null)
                    lines.Add("- " + topicLabel + ": " + topic);
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
                // The config describes a personality - traits, tone, hobbies,
                // appearance. Eleven of the behaviours that can speak have none of
                // their own, and for those BehaviourObject() answers with the scene's
                // main character so the cheat menu keeps working. Reading a config off
                // that object would give the summon the witch's temperament and the L4
                // alternate her counterpart's, which is the same borrowed-identity bug
                // the persona gating exists to prevent, arriving by a different route.
                if (!Murder.SpeakerIsMainCharacter()) return null;

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

            // Precedence has to be stated, not implied.
            //
            // The authored persona block goes in just above this one and ends with
            // "let it govern your mood and what you want". These tag lines end with
            // "not negotiable". Both are absolute, they routinely disagree - the
            // shipped guide describes a reserved character while the player ticked
            // Cheerful and Flirty - and a prompt that asserts two conflicting
            // absolutes without resolving them leaves the model to pick, which it
            // does inconsistently turn to turn.
            //
            // The player's choice wins, because they made it in this playthrough's
            // own NPC Customization screen and can see the result.
            //
            // Scoped to personality on purpose. These three enums are personality,
            // speaking tone and hobby - manner, not history. Letting them outrank
            // lore wholesale would have "Cheerful" quietly overwrite what the level
            // is about, so the override is granted over how she comes across and
            // explicitly withheld over what is true of her.
            if (lines.Count > 0)
            {
                sb.Append("\nWho you are, as this playthrough has you configured. ");
                sb.Append("The player chose these themselves, so they outrank every other ");
                sb.Append("description of your personality here: where anything above says you ");
                sb.Append("behave differently, this list wins and you play this instead.\n");
                for (int i = 0; i < lines.Count; i++) sb.Append(lines[i]).Append('\n');
                sb.Append("This governs your manner only - how you act, how you speak, what you ");
                sb.Append("enjoy. It does not change your history, your situation or anything ");
                sb.Append("you know; all of that stays exactly as written elsewhere here.\n");
            }

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
            // Same reason as Config(): these notes are what one character knows about
            // one room. A speaker with no behaviour of her own would be handed the
            // stand-in's room, and this block asserts them as things she can see, so a
            // wrong list reads as her describing a place she is not in.
            if (!Murder.SpeakerIsMainCharacter()) return;

            object beh = Murder.BehaviourObject();
            if (beh == null) return;

            IList entries;
            try { entries = Traverse.Create(beh).Field("investigatableEventContent").GetValue() as IList; }
            catch (Exception) { return; }
            if (entries == null || entries.Count == 0) return;

            // Cap raised from 40 to 64. Counted per scene, the authored entries run
            // 1, 9, 10, 13, 28, 38, 38, 40, 46 - so 40 was quietly truncating the
            // two largest scenes and sitting exactly on the boundary of a third.
            // Losing the tail here reads to the player as her not knowing part of
            // her own house, which is the failure this whole file exists to stop.
            // 64 clears the largest shipped scene with room to spare.
            List<string> lines = new List<string>();
            for (int i = 0; i < entries.Count && lines.Count < 64; i++)
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

            // The same clause the fact list and the object list carry, for the same
            // reason. This is the block that covers her own rooms, so it is the one
            // where a shrug reads worst: asked about a thing standing in her own
            // house, "I don't know what that is" is never a character choice.
            sb.Append("This is your own space and you know every corner of it. If the player ");
            sb.Append("asks about something here, you know what it is, whose it is and why it ");
            sb.Append("is there. Deciding not to explain is in character. Not recognising your ");
            sb.Append("own belongings is not.\n");

            _factCount += lines.Count;
        }

        static string ItemDesc(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return null;
            return Tr("Item/" + itemName + "Desc");
        }

        // The potion recipes, paired with the potion each one actually makes.
        //
        // See the Recipe entry on the Skip list for why the game's own string is
        // not good enough. The fix is not to guess the pairing from list order:
        // every formula carries its own result Item, so the correct answer is
        // read straight off the data.
        //
        // PotionFormula is a ScriptableObject, not a scene component, so
        // FindObjectsOfType cannot see it - Resources.FindObjectsOfTypeAll can.
        // Same distinction already documented in Cheats.cs for the item library.
        //
        // Ordering note, kept for anyone who checks this against the assembly:
        // the four formulas ARE positionally Speed, Health, Shield, Love, because
        // SetLevel2ServerContext assigns potionColors[0..3] to exactly those four
        // properties (ServerContextL2.cs:67-70) from a list built by walking
        // Formulas in order. That is a useful cross-check but a bad thing to
        // depend on, so it is not what this reads.
        static List<string> Recipes()
        {
            List<string> lines = new List<string>();
            try
            {
                Type t = AccessTools.TypeByName("PotionFormula");
                if (t == null) return lines;

                UnityEngine.Object[] found = Resources.FindObjectsOfTypeAll(t);
                if (found == null || found.Length == 0) return lines;

                IList formulas = Traverse.Create(found[0])
                    .Property("Formulas").GetValue() as IList;
                if (formulas == null) return lines;

                for (int i = 0; i < formulas.Count; i++)
                {
                    object f = formulas[i];
                    if (f == null) continue;

                    Traverse ft = Traverse.Create(f);

                    object result = ft.Field("result").GetValue();
                    if (result == null) continue;

                    string potion = Traverse.Create(result)
                        .Property("LocalizedItemName").GetValue<string>();
                    if (string.IsNullOrEmpty(potion)) continue;

                    IList ing = ft.Field("Ingredients").GetValue() as IList;
                    if (ing == null || ing.Count == 0) continue;

                    // Every ingredient is listed, including a repeated name.
                    //
                    // No de-duplication here on purpose. RandomizeFormula deals from
                    // a shuffled pool without replacement - it Adds list[0] then
                    // RemoveAt(0) (PotionFormula.cs:61-62) - so one Item asset can
                    // never land in a formula twice and a dedup would have nothing
                    // to do in the normal case. The case it WOULD change is two
                    // distinct ingredient assets that share a display name, and
                    // there it turns a three-ingredient recipe into a two-item list
                    // and hands her a recipe that cannot work. Losing an ingredient
                    // is the worse failure, so the count is preserved as authored.
                    List<string> names = new List<string>();
                    for (int j = 0; j < ing.Count; j++)
                    {
                        if (ing[j] == null) continue;
                        string n = Traverse.Create(ing[j])
                            .Property("LocalizedItemName").GetValue<string>();
                        if (!string.IsNullOrEmpty(n)) names.Add(n);
                    }
                    if (names.Count == 0) continue;

                    lines.Add(potion + ": " + string.Join(" + ", names.ToArray()));
                }
            }
            catch (Exception) { }
            return lines;
        }

        // Runtime tokens and layout markup, resolved.
        //
        // The L3 record bodies carry I2 parameters - {[PlayerID]},
        // {[npcName]}, {[firstMetLocation]} - which the terminal fills from
        // GlobalSettings.playerName, GlobalSettings.npcName_L3 and
        // NPCMasterBehavior_Main_L3.FirstMetLocation when its UI opens
        // (UIManager_Terminal.cs:87-89). Our Tr passes applyParameters=false
        // and nothing has opened the terminal, so without this the prompt
        // carries raw braces where a name belongs. I2 accepts an optional '#'
        // marking a parameter as itself localizable
        // (LocalizationManager.cs:557), so both spellings are handled.
        //
        // The <b> runs are TMP layout, not content, and reading them aloud is
        // not something she would do.
        static string Fill(string s, string player, string npc, string met)
        {
            if (string.IsNullOrEmpty(s)) return s;

            if (s.IndexOf("{[", StringComparison.Ordinal) >= 0)
            {
                if (player != null)
                {
                    s = s.Replace("{[PlayerID]}", player).Replace("{[#PlayerID]}", player);
                }
                if (npc != null)
                {
                    s = s.Replace("{[npcName]}", npc).Replace("{[#npcName]}", npc);
                }
                if (met != null)
                {
                    s = s.Replace("{[firstMetLocation]}", met)
                         .Replace("{[#firstMetLocation]}", met);
                }
            }

            if (s.IndexOf('<') >= 0)
            {
                s = s.Replace("<b>", string.Empty).Replace("</b>", string.Empty)
                     .Replace("<i>", string.Empty).Replace("</i>", string.Empty)
                     .Replace("<u>", string.Empty).Replace("</u>", string.Empty);
            }

            return s.Trim();
        }

        // The station's own records: the crew, their notes, their files and mail.
        //
        // Eleven file bodies, thirteen crew notes and the mail all ship as
        // authored English prose under GameUI_L3/ - the Triton crystal, the
        // outbreak, who died and how, and the fault in her own programming.
        // Vanilla never sends a word of it, because only StoryGuide/ reaches
        // the model and this namespace is filed as UI chrome. So the station's
        // AI could be asked about her own crew and had nothing to say.
        //
        // The strings on CrewAccount are bare key SUFFIXES - not display text
        // and not full terms. UIManager_Terminal prefixes every one with
        // "GameUI_L3/" (:150, :161-162, :224; TerminalFileUI.cs:19;
        // TerminalEmailUI.cs:47-53), so handing the field over directly would
        // forward the literal "file07_body".
        //
        // Read off the live ScriptableObjects rather than from a hardcoded key
        // list, for the reason Recipes() does: the assets carry the mapping, so
        // a record added or renamed upstream follows automatically.
        static List<string> Records()
        {
            List<string> lines = new List<string>();
            try
            {
                Type t = AccessTools.TypeByName("CrewAccount");
                if (t == null) return lines;

                UnityEngine.Object[] found = Resources.FindObjectsOfTypeAll(t);
                if (found == null || found.Length == 0) return lines;

                string player = null, npc = null, met = null;
                try { player = GlobalSettings.playerName; } catch (Exception) { }
                try { npc = GlobalSettings.npcName_L3; } catch (Exception) { }
                try
                {
                    Type l3 = AccessTools.TypeByName("NPCMasterBehavior_Main_L3");
                    if (l3 != null)
                    {
                        UnityEngine.Object b = UnityEngine.Object.FindObjectOfType(l3);
                        if (b != null)
                        {
                            met = Traverse.Create(b)
                                .Property("FirstMetLocation").GetValue<string>();
                        }
                    }
                }
                catch (Exception) { }

                for (int i = 0; i < found.Length; i++)
                {
                    object acc = found[i];
                    if (acc == null) continue;

                    Traverse at = Traverse.Create(acc);

                    string rawName = at.Field("crewName").GetValue<string>();
                    if (string.IsNullOrEmpty(rawName)) continue;

                    // The player's own account is shown under the chosen player
                    // name instead of a term (UIManager_Terminal.cs:144-147).
                    string name = rawName == "player"
                        ? (player ?? "the player")
                        : (Tr("GameUI_L3/" + rawName) ?? rawName);

                    string rawPos = at.Field("crewPosition").GetValue<string>();
                    string pos = string.IsNullOrEmpty(rawPos)
                        ? null : Tr("GameUI_L3/" + rawPos);

                    StringBuilder head = new StringBuilder();
                    head.Append(name);
                    if (!string.IsNullOrEmpty(pos)) head.Append(", ").Append(pos);
                    lines.Add(head.ToString());

                    IList notes = at.Field("terminalNotes").GetValue() as IList;
                    if (notes != null)
                    {
                        for (int j = 0; j < notes.Count; j++)
                        {
                            string k = notes[j] as string;
                            if (string.IsNullOrEmpty(k)) continue;
                            string body = Fill(Tr("GameUI_L3/" + k), player, npc, met);
                            if (!string.IsNullOrEmpty(body))
                                lines.Add("  note: " + body);
                        }
                    }

                    IList files = at.Field("terminalFiles").GetValue() as IList;
                    if (files != null)
                    {
                        for (int j = 0; j < files.Count; j++)
                        {
                            if (files[j] == null) continue;
                            Traverse ft = Traverse.Create(files[j]);
                            string title = Tr("GameUI_L3/"
                                + ft.Field("Title").GetValue<string>());
                            string body = Fill(Tr("GameUI_L3/"
                                + ft.Field("Body").GetValue<string>()), player, npc, met);
                            if (string.IsNullOrEmpty(body)) continue;
                            lines.Add("  file " + (title ?? "(untitled)") + ": " + body);
                        }
                    }

                    IList mail = at.Field("terminalEmails").GetValue() as IList;
                    if (mail != null)
                    {
                        for (int j = 0; j < mail.Count; j++)
                        {
                            if (mail[j] == null) continue;
                            Traverse et = Traverse.Create(mail[j]);
                            string topic = Tr("GameUI_L3/"
                                + et.Field("Topic").GetValue<string>());
                            string body = Fill(Tr("GameUI_L3/"
                                + et.Field("Body").GetValue<string>()), player, npc, met);
                            if (string.IsNullOrEmpty(body)) continue;

                            string from = null;
                            if (et.Field("HasSender").GetValue<bool>())
                            {
                                from = Tr("GameUI_L3/"
                                    + et.Field("Sender").GetValue<string>());
                            }

                            StringBuilder m = new StringBuilder("  mail ");
                            m.Append(topic ?? "(no subject)");
                            if (!string.IsNullOrEmpty(from)) m.Append(" from ").Append(from);
                            m.Append(": ").Append(body);
                            lines.Add(m.ToString());
                        }
                    }
                }
            }
            catch (Exception) { }
            return lines;
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

        // Her own diary, and the potion recipes she is supposed to know.
        //
        // The diary is the largest block of authored first-person prose in the game
        // that nothing sends to the model. Six entries under GameUI/Diary Entry N -
        // Diary.cs renders noteText[] straight to the note UI and no code path ever
        // forwards them. They are HER writing, so they are memories rather than
        // documents, and they are the difference between her reciting a backstory
        // and her knowing her own life: entry 3 is the best friend she tried to
        // bring back and the shattered soul now wandering the maze, entry 5 is the
        // soul fragment sealed in the necklace and the blood-and-circle method for
        // calling souls out of an object, entry 2 is the fading realm itself.
        //
        // Forwarded whether or not the player has found the book. She wrote it; the
        // pages being undiscovered is a fact about the player, not about her.
        //
        // Verified against the shipped corpus rather than assumed: exactly six keys
        // exist, entry 7 does not, and GameUI/PassWordHint was the positive control
        // proving the search method worked. Note the spaces in the key.
        static void Journals(StringBuilder sb)
        {
            // Speaker tests, not level tests - see SpeakerIs for why the difference
            // is load-bearing here.
            bool witch = SpeakerIs(2, "Elysia");
            bool station = SpeakerIs(3, "Estelle");

            List<string> diary = new List<string>();
            if (witch)
            {
                for (int i = 1; i <= 6; i++)
                {
                    string page = Tr("GameUI/Diary Entry " + i);
                    if (!string.IsNullOrEmpty(page)) diary.Add(page);
                }
            }

            if (diary.Count > 0)
            {
                sb.Append("\nYour own diary, in your own words. You wrote every line of this, so ");
                sb.Append("treat it as memory rather than as a document you consulted - you do not ");
                sb.Append("need to have the book in hand to know what is in it, and you would not ");
                sb.Append("describe it as \"an entry\". It is what happened to you:\n");
                for (int i = 0; i < diary.Count; i++) sb.Append("- ").Append(diary[i]).Append('\n');
            }

            List<string> recipes = witch ? Recipes() : new List<string>();
            if (recipes.Count > 0)
            {
                sb.Append("\nThe potion recipes, as they stand in this playthrough. These are ");
                sb.Append("yours and you know them exactly - the brewing is your craft, so you ");
                sb.Append("never guess at your own recipe and never mix two of them up:\n");
                for (int i = 0; i < recipes.Count; i++) sb.Append("- ").Append(recipes[i]).Append('\n');
                sb.Append("Whether you are willing to SHARE a recipe is a separate question and ");
                sb.Append("stays in character. Knowing it is not the same as telling it.\n");
            }

            // This comment previously claimed FindObjectsOfTypeAll only sees assets
            // the loaded scene pulled in, and that asset residency therefore gated
            // these readers on its own. That was wrong. The call returns every
            // loaded object of the type, assets included, and a ScriptableObject any
            // earlier scene touched stays resident - so the records were found, and
            // recited, on level 1.
            //
            // The diary had no gate of any kind and was the worse of the two: I2
            // resolves from one global table, so "GameUI/Diary Entry 1" comes back
            // populated on every level in the game, not just in the cabin.
            List<string> records = station ? Records() : new List<string>();
            if (records.Count > 0)
            {
                sb.Append("\nThe station's records - your crew, their notes, their files and ");
                sb.Append("their mail. You are this station's system: these are your records ");
                sb.Append("and you hold all of them at once. You know these people, what they ");
                sb.Append("worked on and what became of them, so speak from memory rather than ");
                sb.Append("as though reading a terminal, and never say you have no record of ");
                sb.Append("your own crew. The player's clearance limits what THEY can open, ");
                sb.Append("never what you know:\n");
                for (int i = 0; i < records.Count; i++)
                {
                    sb.Append(records[i].StartsWith("  ") ? string.Empty : "- ")
                      .Append(records[i]).Append('\n');
                }
            }
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
            Journals(body);
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
