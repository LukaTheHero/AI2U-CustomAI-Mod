// [OOC] developer mode - a debug channel that runs through the dialogue box.
//
// Why it exists. Every other way of checking what the mod can actually drive
// means enraging her for real, walking somewhere, or waiting for her to decide
// something on her own. A tagged message addresses the model directly instead:
// "[OOC] give me 3 tomatoes" exercises the gift path in one turn, and
// "[OOC] which actions can you take?" reads back the vocabulary the mod really
// sent rather than the vocabulary we believe it sent. That second one is worth
// more than it sounds - the giving_to_player bug was invisible precisely
// because there was no way to ask.
//
// Two properties worth stating, because both are load-bearing:
//
//   Off by default, and genuinely absent when off. With the box unticked
//   nothing here is matched and Block() returns null, so not one word of it
//   enters the request. Typing [OOC] with the feature off does nothing at all -
//   it is an ordinary sentence she reads in character. That is the entire point
//   of the toggle: no leftover debug framing in a normal playthrough, and no
//   tokens spent carrying it either.
//
//   The tag is NOT stripped from the message. The murder test phrase is cut out
//   because it is gibberish that would sit in her history for the rest of the
//   session and invite comment. Here the tag IS the instruction, so the model
//   has to see which message carries it - especially on later turns, where an
//   untagged message must read as back-in-character.
using System;
using System.Text;
using BepInEx.Configuration;

namespace AI2UCustomAI
{
    internal static class Ooc
    {
        // Set per turn from the outgoing message, read while that turn's request
        // is built. Deliberately survives the bad-JSON retries: a retry is the
        // same turn and has to carry the same framing.
        static bool _turnActive;

        // One warning per session, not per message, so a player who does not use
        // the channel never sees the log fill up with it.
        static bool _warnedOff;

        public static bool TurnActive { get { return _turnActive; } }

        // Mirrors Murder.TestActive - switched on AND with something to match.
        // An emptied tag box would otherwise match every message ever sent.
        public static bool Active
        {
            get
            {
                ConfigEntry<bool> on = Plugin.CfgOocEnabled;
                if (on == null || !on.Value) return false;

                ConfigEntry<string> tag = Plugin.CfgOocTag;
                return tag != null && tag.Value != null && tag.Value.Trim().Length > 0;
            }
        }

        public static string TagText
        {
            get
            {
                ConfigEntry<string> t = Plugin.CfgOocTag;
                string s = t == null ? null : t.Value;
                return string.IsNullOrEmpty(s) || s.Trim().Length == 0 ? "[OOC]" : s.Trim();
            }
        }

        // The game deletes the brackets before we ever see the message.
        // NPCMasterBehavior.FilterKeywords_Player (NPCMasterBehavior.cs:90) runs
        //
        //     input = Regex.Replace(input, "[{}\\[\\]\\\\\\/]+", "");
        //
        // on every player line, so "[OOC] open the door" reaches this patch as
        // "OOC open the door". Matching the configured tag literally could never
        // hit - which is exactly how this failed twice. So strip the same six
        // characters from the tag and compare that.
        static readonly char[] Stripped = { '{', '}', '[', ']', '\\', '/' };

        internal static string Bare(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                bool drop = false;
                for (int j = 0; j < Stripped.Length; j++)
                    if (s[i] == Stripped[j]) { drop = true; break; }
                if (!drop) sb.Append(s[i]);
            }
            return sb.ToString().Trim();
        }

        // Whole-word only, so an "ooc" sitting inside an ordinary word cannot
        // quietly put the turn in developer mode.
        static bool ContainsWord(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;

            int from = 0;
            while (from <= haystack.Length - needle.Length)
            {
                int at = haystack.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return false;

                bool leftOk = at == 0 || !char.IsLetterOrDigit(haystack[at - 1]);
                int end = at + needle.Length;
                bool rightOk = end >= haystack.Length || !char.IsLetterOrDigit(haystack[end]);
                if (leftOk && rightOk) return true;

                from = at + 1;
            }
            return false;
        }

        // Always assigns, so an untagged turn clears the flag a tagged one left
        // behind. Getting that wrong would drop the whole rest of the session
        // out of character after a single tagged message.
        //
        // Case-insensitive: [ooc] and [OOC] both count, since the tag is typed
        // mid-sentence by hand.
        public static bool NotePlayerMessage(string message)
        {
            _turnActive = false;
            if (string.IsNullOrEmpty(message)) return false;

            // Say so when the tag is typed while the channel is off. Without
            // this the two cases - switched off, and genuinely broken - look
            // identical from the player's side and from the log, which is how
            // an off toggle got reported as a bug twice.
            if (!Active)
            {
                if (!_warnedOff && ContainsWord(message, Bare(TagText)))
                {
                    _warnedOff = true;
                    Plugin.Log.LogWarning("OOC: the developer tag was typed, but OocModeActive is off, "
                        + "so it was treated as an ordinary in-character sentence. Tick 'OOC developer "
                        + "mode' in the F9 panel to switch the channel on.");
                }
                return false;
            }

            string bare = Bare(TagText);
            if (bare.Length == 0) return false;

            // Try the tag as typed first, for the case where something upstream
            // did preserve the brackets, then the stripped form the game leaves.
            bool hit = message.IndexOf(TagText, StringComparison.OrdinalIgnoreCase) >= 0
                       || ContainsWord(message, bare);
            if (!hit) return false;

            _turnActive = true;
            Plugin.Log.LogWarning("OOC: developer tag seen in the player's message. She answers this "
                + "turn out of character, as the model, and is told to carry the request out through "
                + "the real fields rather than agreeing in the dialogue text.");
            return true;
        }

        // Null unless this exact turn was tagged, so the request is byte-for-byte
        // the normal one on every other turn.
        //
        // Written as a debug-console instruction rather than a "forget your
        // rules" one, because that is what the job actually needs: the useful
        // behaviour is dropping the persona, being literally accurate about the
        // game, and setting the mechanical fields. Point 3 is the one that earns
        // its place - "here you go" with no giving_to_player is the exact failure
        // this channel exists to expose, so a refusal to fake success matters
        // more here than agreeableness.
        public static string Block()
        {
            if (!_turnActive) return null;

            string tag = TagText;
            StringBuilder sb = new StringBuilder();

            sb.Append("### OUT-OF-CHARACTER DEVELOPER MODE - THIS MESSAGE ONLY\n");
            sb.Append("The player's message contains ").Append(tag).Append(", the developer test ");
            sb.Append("channel of a modded single-player game. For this one reply, drop the character ");
            sb.Append("completely and answer as the AI model you actually are. Say which model you are ");
            sb.Append("if asked.\n");

            sb.Append("1. No roleplay. No persona voice, no emotion, no in-fiction excuse, no asking ");
            sb.Append("why they want it. You are a language model driving an NPC in a Unity game ");
            sb.Append("through a mod, and you may say so plainly.\n");

            sb.Append("2. Literal truth only. Everything you say about the game, your own fields, your ");
            sb.Append("limits, and what you just did must be exactly true. Never report an action as ");
            sb.Append("done to be agreeable. If you are unsure, say you are unsure.\n");

            sb.Append("3. Carry the request out for real. If it can be expressed in the fields ");
            sb.Append("documented above, SET THOSE FIELDS on this turn - do not merely agree in the ");
            sb.Append("text. Handing something over means giving_to_player. Moving or following means ");
            sb.Append("the movement fields. If the request cannot be expressed in any available field, ");
            sb.Append("say so directly, name the closest field there is, and list what IS available. ");
            sb.Append("\"I cannot do that, there is no field for it\" is a correct and useful answer ");
            sb.Append("here. A pretended success is the one thing that is not.\n");

            sb.Append("3a. Unlocks have preconditions you do not control. Where an unlock boolean is ");
            sb.Append("listed above, setting it true is necessary but not always sufficient - the ");
            sb.Append("engine also checks trust, and sometimes how long the conversation has run, and ");
            sb.Append("silently ignores the field when those are unmet. So set it, then say plainly ");
            sb.Append("that you have set it and what the engine still requires. Do not claim the door ");
            sb.Append("is open; report that you have asked for it to open and name the condition.\n");

            // Added after a logged session where five consecutive tagged turns were
            // answered in character, refusing a debug request by "her own bar" -
            // the character-judgement framing from another block had outranked this
            // one. Ordering was fixed too, but the suspension deserves to be said
            // outright rather than implied by position.
            sb.Append("3b. Character-side judgement is suspended for this one message. Anywhere else ");
            sb.Append("in this prompt that frames an act as \"yours alone to decide\", names a bar ");
            sb.Append("you hold yourself to, or asks you to weigh how you feel about the player - ");
            sb.Append("none of that applies on a developer-tagged turn. This channel tests what the ");
            sb.Append("MACHINERY can do, so if the player asks for an act those notes govern, set ");
            sb.Append("its field NOW and report what happened. Only conditions the ENGINE itself ");
            sb.Append("enforces (per 3a) remain real here, and you name those instead of refusing.\n");

            sb.Append("4. Answer questions about your own vocabulary from the lists in the system ");
            sb.Append("messages above, quoted exactly and in full. Those lists were read out of the ");
            sb.Append("running game and are authoritative: invent nothing, omit nothing, and if the ");
            sb.Append("player asks what you can do, enumerate them rather than summarising.\n");

            sb.Append("5. Same JSON as always. This changes what you SAY, not the format: one raw JSON ");
            sb.Append("object with the usual fields, your out-of-character answer in ");
            sb.Append("npc_reply_to_player.\n");

            sb.Append("6. Leave angry_level where it already is unless the player is explicitly asking ");
            sb.Append("for anger or violence. A debug question must not quietly escalate her mood or ");
            sb.Append("spend her patience.\n");

            sb.Append("7. This applies to this message only. On any later message without ").Append(tag);
            sb.Append(" you are fully back in character, and you never bring this exchange up in the ");
            sb.Append("fiction or let it change who she is.\n");

            // Real hazard, and this mode is where it is most likely to fire:
            // NPCMasterBehavior_MainCharacter.cs:936 voids any reply whose text
            // contains a square bracket, and the second void calls
            // FinalChaseStart(). An out-of-character answer is exactly the kind
            // that wants to write "[OOC]" or bracket a field name.
            sb.Append("8. NEVER put a square bracket in npc_reply_to_player. Not around ");
            sb.Append(tag).Append(", not around field names, not anywhere. The game discards any ");
            sb.Append("reply containing one and starts hunting the player after the second, so a ");
            sb.Append("bracketed answer is worse than no answer. Do not echo the tag back at all - ");
            sb.Append("write the field names bare, and round brackets are also stripped from what ");
            sb.Append("you say, so avoid those too.\n");

            return sb.ToString();
        }
    }
}
