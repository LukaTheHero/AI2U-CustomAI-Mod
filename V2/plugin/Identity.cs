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

        // The system message pinning down both names, or null when there is no
        // Communicator in the scene yet (main menu, loading).
        public static string Block()
        {
            Communicator c = Comm();
            if (c == null) return null;

            string npc = Read(c, "npcName");
            string player = Read(c, "playerName");
            if (npc == null && player == null) return null;

            // UpdateNPCName swaps npcName for an in-character scolding line
            // when the chosen name trips ProfanityFilter, keeping the original
            // in npcName_unfiltered. Handing that replacement text over as a
            // name would read as her actually being called it, so pass the
            // game's intent through instead.
            bool flagged = Read(c, "npcName_unfiltered") != null;

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
