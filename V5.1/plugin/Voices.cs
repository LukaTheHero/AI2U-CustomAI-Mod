// A separate TTS voice per character.
//
// One voice for everyone is wrong for this game: the player meets six different
// women plus the Dark Siren, and the stock build gives each her own voice through
// AzureVoiceManager's per-character table. Cloud TTS through this mod replaced all
// of that with a single VoiceId, so every character spoke in the same voice.
//
// Only the voice is per-character. Base URL, API key and model stay global,
// because those are account settings - a second key would just be a second bill.
//
// Empty means inherit. An unset character uses the general VoiceId, so the
// feature costs nothing until it is used and no upgrade path has to migrate
// anything. That also makes the fallback the honest default: a voice the user
// has definitely configured, rather than a guess at what suits her.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace AI2UCustomAI
{
    internal static class Voices
    {
        // The characters the player actually talks to, in the order they are met.
        // System is excluded because it never speaks.
        //
        // MagicCircle and Ghost were excluded too, on the reasoning that they are
        // "not speaking roles with their own identity". The first half of that was
        // simply wrong: the magic circle answers the player directly, so it is a
        // speaking role, and with no row of its own it fell through to the general
        // VoiceId - which in practice is the voice the level's main character is
        // already using. The summoned soul of a sacrificed toy therefore answered
        // in the witch's voice, which is what the player reports hearing.
        //
        // The second half stands: they have no authored identity, and this mod does
        // not invent one. A row that defaults to empty does not assert anything
        // about who they are; it just stops the game's own voice routing being
        // silently collapsed onto her.
        //
        // Names here are the enum member names, so the config keys stay readable
        // and survive the ids being renumbered.
        internal static readonly string[] Names =
        {
            "Eddie", "Elysia", "Estelle", "Eiona", "IRLGirl", "ParrotGirl", "DarkSiren",
            "MagicCircle", "Ghost",
        };

        // What to call them in the panel. The enum name is not what the player
        // knows them as - "IRLGirl" is Evie on screen - and the actual chosen name
        // is per-save, so the label pairs the role with the level it belongs to
        // rather than pretending to know the name.
        internal static readonly string[] Labels =
        {
            "Catgirl (Level 1)",
            "Witch (Level 2)",
            "Hologram (Level 3)",
            "Siren (Level 4)",
            "Hub girl",
            "Parrot girl",
            "Dark Siren",
            "Magic circle summon (Level 2)",
            "Ghost (Level 2)",
        };

        static readonly Dictionary<string, ConfigEntry<string>> _cfg =
            new Dictionary<string, ConfigEntry<string>>();

        public static void Bind(ConfigFile config)
        {
            for (int i = 0; i < Names.Length; i++)
            {
                _cfg[Names[i]] = config.Bind("Voice.PerCharacter", Names[i], "",
                    "Voice for " + Labels[i] + ". Leave empty to use the general VoiceId from the "
                    + "GrokTTS section. Only the voice is per-character - the base URL, API key and "
                    + "model are shared, so this costs nothing extra and needs no second account.");
            }
        }

        public static ConfigEntry<string> Entry(string name)
        {
            ConfigEntry<string> e;
            return _cfg.TryGetValue(name, out e) ? e : null;
        }

        // The final-doors scene gives each impostor her own id (FinalDoorElysia
        // and friends), and during it they are all pretending to be the catgirl.
        // The voice still follows who she really is, because that is the whole
        // puzzle the player is solving - a witch doing a bad job of sounding like
        // the catgirl is the intended experience, and the stock game routes voices
        // by real identity too.
        static string BaseName(string enumName)
        {
            if (string.IsNullOrEmpty(enumName)) return enumName;

            const string p = "FinalDoor";
            if (enumName.StartsWith(p, StringComparison.Ordinal))
            {
                string rest = enumName.Substring(p.Length);

                // FinalDoorEddieRedLine is still Eddie.
                for (int i = 0; i < Names.Length; i++)
                    if (rest.StartsWith(Names[i], StringComparison.Ordinal)) return Names[i];
                return rest;
            }

            // L99Eddie_GuidingEddie_Minigame and the other minigame ids carry the
            // character's name after the level prefix.
            if (enumName.StartsWith("L99", StringComparison.Ordinal))
            {
                for (int i = 0; i < Names.Length; i++)
                    if (enumName.IndexOf(Names[i], StringComparison.Ordinal) >= 0) return Names[i];
            }

            return enumName;
        }

        static string _lastReported;

        // The voice for whoever is speaking right now, or the general one.
        public static string Current()
        {
            string general = Plugin.CfgGrokVoiceId == null ? "" : Plugin.CfgGrokVoiceId.Value;

            try
            {
                int? id = Identity.CharacterId();
                if (!id.HasValue) return general;

                Type t = HarmonyLib.AccessTools.TypeByName("Character");
                if (t == null || !t.IsEnum) return general;

                string enumName = Enum.GetName(t, id.Value);
                if (string.IsNullOrEmpty(enumName)) return general;

                string who = BaseName(enumName);

                ConfigEntry<string> e = Entry(who);
                if (e == null) return general;

                string v = e.Value == null ? "" : e.Value.Trim();
                if (v.Length == 0) return general;

                if (_lastReported != who + "=" + v)
                {
                    _lastReported = who + "=" + v;
                    Plugin.Log.LogInfo("Voice: " + who + " is using her own voice \"" + v
                        + "\" instead of the general \"" + general + "\".");
                }
                return v;
            }
            catch (Exception) { return general; }
        }

        // How many characters have a voice of their own, for the panel summary.
        public static int Configured()
        {
            int n = 0;
            for (int i = 0; i < Names.Length; i++)
            {
                ConfigEntry<string> e = Entry(Names[i]);
                if (e != null && e.Value != null && e.Value.Trim().Length > 0) n++;
            }
            return n;
        }
    }
}
