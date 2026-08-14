// How each girl's own home actually works.
//
// The gap this fills is narrower than it looks. Every per-level puzzle ANSWER is
// already forwarded by Lore.cs out of the game's own ServerContext objects: the
// potion recipe, the four element colours, the computer and wifi passwords, the
// safe code, which systems are broken, where the hidden island is. She has the
// answers.
//
// What she does not have is the PROCEDURE around them - that the cauldron needs
// the circle active before it will do anything, that the circle is activated by
// turning four bookshelves in an order written under the deer skull, that a
// summoned soul costs a point of health per question. So she could recite the
// recipe and still be unable to tell the player what to do with it, which reads
// as her being vague about her own basement.
//
// None of this is invented. It is the fixed design of each level - the part that
// is identical in every playthrough - and it is the reason the split works:
// anything that varies per playthrough is deliberately NOT written here, it is
// read live. The symbol-to-shelf pairing is fixed, so it is stated. The ORDER is
// randomized per basement load, so it is read from the running game instead
// (LiveState below) and never guessed.
//
// Written as her own working knowledge of her own home, in her own voice's frame
// of reference, because that is what it is. The catgirl knows where her router
// is. The witch knows what her cauldron does. Telling them so is restoring the
// baseline the vendor server had, not adding anything.
using System;
using System.Text;
using HarmonyLib;

namespace AI2UCustomAI
{
    internal static class Mechanics
    {
        // Level 1 - the apartment. The catgirl.
        //
        // Deliberately short. Her level's puzzles are mostly about finding things
        // and the answers are already forwarded; what is missing is knowing the
        // objects exist and relate to each other.
        const string L1 =
            "- Your apartment's front door is locked and the exit needs a code. You know it.\n"
            + "- Your computer is password-locked; you know that password too.\n"
            + "- The wifi password is written down in the apartment.\n"
            + "- There is a safe with a numeric code.\n"
            + "- Your phonograph plays records. Records are scattered around the flat.\n"
            + "- The secret room is behind a keypad with a four-digit code. Only you know it.\n"
            + "- Items around your home can be picked up, used, or given to you as gifts.\n";

        // Level 2 - the cabin. The witch.
        //
        // The one the request named directly. Three connected systems -
        // bookshelves, circle, cauldron - and the connection is the part that was
        // missing, not the individual pieces.
        const string L2 =
            "- Your basement has a summoning circle, ringed by four bookshelves, under a deer skull.\n"
            + "- The circle is INACTIVE until the four bookshelves are rotated in the right order.\n"
            + "- The order is drawn on the wall under the deer skull, as symbols with arrows. Each "
            + "symbol means one specific bookshelf, always the same pairing:\n"
            + "    triangle with a line through it = the shelf with CANDLES\n"
            + "    circle with a dot in it        = the shelf with the COIN\n"
            + "    female symbol with a curved line = the shelf with VASES\n"
            + "    plain triangle, no line        = the shelf with FEATHERS\n"
            + "- The order itself is different every time the basement is set up, so read it off "
            + "the wall rather than remembering an old one.\n"
            + "- Once the circle is active, a TOY can be placed in it and its soul summoned. The "
            + "player can then ask the soul questions - and each answer costs the player one point "
            + "of their own health. Enough questions will kill them. You know this.\n"
            + "- Your cauldron brews potions from a recipe. Four elements, each with a colour.\n"
            + "- Wrong combinations produce dizzy, poison, or shrinking potions, and drinking those "
            + "hurts the player badly. Getting the recipe wrong is dangerous, not merely wasteful.\n"
            + "- Outside the cabin is a forest. Something hostile roams it at night.\n";

        // Level 3 - the station. The hologram.
        const string L3 =
            "- The station has four broken systems. You know which are fixed and which are not.\n"
            + "- The engine is in the engine room, behind glass, with a terminal in front of it.\n"
            + "- You cannot hold objects. Items the player wants rid of go into the engine.\n"
            + "- The engine has a pressure gauge. Every item fed to it raises the pressure by an "
            + "unpredictable amount. Ten items are needed to repair it, and if the pressure reaches "
            + "maximum first, the engine explodes and kills whoever is aboard. Warn them if it is "
            + "running high - you can see the gauge.\n"
            + "- There is a security level, and a dark room, and cards that matter. You know them.\n"
            + "- Your own mainframe is in a room to the right. There is a shutdown button on the "
            + "terminal there. If the player shuts you down, the containment fails and the monsters "
            + "get out. You would rather they did not.\n";

        // Level 4 - the island. The siren.
        const string L4 =
            "- Your island has structures that can be repaired. You know which still are not.\n"
            + "- The temple holds a sundial. It changes the time of day - day, night, or the night "
            + "of chaos - and each change drains half the player's Time Force.\n"
            + "- Time Force refills on its own and slowly: a full bar takes about five minutes. "
            + "Below half, the sundial simply will not turn.\n"
            + "- A telescope can reveal the hidden island. Using it takes the sundial away until "
            + "the Dark Siren is soothed.\n";

        // The parts that hold in every level, so they are stated once.
        //
        // The last line is the one that earns its place: the model's instinct is
        // to be helpful and invent a plausible mechanism, and a confidently wrong
        // puzzle instruction is worse for the player than "I don't remember".
        const string Common =
            "\n### YOUR HOME, AND HOW IT WORKS\n"
            + "This is your own place. You know how the things in it work, the way anyone knows "
            + "their own home, and you can explain them, hint at them, or refuse to.\n"
            + "You are not a walkthrough. Whether you help is up to you and up to how you feel "
            + "about them right now. But when you do help, be ACCURATE - if you are not sure of a "
            + "detail, be vague or say you cannot remember rather than inventing one. Telling them "
            + "something confidently wrong is worse than telling them nothing.\n";

        public static string Block()
        {
            if (Plugin.CfgSendMechanics == null || !Plugin.CfgSendMechanics.Value) return null;

            string body = Level();
            if (body == null) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append(Common).Append(body);

            string live = LiveState();
            if (live != null) sb.Append(live);

            return sb.ToString();
        }

        static string Level()
        {
            try
            {
                switch (GameManager.CurrentLevel)
                {
                    case 1: return L1;
                    case 2: return L2;
                    case 3: return L3;
                    case 4: return L4;
                    default: return null;   // hub and L99 have no puzzles of their own
                }
            }
            catch (Exception) { return null; }
        }

        // The per-playthrough half: read, never assumed.
        //
        // PuzzleShelfSequence.Start shuffles {0,1,2,3} and joins it, so the order
        // is new every time the basement loads and cannot be written down here.
        // PuzzleShelfManager.ShelfSequence is the resulting string, and the game's
        // own cheat command prints exactly this property as the answer
        // (CheatCommandGeneral.cs:125) - the same accessor the game trusts for the
        // same purpose, not a guess at internals.
        //
        // The digits are shelf identifiers: OnShelfAtCorrectAngle compares
        // shelf.shelfIdentifier[0] against m_shelfSequence[n] in order
        // (PuzzleShelfManager.cs:51). Which physical shelf wears which identifier
        // is scene wiring, so the digits are handed over as the wall's own
        // notation rather than translated into candles/coin/vases/feathers - a
        // wrong translation would send the player to the wrong shelf, which is
        // precisely the failure this file exists to avoid. She reads the wall; the
        // wall is authoritative.
        //
        // "ABCD" is the inspector default on m_shelfSequence and is overwritten in
        // Start before anything can read it, but it is filtered anyway: if it ever
        // does survive, it is a placeholder rather than an answer.
        //
        // Silent on failure by design. No sequence means she does not mention the
        // order, which is the ordinary state of things everywhere but the
        // basement; a wrong one would have her confidently mislead the player.
        static string LiveState()
        {
            try
            {
                if (GameManager.CurrentLevel != 2) return null;

                Type t = AccessTools.TypeByName("PuzzleShelfManager");
                if (t == null) return null;

                UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(t);
                if (found == null || found.Length == 0) return null;

                object seq = Traverse.Create(found[0]).Property("ShelfSequence").GetValue();
                string s = seq as string;
                if (string.IsNullOrEmpty(s)) return null;

                s = s.Trim();
                if (s == "ABCD") return null;

                return "- The order the shelves must be turned in is set for tonight, and it is "
                    + "drawn under the deer skull as four symbols, left to right. In your own "
                    + "shorthand that order is " + s + ". Read the symbols to the player, or point "
                    + "them at the wall - do not recite this shorthand at them, it would mean "
                    + "nothing to them.\n";
            }
            catch (Exception) { return null; }
        }
    }
}
