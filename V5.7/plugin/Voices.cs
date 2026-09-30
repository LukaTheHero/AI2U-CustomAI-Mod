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

        // Azure gets its own per-character voices, separate from the ones above.
        //
        // Until 5.6 the Azure mode read the same per-character fields as the
        // Custom Endpoint mode, and those hold provider-specific names: "eve" for
        // xAI, a voice id for ElevenLabs. Azure then had to guess which values
        // were meant for it, and the guess was "contains the word Neural", which
        // silently ignored a typed "Jenny" and accepted names the key's region
        // does not have. Two providers, two sets of fields: nothing typed for one
        // can be misread by the other.
        static readonly Dictionary<string, ConfigEntry<string>> _az =
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

            for (int i = 0; i < Names.Length; i++)
            {
                _az[Names[i]] = config.Bind("Voice.PerCharacterAzure", Names[i], "",
                    "Azure Speech voice for " + Labels[i] + " in Cloud Original (Azure) mode. Leave "
                    + "empty for her original game voice. Any voice your Azure region offers works - a "
                    + "full name like en-US-JennyNeural, or just Jenny. The HD voices "
                    + "(...MultilingualNeuralHD) sound the most natural.");
            }

            for (int i = 0; i < Names.Length; i++)
            {
                _azPitch[Names[i]] = config.Bind("Voice.PerCharacterAzure", Names[i] + "Pitch", "",
                    "Azure pitch for " + Labels[i] + ", in percent (-50 to 50). Empty = the default: the "
                    + "original game's tuning for her original voice, natural pitch for a voice you chose.");
                _azSpeed[Names[i]] = config.Bind("Voice.PerCharacterAzure", Names[i] + "Speed", "",
                    "Azure speaking speed for " + Labels[i] + ", in percent (-50 to 100). Empty = the "
                    + "default, as for pitch.");
            }

            // Once, ever. Running it on every start put back any Azure voice the
            // player had deliberately cleared, because the old shared field still
            // held it and the Azure field was empty again.
            _azMigrated = config.Bind("Voice.PerCharacterAzure", "_MigratedFromShared", false,
                "Internal: set after the one-time copy of Azure voice names from the shared "
                + "per-character fields (5.6). Leave it alone.");
            if (!_azMigrated.Value)
            {
                MigrateAzure();
                _azMigrated.Value = true;
            }
        }

        static ConfigEntry<bool> _azMigrated;

        // Would this value, typed into a shared per-character field, have been
        // meant for Azure? The same test the migration and old profiles use.
        internal static bool AzureCandidate(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            v = v.Trim();
            return v.IndexOf("Neural", StringComparison.OrdinalIgnoreCase) >= 0 || AzureTts.LooksLikeShortName(v);
        }

        // A player who typed an Azure voice into the shared field before 5.6 did
        // it because that was the only field there was. Carry those values over
        // once, so nobody's setup changes under them; values that were plainly
        // meant for another provider stay where they are.
        static void MigrateAzure()
        {
            int moved = 0;
            for (int i = 0; i < Names.Length; i++)
            {
                ConfigEntry<string> shared, az;
                if (!_cfg.TryGetValue(Names[i], out shared) || !_az.TryGetValue(Names[i], out az)) continue;
                string v = shared.Value == null ? "" : shared.Value.Trim();
                string cur = az.Value == null ? "" : az.Value.Trim();
                if (cur.Length > 0 || v.Length == 0) continue;
                if (!AzureCandidate(v)) continue;
                az.Value = v;
                moved++;
            }
            if (moved > 0)
                Plugin.Log.LogInfo("Voice: copied " + moved + " Azure voice name(s) into the new per-character "
                    + "Azure fields.");
        }

        // The panel's name for a character, for messages the player reads.
        internal static string LabelFor(string name)
        {
            for (int i = 0; i < Names.Length; i++)
                if (Names[i] == name) return Labels[i];
            return string.IsNullOrEmpty(name) ? "This character" : name;
        }

        public static ConfigEntry<string> AzureEntry(string name)
        {
            ConfigEntry<string> e;
            return name != null && _az.TryGetValue(name, out e) ? e : null;
        }

        // Per-character pitch and speed for Azure (5.7), asked for by a player
        // who tunes every voice in his own app. Empty means "the default", which
        // is not a number: her original voice keeps the game's own tuning, and a
        // chosen voice speaks as it was built. A number replaces that default.
        static readonly Dictionary<string, ConfigEntry<string>> _azPitch =
            new Dictionary<string, ConfigEntry<string>>();
        static readonly Dictionary<string, ConfigEntry<string>> _azSpeed =
            new Dictionary<string, ConfigEntry<string>>();

        internal const int PitchMin = -50, PitchMax = 50, SpeedMin = -50, SpeedMax = 100;

        public static ConfigEntry<string> AzurePitchEntry(string name)
        {
            ConfigEntry<string> e;
            return name != null && _azPitch.TryGetValue(name, out e) ? e : null;
        }

        public static ConfigEntry<string> AzureSpeedEntry(string name)
        {
            ConfigEntry<string> e;
            return name != null && _azSpeed.TryGetValue(name, out e) ? e : null;
        }

        public static int? AzurePitchFor(string who)
        {
            return ReadPct(AzurePitchEntry(who), PitchMin, PitchMax);
        }

        public static int? AzureSpeedFor(string who)
        {
            return ReadPct(AzureSpeedEntry(who), SpeedMin, SpeedMax);
        }

        internal static int? ReadPct(ConfigEntry<string> e, int min, int max)
        {
            if (e == null || e.Value == null) return null;
            string t = e.Value.Trim().TrimEnd(new[] { '%' }).TrimStart(new[] { '+' });
            if (t.Length == 0) return null;
            int v;
            if (!int.TryParse(t, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out v)) return null;
            return Math.Max(min, Math.Min(max, v));
        }

        // null puts her back on the default.
        public static void SetAzureTuning(ConfigEntry<string> e, int? pct)
        {
            if (e == null) return;
            string v = pct.HasValue
                ? pct.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            if (e.Value != v) e.Value = v;
        }

        // The Azure voice a player set for this character, or null for "her
        // original voice".
        public static string AzureVoiceFor(string who)
        {
            ConfigEntry<string> e = AzureEntry(who);
            if (e == null || e.Value == null) return null;
            string v = e.Value.Trim();
            return v.Length == 0 ? null : v;
        }

        // Which of Names a game character id belongs to - the final-doors and
        // minigame ids resolve to the girl behind them. Null when the id is not a
        // speaking character.
        internal static string BaseNameForId(int? id)
        {
            if (!id.HasValue) return null;
            try
            {
                Type t = HarmonyLib.AccessTools.TypeByName("Character");
                if (t == null || !t.IsEnum) return null;
                string enumName = Enum.GetName(t, id.Value);
                if (string.IsNullOrEmpty(enumName)) return null;
                string who = BaseName(enumName);
                for (int i = 0; i < Names.Length; i++)
                    if (Names[i] == who) return who;
                return null;
            }
            catch (Exception) { return null; }
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
            return Resolve(Identity.CharacterId());
        }

        // Resolves the voice for a specific character ID, falling back to general voice.
        public static string Resolve(int? id)
        {
            string general = Plugin.CfgGrokVoiceId == null ? "" : Plugin.CfgGrokVoiceId.Value;

            try
            {
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
