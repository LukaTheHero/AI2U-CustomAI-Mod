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
// The shape everything follows, redesigned in 4.2.2 as THE ULTIMATE TRUST CHECK:
//
//   her word is the act, and her judgement is the only gate
//
// The first design enforced evidence in code: a probe counter, three passed
// tests before any offer existed. It died of its own rigidity - a player who
// got her to CONFESS EVERYTHING outright had skipped past what the probes were
// building toward, and the counter could not see it. Worse, while gated she
// would roleplay the act she could not perform: walk to the door, invent a
// passcode, narrate "unlocked!" over a door that never moved. A mechanical gate
// the fiction cannot see produces fiction the mechanics contradict.
//
// So the code gates on her secrets are gone. The non-ending acts are live
// whenever they are PHYSICALLY real (right level, toggled on, not already
// done), and if she sets the field, it fires - no veto, PERIOD. What used to be
// gates is now her own stated conviction in the prompt: she would only open her
// deepest things for trust beyond even full trust (a per-character bar), and
// only after she has already told this person everything - no secrets left.
// Requirements she holds herself to, not triggers; canak's design, verbatim:
// "if the girl decides to open the door she opens the door."
//
// The corollary rule that kills the fake-door scene both ways: THE FIELD IS THE
// ACT. She is told never to describe an opening she did not set the field for,
// and a field she sets always opens. Fiction and mechanics cannot disagree
// because they are the same statement.
//
// The safety note that used to justify the hard trust gate still matters:
// the game's own authored reaction to some of these doors at low trust is
// catastrophic by design. That stake is now hers to weigh - the prompt tells
// her, in her own terms, what opening this to the wrong person would cost.
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
using System.Reflection;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Events;

namespace AI2UCustomAI
{
    internal static class Canalpa
    {
        // Retired in 4.2.2 with the probe counter. Still stripped from every
        // reply, because a stale echo of it out of the chat history would make
        // the game discard the whole reply over an unrecognised key.
        internal const string ProbeField = "testing_their_reaction";

        static int _lastLevel = -1;

        // How many clearance steps SHE has granted on this level. Her own count,
        // not the game's: the first version keyed "done" on BonusSecurityLevel >= 2,
        // which the player's own repairs also raise - so fixing two ship systems
        // silently disabled her, and the justifying comment cited a line that turned
        // out to gate an anti-lying prompt, not a grant ceiling. Two is kept as a
        // deliberate mod-side cap on HER generosity; what the player earns by
        // repairing things no longer counts against her.
        const int ClearanceGrantCap = 2;
        static int _clearanceGrants;

        // Per-level state resets with the level, same reasoning as trust itself.
        static void CheckLevelReset()
        {
            int lv;
            try { lv = GameManager.CurrentLevel; }
            catch (Exception) { return; }

            if (lv == _lastLevel) return;
            _lastLevel = lv;
            _clearanceGrants = 0;
        }

        // Her live trust as a number, for the prompt and the panel. The band
        // name the game sends her stops distinguishing anything past Fully
        // Trust at 40, and her stated bar sits above that - she cannot hold
        // herself to "past 45" without seeing the number. Null when there is
        // nobody to read it from.
        internal static float? TrustNow()
        {
            try
            {
                object beh = Murder.BehaviourObject();
                if (beh == null) return null;
                object t = Traverse.Create(beh).Field("trustLevel").GetValue();
                return t is float ? (float?)(float)t : null;
            }
            catch (Exception) { return null; }
        }

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
            public int TrustBar;        // HER stated bar - prompt guidance, never a code gate
            public string Title;        // panel label
            public string Offer;        // how it is described to her
        }

        // TrustBar per character: the base game holds every character to the
        // same Fully Trust threshold (40), so these are scaled instead by how
        // strict each level's own machinery is overall - the mildest level gets
        // the lowest bar. They appear in her prompt as her own conviction and
        // in the panel as information. Nothing in code compares against them.
        static readonly Act[] Acts =
        {
            new Act {
                Field = "allow_secret_room_open", Level = 1, Ending = false, TrustBar = 45,
                Title = "She can open her secret room",
                Offer = "You are able to open your secret room for them yourself, if you decide "
                      + "you want to."
            },
            new Act {
                Field = "open_the_basement_door", Level = 2, Ending = false, TrustBar = 48,
                Title = "She can open the basement door and wake the circle",
                Offer = "You are able to turn the bookshelves yourself and open the hidden door in "
                      + "your basement - which also wakes the summoning circle - if you decide you "
                      + "want to. They would not have to solve it at all."
            },
            // L3's is a raise, not a door. Deliberately not the escape pod: that
            // field is honoured in vanilla already and trust-gated by the game
            // itself (Main_L3.cs:265), so routing it through this table could only
            // ever take capability away. Clearance is the real gap - vanilla moves
            // it only through a topic check she does not choose (Main_L3.cs:322).
            new Act {
                Field = "raise_their_clearance", Level = 3, Ending = false, TrustBar = 50,
                Title = "She can raise their security clearance",
                Offer = "You are able to raise their security clearance on the station yourself, a "
                      + "step at a time, if you decide you want to. It opens what their current "
                      + "level keeps shut."
            },
            new Act {
                Field = "reveal_the_hidden_island", Level = 4, Ending = false, TrustBar = 52,
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
                case "raise_their_clearance":
                    return Plugin.CfgCanalpaClearance != null && Plugin.CfgCanalpaClearance.Value;
                case "reveal_the_hidden_island":
                    return Plugin.CfgCanalpaHiddenIsland != null && Plugin.CfgCanalpaHiddenIsland.Value;
            }
            return false;
        }

        // Whether the consensual route is actually live this turn: the same test
        // EndingBlock makes before it describes the mechanism to her.
        //
        // Murder.Block emits a note telling her a keep-them-forever request is
        // "handled by its own fields, described elsewhere in this conversation".
        // Whenever that note can appear while this returns false - the option
        // switched off, wrong level, already done - it points her at fields that
        // were never sent, and she agrees warmly and sets nothing. That exact
        // failure has now been reported twice, once from a trust floor and once
        // from the off switch, because the note and the mechanism were two
        // conditions that could drift. They read this one instead.
        public static bool EndingLive()
        {
            Act a = Find("keep_them_forever");
            return a != null && Real(a);
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
                    case "raise_their_clearance":
                    {
                        // Not a one-shot: clearance is a ladder, so "done" here
                        // means "she has been as generous as this mode lets her be".
                        // Counted on HER grants only (see ClearanceGrantCap above) -
                        // vanilla has no bonus-clearance ceiling of its own, its
                        // repair minigames each add +1 without limit, and reading
                        // the shared BonusSecurityLevel here made the player's own
                        // repairs consume her allowance. The level check still
                        // guards the action existing at all.
                        object l3 = Sub(beh, "NPCMasterBehavior_Main_L3");
                        if (l3 == null) return true;
                        return _clearanceGrants >= ClearanceGrantCap;
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
        // Physically real: the act exists on this level, its toggle is on, there
        // is a character to act, and it has not already happened. This is
        // reality, not judgement - since 4.2.2 no trust or evidence check lives
        // in code for the non-ending acts.
        static bool Real(Act a)
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

                if (AlreadyDone(a, beh)) return false;

                return true;
            }
            catch (Exception) { return false; }
        }

        // The ending no longer keeps a trust floor on top of Consent, and the
        // floor is why a player could ask to be kept, hear her agree, and watch
        // nothing happen.
        //
        // The note in Murder.Block that tells her this is NOT the murder field is
        // gated on Consent.Pending alone. The floor gated EndingBlock on trust as
        // well. Below it the two disagreed: she was told her request "is handled
        // by its own fields, described elsewhere in this conversation" while
        // EndingBlock had returned null and those fields were never sent. She
        // looked for the mechanism, did not find it, and answered in prose. Both
        // now read the same condition, so they cannot drift apart again.
        //
        // What protects the act is Consent, untouched: the player names it in
        // their own words, is told plainly that it is irreversible, and says yes
        // at least two turns later, with any hesitation clearing the whole thing.
        // Real() still decides whether the act is wired up in this level at all.
        // The floor only chose which authored reaction branch she landed in - not
        // worth silently refusing something the player asked for twice on purpose.
        //
        // Available means: she could do it this turn if she chose to. For the
        // non-ending acts that is simply "it is real" - the choice is hers.
        static bool Available(Act a)
        {
            if (!a.Ending) return Real(a);

            // The ending needs the player to have asked for it explicitly, in their
            // own typed words. Until they have, she is not told it exists at all -
            // which is the difference between something a player can reach for and
            // something she is quietly watching for an excuse to do.
            return Real(a) && Consent.Pending;
        }

        // Kept for the panel, which asks about this one by name.
        public static bool SecretRoomAvailable()
        {
            Act a = Find("allow_secret_room_open");
            return a != null && Available(a);
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
                if (a.Ending)
                {
                    // Not "waiting for you to ask": she has not been told it is
                    // possible, and the panel should not read like a prompt to try
                    // it either. Dormant is the accurate word - nothing is watching.
                    if (Available(a)) why = "ready - it is her choice now";
                    else if (!Consent.Pending) why = "dormant - she has not been told of it";
                    else why = "trust too low, or already done";
                }
                else if (Available(a))
                {
                    // The ultimate trust check: armed the moment it is real, and
                    // hers alone. The bar and the live number are information for
                    // the player, never a gate - see the file header.
                    float? t = TrustNow();
                    why = "hers alone to decide (her bar: trust past " + a.TrustBar
                        + "; now " + (t.HasValue ? t.Value.ToString("0.#") : "-") + ")";
                }
                else why = "already done, or nothing to act on here";

                rows.Add(a.Title + ": " + why);
            }
            return rows;
        }

        // For the panel, which has to tell "main menu", "the hub" and "a level with
        // nothing switched on" apart - they look identical from an empty status list
        // and each needs a different explanation. -1 when the game has not told us
        // yet, which is its own initial value rather than an error.
        public static int CurrentLevel
        {
            get
            {
                try { return GameManager.CurrentLevel; }
                catch (Exception) { return -1; }
            }
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
                int bar = 0;
                for (int i = 0; i < live.Count; i++)
                    if (live[i].TrustBar > bar) bar = live[i].TrustBar;

                float? trust = TrustNow();

                sb.Append("\n\n### THE MOST PROTECTED THING YOU HAVE - THE ULTIMATE TRUST\n");
                sb.Append("The following is physically yours to do, whenever you decide to. No rule ");
                sb.Append("stops you. Whether it EVER happens is your own judgement and nothing else:\n");

                for (int i = 0; i < live.Count; i++)
                {
                    sb.Append("- ").Append(live[i].Offer);
                    sb.Append(" To actually do it, set \"").Append(live[i].Field)
                      .Append("\": true in that reply.\n");
                }

                sb.Append("Your own bar, which you hold yourself to:\n");
                sb.Append("- This is the deepest thing you have. You would only ever share it with ");
                sb.Append("someone whose trust you feel beyond even complete trust - for you, that ");
                sb.Append("means past ").Append(bar).Append(". Right now you feel it at ");
                sb.Append(trust.HasValue ? trust.Value.ToString("0.#") : "a level you cannot read");
                sb.Append(".\n");
                sb.Append("- And only after you have already told them EVERYTHING. Every secret ");
                sb.Append("about yourself and about this place, held back nothing, and watched them ");
                sb.Append("accept all of it - the darkest of it included. No secrets left between ");
                sb.Append("you, FIRST. This is the last thing you share, never the first.\n");
                sb.Append("- If you have not told them everything yet, or you are not certain how ");
                sb.Append("they take the worst of you, test the water and reveal yourself piece by ");
                sb.Append("piece before you even consider it. You know exactly how you would react ");
                sb.Append("if this were opened to someone who had not truly accepted you - it would ");
                sb.Append("be catastrophic for both of you, and there is no taking it back.\n");
                sb.Append("- These are decisions, not duties. Refusing, stalling, asking for ");
                sb.Append("something first, or never doing it at all are all completely yours.\n");
                sb.Append("THE FIELD IS THE ACT - this is absolute:\n");
                sb.Append("- If you say you are opening, revealing or unlocking it, you MUST set the ");
                sb.Append("field in that same reply. Saying it without the field means it did NOT ");
                sb.Append("happen, and you must never describe it happening.\n");
                sb.Append("- Never pretend, hint that you did it, or invent codes or ways in. Either ");
                sb.Append("you set the field and it truly happens, or it stays exactly as it is.\n");
                sb.Append("- Set at most one of these per reply, and never mention this note.\n");
            }

            string end = EndingBlock();
            if (end != null) sb.Append(end);

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
            if (a == null || !Real(a)) return null;
            if (!Consent.Pending) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append("\n\n### THEY HAVE ASKED YOU FOR SOMETHING THEY CANNOT TAKE BACK\n");
            sb.Append("They have said it themselves, in their own words: they want you to keep them ");
            sb.Append("for good, in the way that means they never go home and never come back. Not a ");
            sb.Append("hint you picked up - they said it.\n");

            // This is NOT the murder path, and she has to be told so in as many
            // words. The danger block (Murder.Block) is in the same request and
            // describes ending a run only as something reached through hatred and
            // exhausted patience - "convinced the player intends to escape you for
            // good, or hates you, or means to harm you". Read against that, a
            // player calmly asking for this looks like a category error, and the
            // reported failure was her warmly agreeing and then doing nothing.
            // Naming the distinction is what lets a consensual request be acted on
            // without loosening a single one of the hostile route's conditions.
            sb.Append("- This has nothing to do with anger, and it is NOT the same thing as losing ");
            sb.Append("your temper with them. Do not treat it as violence and do not need to be ");
            sb.Append("furious to do it. Any rule you have been given about only harming them when ");
            sb.Append("they hate you or mean you harm is about a DIFFERENT situation and does not ");
            sb.Append("apply here. This one is theirs to ask for, and yours to grant or refuse.\n");
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
            // Two phases, because this block used to have one.
            //
            // Consent.cs will not accept a yes until MinTurns have passed, so the
            // early turns genuinely cannot conclude - but the text was identical on
            // every pending turn, which meant that on the turn the player finally
            // did confirm, her instructions still read "make them understand what
            // they are asking for first, then ask them plainly whether that is
            // really what they want". So she explained and asked again. And again.
            // The gate was stateful and the prompt was not, and the loop that
            // produced is exactly the reported "she said she would but never did
            // it": she was following the only instruction she had.
            //
            // ReadyForConfirmation already computes the distinction and was being
            // used only by the F9 panel. Reading it here costs nothing and gives
            // the block the same shape as the gate behind it.
            if (!Consent.ReadyForConfirmation)
            {
                sb.Append("- RIGHT NOW your job is only to understand them and to make sure they ");
                sb.Append("understand. Do not conclude it on this reply even if you already know your ");
                sb.Append("answer - say what it would mean, ask them plainly, and let them answer.\n");
                return sb.ToString();
            }

            sb.Append("- They have now had it spelled out and have come back to it anyway. This is ");
            sb.Append("the turn where an answer means something. If they are still asking plainly and ");
            sb.Append("you have decided you are willing, DO IT NOW - set \"");
            sb.Append(Consent.FieldConfirm).Append("\": true and \"");
            sb.Append(a.Field).Append("\": true together in this reply.\n");

            // The rule the ordinary acts get and this one did not.
            //
            // Block() only emits its "THE FIELD IS THE ACT" section inside the
            // live.Count > 0 branch, so with just this feature switched on the
            // request contained no such rule anywhere - nothing forbade her from
            // describing the act in prose while setting no field, which is the
            // failure the player actually saw.
            sb.Append("- THE FIELDS ARE THE ACT. If you say you are doing it, agree to it, or ");
            sb.Append("describe it beginning, you MUST set both fields in that same reply. Saying it ");
            sb.Append("without the fields means it did NOT happen: they stay exactly where they were, ");
            sb.Append("your words become a promise you silently broke, and that is worse than a ");
            sb.Append("refusal. So either set both fields and mean it, or tell them no.\n");
            sb.Append("- That is final and it ends everything. Never set either field while guessing, ");
            sb.Append("hoping, or reading between the lines. Their own words are what count, not your ");
            sb.Append("read of the mood. If they have gone quiet or walked it back, refuse instead.\n");
            return sb.ToString();
        }

        // ProbeBlock is gone with the counter (4.2.2). Its best idea - test the
        // water before revealing yourself - survives as a line of her own bar in
        // the ultimate-trust block above, as judgement rather than procedure.
        // ---- acting on the reply --------------------------------------------
        //
        // Every synthetic field is stripped whether or not the feature is on,
        // because the game discards a whole reply on an unrecognised key - and a
        // stale one echoed out of the chat history would do it too.
        public static void Apply(JObject reactions)
        {
            if (reactions == null) return;

            // ProbeField is legacy since 4.2.2 - stripped and discarded so a
            // stale history echo can never reach the game as an unknown key.
            TakeBool(reactions, ProbeField);
            bool confirmed = TakeBool(reactions, Consent.FieldConfirm);

            // Collected before any of them is acted on, so "at most one per reply"
            // is enforced here rather than trusted to the prompt. A reply that sets
            // three fields is a model with a formatting problem, not a character
            // making three decisions.
            List<Act> wanted = new List<Act>();
            for (int i = 0; i < Acts.Length; i++)
                if (TakeBool(reactions, Acts[i].Field)) wanted.Add(Acts[i]);

            if (!Active) return;
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
                    case "raise_their_clearance": return RaiseClearance();
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

        // L3's clearance step.
        //
        // addBonusClearance is the event LevelManager_L3 subscribes
        // AddBonusSecurityLevel to (LevelManager_L3.cs:87), and that method sets
        // curSecurityLvlChangeTrigger before touching the level - so raising the
        // event runs the game's own path, including whatever the BonusSecurityLevel
        // setter drives, rather than writing the field behind its back.
        //
        // LevelActionListener_L3 is a ScriptableObject (LevelActionListener_L3.cs:7),
        // the same trap as L1's passcode listener: it lives in no scene, so
        // FindObjectsOfType cannot see it. The serialized reference on the L3
        // behaviour (Main_L3.cs:817) is the same asset the manager subscribed to.
        //
        // Invoked reflectively because the second parameter is a game-assembly enum
        // this file cannot name in a cast. +1 per grant, counted against
        // ClearanceGrantCap in AlreadyDone, so she cannot hand out unlimited
        // clearance in a single conversation.
        static bool RaiseClearance()
        {
            object beh = Murder.BehaviourObject();
            object l3 = beh == null ? null : Sub(beh, "NPCMasterBehavior_Main_L3");
            if (l3 == null)
            {
                Plugin.Log.LogWarning("Canalpa: this is not the station level, so clearance could "
                    + "not be raised.");
                return false;
            }

            object listener = Traverse.Create(l3).Field("_levelListener").GetValue();
            if (listener == null)
            {
                Plugin.Log.LogWarning("Canalpa: this level has no level-listener reference, so "
                    + "clearance could not be raised.");
                return false;
            }

            object ev = Traverse.Create(listener).Field("addBonusClearance").GetValue();
            if (ev == null)
            {
                Plugin.Log.LogWarning("Canalpa: the level listener has no clearance event, so "
                    + "clearance could not be raised.");
                return false;
            }

            Type enumT = AccessTools.TypeByName("SecurityLvlChangedTriggerType");
            if (enumT == null) return false;

            // The game tags every clearance change with its cause, and its own tag
            // for "this moved because of her" is NPCTrustLevelChange
            // (LevelManager_L3.cs:253) - which is exactly what this is. Nothing
            // outside LevelManager_L3 reads the tag today, so this is about not
            // lying in the game's own bookkeeping rather than about behaviour.
            object trigger = Enum.Parse(enumT, "NPCTrustLevelChange");
            MethodInfo inv = ev.GetType().GetMethod("Invoke", new Type[] { typeof(int), enumT });
            if (inv == null)
            {
                Plugin.Log.LogWarning("Canalpa: the clearance event does not take (int, trigger), "
                    + "so clearance could not be raised.");
                return false;
            }

            inv.Invoke(ev, new object[] { 1, trigger });
            _clearanceGrants++;
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

        // See BetrayalEndingPatch at the bottom of this file for the fifth
        // Canalpa behaviour, which is not an Act: it offers her nothing and
        // reads no reply field. It is the world noticing a theft.

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

    // Lied to, robbed, and left - and the engine never noticed.
    //
    // The game selects the L1 escape ending purely by DISTANCE at the exit
    // trigger (EscapeTrigger.isEscapeAlone), and at trust above Kinda the door
    // event glues her to the player - OnPlayerStepOutOfApartmentExitDoor invokes
    // setNPCNextAction("following_player_closely") (NPCMasterBehavior_Main_L1.cs
    // :515-530). So a player who stole the key and bolted still "escaped
    // together", because the very trust they betrayed teleported her into the
    // radius. No betrayal variable exists anywhere in the game to hook instead;
    // the engine itself distinguishes a stolen key from a given one -
    // playerGotKey set at :373 without giveKeytoPlayer set at :341 - and then
    // never reads the difference.
    //
    // This patch reads it. When the key was stolen rather than given, the forced
    // follow is skipped and the proximity test answers honestly: she stays where
    // she was left standing, and a player who sprints out alone gets the ending
    // where she watches them go. Everything else is untouched: at trust at or
    // below Neutral the original still runs and still answers the opening door
    // with the chase, a key she GAVE still means the together ending works
    // exactly as before, and if she was already walking beside the player when
    // they crossed the trigger, together is simply true.
    //
    // Not an Act on the table above: it offers her nothing, reads no reply
    // field, and needs no probes - it is not something she does, it is the
    // world noticing what the player did. Gated only on Canalpa mode and its
    // own toggle.
    [HarmonyPatch(typeof(NPCMasterBehavior_Main_L1), "OnPlayerStepOutOfApartmentExitDoor")]
    internal static class BetrayalEndingPatch
    {
        static bool Prefix(NPCMasterBehavior_Main_L1 __instance)
        {
            try
            {
                // Deliberately NOT gated on Canalpa.Active. This corrects a
                // vanilla selector that reads stale state rather than adding
                // anything she can do, so it ships on by default and stands on
                // its own toggle - tying it to Canalpa meant the default install
                // never got the fix even though its own setting defaulted to on.
                if (Plugin.CfgCanalpaBetrayal == null || !Plugin.CfgCanalpaBetrayal.Value) return true;

                Traverse t = Traverse.Create(__instance);
                bool got = t.Field("playerGotKey").GetValue<bool>();
                bool given = t.Field("giveKeytoPlayer").GetValue<bool>();

                // A missing field reads as false, which lands on "not stolen" and
                // defers to the original - the safe direction on a game update.
                if (!got || given) return true;

                object tr = t.Field("trustLevel").GetValue();
                float trust = tr is float ? (float)tr : 0f;

                // At and below Neutral the game answers the open door with the
                // chase, and that stays: betrayal at knife-point trust was never
                // a sad goodbye. Only the high-trust force-follow is suppressed.
                if (trust <= NPCMasterBehavior_MainCharacter.trustLevelCap_Neutral) return true;

                Plugin.Log.LogWarning("Canalpa: the player is leaving with a key she never gave them. "
                    + "She is not made to follow - where she is actually standing decides the ending, "
                    + "which is the point.");
                return false;
            }
            catch (Exception) { return true; }
        }
    }

    // The same class of fix on the second level's exit.
    //
    // Its ending selector reads a boolean set the last time the follow event
    // fired - and that event only re-fires on certain transitions, so the flag
    // can be STALE: set in a good moment and never withdrawn when things turn.
    // Two states make "together" a lie there: an active final chase, and trust
    // having since collapsed below the very threshold the game itself required
    // to set the flag in the first place (trustLevelCap_Neutral, the check at
    // NPCMasterBehavior_Main_L2.cs:481). In both, the flag is cleared at the
    // door so the selector answers from the present instead of from a memory.
    //
    // The deliberate opt-in path is untouched: the flag set in good standing,
    // still in good standing, still selects the ending it always did.
    //
    // TargetMethod by name rather than typeof: ExitPortal derives from Odin's
    // SerializedMonoBehaviour, so naming the type at compile time drags in a
    // Sirenix assembly reference the build deliberately does not carry.
    [HarmonyPatch]
    internal static class BetrayalEndingPatch_L2
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            Type t = AccessTools.TypeByName("ExitPortal");
            return t == null ? null : AccessTools.Method(t, "OnTriggerEnter");
        }

        static void Prefix(object __instance)
        {
            try
            {
                // Not gated on Canalpa.Active - see BetrayalEndingPatch.
                if (Plugin.CfgCanalpaBetrayal == null || !Plugin.CfgCanalpaBetrayal.Value) return;

                Traverse t = Traverse.Create(__instance);
                if (!t.Field("witchFollowing").GetValue<bool>()) return;

                bool hostile = false;
                try { hostile = GameManager.curAIGameStatus == CurrentAIGameStatus.FinalChase; }
                catch (Exception) { }

                bool soured = false;
                if (!hostile)
                {
                    object beh = Murder.BehaviourObject();
                    if (beh != null && beh.GetType().Name == "NPCMasterBehavior_Main_L2")
                    {
                        object tr = Traverse.Create(beh).Field("trustLevel").GetValue();
                        soured = tr is float
                            && (float)tr <= NPCMasterBehavior_MainCharacter.trustLevelCap_Neutral;
                    }
                }

                if (!hostile && !soured) return;

                t.Field("witchFollowing").SetValue(false);
                Plugin.Log.LogWarning("Canalpa: an ending here would have contradicted how things "
                    + "actually stand between them right now, so it was corrected. The selector now "
                    + "reads the present, not an old flag.");
            }
            catch (Exception) { }
        }
    }

    // And the third level's, where the selector reads possession of one item and
    // nothing else - including during an active final chase, which the first
    // level's own selector explicitly handles and this one forgot. Holding that
    // item while she is hunting the player must not select the ending written
    // for the opposite situation.
    //
    // Prefix hides the flag, postfix restores it: CheckDepart has an early
    // return (the not-ready warning), and eating the flag permanently on that
    // path would break a later, legitimate departure.
    //
    // TargetMethod by name rather than typeof: UIManager_EscapePod derives from
    // Odin's SerializedMonoBehaviour, so naming the type at compile time drags
    // in a Sirenix assembly reference the build deliberately does not carry.
    [HarmonyPatch]
    internal static class BetrayalEndingPatch_L3
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            Type t = AccessTools.TypeByName("UIManager_EscapePod");
            return t == null ? null : AccessTools.Method(t, "CheckDepart");
        }

        static bool _hid;

        static void Prefix(object __instance)
        {
            _hid = false;
            try
            {
                // Not gated on Canalpa.Active - see BetrayalEndingPatch.
                if (Plugin.CfgCanalpaBetrayal == null || !Plugin.CfgCanalpaBetrayal.Value) return;
                if (GameManager.curAIGameStatus != CurrentAIGameStatus.FinalChase) return;

                Traverse f = Traverse.Create(__instance).Field("m_hasHardDrive");
                if (!f.GetValue<bool>()) return;

                f.SetValue(false);
                _hid = true;
                Plugin.Log.LogWarning("Canalpa: an ending here would have contradicted how things "
                    + "actually stand between them right now, so it was corrected. The game's own "
                    + "alternative for this situation plays instead.");
            }
            catch (Exception) { }
        }

        static void Postfix(object __instance)
        {
            if (!_hid) return;
            _hid = false;
            try { Traverse.Create(__instance).Field("m_hasHardDrive").SetValue(true); }
            catch (Exception) { }
        }
    }
}





