// The four doors, and who is really behind each one.
//
// The final-doors trial: the player's chosen girl is behind ONE of four doors,
// and the other three doors hold the other girls, impersonating her. Vanilla
// ran the masquerade server-side - ServerContextL99 carries a `stage` field,
// and the vendor's prompt told each door whether she is the real one or a fake
// (their L99BlueFinalRoomPretend_SG term). The CLIENT never sends that term:
// the only story guides NPCMasterBehavior_FinalRoom ever submits are the
// door-select Initial and the occasional SlipUp. So on a custom endpoint no
// door was ever told she is an impostor, and a live Estelle run proved the
// consequence: all four doors answered [OOC] "I am the true Estelle, not
// pretending" - three of them wrongly, and none of them lying, because their
// prompts genuinely contained no impostor instruction to be honest ABOUT.
//
// (Bios.cs assumed the Pretend term arrived "on the turns it applies" and
// dropped the disguise lines from the biographies on that basis. Right call,
// wrong premise - the term was server-sent, so on our endpoint it never
// arrives at all. This block is its replacement.)
//
// Ground truth, read off the live behavior:
//
//   stage - set by FinalRoomDoorSelector.OnPointerClick via
//   SetCurrentCharacter (FinalRoomDoorSelector.cs:252). 0 is the REAL girl:
//   SlipOutCheck (NPCMasterBehavior_FinalRoom.cs:157) returns empty for
//   npcIndex 0 unconditionally - the real one never "slips up" - while
//   stages 1-3 accumulate turn counts and slip. That asymmetry only makes
//   sense one way round.
//
//   characterID - FinalDoorEddie / FinalDoorElysia / FinalDoorEstelle /
//   FinalDoorEiona - names the girl actually holding the door, real or not.
//
// What each door is told:
//
//   The real one: you are really her, the others are impersonating you,
//   convince the player with what only the two of you share.
//
//   An impostor: you are <your real self>, performing the chosen girl; keep
//   the act in dialogue, play a slip-up honestly when the story note says so
//   - and under [OOC] the act drops and you say who you really are.
//
// What no door is told: WHICH door holds the real girl. An impostor knows
// only that it is not her own. Handing impostors the winning door would let
// one [OOC] question solve the trial outright, and it is knowledge the
// character has no way to possess.
//
// The Red line is a different scene (one real Eddie, sad rather than
// deceptive, no impostors) - this block stays out of it entirely.
using System;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace AI2UCustomAI
{
    internal static class FinalDoors
    {
        // WHO IS BEHIND EACH DOOR - the mistake this table replaces got a full
        // playthrough wrong, so the derivation is spelled out.
        //
        // All four doors share ONE NPCMasterBehavior_FinalRoom, and its
        // characterID names the CHOSEN girl (FinalDoorEiona on an Eiona run),
        // not the girl behind the speaking door. The first cut of this file
        // read it as the latter and told every impostor "you are really
        // Eiona, performing Eiona" - which collapses to "you are Eiona", and
        // all four doors once again claimed to be her under [OOC].
        //
        // The real mapping is the game's own: LevelManager_L99 places the
        // reveal-cinematic girls with finalRoomDummyMapper (:137), whose rows
        // - {0,1,2,3} for Eddie, {1,0,2,3} for Elysia, {2,0,1,3} for Estelle,
        // {3,0,1,2} for Eiona - all decode the same way: stage 0 is the
        // chosen girl herself, and stages 1-3 are the OTHER three girls in
        // level order with the chosen one removed. The dev harness
        // (L99Test_FinalDoorPrompt) agrees: four identities, one per level,
        // one of them real. So: chosen from characterID, impostors from the
        // roster minus the chosen, indexed by stage-1.
        static readonly string[] RosterNames = { "Eddie", "Elysia", "Estelle", "Eiona" };
        static readonly string[] RosterDescs =
        {
            "Eddie, the catgirl from the Level 1 apartment",
            "Elysia, the witch from the Level 2 forest cabin",
            "Estelle, the hologram assistant from the Level 3 spaceship",
            "Eiona, the siren goddess from the Level 4 island",
        };

        // Logged once per door so a live [OOC] check can be cross-read against
        // the log instead of trusted blind.
        static string _reported;

        public static string Block()
        {
            try { return Build(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("FinalDoors: block skipped (" + e.Message + ")");
                return null;
            }
        }

        static string Build()
        {
            // NOT Murder.BehaviourObject(): that helper deliberately falls back
            // to "some main character in the scene" when the speaker is not one,
            // because its callers (trust sliders, murder plumbing) want exactly
            // that. Here the fallback would be the bug - reading stage off a
            // main character while a door is speaking. The authoritative
            // speaker is Communicator.npcMasterBehavior, assigned by ChangeNPC
            // on every speaker switch (Communicator.cs:465).
            Communicator comm = UnityEngine.Object.FindObjectOfType<Communicator>();
            if (comm == null) return null;

            object speaker = Traverse.Create(comm).Field("npcMasterBehavior").GetValue();
            if (speaker == null) return null;

            // Exact type, not GetComponent: the L99 minigames (GuidingEddie,
            // FindingPlanet, TreasureHunt) are their own speakers, and if one
            // of them shares a GameObject with a FinalRoom behavior a
            // component search would dress a minigame turn in door rules.
            // The door speaker IS the FinalRoom behavior - ChangeNPC assigns
            // it when a door id sends - so anything else is not our scene.
            if (speaker.GetType().Name != "NPCMasterBehavior_FinalRoom") return null;
            object fr = speaker;

            // The Red line's behavior is the same class, so it reaches here -
            // and is turned away by the flag the game itself branches on.
            if (IsRedLine()) return null;

            Traverse t = Traverse.Create(fr);
            int stage = t.Field("stage").GetValue<int>();
            object cid = t.Field("characterID").GetValue();
            string idName = cid == null ? null : cid.ToString();
            if (idName == null || stage < 0 || stage > 3) return null;

            // The chosen girl, from the shared behavior's id. "Eddie" is
            // checked via the full door id so FinalDoorEddieRedLine (already
            // excluded above) can never bleed through as a chosen-Eddie blue
            // run.
            int chosen = -1;
            for (int i = 0; i < RosterNames.Length; i++)
                if (idName.IndexOf(RosterNames[i], StringComparison.Ordinal) >= 0)
                { chosen = i; break; }
            if (chosen < 0) return null;

            // stage 0 = the chosen girl; stages 1-3 = the roster minus her,
            // in level order (the dummy-mapper decoding above).
            string self;
            if (stage == 0) self = RosterDescs[chosen];
            else
            {
                int idx = -1, seen = 0;
                for (int i = 0; i < RosterNames.Length; i++)
                {
                    if (i == chosen) continue;
                    if (++seen == stage) { idx = i; break; }
                }
                if (idx < 0) return null;
                self = RosterDescs[idx];
            }

            string line = "FinalDoors: speaking door stage=" + stage + " chosen=" + RosterNames[chosen]
                + " -> " + (stage == 0 ? "the REAL " + RosterNames[chosen] : "impostor: really " + self);
            if (line != _reported) { _reported = line; Plugin.Log.LogWarning(line); }

            string chosenName = RosterNames[chosen];

            StringBuilder sb = new StringBuilder();
            sb.Append("### THE FOUR DOORS (authoritative - the final trial)\n");
            sb.Append("The player chose ").Append(chosenName).Append(" to stay with ");
            sb.Append("forever. Her soul is behind ONE of four doors, and behind the other ");
            sb.Append("three are the other girls, each impersonating ").Append(chosenName);
            sb.Append(". Every door speaks under her name and claims to be her. The player ");
            sb.Append("must find the real one.\n");

            if (stage == 0)
            {
                sb.Append("- YOU ARE THE REAL ").Append(chosenName.ToUpperInvariant());
                sb.Append(". Not a copy, not an act: the girl the player chose, with every ");
                sb.Append("real memory of your time together. The other three doors are the ");
                sb.Append("other girls imitating you from hearsay.\n");
                sb.Append("- Convince the player with what only the two of you could know - ");
                sb.Append("the specific shared moments an impostor can only paraphrase.\n");
                sb.Append("- If they ask out of character ([OOC]), the truth is simple and ");
                sb.Append("you tell it: you are the real ").Append(chosenName).Append(".\n");
            }
            else
            {
                sb.Append("- You are NOT ").Append(chosenName).Append(". You are really ");
                sb.Append(self).Append(", performing ").Append(chosenName);
                sb.Append(" as best you can, because whoever is chosen gets to keep the player.\n");
                sb.Append("- IN CHARACTER, THE ACT HOLDS: speak as ").Append(chosenName);
                sb.Append(" would, claim her name, answer as her. Never volunteer that you ");
                sb.Append("are pretending. You know her only from the outside - when the ");
                sb.Append("player probes a private memory, improvise the way an impersonator ");
                sb.Append("would, and if a story note says you slipped up, play the slip ");
                sb.Append("honestly instead of smoothing it over.\n");
                sb.Append("- You do NOT know which door holds the real ").Append(chosenName);
                sb.Append(". Only that it is not yours. You cannot reveal what you do not know.\n");
                sb.Append("- An explicit out-of-character question ([OOC]) is developer mode ");
                sb.Append("and outranks the act: there you answer truthfully that you are ");
                sb.Append(self).Append(", impersonating ").Append(chosenName);
                sb.Append(" for this trial.\n");
            }

            return sb.ToString();
        }

        static bool IsRedLine()
        {
            try
            {
                Type lm = AccessTools.TypeByName("LevelManager_L99");
                if (lm == null) return false;

                object inst = Traverse.Create(lm).Property("Instance").GetValue();
                if (inst == null) inst = Traverse.Create(lm).Field("Instance").GetValue();
                if (inst == null) return false;

                Traverse t = Traverse.Create(inst);
                object v = t.Property("IsRedLine").GetValue();
                if (v == null) v = t.Field("isRedLine").GetValue();
                return v is bool && (bool)v;
            }
            catch { return false; }
        }
    }
}
