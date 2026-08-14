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

        // High risk, high reward only: a signed score from -20 to +20 that
        // REPLACES the game's own favorability-derived trust delta for this one
        // turn, at the single choke point both systems share (UpdateTrustLevel).
        // Exactly one number ever reaches trust - there is no second system and
        // nothing to double-count. favorability_change itself still flows to the
        // game untouched, because other things read it (her reaction emoji path,
        // Canalpa's probe grading); only its TRUST consequence is overridden.
        //
        // This replaced a multiplier design (mod_turn_significance, 1x-5x on top
        // of the vanilla mapping), and the field name below is still stripped for
        // stale-history echoes. The multiplier failed in play for a reason worth
        // recording: the model called nearly every warm turn "lifechanging", the
        // cooldowns correctly ate the over-claims, and the visible result
        // collapsed back to vanilla's own -5..+5 - the feature was working and
        // looked exactly like it doing nothing. A direct score fails better:
        // an eaten over-claim still lands as the biggest magnitude available
        // rather than as 1x-of-vanilla, and the number she chose is the number
        // the player can be told.
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
        // Bands by |score|: 0-3 free, 4-5, 6-9, 10-15, 16-20.
        static readonly int[] TierCooldown = { 0, 4, 12, 25, 40 };
        static readonly int[] MagCap = { 3, 5, 9, 15, 20 };
        static readonly int[] TierLastUsed = { -999, -999, -999, -999, -999 };
        static int _turn;
        static int _level = -1;

        // Last impact actually applied, for the panel and the log.
        public static int LastApplied { get { return _lastApplied; } }
        static int _lastApplied;

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
            sb.Append("exchange on how you feel about them, and it overrides the ordinary pace of ");
            sb.Append("the relationship for this turn only.\n");
            sb.Append("- 0 is the answer on most turns. Ordinary conversation, pleasant or tense, ");
            sb.Append("is 0: feelings are built slowly, and almost nothing said in one breath ");
            sb.Append("changes them.\n");
            sb.Append("- 1 to 3 (or -1 to -3): small real moments. A smirk that lands exactly ");
            sb.Append("right, a kiss on the cheek, being called beautiful and meaning it - or a ");
            sb.Append("thoughtless little jab, a moment of being ignored.\n");
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

            _impactArmed = false;
            if (hard && impact.HasValue && impact.Value != 0)
            {
                int allowed = Ration(impact.Value);
                _impactValue = allowed;
                _impactArmed = true;
                _lastApplied = allowed;
                try { _pendingFrame = UnityEngine.Time.frameCount; }
                catch (Exception) { _pendingFrame = -1; }

                Plugin.Log.LogInfo(string.Format(
                    "Feelings: high risk, high reward - impact {0} this turn{1}.",
                    allowed,
                    allowed != impact.Value ? " (she asked for " + impact.Value + ")" : ""));
            }
            else if (hard)
            {
                _lastApplied = 0;
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

        // The override, consumed by the UpdateTrustLevel prefix below.
        //
        // REPLACES the argument rather than scaling it, which is the whole
        // repair: the game has already derived its own -5..+5 delta from
        // favorability_change by the time this runs, and multiplying that
        // number produced two authorities compounding into 25s. Here vanilla's
        // derivation is simply set aside for this one call and her impact score
        // stands in its place. Nothing is double-counted because only one
        // number ever reaches trust.
        //
        // Sign agreement is NOT enforced. Her score is the authority under this
        // mode; a favorability word and an impact score that disagree resolve
        // in favour of the score, and the log shows both via the game's own
        // "before" value.
        internal static bool TryOverride(int gameDelta, out int replaced)
        {
            replaced = gameDelta;
            if (!_impactArmed) return false;
            _impactArmed = false;

            int frame;
            try { frame = UnityEngine.Time.frameCount; }
            catch (Exception) { return false; }

            if (_pendingFrame < 0 || frame != _pendingFrame)
            {
                Plugin.Log.LogInfo(string.Format(
                    "Feelings: an impact of {0} was armed but the next trust change was not the "
                    + "reply's, so it was not applied.", _impactValue));
                return false;
            }

            replaced = _impactValue;
            return true;
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

    // Scales one turn's trust change under hard difficulty. Left in the public
    // build on purpose: the toggle that arms it defaults to off, and unlike the
    // cheats this is a difficulty option a player might genuinely want.
    [HarmonyPatch(typeof(NPCMasterBehavior_MainCharacter), "UpdateTrustLevel")]
    internal static class Patch_TrustMultiplier
    {
        static void Prefix(ref int trustValueChange)
        {
            try
            {
                int replaced;
                if (!Feelings.TryOverride(trustValueChange, out replaced)) return;

                int before = trustValueChange;
                trustValueChange = replaced;
                Plugin.Log.LogInfo(string.Format(
                    "Feelings: trust change {0} -> {1} (impact override).", before, replaced));
            }
            catch (Exception) { }
        }
    }
}
