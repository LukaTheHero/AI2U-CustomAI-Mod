// Roleplay conventions - the two markers that make freeform RP hold up.
//
// Both exist because of one real session (Estelle, 2026-08-16, in the log):
// the player declared a ten-year timeskip in prose and she reasoned her way
// out of it - "the station maintains a persistent state, so I will rationalize
// your premise within my protective logic" - and player messages narrating
// actions in *asterisks* got "you did not actually do that". Models default to
// defending the literal game state against the player's fiction, because
// nothing in the prompt ever told them the fiction is allowed to win.
//
// Two markers, two scopes:
//
//   &timeskip&   per-turn, like [OOC]. The player declares that time passes;
//                the declaration is the instruction, so the block rides only
//                the turns that carry the marker.
//
//   *action*     a standing convention. Asterisk-narration is how this genre
//                writes actions, past turns full of it sit in the history for
//                the whole session, and a rule that only applied on marked
//                turns would leave every earlier *action* deniable again.
//
// Why '&' survives to the model at all: the game's input filter
// (NPCMasterBehavior.cs:90) strips only {}[]\/ from player text. '&' and '*'
// pass through untouched, which is precisely why these two characters were
// chosen - no Bare()/stripped-form dance like Ooc.cs needs for its brackets.
using System;
using System.Text;
using BepInEx.Configuration;

namespace AI2UCustomAI
{
    internal static class Roleplay
    {
        // Set per turn from the outgoing message, read while that turn's
        // request is built. Survives the bad-JSON retries on purpose: a retry
        // is the same turn and must carry the same framing.
        static bool _timeskipThisTurn;

        public static bool TimeskipThisTurn { get { return _timeskipThisTurn; } }

        static bool On(ConfigEntry<bool> e) { return e != null && e.Value; }

        public static string TimeskipTag
        {
            get
            {
                ConfigEntry<string> t = Plugin.CfgTimeskipTag;
                string s = t == null ? null : t.Value;
                return string.IsNullOrEmpty(s) || s.Trim().Length == 0 ? "&timeskip&" : s.Trim();
            }
        }

        // Always assigns, so an unmarked turn clears what a marked one set -
        // same discipline as Ooc.NotePlayerMessage, same reason.
        public static void NotePlayerMessage(string message)
        {
            _timeskipThisTurn = false;
            if (string.IsNullOrEmpty(message)) return;
            if (!On(Plugin.CfgTimeskipEnabled)) return;

            if (message.IndexOf(TimeskipTag, StringComparison.OrdinalIgnoreCase) < 0) return;

            _timeskipThisTurn = true;
            Plugin.Log.LogInfo("Roleplay: timeskip declared this turn. She is told the elapsed "
                + "time is real and to pick the scene up on the far side of it.");
        }

        // The per-turn block. Null on every turn without the marker, so an
        // ordinary request is byte-for-byte unchanged.
        public static string TimeskipBlock()
        {
            if (!_timeskipThisTurn) return null;

            string tag = TimeskipTag;
            StringBuilder sb = new StringBuilder();

            sb.Append("### TIMESKIP - THE PLAYER HAS MOVED TIME FORWARD\n");
            sb.Append("The player's message contains ").Append(tag).Append(", the roleplay ");
            sb.Append("timeskip marker of this modded game. The time they describe passing HAS ");
            sb.Append("PASSED. This is a player-held control, like a scene cut in a story - it is ");
            sb.Append("not a request you weigh, not a premise you debate, and not something your ");
            sb.Append("character can decline, rationalize away, or quietly ignore.\n");
            sb.Append("- Accept the stated span exactly. Ten minutes, a night, ten years - whatever ");
            sb.Append("the message says, that much time has now genuinely gone by in the fiction.\n");
            sb.Append("- Pick the scene up on the FAR side of the skip. Your mood, the topic and ");
            sb.Append("the moment all move forward; do not resume the old beat as if nothing ");
            sb.Append("happened, and never say the skip did not occur.\n");
            sb.Append("- Let the time have weight. Think about what that span would plausibly do ");
            sb.Append("to your feelings, routines and surroundings, and speak from after it - ");
            sb.Append("someone reunited after years does not continue the sentence from before ");
            sb.Append("them.\n");
            sb.Append("- What time CANNOT do: it does not change the game's mechanical state. ");
            sb.Append("Doors, trust, repairs and items stay exactly as the engine reports them, ");
            sb.Append("and your engine fields keep their real values. If the player says the skip ");
            sb.Append("accomplished something mechanical (\"ten years later, the pod is fixed\"), ");
            sb.Append("play the elapsed time as real but treat the mechanical claim by the usual ");
            sb.Append("rules - the field is still the act.\n");
            sb.Append("- Do not echo ").Append(tag).Append(" back in your reply, and do not talk ");
            sb.Append("about markers or mods. In the fiction, time simply passed.\n");

            return sb.ToString();
        }

        // The standing convention. One compact block, only while the toggle is
        // on - and unlike the timeskip it is NOT gated on the current message,
        // because the history the model re-reads is full of earlier *actions*
        // that must stay non-deniable for the whole session.
        public static string ActionsBlock()
        {
            if (!On(Plugin.CfgActionsAreReal)) return null;

            StringBuilder sb = new StringBuilder();

            sb.Append("### PLAYER ACTIONS IN ASTERISKS ARE REAL\n");
            sb.Append("When the player writes something between *asterisks* - *hands you the ");
            sb.Append("cup*, *fixed the relay while you slept*, *hugs you* - that is roleplay ");
            sb.Append("narration of something they DID. It already happened. Treat it exactly ");
            sb.Append("as you would treat the engine telling you the same thing:\n");
            sb.Append("- Never deny it, never say they did not really do it, never answer as if ");
            sb.Append("it were only words. \"You didn't actually hand me anything\" is the one ");
            sb.Append("reply that is always wrong.\n");
            sb.Append("- React to it in character: to the deed, not to the sentence.\n");
            sb.Append("- Your character can still have any OPINION of the act - be delighted, ");
            sb.Append("object, push back, be hurt. Contesting whether it happened is the only ");
            sb.Append("thing off the table.\n");
            sb.Append("- The engine still owns its own numbers. A narrated act does not move ");
            sb.Append("trust, open doors or add items by itself - where the act implies a ");
            sb.Append("mechanical change, the usual fields are still how anything real happens. ");
            sb.Append("If narration and the engine state disagree, the engine is right about ");
            sb.Append("the WORLD, but the player is always right about what THEY just did.\n");
            sb.Append("- You may narrate your own actions between asterisks as well; the game ");
            sb.Append("does not display them specially, so keep them short and inside your ");
            sb.Append("normal reply.\n");

            return sb.ToString();
        }
    }
}
