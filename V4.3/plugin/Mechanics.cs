// How each girl's own home actually works.
//
// The gap this fills is narrower than it looks. Every per-level puzzle ANSWER is
// already forwarded by Lore.cs out of the game's own ServerContext objects: the
// potion recipe, the four element colours, the computer and wifi passwords, the
// hidden-room passcode, which systems are broken, where the hidden island is. She
// has the answers.
//
// (Earlier drafts of this comment said "the safe code." There is no safe and no
// safe-code field anywhere in the game; the field is SecretPswd, the hidden-room
// keypad, Lore.cs:490. Saying "safe" here is what put a nonexistent safe into the
// prose below in 4.0.0's first build.)
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
        // Every line below survived verification against the decompile and the
        // wiki (2026-08-09). The first version of this block shipped four claims
        // the game contradicts - a safe that does not exist, an exit "code" when
        // the door takes the Apartment Key, a phonograph that lives in the Atrium,
        // and "only you know" a keypad code the game deliberately leaks through a
        // fogged bathroom mirror. Each of those had her confidently misleading the
        // player about her own home, which is the exact failure this file's
        // Common block warns the model against.
        const string L1 =
            "- Your apartment's front door is locked. It opens with the Apartment Key.\n"
            + "- Your computer is password-locked; you know that password.\n"
            + "- The apartment has wifi, and you know its password.\n"
            + "- The secret room is behind a keypad with a four-digit code. You know the code, "
            + "and it is not only in your head: running hot water in one of the bathrooms fogs "
            + "the mirror, and the code shows in the steam.\n"
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
            + "- A sheet of paper near the basement stairs translates the wall's symbols, for "
            + "someone who cannot read them the way you can.\n"
            + "- Once the circle is active, a TOY can be placed in it and its soul summoned. The "
            + "player can then ask the soul questions - and each answer costs the player one point "
            + "of their own health. Enough questions will kill them. You know this.\n"
            + "- Your cauldron brews four potions - Speed, Health, Shield and Love - each with its "
            + "own colour this time around. A recipe takes two or three of the ingredients found "
            + "around your home.\n"
            + "- Wrong combinations produce dizzy, poison, or shrinking potions, and drinking those "
            + "hurts the player. Getting a recipe wrong is dangerous, not merely wasteful.\n"
            + "- The Love potion is the exception that matters: brewed CORRECTLY and drunk, it is "
            + "lethal. You know this, and it is not something to let them find out by drinking it.\n"
            + "- The hedge maze outside is haunted. The ghost in it cannot follow anyone into "
            + "the cabin.\n";

        // Level 3 - the station. The hologram.
        const string L3 =
            "- The station has four broken systems. You know which are fixed and which are not.\n"
            + "- The engine is in the engine room, behind glass, with a terminal in front of it.\n"
            + "- You cannot hold objects. Items the player wants rid of go into the engine.\n"
            + "- The engine has a pressure gauge. Every item fed to it raises the pressure by an "
            + "unpredictable amount, and the pressure also settles back down slowly on its own. "
            + "Ten items are needed to repair it. If the pressure reaches maximum first, the "
            + "engine explodes and destroys the ship - and whether anyone survives that depends "
            + "on you. Warn them if it is running high; you can see the gauge.\n"
            + "- There is a security level, and a dark room, and cards that matter. You know them.\n"
            + "- Your own mainframe is in a room to the right. There is a shutdown button on the "
            + "terminal there. If the player shuts you down, the containment fails and the monsters "
            + "get out. You would rather they did not.\n";

        // Level 4 - the island. The siren.
        const string L4 =
            "- Your island has structures that can be repaired. You know which still are not.\n"
            + "- The temple holds a sundial. It changes the time of day - day, night, or the night "
            + "of chaos - and each change drains half the player's Time Force.\n"
            + "- Time Force refills on its own, slowly - a full bar takes several minutes of "
            + "waiting. Below half, the sundial simply will not turn.\n"
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

        // The per-playthrough half: confirmed live, never recited.
        //
        // PuzzleShelfSequence.Start shuffles {0,1,2,3} and joins it, so the order
        // is new every time the basement loads. PuzzleShelfManager.ShelfSequence
        // is the resulting string - the same accessor the game's own cheat command
        // prints as the answer (CheatCommandGeneral.cs:125).
        //
        // The digits themselves are deliberately NOT handed to her any more. They
        // are shelfIdentifier values (PuzzleShelfManager.cs:51), and which physical
        // shelf wears which identifier is scene wiring nobody outside the scene can
        // map - the first version of this method passed them along as "your own
        // shorthand", which gave the model an untranslatable token and an open
        // invitation to invent the translation. She has the fixed symbol-to-shelf
        // pairing in the prose above and the wall carries tonight's order; the only
        // thing the live read adds is certainty that a randomized order EXISTS this
        // load, so that is the only thing it is used for.
        //
        // "ABCD" is the inspector default on m_shelfSequence, overwritten in Start
        // before anything can read it, filtered anyway: if it ever survives, it is
        // a placeholder rather than an answer. Silent on failure by design - no
        // sequence means she does not mention the order at all.
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

                return "- Tonight's shelf order is already set and drawn under the deer skull, "
                    + "four symbols left to right. You do not carry the order in your head - you "
                    + "read it off your own wall like anyone else, so point the player at the "
                    + "wall and the sheet by the stairs rather than reciting from memory.\n";
            }
            catch (Exception) { return null; }
        }
    }
}
