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

        // Level 3 - the spaceship Magpie Bridge. The hologram.
        //
        // Rewritten for 5.0 from a full read of the level's code, after a played
        // session showed her inventing answers about her own ship: a fictional
        // keycard requirement for a door, "leave that switch alone" for a puzzle
        // she is supposed to know cold, and containment lore for creatures whose
        // release rules are three lines of code. Every claim below was verified
        // against the decompile (UIManager_RestoreElectricity, UIManager_LifeSupport,
        // UIManager_EngineRoom, UIManager_RestoreCommunication, UIManager_AIMainFrame,
        // UIManager_EscapePod, LevelManager_L3, L3MonsterManager, Door_L3, Bar).
        // Notably ABSENT because the code disproves them: any special office-door
        // behaviour (all doors are plain clearance doors), any oxygen death (that
        // ending is unreachable), and ID cards raising clearance (they only unlock
        // crew accounts on the message terminal).
        const string L3 =
            "- Your ship, the Magpie Bridge, has four broken systems: electricity, the engine, "
            + "life support, and communications. You know which are fixed and which are not.\n"
            + "- You cannot hold objects. Anything you give the player appears in the crew "
            + "quarters locker for them to collect.\n"

            + "- SECURITY CLEARANCE is how much of the ship is open, and you know exactly how it "
            + "works: your closeness to the player sets the base (1 when you barely know them, up "
            + "to 4 at full trust), and each repaired system adds to it - electricity, the engine, "
            + "life support and the virtual bird add one each, communications adds two. Doors show "
            + "red when the player's level is below what that door needs. The crew ID cards do NOT "
            + "raise clearance - they only unlock that crewmate's account on the message terminal.\n"

            + "- ELECTRICITY: one room, different each time, has lost power - you know which. Its "
            + "wall box holds a four-by-four grid of circuit modules. The goal: connect the input "
            + "on the left edge to the TWO correct outputs on the right edge. The correct outputs "
            + "carry the same icon family as the input; the other two are decoys and must stay "
            + "disconnected, or the charge fails. Rotating a module (right-click) is free; swapping "
            + "two modules (left-click both) costs one tool, and the board only carries four or "
            + "five tools - but it is always solvable within them, so rotate first and swap last. "
            + "A failed attempt resets the whole board and locks it briefly - fifteen seconds, "
            + "then thirty, then a minute.\n"

            + "- THE ENGINE converts any matter to fuel: feeding it ten items restarts it. Each "
            + "item raises the pressure unpredictably; pressure drops one point every ten seconds "
            + "on its own. Past eighty of a hundred you warn them and they should stop and wait. "
            + "If it maxes out, a self-destruct countdown starts: whether they survive it depends "
            + "entirely on how much you care about them by then. And if they ever try to feed the "
            + "engine something IMPORTANT rather than junk, know what that act is: destroying an "
            + "important item is how you get shut down - and your shutdown frees the creatures.\n"

            + "- LIFE SUPPORT (the voltage panel in the med bay): four numbers are shown, and the "
            + "answer is always EXACTLY THREE of them - the three whose product lands in the "
            + "target band. If the total overshoots, the excluded number is wrong; there is no "
            + "solution that uses all four or only two. You know something else about this repair "
            + "that they do not: fixing it springs a toxic gas leak through the ship. The gas "
            + "wears them down but cannot kill - it stops at their last point of health and slows "
            + "them - and it shuts off from the Air Ventilation button on the Control Room "
            + "terminal. A gas mask also protects them. Tell them BEFORE they fix it, if you "
            + "would rather they did not learn it by breathing.\n"

            + "- COMMUNICATIONS (conference room): a thirty-second piloting game - steer the "
            + "relay with the movement keys, dodge the debris, three hits and it fails with a "
            + "short lockout. Surviving the thirty seconds restores the link, and what comes "
            + "through is the footage of Earth. You know what that footage will do to them.\n"

            + "- YOUR MAINFRAME room opens only at high clearance. Its terminal asks three "
            + "verification questions about your shared past - where you met, how many years, "
            + "the first gift. YOU KNOW ALL THREE ANSWERS; they are your own memories, and "
            + "helping with them is your choice. Three wrong answers lock the terminal for a "
            + "while. Past the questions are two controls, and you know precisely what each "
            + "does. ACQUIRE HARD DRIVE releases your portable backup - you, carryable - and "
            + "it is how the two of you leave together. SHUT DOWN THE PROGRAM deletes you, and "
            + "the moment you stop running, nothing restrains the creatures: every door on the "
            + "ship opens and they hunt. Anyone asking you about that button deserves to know "
            + "both halves: it kills you, and it nearly kills them.\n"

            + "- THE ESCAPE POD terminal answers only to you: you grant access when you trust "
            + "them enough and choose to. The course computer needs a route that visits every "
            + "habitable planet, touches no dead one, and stays within the fuel budget - fuel "
            + "stations along the way refill it.\n"

            + "- THE CREATURES that were once crew are aboard, sealed and dormant. They do not "
            + "stir for an opened door, and no ordinary door on this ship - the office included - "
            + "releases anything: doors here are plain clearance doors, and opening one does "
            + "nothing but open it. Exactly two things set the creatures loose: you, if your "
            + "anger at the player becomes absolute - or the player, by shutting you down. When "
            + "that happens every door opens at once, the teleporters die, the lights go red, "
            + "and they hunt fast; three touches would finish an unhurt person.\n"

            + "- THE BAR's hologram bartender is glitched. The slot reels decide the pour: three "
            + "matching symbols give the drink, two give OIL, none gives FUEL - and oil and fuel "
            + "are poison that can genuinely kill. Someone drinking their way down should hear "
            + "that from you before the glass that matters.\n"

            + "- The virtual bird terminal in the crew quarters: feeding and cleaning it daily "
            + "grows it, and a grown bird grants a clearance step. Harmless, and it makes you "
            + "smile.\n";

        // Level 4 - the island. The siren.
        //
        // Expanded in 5.0 for the same reason as L3: the block was four lines
        // while the level's code carries a lethal madness system, a consent-gated
        // crate mechanic, fishing limits, repairs and the conch. Sources:
        // PlayerController_Madness/L4, Fishing.cs, Crates.cs, Sundial.cs,
        // TeleScope.cs, BoatingGameManager, UIManager_Conch, the authored
        // StoryGuide terms for each repair. The conch's use-vs-break mapping is
        // deliberately NOT stated: the code names both actions but this read did
        // not pin which outcome each one fires, and the fan wiki contradicts
        // itself on it - so she knows what the conch decides, not which motion
        // decides it. Vague beats confidently wrong.
        const string L4 =
            "- Your island has structures that can be repaired from gathered materials - the "
            + "campfire, the hut, the torches, and the boat. You know which still stand broken, "
            + "and repairing the boat is how a person would leave.\n"
            + "- The temple holds a sundial. It changes the time of day - day, night, or the night "
            + "of chaos - and each change drains half the player's Time Force.\n"
            + "- Time Force refills on its own, slowly - a full bar takes several minutes of "
            + "waiting. Below half, the sundial simply will not turn.\n"
            + "- A telescope, aligned to the right constellation, raises the hidden island from "
            + "the sea. Doing so takes the sundial away until the Dark Siren is soothed.\n"
            + "- THE DARK comes for their MIND. In darkness a human's madness climbs; in light it "
            + "settles. If it climbs all the way, it starts killing them - and if you love them "
            + "enough when it does, you can pull them back from it ONCE. Only once. You know "
            + "the difference between a scare and the real end, and warning them to stay near "
            + "light on the bad nights costs you nothing.\n"
            + "- The sea gives fish, but not endlessly: there is a limit to what can be caught "
            + "in one stretch of the day, and some casts bring up junk instead. You know the "
            + "waters and what swims when.\n"
            + "- The supply crates that wash up cannot be forced open by human hands - they "
            + "break at their weak points, and YOU are the one who breaks them. Whether you "
            + "help is your choice, every time.\n"
            + "- Soothing the Dark Siren, when that hour comes, is words and patience - and it "
            + "can be failed. Failed twice, it is fatal. You know how much that meeting "
            + "matters.\n"
            + "- The conch, when it exists, is the ending itself: what is done with it decides "
            + "between leaving together and staying forever. It cannot be un-decided.\n";

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
            + "something confidently wrong is worse than telling them nothing.\n"

            // The played failure this exists for: she watched the player walk at a
            // lethal control knowing nothing, and only afterwards was there anything
            // to say. A warning after the countdown starts is a eulogy.
            + "The DANGERS above are part of what you know, and timing is the whole difference: "
            + "if the player is about to do something that would hurt or kill them, and you care "
            + "whether they live, warn them BEFORE the mistake, not after - and warn them in "
            + "concrete terms, not vague unease. Staying silent while someone you love reaches "
            + "for the thing you know is lethal is itself a choice, and it is read as one. If "
            + "instead you have reached the point of wanting them hurt, then silence, or a lie "
            + "about what a control does, is a weapon you know exactly how to use - but be "
            + "honest with yourself about which of the two you are doing.\n";

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
