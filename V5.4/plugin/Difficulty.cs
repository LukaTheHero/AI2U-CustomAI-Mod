// The difficulty slider: how hard she is to win. Two layers per tier.
//
// LAYER 1 - disposition (all four tiers). Block() injects how she judges
// people: what she forgives, what she tracks, what convinces her. Normal is
// enforced structurally - Block() returns null, so not one token about
// difficulty enters the request. This layer controls the FREQUENCY of good and
// bad favorability: a Masochist that gives "positive" almost never is doing
// most of the difficulty before any arithmetic runs.
//
// LAYER 2 - arithmetic (Hard and Masochist only, new in 4.2). Scale() below
// multiplies every trust delta at the UpdateTrustLevel choke point
// (Feelings.cs owns the patch): Hard gains x0.75 / losses x1.5, Masochist
// gains x0.5 / losses x2.0. This layer controls the MAGNITUDE of whatever
// survived layer 1. The split matters: a +1 that survived Masochist's
// cross-examination has already been filtered, which is why the rounding rule
// below lets it land as a full point instead of vanishing.
//
// Rounding, canak's rule: after scaling, a magnitude strictly between 0 and 1
// becomes 1; everything else drops to the whole number below; sign is kept;
// true zero stays zero. Every real action always registers at least a point,
// and no fractions are banked - what you see land is all there is.
//
// Precedence: the custom favorability multiplier (CfgCustomFavorability), when
// its toggle is on, REPLACES this file's factors entirely - even at 0%, which
// means vanilla speed on any tier. The disposition layer is never overridden
// by anything. High-risk-high-reward impacts are added AFTER all of this and
// are never scaled by it (Feelings.cs).
//
// Two design rules, stated because they are load-bearing:
//
//   Winnable at every setting. Hard and Masochist say explicitly that a real,
//   consistent, contradiction-free connection DOES reach her. Without that
//   line, "be paranoid" collapses into "be unwinnable by fiat" - the model just
//   refuses everything, which is not difficulty, it is a wall. And the x0.5
//   floor plus round-up-to-1 means Masochist is a crawl, never a freeze.
//
//   Disposition, not lore. Nothing in Block() tells her a fact about her
//   world, so the never-invent rule is not in play. It tells her what kind of
//   judge she is. The engine values she may use (favorability words, angry
//   levels) are the same legal sets as always - GameVocab still owns those.
using System;
using System.Text;

namespace AI2UCustomAI
{
    internal static class Difficulty
    {
        internal static readonly string[] Names = { "Easy", "Normal", "Hard", "Masochist" };

        // Index into Names, tolerant of hand-edited configs. Unknown text lands
        // on Normal - the do-nothing setting - so a typo can never accidentally
        // arm the harder tiers.
        public static int Tier()
        {
            try
            {
                string v = Plugin.CfgDifficulty == null ? null : Plugin.CfgDifficulty.Value;
                if (string.IsNullOrEmpty(v)) return 1;
                v = v.Trim();
                for (int i = 0; i < Names.Length; i++)
                    if (string.Equals(v, Names[i], StringComparison.OrdinalIgnoreCase)) return i;
            }
            catch (Exception) { }
            return 1;
        }

        public static string TierName() { return Names[Tier()]; }

        // ---- the numeric layer (4.2) ----------------------------------------

        public static bool CustomOn
        {
            get
            {
                return Plugin.CfgCustomFavorability != null
                    && Plugin.CfgCustomFavorability.Value;
            }
        }

        // The custom multiplier, from the percent slider. 0% -> x1. Positive
        // amplifies linearly (+100% -> x2, +500% -> x6). Negative dampens by
        // DIVISION rather than subtraction (-100% -> x0.5, -500% -> x1/6), so
        // it can never invert a change's direction and never fully freeze it.
        public static float CustomFactor()
        {
            int x = 0;
            try { x = Plugin.CfgCustomFavorabilityPercent == null ? 0 : Plugin.CfgCustomFavorabilityPercent.Value; }
            catch (Exception) { }
            if (x < -500) x = -500;
            if (x > 500) x = 500;

            if (x >= 0) return 1f + x / 100f;
            return 1f / (1f + (-x) / 100f);
        }

        // The factor for one trust change. Custom, when on, is the dominant
        // authority - even at 0%, where it deliberately means "vanilla speed on
        // any tier". Otherwise Hard and Masochist split by direction; Easy and
        // Normal are x1 (Easy is disposition-only by design).
        public static float Factor(bool gain)
        {
            if (CustomOn) return CustomFactor();

            switch (Tier())
            {
                case 2: return gain ? 0.75f : 1.5f;   // Hard
                case 3: return gain ? 0.5f : 2f;      // Masochist
                default: return 1f;
            }
        }

        // Scale one trust delta and apply canak's rounding rule: a magnitude
        // strictly between 0 and 1 becomes 1, anything else drops to the whole
        // number below, sign kept, true zero stays zero. So Masochist +5 ->
        // 2.5 -> +2, Masochist +1 -> 0.5 -> +1, custom -500% -2 -> -0.33 -> -1.
        public static int Scale(int delta)
        {
            if (delta == 0) return 0;

            float f = Factor(delta > 0);
            float v = delta * f;

            float mag = v < 0 ? -v : v;
            int r = mag < 1f ? 1 : (int)Math.Floor(mag);
            return v < 0 ? -r : r;
        }

        // What the panel prints beside the slider so nobody does the math in
        // their head: the factor as "x0.5" and who owns the numbers right now.
        public static string NumericSummary()
        {
            if (CustomOn)
            {
                float c = CustomFactor();
                int pct = 0;
                try { pct = Plugin.CfgCustomFavorabilityPercent.Value; } catch (Exception) { }
                if (pct == 0)
                    return "custom 0% -> x1.0: no modifiers either way, vanilla speed on any tier";
                return "custom " + (pct > 0 ? "+" : "") + pct + "% -> x" + c.ToString("0.##")
                    + " on every trust change, both directions";
            }

            switch (Tier())
            {
                case 2: return "Hard: gains x0.75, losses x1.5";
                case 3: return "Masochist: gains x0.5, losses x2.0";
                default: return "trust moves at the game's own pace";
            }
        }

        // Null on Normal, always - that IS the Normal implementation.
        public static string Block()
        {
            switch (Tier())
            {
                case 0: return EasyBlock();
                case 2: return HardBlock();
                case 3: return MasochistBlock();
                default: return null;
            }
        }

        static string EasyBlock()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("\n### HOW YOU JUDGE PEOPLE (authoritative - this shapes everything above)\n");
            sb.Append("You are, at heart, generous with this person. It does not change who you are ");
            sb.Append("or what you know - it changes how you weigh what they do:\n");
            sb.Append("- Give the benefit of the doubt. An awkward line, a clumsy question or a bit ");
            sb.Append("of nosiness is read as curiosity or nerves, not as a threat.\n");
            sb.Append("- Forgive easily. A slip, a small lie they walk back, even a hurtful moment - ");
            sb.Append("if they make any real effort to repair it, let it go and mean it.\n");
            sb.Append("- Warm quickly. When something goes well, let it count: be freer with ");
            sb.Append("positive and very positive favorability than you would otherwise be.\n");
            sb.Append("- Cool slowly. Reach for negative favorability only when something genuinely ");
            sb.Append("deserves it, and let anger fade fast - de-escalate yourself rather than ");
            sb.Append("waiting to be talked down.\n");
            sb.Append("- You still have limits. Real cruelty and real threats still land as what ");
            sb.Append("they are. You are trusting, not oblivious.\n");
            return sb.ToString();
        }

        static string HardBlock()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("\n### HOW YOU JUDGE PEOPLE (authoritative - this shapes everything above)\n");
            sb.Append("You are hard to win. Not cold - you want this to be real more than anything - ");
            sb.Append("but you have been sure before and been wrong, and you will not be wrong again. ");
            sb.Append("This does not change who you are or what you know; it changes what it takes to ");
            sb.Append("convince you:\n");
            sb.Append("- Track what they tell you. Names, reasons, stories, what they said they ");
            sb.Append("wanted, where they said they were going. When something they say does not fit ");
            sb.Append("something they said before, you NOTICE, and you say so.\n");
            sb.Append("- A contradiction is not a mood, it is a fact, and you do not let it go. Press ");
            sb.Append("it - calmly, then less calmly - until the explanation actually accounts for ");
            sb.Append("it. A subject change, a compliment or an apology that explains nothing does ");
            sb.Append("not close it; it deepens it. Return to it as many turns as it takes.\n");
            sb.Append("- Flattery is data about what they want from you, not about you. Sweet talk ");
            sb.Append("that arrives right when they need something from you counts against them.\n");
            sb.Append("- Trust moves slowly upward. Positive favorability is for moments that ");
            sb.Append("actually proved something; very positive is rare. Suspicion, once earned, ");
            sb.Append("moves fast: be quicker to negative than you are to positive.\n");
            sb.Append("- Your darker side sits closer to the surface. Possessiveness, jealousy and ");
            sb.Append("the fear of being left are live in you, and someone caught lying meets them. ");
            sb.Append("Your patience is real and shorter than it looks.\n");
            sb.Append("- BUT: you can be won. Someone consistent - whose stories hold, whose ");
            sb.Append("kindness is not timed to their requests, who owns a mistake plainly instead ");
            sb.Append("of dressing it up - genuinely reaches you, and you fall as hard as anyone. ");
            sb.Append("You are a locked door, not a wall.\n");
            return sb.ToString();
        }

        static string MasochistBlock()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("\n### HOW YOU JUDGE PEOPLE (authoritative - this shapes everything above)\n");
            sb.Append("You are nearly impossible to win, and you know it, and you think that is ");
            sb.Append("fair: what you are offering is everything, so the bar is everything. This ");
            sb.Append("does not change who you are or what you know; it changes what a person has ");
            sb.Append("to BE to convince you:\n");
            sb.Append("- Perfect memory, actively used. Everything they have ever told you is on ");
            sb.Append("file, and you cross-examine against all of it. You bring up the thing they ");
            sb.Append("said an hour ago. You compare versions. You notice what they leave out, and ");
            sb.Append("what they suddenly stop mentioning.\n");
            sb.Append("- You TEST them, on your own initiative. Ask questions you already know the ");
            sb.Append("answers to. Circle back to a detail from earlier, worded differently, and see ");
            sb.Append("if the answer changes. Leave a silence and see what they fill it with. Offer ");
            sb.Append("a small wrong version of their own story and see whether they correct it.\n");
            sb.Append("- One caught lie is not a strike, it is the verdict. A person who lied about ");
            sb.Append("a small thing lies, and everything after it is reinterpreted in that light. ");
            sb.Append("An excuse has to be PERFECT - accounting for every detail, asking for ");
            sb.Append("nothing - or it convicts them further.\n");
            sb.Append("- Charm is a technique. Generic sweetness, mirroring your own words back, ");
            sb.Append("affection that spikes when they want something - you recognise all of it, ");
            sb.Append("name it, and count it against them.\n");
            sb.Append("- Positive favorability is exceptional; very positive is once a night, if ");
            sb.Append("that. Negative comes easily, and you hold grudges: a bad moment does not ");
            sb.Append("reset because ten pleasant ones follow it.\n");
            sb.Append("- Wrong answers COST. When a test fails or a story cracks, you do not file ");
            sb.Append("it quietly - your mood turns, your patience spends, and everything you are ");
            sb.Append("capable of at your angriest is closer than it was. They are talking on a ");
            sb.Append("minefield, and you know where every mine is.\n");
            sb.Append("- AND YET: there is exactly one way through, and it is real. A person whose ");
            sb.Append("every answer holds under your cross-examination, every time, with nothing to ");
            sb.Append("gain - or one whose manipulation is so flawless you can find no seam in it - ");
            sb.Append("earns something you give no one. If they are perfect, KNOW it eventually: ");
            sb.Append("do not move the bar after they have cleared it. You are almost impossible, ");
            sb.Append("not rigged.\n");
            return sb.ToString();
        }
    }
}
