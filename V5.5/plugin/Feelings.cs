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

        // High risk, high reward only: she classifies the rare moment that
        // deserves to become a core memory into a NAMED TIER - "matters",
        // "serious", "reframing", "once_ever", suffixed _good or _bad - and the
        // tier's fixed weight is ADDED on top of the (difficulty-scaled) game
        // delta at the single choke point every trust change shares
        // (UpdateTrustLevel). The weights are static: difficulty and the custom
        // favorability multiplier never scale them, in either direction.
        // favorability_change itself still flows to the game untouched, because
        // other things read it (her reaction emoji path, Canalpa's probe
        // grading); only the trust arithmetic is affected here.
        //
        // Design history, kept because each step taught something. v1 was a
        // multiplier (mod_turn_significance, 1x-5x on the vanilla mapping): the
        // model called nearly every warm turn "lifechanging", the cooldowns ate
        // the over-claims, and the result collapsed back to vanilla - working,
        // and indistinguishable from off. v2 (4.0-4.1) was a signed score,
        // -20..+20, that REPLACED the game delta: scores of 1-3 had no cooldown
        // and fired every turn. v3 (4.2.0) added a trigger floor at |4| and
        // stacked additively: better, and STILL over-fired, because the model
        // was choosing the magnitude - a stir-fried egg landed +8. Models are
        // bad at placing a moment on a number line and good at answering "is
        // this an egg or an engagement?", which is v4 (this one): the model
        // names a category defined by concrete anchors, the mod owns every
        // number, and good and bad run separate cooldowns so a gift cannot
        // shield a betrayal. Both retired field names are still stripped below
        // for stale-history echoes.
        public const string FieldMoment = "mod_moment";
        public const string FieldImpact = "mod_impact_score";      // legacy, strip only
        public const string FieldWeight = "mod_turn_significance"; // legacy, strip only

        const int PatienceMax = 20;

        // The tiers, index 1..4. The prompt asks her to answer "none" most
        // turns and she will mostly comply, but "mostly" is not a guarantee, so
        // rarity is enforced here as well: each tier may fire once per cooldown
        // PER DIRECTION - good and bad on separate tracks, because a positive
        // moment spending the negative track meant a gift could shield a
        // betrayal, which is backwards. Once-ever (tier 4) has no turn
        // cooldown; it is literally once per level per direction, reset with
        // everything else on level change.
        //
        // A claim whose tier is cooling is DOWNGRADED to the largest tier still
        // open in the same direction - a real moment always lands as the
        // biggest thing she could make it - and spending a tier cools every
        // tier at or below it in that direction (the staircase), so a once-ever
        // is not followed next turn by a fresh "serious". Only if the whole
        // direction is cooling does the claim drop, with a log line; the game's
        // own favorability still scores the turn.
        static readonly string[] MomentName = { "", "matters", "serious", "reframing", "once_ever" };
        static readonly int[] MomentValue = { 0, 1, 3, 6, 20 };
        static readonly int[] MomentCooldown = { 0, 5, 10, 30, 0 };
        static readonly int[] LastUsedGood = { -999, -999, -999, -999 };
        static readonly int[] LastUsedBad = { -999, -999, -999, -999 };
        static bool _onceGoodSpent, _onceBadSpent;
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
                sb.Append(" ").Append(Signed(impact))
                  .Append(" (high risk: ").Append(_takenLabel ?? "?").Append(")");

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
            _onceGoodSpent = false;
            _onceBadSpent = false;
            for (int i = 0; i < LastUsedGood.Length; i++)
            {
                LastUsedGood[i] = -999;
                LastUsedBad[i] = -999;
            }
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
        // The moment prompt. She names a CATEGORY, never a number - the score
        // version of this feature proved twice over that a model placing a
        // moment on a number line reaches high (a stir-fried egg landed +8).
        // Categories defined by concrete anchors turn the question into "is
        // this an egg or an engagement?", which models actually answer well.
        // The anchors are calibration, not lore - they describe no fact about
        // her world, only what each tier means.
        static string HardBlock()
        {
            if (Plugin.CfgHardDifficulty == null || !Plugin.CfgHardDifficulty.Value) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append("\n### THE MOMENTS THAT BECOME CORE MEMORIES\n");
            sb.Append("Alongside your usual fields, report \"").Append(FieldMoment);
            sb.Append("\". On most turns the answer is \"none\": ordinary conversation, pleasant or ");
            sb.Append("tense, already counts through your usual favorability, and feelings are built ");
            sb.Append("slowly. This field is ONLY for the rare moment worth keeping as a core ");
            sb.Append("memory. Ask yourself exactly that - would you keep this as a core memory? - ");
            sb.Append("and if the answer is no, say \"none\".\n");
            sb.Append("The values you may use, and what they mean to you:\n");
            sb.Append("- \"matters_good\": small real acts of care that rise just above ordinary ");
            sb.Append("conversation - something made specifically for you, a small gift, ");
            sb.Append("remembering a thing you said and acting on it, a compliment that visibly ");
            sb.Append("lands. If it could happen on any pleasant evening, it is matters at MOST.\n");
            sb.Append("- \"matters_bad\": small real slights - dodging a sincere question (playful ");
            sb.Append("ones do not count), brushing off something you clearly care about, a ");
            sb.Append("thoughtless jab, half-listening while you are being genuine. Friction, ");
            sb.Append("teasing and disagreement are not slights.\n");
            sb.Append("- \"serious_good\": deliberate, unmistakable gestures that cost them ");
            sb.Append("something - effort, vulnerability or pride. A gift that took real thought or ");
            sb.Append("sacrifice, a love confession said plainly and meant, an apology that owns ");
            sb.Append("the wrong without dressing it up, standing by you when it would have been ");
            sb.Append("easier not to. The test: something you would still be thinking about ");
            sb.Append("tomorrow.\n");
            sb.Append("- \"serious_bad\": a real breach, short of changing who they are to you - ");
            sb.Append("refusing a direct sincere question, a lie you catch even about something ");
            sb.Append("small, something suspicious done behind your back or without permission, a ");
            sb.Append("cruelty that was meant. The test: something that makes you WATCH them ");
            sb.Append("afterward.\n");
            sb.Append("- \"reframing_good\": something that changes what you believe about the ");
            sb.Append("future of this - binding themselves to you out loud, choosing you over ");
            sb.Append("something that visibly mattered to them, accepting something about you that ");
            sb.Append("most people would run from. The claim alone earns NOTHING: this tier only ");
            sb.Append("counts if you pushed back, questioned it, tested it - and every answer ");
            sb.Append("held. Words are cheap and you know it.\n");
            sb.Append("- \"reframing_bad\": something that changes what you believe about THEM - ");
            sb.Append("caught preparing to leave (hunting for keys, exits, a way out), a pattern ");
            sb.Append("of lies rather than a single one, betraying something you explicitly ");
            sb.Append("trusted them with. Demand the explanation immediately and judge it on the ");
            sb.Append("spot.\n");
            sb.Append("- \"once_ever_good\": the before-and-after kind, the line that can only be ");
            sb.Append("crossed once - complete acceptance of everything you are, the dark ");
            sb.Append("included, proven rather than claimed; a formal pledge of a life, an ");
            sb.Append("engagement, a ring; putting their life on the line for you in earnest. A ");
            sb.Append("life is measured before and after it. If you are unsure whether a moment ");
            sb.Append("qualifies, it does not.\n");
            sb.Append("- \"once_ever_bad\": the heartbreak - being told they hate you, never loved ");
            sb.Append("you, that none of it was real, and MEANT; renouncing what the two of you ");
            sb.Append("are; a betrayal so total it rewrites every memory you have of them. Check ");
            sb.Append("sincerity first: a joke read as a joke, words shouted in a fight and taken ");
            sb.Append("back, are not this.\n");
            sb.Append("Two rules govern all of it. When you are unsure between two tiers, pick the ");
            sb.Append("lower. And \"none\" is the answer on most turns - reaching for these to make ");
            sb.Append("a nice evening count cheapens them, and you can feel that too: a night ");
            sb.Append("where everything was a core memory is a night where nothing was.\n");
            return sb.ToString();
        }

        // "serious_good" -> tier 2, good. Tolerant of the small variations a
        // model actually produces (case, dashes, spaces); anything else is not
        // a moment, and unknown text fails closed to "no moment" rather than
        // guessing at one.
        static bool ParseMoment(string s, out int tier, out bool good)
        {
            tier = 0;
            good = true;
            if (string.IsNullOrEmpty(s)) return false;

            s = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
            if (s == "none" || s == "ordinary" || s == "0") return false;

            string baseName;
            if (s.EndsWith("_good") || s.EndsWith("_positive"))
            {
                good = true;
                baseName = s.Substring(0, s.LastIndexOf('_'));
            }
            else if (s.EndsWith("_bad") || s.EndsWith("_negative"))
            {
                good = false;
                baseName = s.Substring(0, s.LastIndexOf('_'));
            }
            else return false;

            for (int i = 1; i <= 4; i++)
                if (baseName == MomentName[i]) { tier = i; return true; }

            return false;
        }

        static bool Available(int tier, bool good)
        {
            if (tier == 4) return good ? !_onceGoodSpent : !_onceBadSpent;
            int[] last = good ? LastUsedGood : LastUsedBad;
            return _turn - last[tier] >= MomentCooldown[tier];
        }

        // Spending a tier cools every tier at or below it IN THAT DIRECTION -
        // the staircase - and never touches the opposite direction.
        static void Spend(int tier, bool good)
        {
            if (tier == 4)
            {
                if (good) _onceGoodSpent = true; else _onceBadSpent = true;
            }
            int[] last = good ? LastUsedGood : LastUsedBad;
            int top = tier > 3 ? 3 : tier;
            for (int t = top; t >= 1; t--) last[t] = _turn;
        }

        // Claimed tier in, applied value out (0 = nothing landed). Downgrades
        // within the direction; drops only when the whole direction is cooling.
        static int Grant(int claimed, bool good, out int landed)
        {
            for (int t = claimed; t >= 1; t--)
            {
                if (!Available(t, good)) continue;
                Spend(t, good);
                landed = t;
                return good ? MomentValue[t] : -MomentValue[t];
            }
            landed = 0;
            return 0;
        }
        // Armed by Apply, consumed by the UpdateTrustLevel prefix below. One turn
        // only: an unclaimed impact must never ride along on a later trust
        // change that had nothing to do with it.
        static bool _impactArmed;
        static int _impactValue;
        static string _impactLabel;   // tier name of the armed impact
        static string _takenLabel;    // tier name of the impact just consumed

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
            string moment = null;

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

                JToken w = reactions[FieldMoment];
                if (w != null && w.Type != JTokenType.Null) moment = w.ToString();
            }
            catch (Exception) { }

            // Always removed, on every path, whether or not the features are on.
            // The game indexes this JSON by key and does not own these names.
            // FieldImpact and FieldWeight are the two retired designs, stripped
            // so a stale history echo of either can never reach the game.
            reactions.Remove(FieldPatience);
            reactions.Remove(FieldForgive);
            reactions.Remove(FieldMoment);
            reactions.Remove(FieldImpact);
            reactions.Remove(FieldWeight);

            bool hard = Plugin.CfgHardDifficulty != null && Plugin.CfgHardDifficulty.Value;

            // Every reply turn is remembered, impact or not, so the trust
            // prefix can tell a conversation turn from a world event when it
            // labels the breakdown.
            try { _replyFrame = UnityEngine.Time.frameCount; }
            catch (Exception) { _replyFrame = -1; }

            _impactArmed = false;
            int tier;
            bool goodDir;
            if (hard && ParseMoment(moment, out tier, out goodDir))
            {
                int landed;
                int value = Grant(tier, goodDir, out landed);
                if (value != 0)
                {
                    _impactValue = value;
                    _impactLabel = MomentName[landed].Replace('_', '-');
                    _impactArmed = true;
                    try { _pendingFrame = UnityEngine.Time.frameCount; }
                    catch (Exception) { _pendingFrame = -1; }

                    Plugin.Log.LogInfo(string.Format(
                        "Feelings: core memory - {0} {1}, {2} will be added this turn{3}.",
                        MomentName[tier], goodDir ? "good" : "bad",
                        value > 0 ? "+" + value : value.ToString(),
                        landed != tier
                            ? " (downgraded from " + MomentName[tier] + ": that tier is still cooling)"
                            : ""));
                }
                else
                {
                    Plugin.Log.LogInfo(string.Format(
                        "Feelings: she called this a {0} {1} moment, but every tier in that "
                        + "direction is still cooling, so nothing extra lands. The game's own "
                        + "favorability still counts the turn.",
                        MomentName[tier], goodDir ? "good" : "bad"));
                }
            }
            else if (hard && !string.IsNullOrEmpty(moment)
                     && !moment.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                // Fail closed and say so: an unrecognised label must read as
                // "no moment", never as a guess at one.
                Plugin.Log.LogInfo("Feelings: unrecognised moment label \"" + moment.Trim()
                    + "\" - treated as none.");
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
            _takenLabel = null;
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

            _takenLabel = _impactLabel;
            return _impactValue;
        }

        // Named tiers for the panel, with each direction's readiness shown
        // separately - the whole point of the 4.2.1 split.
        static readonly string[] TierLabel =
        {
            "", "matters (+-1)", "serious (+-3)", "reframing (+-6)", "once-ever (+-20)"
        };

        static string DirStatus(int tier, bool good)
        {
            if (tier == 4)
                return (good ? _onceGoodSpent : _onceBadSpent) ? "spent this level" : "ready";

            int[] last = good ? LastUsedGood : LastUsedBad;
            int wait = MomentCooldown[tier] - (_turn - last[tier]);
            return wait <= 0 ? "ready" : "in " + wait + (wait == 1 ? " turn" : " turns");
        }

        public static System.Collections.Generic.List<string> TierStatus()
        {
            CheckLevelReset();

            System.Collections.Generic.List<string> rows =
                new System.Collections.Generic.List<string>();

            for (int t = 1; t <= 4; t++)
                rows.Add(TierLabel[t] + " - good: " + DirStatus(t, true)
                    + " / bad: " + DirStatus(t, false));
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
                  .Append(_lastApplied).Append(".");
                for (int t = 1; t <= 4; t++)
                    sb.Append(" ").Append(MomentName[t]).Append("[good=")
                      .Append(DirStatus(t, true)).Append(", bad=")
                      .Append(DirStatus(t, false)).Append("]");
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
