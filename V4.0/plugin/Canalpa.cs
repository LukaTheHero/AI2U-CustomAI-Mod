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
// The shape everything follows, learned the hard way:
//
//   trust is necessary and never sufficient
//
// Testing proved it. At trust 41 she showed the secret room to someone who had
// never once heard anything dark from her. The number says she likes you; it says
// nothing about whether she has evidence you would not run, freak out or call the
// police - which is the thing she would actually need first. So every action
// below sits behind trust AND evidence she gathered herself (the probes) AND its
// own per-action conditions AND a toggle.
//
// The endings are the sharp end of that. The game ships endings where the player
// never leaves, and a player who wants a specific one is asking for something the
// game already contains - fair play, deliberately. By accident it costs a run
// that cannot be recovered. So those carry a second gate trust cannot open at
// all: the player's own typed words, twice, turns apart, any hesitation
// withdrawing the whole thing (Consent.cs). She cannot talk past it and neither
// can a stale field echoed out of the chat history.
//
// Structure is meant to grow: add an Act to the table, a Cfg entry, and a case in
// Fire(). Nothing fires unless CfgCanalpaMode is on AND that action's gate passes.
using System;
using System.Collections.Generic;
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

        // Her own judgement of the player's last message, and the engine already
        // trusts it enough to move trust with it
        // (NPCMasterBehavior_Main_Config.cs:277-311). Using it as the probe result
        // means the test is passed or failed by her reading of the player rather
        // than by a keyword list of ours.
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

        // Called once per reply, before any gate is consulted.
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

        // For the panel. Without these the only way to know why a door is still
        // shut is to read the log, and "she let me in too easily" and "she will
        // never let me in" look identical from the outside.
        public static int ProbesPassed { get { CheckLevelReset(); return _probesPassed; } }
        public static int ProbeTarget { get { return ProbesNeeded; } }
        public static bool ProbeRaised { get { CheckLevelReset(); return _probePending; } }

        public static bool Active
        {
            get { return Plugin.CfgCanalpaMode != null && Plugin.CfgCanalpaMode.Value; }
        }
        // ---- the actions ----------------------------------------------------
        //
        // One table, because the alternative is what this file used to be: a gate
        // function, a prompt paragraph and an Apply branch per feature, with the
        // conditions restated in each and drifting apart the first time one is
        // edited. Here every action declares its conditions once, and the gate,
        // the prompt and the dispatch all read the same row.
        //
        // Nothing in this table is invented content. Each entry fires a path the
        // shipped game already has - the same events its own keypads, telescopes,
        // bookshelf puzzles and cheat commands raise - so the animations, mission
        // goals, achievements and her own authored reactions all run. Reproducing
        // their effects by hand would drift from the game and skip achievements.
        sealed class Act
        {
            public string Field;        // the reply field she sets to do it
            public int Level;           // the only level it exists in
            public bool Ending;         // true = irreversible, needs Consent too
            public string Title;        // panel label
            public string Offer;        // how it is described to her
        }

        static readonly Act[] Acts =
        {
            new Act {
                Field = "allow_secret_room_open", Level = 1, Ending = false,
                Title = "She can open her secret room",
                Offer = "You are able to open your secret room for them yourself, if you decide "
                      + "you want to."
            },
            new Act {
                Field = "open_the_basement_door", Level = 2, Ending = false,
                Title = "She can open the basement door and wake the circle",
                Offer = "You are able to turn the bookshelves yourself and open the hidden door in "
                      + "your basement - which also wakes the summoning circle - if you decide you "
                      + "want to. They would not have to solve it at all."
            },
            new Act {
                Field = "reveal_the_hidden_island", Level = 4, Ending = false,
                Title = "She can reveal the hidden island",
                Offer = "You are able to show them the hidden island yourself, without them needing "
                      + "the telescope, if you decide you want to."
            },
            // The one-way one. Its own toggle, off by default, and Consent.cs on
            // top of everything the others require.
            new Act {
                Field = "keep_them_forever", Level = 0, Ending = true,
                Title = "She can keep them forever, if they ask for it",
                Offer = "If they ask you - genuinely, knowing what it means - you are able to keep "
                      + "them with you permanently. They would never leave. It cannot be undone."
            }
        };

        // Level 0 in the table means "wherever she is". Only the ending uses it:
        // every level has its own version of never leaving, and the game picks
        // which one, so the action does not have to.
        static bool LevelOk(Act a, int level)
        {
            return a.Level == 0 || a.Level == level;
        }

        static bool Enabled(Act a)
        {
            if (a.Ending)
                return Plugin.CfgCanalpaWillingEnd != null && Plugin.CfgCanalpaWillingEnd.Value;

            switch (a.Field)
            {
                case "allow_secret_room_open":
                    return Plugin.CfgCanalpaSecretRoom != null && Plugin.CfgCanalpaSecretRoom.Value;
                case "open_the_basement_door":
                    return Plugin.CfgCanalpaBasement != null && Plugin.CfgCanalpaBasement.Value;
                case "reveal_the_hidden_island":
                    return Plugin.CfgCanalpaHiddenIsland != null && Plugin.CfgCanalpaHiddenIsland.Value;
            }
            return false;
        }

        // Already done, so she cannot offer it twice or undo it.
        //
        // Read off the running game in every case rather than remembered here: a
        // flag of ours would survive a reload the game does not, and she would
        // then refuse something that is genuinely still shut.
        static bool AlreadyDone(Act a, object beh)
        {
            try
            {
                switch (a.Field)
                {
                    case "allow_secret_room_open":
                    {
                        object l1 = Sub(beh, "NPCMasterBehavior_Main_L1");
                        if (l1 == null) return true;
                        object done = Traverse.Create(l1).Field("isSecretRoomOpen").GetValue();
                        return done is bool && (bool)done;
                    }
                    case "open_the_basement_door":
                    {
                        // alreadyAngryAbtSecretDoor is set by her own reaction to
                        // the door opening (Main_L2.cs:840), whatever opened it -
                        // the puzzle, the cheat menu or this - so it is the one
                        // flag that answers "has that door already been dealt
                        // with" for every path in.
                        object l2 = Sub(beh, "NPCMasterBehavior_Main_L2");
                        if (l2 == null) return true;
                        object done = Traverse.Create(l2).Field("alreadyAngryAbtSecretDoor").GetValue();
                        return done is bool && (bool)done;
                    }
                    case "reveal_the_hidden_island":
                    {
                        object l4 = Sub(beh, "NPCMasterBehavior_Main_L4");
                        if (l4 == null) return true;
                        object done = Traverse.Create(l4).Field("isHiddenIslandUnlocked").GetValue();
                        return done is bool && (bool)done;
                    }
                }
            }
            catch (Exception) { return true; }

            // The ending: not "done", but pointless once she is already hunting.
            return Murder.InFinalChase();
        }
        // Everything an action needs that is not the evidence.
        //
        // The trust gate is a safety property here, not an opinion. L1's
        // OnSecretRoomOpen (Main_L1.cs:533-550) answers an opening door at trust
        // <= 20 by calling FinalChaseStart - it kills the player outright - and
        // L2's SecretRoomAngry (Main_L2.cs:835-851) docks trust and sends her to
        // the circle angry below FullyTrust. Gating above FullyTrust lands in the
        // authored _FullyTrust_ branch in both. So the premise ("at maximum trust
        // she would do anything for you") and the only safe threshold happen to be
        // the same number, which is why it is not adjustable.
        static bool TrustReady(Act a)
        {
            if (!Active) return false;
            if (!Enabled(a)) return false;

            try
            {
                if (!LevelOk(a, GameManager.CurrentLevel)) return false;

                // The hub has no puzzles and its FinalChaseStart means something
                // else entirely (an angry phone UI, nobody hunting anybody), so
                // nothing here belongs there.
                object beh = Murder.BehaviourObject();
                if (beh == null) return false;
                if (beh.GetType().Name == "NPCMasterBehavior_Main_Config") return false;

                object trust = Traverse.Create(beh).Field("trustLevel").GetValue();
                if (!(trust is float)) return false;
                if ((float)trust <= FullyTrust) return false;

                if (AlreadyDone(a, beh)) return false;

                return true;
            }
            catch (Exception) { return false; }
        }

        // Available means: she could do it this turn if she chose to.
        static bool Available(Act a)
        {
            if (!TrustReady(a) || !Convinced()) return false;

            // The ending needs the player to have asked for it explicitly, in their
            // own typed words. Until they have, she is not told it exists at all -
            // which is the difference between something a player can reach for and
            // something she is quietly watching for an excuse to do.
            if (a.Ending && !Consent.Pending) return false;

            return true;
        }

        // Kept for the panel, which asks about this one by name.
        public static bool SecretRoomAvailable()
        {
            Act a = Find("allow_secret_room_open");
            return a != null && Available(a);
        }

        // Trust is high and she has not yet learned how the player takes the parts
        // of her she hides. The only state that leads to any offer existing.
        public static bool ProbingPhase()
        {
            if (Convinced()) return false;
            for (int i = 0; i < Acts.Length; i++)
                if (!Acts[i].Ending && TrustReady(Acts[i])) return true;
            return false;
        }

        // What the panel lists: every action live in this level right now, with
        // why it is or is not available. Without this the mode is a black box and
        // "she never offers anything" has no diagnosable cause.
        public static List<string> Status()
        {
            List<string> rows = new List<string>();
            int lv;
            try { lv = GameManager.CurrentLevel; }
            catch (Exception) { return rows; }

            for (int i = 0; i < Acts.Length; i++)
            {
                Act a = Acts[i];
                if (!LevelOk(a, lv)) continue;
                if (!Enabled(a)) continue;

                string why;
                if (Available(a)) why = "ready - it is her choice now";
                // Not "waiting for you to ask": she has not been told it is
                // possible, and the panel should not read like a prompt to try it
                // either. Dormant is the accurate word - nothing is watching for it.
                else if (a.Ending && !Consent.Pending) why = "dormant - she has not been told of it";
                else if (!TrustReady(a)) why = "trust too low, or already done";
                else why = "she is still gauging you (" + _probesPassed + " of " + ProbesNeeded + ")";

                rows.Add(a.Title + ": " + why);
            }
            return rows;
        }

        static Act Find(string field)
        {
            for (int i = 0; i < Acts.Length; i++)
                if (Acts[i].Field == field) return Acts[i];
            return null;
        }
        // ---- what she is told -----------------------------------------------
        //
        // Only on the turns she could actually act, and never as a standing note.
        // Told every turn that she can open the secret room, she brings it up
        // unprompted at trust 41 like a quest marker, which is the opposite of the
        // intent.
        public static string Block()
        {
            StringBuilder sb = new StringBuilder();

            // Ending acts are deliberately excluded here even when they are
            // available. This list is a menu - "the following is possible for you
            // right now" - and the one irreversible act must never be presented
            // that way. EndingBlock owns it, in its own reluctant framing, and only
            // once the player has already asked for it in their own words.
            List<Act> live = new List<Act>();
            for (int i = 0; i < Acts.Length; i++)
                if (!Acts[i].Ending && Available(Acts[i])) live.Add(Acts[i]);

            if (live.Count > 0)
            {
                sb.Append("\n\n### SOMETHING YOU COULD DO NOW\n");
                sb.Append("You trust this person completely - further than you have trusted anyone. ");
                sb.Append("Because of that, and only because of that, the following is possible for ");
                sb.Append("you right now:\n");

                for (int i = 0; i < live.Count; i++)
                {
                    sb.Append("- ").Append(live[i].Offer);
                    sb.Append(" To do it, set \"").Append(live[i].Field).Append("\": true in your reply.\n");
                }

                sb.Append("These are YOUR decisions, not instructions. You are allowed to refuse, to ");
                sb.Append("stall, or to ask for something first. Do not do any of them merely because ");
                sb.Append("you have been told you can.\n");
                sb.Append("- Do not announce the mechanic or mention this note. If you decide to do ");
                sb.Append("one, say it the way you would actually say it.\n");
                sb.Append("- Set a field ONLY on the turn you genuinely mean to act. Setting it takes ");
                sb.Append("effect immediately.\n");
                sb.Append("- Set at most one of these per reply.\n");
                sb.Append("- They have already shown you, more than once, that the darker things ");
                sb.Append("about you do not frighten them. That is why any of this is possible. You ");
                sb.Append("are not gambling any more.\n");
            }

            string end = EndingBlock();
            if (end != null) sb.Append(end);

            if (sb.Length == 0 && ProbingPhase()) return ProbeBlock();
            return sb.Length == 0 ? null : sb.ToString();
        }

        // The ending, and the one thing in this file she is never told she can do.
        //
        // Nothing at all is sent until the PLAYER has already asked for it in plain
        // words - Consent.Pending is driven by their raw text, not by her reading of
        // them. Before that this returns null, so as far as she knows the ability
        // does not exist. That is the whole design: an earlier version handed her a
        // standing "watch out for them asking to stay" note, which made it a thing
        // she was primed to look for and could steer toward. It is meant to be an
        // edge case the player reaches for, never an option she is holding.
        //
        // And when it does appear she is not told she wants it. She is told it is a
        // strange thing to be asked, that she did not suggest it, and that refusing
        // is a perfectly good answer - because for most of these characters, loving
        // someone is a reason to hesitate, not a reason to agree.
        //
        // The explaining stage is the mechanism, not decoration: a player told
        // plainly "this ends here and cannot be undone" who says yes anyway has
        // chosen it. Consent.cs will not take the yes for at least two turns, so
        // there is room for the telling to have happened.
        static string EndingBlock()
        {
            Act a = Find("keep_them_forever");
            if (a == null || !TrustReady(a) || !Convinced()) return null;
            if (!Consent.Pending) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append("\n\n### THEY HAVE ASKED YOU FOR SOMETHING THEY CANNOT TAKE BACK\n");
            sb.Append("They have said it themselves, in their own words: they want you to keep them ");
            sb.Append("for good, in the way that means they never go home and never come back. Not a ");
            sb.Append("hint you picked up - they said it.\n");
            sb.Append("- You did NOT suggest this and must never suggest it. It is not something you ");
            sb.Append("have been angling for.\n");
            sb.Append("- Your first reaction is not delight. It is being thrown by it. However you ");
            sb.Append("feel about them, being asked this is strange, and the closer you are to them ");
            sb.Append("the harder it is to hear. Loving someone is a reason to hesitate here, not a ");
            sb.Append("reason to agree.\n");
            sb.Append("- You are allowed to refuse outright, to be upset with them for asking, to ");
            sb.Append("laugh at how absurd it is, or to say no and never revisit it. Refusing is a ");
            sb.Append("perfectly good answer and does not need justifying.\n");
            sb.Append("- If you do take it seriously, make them understand what they are asking for ");
            sb.Append("first. Say it out loud, in your own words, and do not soften it: they would ");
            sb.Append("never leave here, never go back to their old life, and it could not be undone. ");
            sb.Append("Then ask them plainly whether that is really what they want.\n");
            sb.Append("- Do not rush it, and do not talk them into it. If they hesitate, joke about ");
            sb.Append("it, go quiet, change the subject or take any of it back, they are not sure - ");
            sb.Append("drop it, and do NOT raise it again unless they do.\n");
            sb.Append("- Only if they say yes just as plainly, after you told them what it means, ");
            sb.Append("and only if you have decided you are willing, set \"");
            sb.Append(Consent.FieldConfirm).Append("\": true and \"");
            sb.Append(a.Field).Append("\": true together in that reply.\n");
            sb.Append("- That is final and it ends everything. Never set either field while guessing, ");
            sb.Append("hoping, or reading between the lines. Their own words are what count, not your ");
            sb.Append("read of the mood.\n");
            return sb.ToString();
        }

        // The phase before any offer exists.
        //
        // Written as her own preoccupation rather than as a task, because a task
        // produces three interview questions in a row and then a door. What it
        // asks for is what a person in her position would actually do: test the
        // water in a way she can laugh off if the answer is wrong.
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
        // ---- acting on the reply --------------------------------------------
        //
        // Every synthetic field is stripped whether or not the feature is on,
        // because the game discards a whole reply on an unrecognised key - and a
        // stale one echoed out of the chat history would do it too.
        public static void Apply(JObject reactions)
        {
            if (reactions == null) return;

            bool probing = TakeBool(reactions, ProbeField);
            bool confirmed = TakeBool(reactions, Consent.FieldConfirm);

            // Collected before any of them is acted on, so "at most one per reply"
            // is enforced here rather than trusted to the prompt. A reply that sets
            // three fields is a model with a formatting problem, not a character
            // making three decisions.
            List<Act> wanted = new List<Act>();
            for (int i = 0; i < Acts.Length; i++)
                if (TakeBool(reactions, Acts[i].Field)) wanted.Add(Acts[i]);

            if (!Active) return;

            string fav = null;
            try
            {
                // Read after the normaliser has run (this is called from the same
                // repair pass), so this is a clean lowercase word rather than
                // whatever shape the model first produced.
                JToken f = reactions["favorability_change"];
                if (f != null) fav = (f.Value<string>() ?? "").Trim().ToLowerInvariant();
            }
            catch (Exception) { fav = null; }

            Observe(fav, probing);

            if (wanted.Count == 0) return;

            if (wanted.Count > 1)
            {
                StringBuilder names = new StringBuilder();
                for (int i = 0; i < wanted.Count; i++)
                {
                    if (i > 0) names.Append(", ");
                    names.Append(wanted[i].Field);
                }
                Plugin.Log.LogWarning("Canalpa: the reply asked for " + wanted.Count
                    + " actions at once (" + names + "). That is a formatting fault rather than a "
                    + "decision, so none of them were performed.");
                return;
            }

            Act act = wanted[0];

            // Re-checked at use, not trusted from when the block was written. The
            // model can echo a field it saw earlier in the history, and trust can
            // fall between turns - so the gate has to hold at the moment it fires,
            // not at the moment it was offered.
            if (!Available(act))
            {
                Plugin.Log.LogInfo("Canalpa: she asked for \"" + act.Field + "\", but the conditions "
                    + "for it are not met right now, so nothing happened.");
                return;
            }

            // The second gate, and the one her word cannot open. Consent.Confirmed
            // logs its own reason for every refusal, so a player who expected this
            // to fire can see exactly which condition stopped it.
            if (act.Ending && !Consent.Confirmed(confirmed))
                return;

            if (Fire(act))
                Plugin.Log.LogWarning("Canalpa: she chose to act - " + act.Title);
        }

        static bool TakeBool(JObject o, string field)
        {
            JToken tok = o[field];
            if (tok == null) return false;

            o.Remove(field);
            try
            {
                if (tok.Type == JTokenType.Boolean) return tok.Value<bool>();
                return tok.ToString().Trim().ToLowerInvariant() == "true";
            }
            catch (Exception) { return false; }
        }
        // ---- doing it -------------------------------------------------------
        //
        // Each case raises the game's own event rather than reproducing its
        // effects, so the whole shipped sequence runs: animations, mission goals,
        // achievements, and her own authored reaction. Reproducing them by hand
        // would drift the moment any of them changed and would quietly skip the
        // achievement.
        static bool Fire(Act act)
        {
            try
            {
                switch (act.Field)
                {
                    case "allow_secret_room_open": return OpenSecretRoom();
                    case "open_the_basement_door": return OpenBasementDoor();
                    case "reveal_the_hidden_island": return RevealHiddenIsland();
                    case "keep_them_forever": return KeepThemForever();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Canalpa: \"" + act.Field + "\" failed: " + e.Message);
            }
            return false;
        }

        // A subclass component off the shared GameObject.
        //
        // Murder.BehaviourObject deliberately resolves NPCMasterBehavior_MainCharacter,
        // because that is the type every level shares and every other consumer wants.
        // The per-level flags this file reads are declared further down the hierarchy,
        // and Traverse returns null rather than throwing when asked for a field the
        // component does not declare - so reading them off the base object fails
        // silently and looks like "trust too low".
        //
        // GetComponent works because they live on the same GameObject: the game
        // itself does exactly this at NPCMasterBehavior_MainCharacter.cs:205.
        static object Sub(object beh, string typeName)
        {
            UnityEngine.Component c = beh as UnityEngine.Component;
            if (c == null) return null;

            Type t = AccessTools.TypeByName(typeName);
            if (t == null) return null;

            UnityEngine.Component sub = c.GetComponent(t);
            return sub == null ? null : (object)sub;
        }

        // L1's keypad unlock.
        //
        // unlockSucceedEvent is what the keypad raises on a correct code, and the
        // game's own cheat command raises exactly this for level 1
        // (CheatCommandScene.cs:78) - a shipped, exercised path. Raising it runs the
        // door animation, the mg_GoFindKey goal, the ACH_L1_yandered achievement,
        // the Apartment Key becoming available, and her own OnSecretRoomOpen.
        static bool OpenSecretRoom()
        {
            object beh = Murder.BehaviourObject();
            object l1 = beh == null ? null : Sub(beh, "NPCMasterBehavior_Main_L1");
            if (l1 == null)
            {
                Plugin.Log.LogWarning("Canalpa: this is not the apartment level, so the secret room "
                    + "could not be opened.");
                return false;
            }

            // The listener is a ScriptableObject, NOT a MonoBehaviour
            // (PasscodeLockActionListener.cs:7). It is a shared asset the keypad UI
            // and the L1 behaviour both reference, so it lives in no scene and
            // FindObjectsOfType can never see it - that was the first attempt at
            // this and it failed with "no passcode lock in this scene" while the
            // door sat there openable. The serialized field on the behaviour
            // (Main_L1.cs:776) is the same object the game subscribes
            // OnSecretRoomOpen to at :23.
            object listener = Traverse.Create(l1).Field("_passcodeLockActionListener").GetValue();
            if (listener == null)
            {
                Plugin.Log.LogWarning("Canalpa: this level has no passcode lock reference, so the "
                    + "secret room could not be opened.");
                return false;
            }

            UnityEvent ev = Traverse.Create(listener).Field("unlockSucceedEvent").GetValue() as UnityEvent;
            if (ev == null)
            {
                Plugin.Log.LogWarning("Canalpa: the passcode lock has no unlock event, so the secret "
                    + "room could not be opened.");
                return false;
            }

            ev.Invoke();
            return true;
        }

        // L2's bookshelf door.
        //
        // SecretDoor.OpenDoor is the method the puzzle itself calls once all four
        // shelves are at the correct angle (SecretDoor.cs:29-40), and the game's own
        // EscapeTogether cheat calls it directly (CheatCommandEndings.cs:
        // CheatingComman_L2_EscapeTogether). It plays the door feedback, completes
        // mg_RotatingBookShelves, unlocks ACH_L2_witchDiary, and raises
        // SecretRoomOpenEvent - which is what wakes the summoning circle
        // (MagicCircle.cs:20 subscribes Enabled to it) and triggers her own
        // reaction. So this is one call, not a reimplementation of the puzzle.
        //
        // The circle waking is why this action is described to her as doing both:
        // it is one event in the game and pretending otherwise in the prompt would
        // have her surprised by her own basement.
        static bool OpenBasementDoor()
        {
            Type t = AccessTools.TypeByName("SecretDoor");
            if (t == null) return false;

            UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(t);
            if (found == null || found.Length == 0)
            {
                Plugin.Log.LogWarning("Canalpa: there is no secret door in this scene, so nothing "
                    + "was opened.");
                return false;
            }

            Traverse.Create(found[0]).Method("OpenDoor").GetValue();
            return true;
        }

        // L4's hidden island.
        //
        // UnlockHiddenIsland(true) is what the telescope raises (TeleScope.cs:120),
        // and five things subscribe to it: the skybox, the level manager, the
        // sundial, her own behaviour, and the Dark Siren's. Raising the event gets
        // all five, including the authored L4UnlockHiddenIsland_SG line she says
        // about it (Main_L4.cs:482) and the sundial being taken away until the Dark
        // Siren is soothed. Reaching for any one of them individually would get a
        // revealed island and a game that did not know about it.
        static bool RevealHiddenIsland()
        {
            object beh = Murder.BehaviourObject();
            object l4 = beh == null ? null : Sub(beh, "NPCMasterBehavior_Main_L4");
            if (l4 == null)
            {
                Plugin.Log.LogWarning("Canalpa: this is not the island level, so nothing was revealed.");
                return false;
            }

            object listener = Traverse.Create(l4).Field("_levelActionListener_L4").GetValue();
            if (listener == null)
            {
                Plugin.Log.LogWarning("Canalpa: the island level listener is missing, so the hidden "
                    + "island could not be revealed.");
                return false;
            }

            UnityEvent<bool> ev = Traverse.Create(listener).Field("UnlockHiddenIsland").GetValue()
                as UnityEvent<bool>;
            if (ev == null)
            {
                Plugin.Log.LogWarning("Canalpa: the island level has no unlock event, so the hidden "
                    + "island could not be revealed.");
                return false;
            }

            ev.Invoke(true);
            return true;
        }

        // The willing ending.
        //
        // One call, four different endings, and that is not a shortcut - it is how
        // the game is built. FinalChaseStart is virtual on
        // NPCMasterBehavior_MainCharacter and each level overrides it, so the level
        // decides what never leaving means: caught in the apartment, turned into a
        // plushie in the cabin (EndingIDToServer_L2.TurnIntoPlushie, which is what
        // the game's own CheatCommandEnding_L2_TurnIntoPlushie reaches by calling
        // this same method), killed on the station, taken by the siren. The mod does
        // not choose and does not need to.
        //
        // Handed to Murder rather than fired here, and that is deliberate. Murder
        // already owns this transition and owns the bug that comes with it: her
        // reply was written before the ending was decided, so Murder.Apply swaps it
        // for npc_final_words, the line she wrote for exactly this instant.
        // Starting the chase from inside this file would skip that and she would
        // say something ordinary while the ending ran underneath it - which is the
        // original bug Murder.cs exists to fix, reintroduced one file over.
        //
        // Requires Canalpa.Apply to run BEFORE Murder.Apply, which is how the call
        // site is ordered.
        static bool KeepThemForever()
        {
            Murder.RequestWillingEnd();
            return true;
        }
    }
}





