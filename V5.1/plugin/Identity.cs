using System;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace AI2UCustomAI
{
    // Who the characters are, read off the live game.
    //
    // The stock game keeps its level prompt server-side and substitutes the
    // player's chosen names into it there ({npcName}/{playerName}). We never
    // reach that server, and ChatGPTConversation._initialPrompt is left at its
    // default "You are ChatGPT, a large language model trained by OpenAI." -
    // so nothing in the forwarded history tells the model who she is. She then
    // invents a name, or answers to none.
    //
    // Communicator.UpdateNPCName already resolves the saved name for whichever
    // level and character is loaded (GlobalSettings.npcName_L1..L4, plus the
    // two L99 girls) and leaves the result in its private npcName field.
    // Reading that field follows the game's own resolution instead of
    // duplicating the level/character mapping here, so renaming her in NPC
    // Customization takes effect on the next line she speaks.
    internal static class Identity
    {
        static Communicator _comm;

        static Communicator Comm()
        {
            // Cached, but re-found once a level change disposes the old one.
            if (_comm == null)
                _comm = UnityEngine.Object.FindObjectOfType<Communicator>();
            return _comm;
        }

        static string Read(Communicator c, string field)
        {
            try
            {
                string v = Traverse.Create(c).Field(field).GetValue<string>();
                return string.IsNullOrEmpty(v) ? null : v.Trim();
            }
            catch { return null; }
        }

        // Who the game thinks is speaking, as the raw Character enum value.
        //
        // Communicator.cs:245 does
        //
        //     currentCharacterID = (Character)currentJSON["character"].AsInt;
        //
        // unconditionally, with no validation. The Character enum starts at
        // Eddie = 1 and has no zero member, so a reply that omits the field
        // resolves to the undefined (Character)0 - and that value then drives the
        // thinking-indicator dismissal, a FinalChase membership test, and the key
        // this NPC's chat history is filed under. In a multi-NPC scene it backs
        // history up to the wrong slot, and AzureVoiceManager.cs:132 throws
        // KeyNotFoundException looking up a voice for it.
        //
        // So the mod echoes the value the game already holds. The assignment at
        // :245 then writes back what was there, which is the no-op it should have
        // been. Returns null when unavailable, and the caller omits the field
        // rather than guessing a number.
        public static int? CharacterId()
        {
            try
            {
                Communicator c = Comm();
                if (c == null) return null;

                object v = Traverse.Create(c).Field("currentCharacterID").GetValue();
                if (v == null) return null;

                return Convert.ToInt32(v);
            }
            catch (Exception) { return null; }
        }

        // Character.MagicCircle = 20, Character.Ghost = 21 (Character.cs).
        //
        // These two are not the level's main character. A magic circle summon is a
        // soul that was sealed in a sacrificed toy, and the game routes it through
        // the same Communicator - NPCMasterBehavior_MagicCircle.SendHintAIReply
        // (:99) calls SendToChatGPT with Character.MagicCircle, stage 100 and all
        // three trait lists sentinel-emptied to {-1} (:105-109).
        //
        // Vanilla therefore selects a *different* persona server-side. Our mod
        // patches one level below that routing, so without this test every persona
        // block below fires and the summon answers as the witch: her name, her
        // traits, her trust level, her potion secrets. That is the bug this gates.
        //
        // ChangeNPC (Communicator.cs:118) assigns currentCharacterID before
        // SendToChatGPT (:141), so the value is already correct when we read it.
        public static bool IsSummon()
        {
            int? id = CharacterId();
            if (id == null) return false;
            return id.Value == 20 || id.Value == 21;
        }

        // Character.DarkSiren = 40: a speaker whose name field belongs to someone
        // else.
        //
        // Communicator.UpdateNPCName switches on GameManager.CurrentLevel, not on
        // who is speaking, so case 4 assigns npcName_L4 - the level's main
        // character's chosen name - no matter which of the two characters in that
        // level is talking. Vanilla gets away with it because it substitutes the
        // name into a different, server-side prompt for this speaker. We do not:
        // Block() below states the name as authoritative and tells her never to call
        // herself anything else, which turns the game's loose end into a hard
        // instruction to answer to the wrong name.
        //
        // Deliberately NOT folded into IsSummon(). That predicate suppresses every
        // persona block, which is right for a summon - the game ships no persona for
        // one - but wrong here: this speaker has her own authored persona term, and
        // suppressing it would cost her the only characterisation she has. The only
        // thing wrong is the name, so the only thing dropped is the name claim. She
        // is introduced by her persona instead, which is how the game does it.
        public static bool NameFieldBelongsToSomeoneElse()
        {
            int? id = CharacterId();
            if (id == null) return false;
            return id.Value == 40;
        }

        // What a summon is told about itself.
        //
        // Suppressing the main character's persona for a summon (see the gate in
        // BuildRequest) was necessary but not sufficient, and the log from a live
        // summon says why. The authored framing the game builds at
        // NPCMasterBehavior_MagicCircle.cs:42-51 resolves to exactly two
        // fragments - "After a toy was sacrificed at magic circle," and
        // "<player> (the player) asks this question:" - and neither one says the
        // thing answering IS the summoned soul. Vanilla did not need them to:
        // it selected a separate persona server-side, which we never reach.
        //
        // So with her blocks correctly gone, the summon was left with no identity
        // at all, and a model handed a witch-shaped location list and a question
        // about the witch answers as the witch. Absence did not read as absence;
        // it read as her.
        //
        // This block is deliberately NOT a persona. There is no authored voice,
        // history or personality for a summoned toy anywhere in the game files,
        // and inventing one would be exactly the substitution this project
        // refuses. What it states instead is the boundary and the mechanics the
        // game itself establishes: a toy was sacrificed (the game's own message
        // text), the reply is a single answer (SendHintAIReply is one shot), and
        // there is no earlier conversation to draw on (ChangeNPC truncates this
        // speaker's history to its first entry, Communicator.cs:469-472). Who the
        // summon is beyond that stays absent, because it is absent.
        //
        // The name is asserted negatively - "you are not X" - so the live name
        // still comes from the game rather than from anything written here.
        public static string SummonBlock()
        {
            Communicator c = Comm();
            string npc = c == null ? null : Read(c, "npcName");

            StringBuilder sb = new StringBuilder();
            sb.Append("### IDENTITY (authoritative)\n");
            sb.Append("You are not the character the player has been talking to");
            if (npc != null) sb.Append(" - you are not ").Append(npc);
            sb.Append(". Do not use her name, do not speak as her, do not answer ");
            sb.Append("on her behalf, and do not claim her memories or her ");
            sb.Append("belongings as yours.\n");
            sb.Append("You are what the magic circle drew out of the toy that was ");
            sb.Append("just sacrificed on it. You speak from inside that summoning ");
            sb.Append("and only for as long as it lasts.\n");
            sb.Append("You have no memory of any earlier conversation. This is the ");
            sb.Append("only thing you say: answer the one question that was asked, ");
            sb.Append("briefly, and nothing beyond it.\n");
            return sb.ToString();
        }

        // The system message pinning down both names, or null when there is no
        // Communicator in the scene yet (main menu, loading).
        public static string Block()
        {
            Communicator c = Comm();
            if (c == null) return null;

            string npc = Read(c, "npcName");
            string player = Read(c, "playerName");

            // Dropped before anything is asserted about it, for the reason given
            // above. The player's name is still correct and is still sent.
            if (npc != null && NameFieldBelongsToSomeoneElse())
            {
                Plugin.Log.LogInfo("Identity: this speaker shares the level's name "
                    + "field with the main character, so no name is claimed for her.");
                npc = null;
            }

            if (npc == null && player == null) return null;

            // UpdateNPCName swaps npcName for an in-character scolding line
            // when the chosen name trips ProfanityFilter, keeping the original
            // in npcName_unfiltered. Handing that replacement text over as a
            // name would read as her actually being called it, so pass the
            // game's intent through instead.
            //
            // Gated on npc for the same reason the name is: the flag describes the
            // name the player chose for the MAIN character, so on a speaker who only
            // borrows that field the scolding would be about a name that was never
            // hers. npc is already null in that case, so one test covers both.
            bool flagged = npc != null && Read(c, "npcName_unfiltered") != null;

            StringBuilder sb = new StringBuilder();
            sb.Append("### IDENTITY (authoritative)\n");

            if (flagged)
            {
                sb.Append("The player has given you an inappropriate name. ");
                sb.Append("You are angry about it and you say so.\n");
            }
            else if (npc != null)
            {
                sb.Append("Your name is ").Append(npc).Append(". ");
                sb.Append("The player chose that name for you: answer to it, ");
                sb.Append("use it when you refer to yourself, and never call ");
                sb.Append("yourself by any other name or say you do not have one.\n");
            }

            if (player != null)
            {
                sb.Append("You are talking to ").Append(player).Append(". ");
                sb.Append("That is the player's name - address them by it.\n");
            }

            return sb.ToString();
        }

        // Logged once per scene so a wrong name is visible in the log rather
        // than only in her dialogue.
        static string _reported;

        public static void Report()
        {
            Communicator c = Comm();
            if (c == null) return;

            string npc = Read(c, "npcName");
            string player = Read(c, "playerName");
            string line = (npc ?? "?") + " / " + (player ?? "?");
            if (line == _reported) return;

            _reported = line;
            Plugin.Log.LogInfo("Identity read from the live scene: npc="
                + (npc ?? "(none)") + " player=" + (player ?? "(none)"));
        }
    }
}
