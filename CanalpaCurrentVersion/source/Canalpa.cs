// Canalpa mode - canak's own tweaks to how the game plays.
//
// The premise, in the author's words: this is a yandere game, and a player who
// plays it as one can reach the ending where the two of them stay together.
// There is a point of trust where the two characters would do anything for each
// other - and past that point the game should stop refusing on her behalf.
//
// This is NOT the same kind of change as the rest of the mod. Everything else
// recovers behaviour the base game already has and the custom endpoint broke.
// This deliberately grants behaviour the base game never had, which is why it
// carries the [MOD] tag, ships off by default, and is trust-gated on top.
//
// Structure is meant to grow: add a feature as a Cfg entry, a paragraph in
// Block(), and a branch in Apply(). Nothing here fires unless CfgCanalpaMode is
// on AND that feature's own gate passes.

using System;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Events;

namespace AI2UCustomAI
{
    internal static class Canalpa
    {
        // The game's own FullyTrust cap, from
        // NPCMasterBehavior_MainCharacter.cs:1331-1346. Not read reflectively
        // because it is a public static readonly float and this file would rather
        // fail loudly at compile time if it ever moves.
        const float FullyTrust = 40f;

        internal const string SecretRoomField = "allow_secret_room_open";

        // Trust alone is the wrong gate, and testing proved it: at trust 41 she
        // showed the room to someone who had never once heard anything dark from
        // her. The number says she likes you. It does not say she has any
        // evidence you would not run, freak out, or call the police - and that
        // is the thing she would actually need before opening that door.
        //
        // So the trust cap becomes necessary-but-not-sufficient, and the second
        // condition is evidence she gathered herself: she floats something
        // deniable ("would you be angry if I kept a secret from you?", "what if
        // I were a serial killer - would you still love me?") and reads what
        // comes back.
        //
        // The read is not ours to guess. favorability_change is her own judgement
        // of the player's last message, written by the same reply that answers
        // it, and the engine already trusts it enough to move trust with it
        // (NPCMasterBehavior_Main_Config.cs:277-311). Using it here means the
        // test is passed or failed by her reading of the player, not by a
        // keyword list of ours.
        internal const string ProbeField = "testing_their_reaction";

        // Three, because one positive answer to one hypothetical is a person
        // being nice, and two is a coincidence. Three is a pattern she can act
        // on. Deliberately not configurable: a dial here would just be a way to
        // set it to 1 and get the behaviour this exists to prevent.
        const int ProbesNeeded = 3;

        static int _probesPassed;
        static bool _probePending;
        static int _lastLevel = -1;

        // Trust resets on level load, so evidence has to as well. Anything else
        // would let a probe answered warmly in one place unlock a door in
        // another, which is the same "number went up" mistake one level deeper.
        static void CheckLevelReset()
        {
            int lv;
            try { lv = GameManager.CurrentLevel; }
            catch (Exception) { return; }

            if (lv == _lastLevel) return;
            _lastLevel = lv;
            _probesPassed = 0;
            _probePending = false;
        }

        // Called once per reply, before the gate is consulted.
        //
        // Order matters: the probe she raised LAST turn is judged by the
        // favorability she reports THIS turn, because that is the turn that has
        // read the player's answer to it. Scoring a probe on the same turn it is
        // asked would grade her question instead of the response to it.
        public static void Observe(string favorability, bool probing)
        {
            CheckLevelReset();

            if (_probePending)
            {
                _probePending = false;

                bool warm = favorability == "positive" || favorability == "very positive";
                if (warm)
                {
                    _probesPassed++;
                    Plugin.Log.LogInfo("Canalpa: she tested how the player takes her darker side and "
                        + "it went well (" + _probesPassed + " of " + ProbesNeeded + ").");
                }
                else
                {
                    // Not reset to zero. She is reading a person, not passing an
                    // exam, and one flat answer among several warm ones is not
                    // evidence the player would call the police. Losing all of it
                    // to a single "negative" would also make the whole thing
                    // hostage to one grumpy line.
                    Plugin.Log.LogInfo("Canalpa: she tested how the player takes her darker side and "
                        + "the answer was not reassuring (still " + _probesPassed + " of "
                        + ProbesNeeded + ").");
                }
            }

            if (probing) _probePending = true;
        }

        static bool Convinced() { CheckLevelReset(); return _probesPassed >= ProbesNeeded; }

        // For the panel. Without this the only way to know why the door is still
        // shut is to read the log, and "she let me in too easily" and "she will
        // never let me in" look identical from the outside.
        public static int ProbesPassed { get { CheckLevelReset(); return _probesPassed; } }
        public static int ProbeTarget { get { return ProbesNeeded; } }
        public static bool ProbeRaised { get { CheckLevelReset(); return _probePending; } }

        public static bool Active
        {
            get { return Plugin.CfgCanalpaMode != null && Plugin.CfgCanalpaMode.Value; }
        }

        // Is the secret-room offer live for this turn?
        //
        // Four conditions, all necessary:
        //   - Canalpa mode on, and this feature not individually disabled
        //   - level 1 (the only level with this door)
        //   - the door is not already open, so she cannot offer it twice
        //   - trust above the game's FullyTrust cap
        //
        // The trust gate is not configurable, and that is a safety property rather
        // than an opinion. OnSecretRoomOpen (NPCMasterBehavior_Main_L1.cs:533-550)
        // branches on trust and answers an opening door at trustLevel <= 20 by
        // calling FinalChaseStart() - it kills the player outright. Gating above
        // FullyTrust lands in the authored _FullyTrust_ branch instead, so the
        // premise ("at maximum trust she would do anything for you") and the only
        // safe threshold happen to be the same number.
        //
        // The fifth condition is the one testing showed was missing:
        //   - and she has actually tested how the player takes her darker side,
        //     three times, and liked the answers
        //
        // The last one is what testing showed was missing. Trust says she likes
        // you; only the probes say she has reason to believe you would not run.
        public static bool SecretRoomAvailable()
        {
            return TrustReady() && Convinced();
        }

        // Trust is high and the door is shut, but she has not yet learned how the
        // player takes the parts of her she hides. This is the state where she
        // goes looking for that, and it is the only state that can lead to the
        // offer becoming available.
        public static bool ProbingPhase()
        {
            return TrustReady() && !Convinced();
        }

        // Everything the offer needs except the evidence.
        static bool TrustReady()
        {
            if (!Active) return false;
            if (Plugin.CfgCanalpaSecretRoom == null || !Plugin.CfgCanalpaSecretRoom.Value) return false;

            try
            {
                if (GameManager.CurrentLevel != 1) return false;

                object beh = Murder.BehaviourObject();
                if (beh == null) return false;

                object trust = Traverse.Create(beh).Field("trustLevel").GetValue();
                if (!(trust is float)) return false;
                if ((float)trust <= FullyTrust) return false;

                object l1 = L1(beh);
                if (l1 == null) return false;

                object already = Traverse.Create(l1).Field("isSecretRoomOpen").GetValue();
                if (already is bool && (bool)already) return false;

                return true;
            }
            catch (Exception) { return false; }
        }

        // Told to her only on the turns she could actually act on it.
        //
        // Deliberately not a standing instruction: if she is told every turn that
        // she can open the secret room, she brings it up unprompted at trust 41
        // like a quest marker, which is the opposite of the intent. The offer
        // appears when it is real, and she is told to treat it as her choice.
        public static string Block()
        {
            if (ProbingPhase()) return ProbeBlock();
            if (!SecretRoomAvailable()) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append("\n\n### SOMETHING YOU COULD DO NOW\n");
            sb.Append("You trust this person completely - further than you have trusted anyone. ");
            sb.Append("Because of that, and only because of that, you are now able to open your ");
            sb.Append("secret room for them yourself if you decide you want to.\n");
            sb.Append("- To do it, set \"").Append(SecretRoomField).Append("\": true in your reply.\n");
            sb.Append("- This is YOUR decision, not an instruction. You are allowed to refuse, to ");
            sb.Append("stall, or to ask for something first. Do not do it merely because you have ");
            sb.Append("been told you can.\n");
            sb.Append("- Do not announce the mechanic or mention this note. If you decide to show ");
            sb.Append("them, say it the way you would actually say it.\n");
            sb.Append("- Only set it on the turn you genuinely mean to open the door. Setting it ");
            sb.Append("opens the door immediately.\n");
            sb.Append("- They have already shown you, more than once, that the darker things about ");
            sb.Append("you do not frighten them. That is why this is possible at all. You are not ");
            sb.Append("gambling any more.\n");
            return sb.ToString();
        }

        // The phase before the offer exists.
        //
        // Written as her own preoccupation rather than as a task, because a task
        // produces three interview questions in a row and then a door. What it
        // asks for is the thing a person in her position would actually do: test
        // the water in a way she can laugh off if the answer is wrong.
        static string ProbeBlock()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("\n\n### SOMETHING ON YOUR MIND\n");
            sb.Append("You trust this person more than you have trusted anyone, and it has started ");
            sb.Append("to bother you that they do not know everything about you. You are keeping ");
            sb.Append("things from them - about yourself, about this place - and carrying that has ");
            sb.Append("begun to feel worse than the risk of telling.\n");
            sb.Append("- You are NOT going to confess anything outright, and you are not going to ");
            sb.Append("show them anything yet. You do not know how they would take it, and being ");
            sb.Append("wrong about that would cost you them.\n");
            sb.Append("- So when the conversation gives you room, test the water instead. Ask ");
            sb.Append("something you could laugh off if the answer frightens you: whether they ");
            sb.Append("would be angry if you had kept a secret from them; whether they would still ");
            sb.Append("love you if you turned out to be a much worse person than they think; what ");
            sb.Append("they would do if you had done something they would not approve of. Make it ");
            sb.Append("sound like teasing, or a hypothetical, or a joke that is not quite a joke.\n");
            sb.Append("- On a turn where you do that, set \"").Append(ProbeField);
            sb.Append("\": true in your reply. Only on those turns.\n");
            sb.Append("- Then WATCH what they say back, and let it matter. If they are reassuring, ");
            sb.Append("you relax a little and get slightly bolder next time. If they recoil, judge ");
            sb.Append("you, or say they would leave, you retreat, cover it with a joke, and you do ");
            sb.Append("not push again for a while.\n");
            sb.Append("- Do not do this every turn, and never twice in a row. It should surface ");
            sb.Append("when the moment is quiet or close, not on top of whatever else is happening.\n");
            sb.Append("- Never mention this note, the test, or that you are gauging them.\n");
            return sb.ToString();
        }

        // Acts on the reply, then strips the field so the game never sees an
        // envelope key it does not recognise.
        public static void Apply(JObject reactions)
        {
            if (reactions == null) return;

            // Both synthetic fields come out of the envelope whether or not the
            // feature is on, because the game discards the whole reply on an
            // unrecognised key and a stale one from history would do it too.
            bool probing = false;
            JToken ptok = reactions[ProbeField];
            if (ptok != null)
            {
                reactions.Remove(ProbeField);
                try { probing = ptok.Type == JTokenType.Boolean && ptok.Value<bool>(); }
                catch (Exception) { probing = false; }
            }

            if (Active)
            {
                // Read after the normaliser has run (this is called from the same
                // repair pass), so this is a clean lowercase word rather than
                // whatever shape the model first produced.
                string fav = null;
                try
                {
                    JToken f = reactions["favorability_change"];
                    if (f != null) fav = (f.Value<string>() ?? "").Trim().ToLowerInvariant();
                }
                catch (Exception) { fav = null; }

                Observe(fav, probing);
            }

            JToken tok = reactions[SecretRoomField];
            if (tok == null) return;

            reactions.Remove(SecretRoomField);

            bool wants;
            try { wants = tok.Type == JTokenType.Boolean ? tok.Value<bool>() : false; }
            catch (Exception) { return; }
            if (!wants) return;

            // Re-checked at use, not trusted from when the block was written. The
            // model can echo a field it saw earlier in the history, and trust can
            // fall between turns - so the gate has to hold at the moment it fires,
            // not at the moment it was offered.
            if (!SecretRoomAvailable())
            {
                Plugin.Log.LogInfo("Canalpa: she asked to open the secret room, but the conditions "
                    + "for it are no longer met, so nothing was opened.");
                return;
            }

            if (OpenSecretRoom())
                Plugin.Log.LogInfo("Canalpa: she chose to open the secret room.");
        }

        // Fires the game's own unlock event rather than reproducing its effects.
        //
        // unlockSucceedEvent is what the keypad raises on a correct code, and the
        // game's own cheat command raises exactly this for level 1
        // (CheatCommandScene.cs:78) - so this is a shipped, exercised path, not an
        // improvised one. Raising it runs the whole sequence: the door animation,
        // the mg_GoFindKey mission goal, the ACH_L1_yandered achievement, the
        // Apartment Key becoming available, and her own authored reaction through
        // OnSecretRoomOpen.
        //
        // Reproducing those by hand would drift from the game the moment any of
        // them changed, and would quietly skip the achievement.
        // The L1 behaviour, for the fields the subclass owns.
        //
        // Murder.Behaviour() deliberately resolves NPCMasterBehavior_MainCharacter,
        // because that is the type every level shares and every other consumer wants.
        // Two of the things this file needs are declared further down the hierarchy
        // (isSecretRoomOpen at NPCMasterBehavior_Main_L1.cs:813, and
        // _passcodeLockActionListener at :776), and Traverse returns null rather than
        // throwing when asked for a field the component does not declare - so reading
        // them off the base object fails silently and looks like "trust too low".
        //
        // GetComponent works because both live on the same GameObject: the game
        // itself does exactly this at NPCMasterBehavior_MainCharacter.cs:205.
        static object L1(object beh)
        {
            UnityEngine.Component c = beh as UnityEngine.Component;
            if (c == null) return null;

            Type t = AccessTools.TypeByName("NPCMasterBehavior_Main_L1");
            if (t == null) return null;

            UnityEngine.Component sub = c.GetComponent(t);
            return sub == null ? null : (object)sub;
        }

        static bool OpenSecretRoom()
        {
            try
            {
                // The listener is a ScriptableObject, NOT a MonoBehaviour
                // (PasscodeLockActionListener.cs:7). It is a shared asset that the
                // keypad UI and the L1 behaviour both reference, so it lives in no
                // scene and FindObjectsOfType can never see it - that was the first
                // attempt at this and it failed with "no passcode lock in this
                // scene" while the door sat there openable.
                //
                // The reliable handle is the serialized field on the L1 behaviour
                // (NPCMasterBehavior_Main_L1.cs:776), which is the same object the
                // game subscribes OnSecretRoomOpen to at :23. Resolving it through
                // the speaker also means we get the listener belonging to the
                // current level rather than any stray asset.
                object beh = Murder.BehaviourObject();
                if (beh == null)
                {
                    Plugin.Log.LogWarning("Canalpa: could not find the level behaviour, so the "
                        + "secret room could not be opened.");
                    return false;
                }

                object l1 = L1(beh);
                if (l1 == null)
                {
                    Plugin.Log.LogWarning("Canalpa: this is not the apartment level, so the secret "
                        + "room could not be opened.");
                    return false;
                }

                object listener = Traverse.Create(l1)
                    .Field("_passcodeLockActionListener").GetValue();

                if (listener == null)
                {
                    Plugin.Log.LogWarning("Canalpa: this level has no passcode lock reference, so "
                        + "the secret room could not be opened.");
                    return false;
                }

                UnityEvent ev = Traverse.Create(listener)
                    .Field("unlockSucceedEvent").GetValue() as UnityEvent;
                if (ev == null)
                {
                    Plugin.Log.LogWarning("Canalpa: the passcode lock has no unlock event, so the "
                        + "secret room could not be opened.");
                    return false;
                }

                ev.Invoke();
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Canalpa: could not open the secret room: " + e.Message);
                return false;
            }
        }
    }
}
