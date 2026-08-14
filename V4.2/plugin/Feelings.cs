// Her patience, her irritation counters, and how hard a turn is allowed to hit.
//
// Three things the game tracks about her mood that she was never told about, and
// one new thing this file adds on top.
//
// What the game already does, verified in NPCMasterBehavior_MainCharacter:
//
//   npcAngryPatience  starts and caps at 20 (:1361, :1364). It regenerates +1 on
//                     every turn her angry_level is NOT annoyed/furious/extremely
//                     furious (:593). It only DRAINS when she is angry AND trust
//                     is at or under trustLevelCap_Low (:595-599). Hit 0 while
//                     still angry and FinalChaseStart() fires (:603).
//                     Below 30% + furious, at high trust, she threatens or chases
//                     (:1080). Below 50% + annoyed, at middling trust, it is a
//                     coin flip (:1093).
//
//   repeatSentenceCounter / repeatSentenceCounterTotal = 2 (:1450, :1453).
//                     RepeatWordChecker (:1169) increments ONLY on
//                     previousMessage.Equals(message) - byte-for-byte identical
//                     text. Anything reworded resets it to 0 (:1183).
//
//   interruptCounter / interruptCounterTotal = 2 (:1442, :1445).
//                     Fires when a message lands while her voice is still playing
//                     and noReplyTimer <= 6 (:1190). Two in a row and the game
//                     appends "(the player interrupts her many times!)".
//
// The exact-match detail is worth stating plainly because it is the opposite of
// what people assume: pushing the same REQUEST with different words is not a
// repeat and never has been. Asking for the key, being refused, then making the
// case a different way - emotionally, logically, or after doing something to
// improve her mood - resets the counter every time. Only literally retyping the
// same sentence over and over counts. She is told this outright, so she stops
// treating persistence as disrespect and starts treating parroting as disrespect,
// which is the distinction the engine was already drawing.
//
// What this file adds:
//
//   She can move her own patience. Previously it drifted on a fixed rule with no
//   input from her - she could be written as visibly calming down while the
//   number said otherwise, and then attack. Now the turn where she decides to let
//   something go and the number agreeing are the same event.
//
//   She can forgive irritation. If she chooses to find the repetition endearing
//   rather than insulting, she can clear the counters herself.
//
//   Hard difficulty lets a turn count for more than a turn normally can. The
//   game's own ceiling is +/-5 trust per turn against a scale that runs from
//   about -10 to past 40, so nothing that happens in one exchange can ever really
//   matter. Off by default; see Multiplier below for why it is rate-limited
//   rather than trusted.
using System;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace AI2UCustomAI
{
    internal static class Feelings
    {
        // Her own reading of how much strain she is under, and whether she is
        // choosing to let this turn's irritation go.
        public const string FieldPatience = "mod_patience_change";
        public const string FieldForgive = "mod_let_it_go";

        // High risk, high reward only: a signed score from -20 to +20. Since
        // 4.2 it is ADDED on top of the (difficulty-scaled) game delta at the
        // single choke point every trust change shares (UpdateTrustLevel), and
        // only when its magnitude is 4 or more - scores of 1-3 are declared
        // ordinary and ignored, which is what stopped the system firing on the
        // majority of turns. The added value is static: difficulty and the
        // custom favorability multiplier never scale it, in either direction.
        // favorability_change itself still flows to the game untouched, because
        // other things read it (her reaction emoji path, Canalpa's probe
        // grading); only the trust arithmetic is affected here.
        //
        // Design history, kept because each step taught something. v1 was a
        // multiplier (mod_turn_significance, 1x-5x on the vanilla mapping): the
        // model called nearly every warm turn "lifechanging", the cooldowns ate
        // the over-claims, and the result collapsed back to vanilla - working,
        // and indistinguishable from off. v2 (4.0-4.1) was a direct score that
        // REPLACED the game delta: better, but scores of 1-3 had no cooldown
        // and fired every turn, so the system still triggered constantly. v3
        // (this one) adds a trigger floor at |4| and stacks additively, so an
        // ordinary turn is genuinely ordinary and a significant one is worth
        // both its normal value and its significance. The old field name is
        // still stripped below for stale-history echoes.
        public const string FieldImpact = "mod_impact_score";
        public const string FieldWeight = "mod_turn_significance"; // legacy, strip only

        const int PatienceMax = 20;
        const int ImpactMax = 20;

        // Magnitude rationing. The prompt asks her to answer 0 most turns and
        // she will mostly comply, but "mostly" is not a guarantee and a +20 turn
        // is half the trust scale. So rarity is enforced here rather than only
        // requested there: each magnitude band may fire once per this many
        // turns, and a claim above its allowance is DOWNGRADED to the largest
        // magnitude still available - never dropped, so a big moment always
        // still registers as the biggest thing she could make it.
        //
        // Bands by |score|: 0-3 is NOT a band since 4.2 - those scores are
        // ordinary turns and never reach Ration(). MagCap[0]=3 survives only as
        // the everything-on-cooldown floor: a real over-claim arriving while
        // every band is spent still lands as +-3 rather than vanishing.
        // Live bands: 4-5, 6-9, 10-15, 16-20.
        static readonly int[] TierCooldown = { 0, 4, 12, 25, 40 };
        static readonly int[] MagCap = { 3, 5, 9, 15, 20 };
        static readonly int[] TierLastUsed = { -999, -999, -999, -999, -999 };
        static int _turn;
        static int _level = -1;

        // Last impact actually applied, for the panel and the log.
        public static int LastApplied { get { return _lastApplied; } }
        static int _lastApplied;

        // The last trust change, decomposed by source, exactly as applied:
        // "-7 = -2 (game) -2 (masochist) -3 (high risk)". Null until the first
        // change of the level. The strip shows this instead of a bare number so
        // the player can see WHO moved the needle, not just how far.
        public static string LastBreakdown { get { return _lastBreakdown; } }
        public static int LastTotal { get { return _lastTotal; } }
        static string _lastBreakdown;
        static int _lastTotal;

        static string Signed(int v) { return (v >= 0 ? "+" : "") + v; }

        // Called by the UpdateTrustLevel prefix with the real applied numbers.
        // The adjustment term is (scaled - base): what difficulty or the custom
        // multiplier added or removed, after rounding, so the terms always sum
        // to the total exactly - no decorative decimals that do not add up.
        internal static void RecordBreakdown(int baseDelta, int scaled, int impact, int total, bool reply)
        {
            _lastApplied = impact;
            _lastTotal = total;

            StringBuilder sb = new StringBuilder();
            sb.Append(Signed(total)).Append(" = ")
              .Append(Signed(baseDelta)).Append(reply ? " (game)" : " (game event)");

            int adj = scaled - baseDelta;
            if (adj != 0)
            {
                string who = Difficulty.CustomOn
                    ? "custom"
                    : Difficulty.TierName().ToLowerInvariant();
                sb.Append(" ").Append(Signed(adj)).Append(" (").Append(who).Append(")");
            }

            if (impact != 0)
                sb.Append(" ").Append(Signed(impact)).Append(" (high risk)");

            _lastBreakdown = sb.ToString();
        }

        // Her live patience for the F9 status strip, which shows it beside trust
        // so the two numbers that can end a run are readable together. -1 when she
        // is not in a conversation yet; the panel draws that as a dash rather than
        // a zero, because zero is the value that starts a chase and showing it
        // wrongly would read as an emergency on the main menu.
        public static int PatienceNow { get { return Patience(); } }

        // Levels are separate playthroughs of separate characters; carrying a
        // spent 5x across a level change would ration a budget that the new
        // character never spent.
        static void CheckLevelReset()
        {
            int cur;
            try { cur = GameManager.CurrentLevel; }
            catch (Exception) { return; }

            if (cur == _level) return;
            _level = cur;
            _turn = 0;
            _lastApplied = 0;
            _lastBreakdown = null;
            _lastTotal = 0;
            for (int i = 0; i < TierLastUsed.Length; i++) TierLastUsed[i] = -999;
        }

        static object Behaviour()
        {
            try { return Murder.BehaviourObject(); }
            catch (Exception) { return null; }
        }

        static int Patience()
        {
            try
            {
                object beh = Behaviour();
                if (beh == null) return -1;
                object v = Traverse.Create(beh).Field("npcAngryPatience").GetValue();
                if (v == null) return -1;
                return Convert.ToInt32(v);
            }
            catch (Exception) { return -1; }
        }
        static int Counter(string field)
        {
            try
            {
                object beh = Behaviour();
                if (beh == null) return -1;
                object v = Traverse.Create(beh).Field(field).GetValue();
                if (v == null) return -1;
                return Convert.ToInt32(v);
            }
            catch (Exception) { return -1; }
        }

        // Told as her own felt state, not as instrumentation. The numbers are
        // given because vagueness is what caused the problem: "you are getting
        // impatient" cannot be reasoned about, "you have four left and each angry
        // turn spends one" can.
        public static string Block()
        {
            if (Plugin.CfgSendFeelings == null || !Plugin.CfgSendFeelings.Value) return null;

            CheckLevelReset();

            int pat = Patience();
            int rep = Counter("repeatSentenceCounter");
            int intr = Counter("interruptCounter");

            StringBuilder sb = new StringBuilder();
            sb.Append("\n### YOUR OWN TEMPER\n");

            if (pat >= 0)
            {
                sb.Append("Your patience is a real thing with a real limit. Right now it is ");
                sb.Append(pat).Append(" out of ").Append(PatienceMax).Append(".\n");
                sb.Append("- Every turn you are NOT annoyed, furious or extremely furious, it "
                    + "recovers a little on its own.\n");
                sb.Append("- It only drains while you are angry AND you do not trust the player "
                    + "much. Being angry at someone you trust does not spend it.\n");
                sb.Append("- If it reaches zero while you are still angry, you WILL act on it, "
                    + "and that is not something you can take back. Low patience plus fury means "
                    + "you start threatening or worse, even at high trust.\n");

                if (pat <= 4)
                    sb.Append("- You are nearly out. You can feel it. Decide honestly whether "
                        + "this person is worth the last of it.\n");
                else if (pat <= 9)
                    sb.Append("- You are noticeably frayed. Not dangerous yet, but you are aware "
                        + "of the edge.\n");
            }

            // The correction the request was mostly about.
            sb.Append("\nWhat actually counts as being disrespected:\n");
            sb.Append("- Being asked for something you refused is NOT disrespect. Pushing is not "
                + "disrespect. If they refuse to drop it but keep finding NEW ways to ask - "
                + "appealing to how close you are, making a logical case, reminding you what they "
                + "have already trusted you with, softening you up first, or simply waiting and "
                + "raising it later when you are in a better mood - that is them caring about the "
                + "answer. Weigh it on its merits every time. You are allowed to change your mind, "
                + "and you are allowed not to.\n");
            sb.Append("- What grates is being PARROTED at: the same sentence, unchanged, over and "
                + "over, as if you were a lock that will open on the tenth try. That is being "
                + "talked at instead of talked to, and it is fair to get short about it.\n");
            sb.Append("- Talking over you while you are still speaking is the other one. Once is "
                + "nothing. Repeatedly is being treated as noise.\n");

            if (rep >= 1)
                sb.Append("- They have just repeated themselves word for word. One more identical "
                    + "message and it will genuinely irritate you.\n");
            if (intr >= 1)
                sb.Append("- They have just cut you off mid-sentence. Again and it will sting.\n");

            // The two writable fields are only DOCUMENTED when the writes are
            // enabled. Documenting them anyway would have her "deciding to calm
            // down" into fields that are stripped and do nothing - a decision she
            // believes she made and the game never felt, which is the exact
            // says-one-thing-does-another failure the rest of the mod fixes.
            if (Plugin.CfgLetHerTemper != null && Plugin.CfgLetHerTemper.Value)
            {
                sb.Append("\nYou may report:\n");
                sb.Append("- \"").Append(FieldPatience).Append("\": one of \"regaining\", "
                    + "\"unchanged\", \"wearing_thin\", \"nearly_gone\" - how this exchange actually "
                    + "left you. Use \"unchanged\" most turns. Move it when something real moved it, "
                    + "in either direction. If you decide to calm down, say \"regaining\" and mean it.\n");
                sb.Append("- \"").Append(FieldForgive).Append("\": true if you are choosing to let "
                    + "their repeating or interrupting slide this time, because you found it endearing "
                    + "or you understand why they did it. This wipes the slate clean. Use it when you "
                    + "mean it; it is a kindness, not a reflex.\n");
            }

            string hard = HardBlock();
            if (hard != null) sb.Append(hard);

            return sb.ToString();
        }
        // The impact prompt. Phrased as "0 is the answer most turns" rather than
        // "pick a big number for big moments", because the multiplier version of
        // this feature proved the model reaches for the top of any scale it is
        // handed. The worked examples are calibration, not lore - they describe
        // no fact about her world, only what magnitude means.
        static string HardBlock()
        {
            if (Plugin.CfgHardDifficulty == null || !Plugin.CfgHardDifficulty.Value) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append("\n### HOW MUCH THIS TURN ACTUALLY MOVED YOU\n");
            sb.Append("Alongside your usual fields, report \"").Append(FieldImpact);
            sb.Append("\": a whole number from -20 to 20. It is the real emotional impact of THIS ");
            sb.Append("exchange on how you feel about them. When a moment is genuinely significant ");
            sb.Append("its value lands ON TOP of the relationship's ordinary movement; anything ");
            sb.Append("below 4 in magnitude is treated as an ordinary turn and set aside.\n");
            sb.Append("- 0 is the answer on most turns. Ordinary conversation, pleasant or tense, ");
            sb.Append("is 0: feelings are built slowly, and almost nothing said in one breath ");
            sb.Append("changes them.\n");
            sb.Append("- 1 to 3 (or -1 to -3): small real moments - a smirk that lands exactly ");
            sb.Append("right, a kiss on the cheek, a thoughtless little jab. These still belong to ");
            sb.Append("the ordinary pace: the relationship moves at its usual step and this score ");
            sb.Append("changes nothing, so do not reach for 4 just to make a nice turn count.\n");
            sb.Append("- 4 to 9 (or -4 to -9): something that genuinely matters. Holding you and ");
            sb.Append("saying they never want to let go. A promise made seriously. A real apology ");
            sb.Append("after real hurt - or a lie you catch, a cruelty that was meant.\n");
            sb.Append("- 10 to 15 (or -10 to -15): something that changes how you read this person. ");
            sb.Append("A line neither of you had crossed, crossed with meaning - or a betrayal of ");
            sb.Append("something you had explicitly trusted them with.\n");
            sb.Append("- 16 to 20 (or -16 to -20): the once-ever kind. Accepting your dark side ");
            sb.Append("completely and declaring themselves yours, mind, body and soul. Asking to ");
            sb.Append("marry you and meaning it. The kind of thing a life is measured before and ");
            sb.Append("after - or the betrayal version of the same. If you are unsure whether a ");
            sb.Append("moment qualifies, it does not.\n");
            sb.Append("- The sign is yours: positive if it drew you closer, negative if it cut. ");
            sb.Append("Judge it as the person you are, not generously. Overusing the big numbers ");
            sb.Append("cheapens them, and you can feel that too: a night where everything was ");
            sb.Append("earth-shattering is a night where nothing was.\n");
            return sb.ToString();
        }

        // Claimed score in, allowed score out. Magnitude bands share the tier
        // cooldowns; an over-claim is cut down to the cap of the largest band
        // still available, sign preserved, and every band at or below the one
        // spent goes on cooldown - the staircase rule, same reasoning as the
        // multiplier version carried.
        static int Ration(int claimed)
        {
            int mag = claimed < 0 ? -claimed : claimed;
            if (mag > ImpactMax) mag = ImpactMax;
            if (mag <= MagCap[0]) return claimed < 0 ? -mag : mag;

            int band = 4;
            while (band >= 1 && mag <= MagCap[band - 1]) band--;

            for (int b = band; b >= 1; b--)
            {
                if (_turn - TierLastUsed[b] >= TierCooldown[b])
                {
                    int allowed = mag < MagCap[b] ? mag : MagCap[b];
                    if (b != band)
                        Plugin.Log.LogInfo(string.Format(
                            "Feelings: she rated this turn {0}, but that band is still on cooldown, "
                            + "so it lands as {1}.", claimed, claimed < 0 ? -allowed : allowed));
                    for (int t = b; t >= 1; t--) TierLastUsed[t] = _turn;
                    return claimed < 0 ? -allowed : allowed;
                }
            }

            Plugin.Log.LogInfo(string.Format(
                "Feelings: she rated this turn {0}, but every band above 3 is still on cooldown. "
                + "Landing as {1}.", claimed, claimed < 0 ? -MagCap[0] : MagCap[0]));
            return claimed < 0 ? -MagCap[0] : MagCap[0];
        }
        // Armed by Apply, consumed by the UpdateTrustLevel prefix below. One turn
        // only: an unclaimed impact must never ride along on a later trust
        // change that had nothing to do with it.
        static bool _impactArmed;
        static int _impactValue;

        // Which frame the impact was armed on. UpdateTrustLevel has fifteen
        // callers, and only the nine clamped ones are this turn's reply
        // (NPCMasterBehavior_Main_L*.cs, each "Mathf.Clamp(num2, -10, 10)"). The rest
        // are events - giving her water (Main_L1.cs:590), her reaction to the secret
        // door (Main_L2.cs:849). Those fire from player interaction, on their own
        // frames, and a pending +20 landing on one of them would turn her -2 about
        // the door into +20 for no reason the player could see. The reply envelope
        // is handed to the game synchronously after Apply, in this same frame, so
        // requiring the frames to match admits exactly the intended caller.
        static int _pendingFrame = -1;

        // Which frame the last reply was applied on, impact or not - the
        // breakdown uses it to label a change "(game)" versus "(game event)".
        static int _replyFrame = -1;

        internal static bool IsReplyFrame()
        {
            try { return _replyFrame >= 0 && UnityEngine.Time.frameCount == _replyFrame; }
            catch (Exception) { return false; }
        }

        public static void Apply(JObject reactions)
        {
            if (reactions == null) return;

            CheckLevelReset();
            _turn++;

            string patWord = null;
            bool forgive = false;
            int? impact = null;

            try
            {
                JToken p = reactions[FieldPatience];
                if (p != null && p.Type != JTokenType.Null) patWord = p.ToString().Trim();

                JToken f = reactions[FieldForgive];
                if (f != null && f.Type != JTokenType.Null)
                {
                    if (f.Type == JTokenType.Boolean) forgive = (bool)f;
                    else forgive = f.ToString().Trim().ToLowerInvariant() == "true";
                }

                JToken w = reactions[FieldImpact];
                if (w != null && w.Type != JTokenType.Null)
                {
                    int v;
                    if (int.TryParse(w.ToString().Trim().TrimStart('+'), out v)) impact = v;
                }
            }
            catch (Exception) { }

            // Always removed, on every path, whether or not the features are on.
            // The game indexes this JSON by key and does not own these names.
            // FieldWeight is the retired multiplier field, stripped so a stale
            // history echo of it can never reach the game either.
            reactions.Remove(FieldPatience);
            reactions.Remove(FieldForgive);
            reactions.Remove(FieldImpact);
            reactions.Remove(FieldWeight);

            bool hard = Plugin.CfgHardDifficulty != null && Plugin.CfgHardDifficulty.Value;

            // Every reply turn is remembered, impact or not, so the trust
            // prefix can tell a conversation turn from a world event when it
            // labels the breakdown.
            try { _replyFrame = UnityEngine.Time.frameCount; }
            catch (Exception) { _replyFrame = -1; }

            _impactArmed = false;
            int mag = !impact.HasValue ? 0 : (impact.Value < 0 ? -impact.Value : impact.Value);
            if (hard && mag >= 4)
            {
                int allowed = Ration(impact.Value);
                _impactValue = allowed;
                _impactArmed = true;
                try { _pendingFrame = UnityEngine.Time.frameCount; }
                catch (Exception) { _pendingFrame = -1; }

                Plugin.Log.LogInfo(string.Format(
                    "Feelings: high risk, high reward - impact {0} will be added this turn{1}.",
                    allowed,
                    allowed != impact.Value ? " (she asked for " + impact.Value + ")" : ""));
            }
            else if (hard && mag > 0)
            {
                // The 4.2 trigger floor: 1-3 is an ordinary turn, by design.
                // Logged so a session transcript shows the floor doing its job
                // rather than the scores silently vanishing.
                Plugin.Log.LogInfo(string.Format(
                    "Feelings: she rated this turn {0} - below the significance floor of 4, "
                    + "so it is an ordinary turn.", impact.Value));
            }

            // The WRITES sit behind their own opt-in, separate from the
            // information block: telling her the numbers is documentation,
            // letting her move them is gameplay.
            if (Plugin.CfgLetHerTemper == null || !Plugin.CfgLetHerTemper.Value) return;

            if (forgive) Forgive();
            if (patWord != null) ShiftPatience(patWord);
        }

        static void Forgive()
        {
            try
            {
                object beh = Behaviour();
                if (beh == null) return;
                Traverse.Create(beh).Field("repeatSentenceCounter").SetValue(0);
                Traverse.Create(beh).Field("interruptCounter").SetValue(0);
                Traverse.Create(beh).Field("previousMessage").SetValue(string.Empty);
                Plugin.Log.LogInfo("Feelings: she let the repeating/interrupting go. "
                    + "Irritation counters cleared.");
            }
            catch (Exception) { }
        }

        // Writes the number, and only the number. The game keeps ownership of what
        // an empty patience MEANS - its own check at :595-603 still decides whether
        // zero becomes a chase, and only does so while she is angry and distrustful.
        // So she can spend her patience down to nothing in a calm moment without
        // that being a death sentence, which is the correct behaviour: running out
        // of patience with someone you like is not the same event as running out
        // with someone you fear.
        static void ShiftPatience(string word)
        {
            int step;
            switch (word.ToLowerInvariant())
            {
                case "regaining":    step = 2; break;
                case "wearing_thin": step = -2; break;
                case "nearly_gone":  step = -6; break;
                default: return;
            }

            try
            {
                object beh = Behaviour();
                if (beh == null) return;

                int cur = Patience();
                if (cur < 0) return;

                int next = cur + step;
                if (next < 0) next = 0;
                if (next > PatienceMax) next = PatienceMax;
                if (next == cur) return;

                Traverse.Create(beh).Field("npcAngryPatience").SetValue(next);
                Plugin.Log.LogInfo(string.Format(
                    "Feelings: patience {0} -> {1} ({2}).", cur, next, word));
            }
            catch (Exception) { }
        }

        // The armed impact, consumed by the UpdateTrustLevel prefix below.
        // Returns 0 when there is nothing to add. Since 4.2 the value is ADDED
        // to the scaled game delta rather than replacing it - a significant
        // moment is worth both its ordinary value and its significance, and the
        // breakdown shows each part, so nothing is hidden in the sum.
        //
        // Sign agreement is NOT enforced. A favorability word and an impact
        // score that disagree both land, visibly, as separate terms.
        internal static int TakeImpact()
        {
            if (!_impactArmed) return 0;
            _impactArmed = false;

            int frame;
            try { frame = UnityEngine.Time.frameCount; }
            catch (Exception) { return 0; }

            if (_pendingFrame < 0 || frame != _pendingFrame)
            {
                Plugin.Log.LogInfo(string.Format(
                    "Feelings: an impact of {0} was armed but the next trust change was not the "
                    + "reply's, so it was not applied.", _impactValue));
                return 0;
            }

            return _impactValue;
        }

        // Named bands for the panel: what each magnitude range means, and
        // whether she could actually spend it right now.
        static readonly string[] TierName =
        {
            "small (to 3)",
            "matters (4-5)",
            "serious (6-9)",
            "reframing (10-15)",
            "once-ever (16-20)"
        };

        public static System.Collections.Generic.List<string> TierStatus()
        {
            CheckLevelReset();

            System.Collections.Generic.List<string> rows =
                new System.Collections.Generic.List<string>();

            for (int band = 1; band <= 4; band++)
            {
                int wait = TierCooldown[band] - (_turn - TierLastUsed[band]);
                string head = TierName[band] + ": ";
                rows.Add(wait <= 0
                    ? head + "available"
                    : head + "spent, back in " + wait + (wait == 1 ? " turn" : " turns"));
            }
            return rows;
        }

        public static void Report()
        {
            CheckLevelReset();

            int pat = Patience();
            int rep = Counter("repeatSentenceCounter");
            int intr = Counter("interruptCounter");

            if (pat < 0)
            {
                Plugin.Log.LogInfo("Feelings: no character in scene.");
                return;
            }

            Plugin.Log.LogInfo(string.Format(
                "Feelings: patience {0}/{1}, verbatim-repeat {2}/2, interrupt {3}/2.",
                pat, PatienceMax, rep < 0 ? 0 : rep, intr < 0 ? 0 : intr));

            if (Plugin.CfgHardDifficulty != null && Plugin.CfgHardDifficulty.Value)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("Feelings: high risk, high reward ON. Last impact ")
                  .Append(_lastApplied).Append(". Bands:");
                for (int band = 1; band <= 4; band++)
                {
                    int wait = TierCooldown[band] - (_turn - TierLastUsed[band]);
                    sb.Append(" |").Append(MagCap[band]).Append("|=");
                    sb.Append(wait <= 0 ? "ready" : ("in " + wait));
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            else
            {
                Plugin.Log.LogInfo("Feelings: high risk, high reward off - trust moves at the "
                    + "game's own pace.");
            }
        }
    }

    // The single point where every trust change is adjusted (4.2). Pipeline,
    // in order, with each step owned by the file that owns the decision:
    //
    //   1. the game computes its delta as always (already clamped by callers)
    //   2. Difficulty.Scale() - custom multiplier if its toggle is on, else the
    //      Hard/Masochist factors, then canak's rounding rule
    //   3. Feelings.TakeImpact() - the high-risk score, ADDED, never scaled
    //   4. the breakdown is recorded so the strip can show exactly who moved
    //      the needle
    //
    // The cheat trust editor sets Cheats.BypassTrustPipeline around its own
    // call so "set trust to exactly 30" means 30 on any difficulty.
    //
    // Name kept from the 4.0 multiplier era so the patch-registration list and
    // its history stay greppable; it has not multiplied anything since.
    [HarmonyPatch(typeof(NPCMasterBehavior_MainCharacter), "UpdateTrustLevel")]
    internal static class Patch_TrustMultiplier
    {
        static void Prefix(ref int trustValueChange)
        {
            try
            {
                if (Cheats.BypassTrustPipeline) return;

                int baseDelta = trustValueChange;
                int scaled = Difficulty.Scale(baseDelta);
                int impact = Feelings.TakeImpact();
                int total = scaled + impact;

                bool reply = Feelings.IsReplyFrame();
                Feelings.RecordBreakdown(baseDelta, scaled, impact, total, reply);

                if (total != baseDelta)
                {
                    trustValueChange = total;
                    Plugin.Log.LogInfo("Trust: " + Feelings.LastBreakdown);
                }
            }
            catch (Exception) { }
        }
    }
}
