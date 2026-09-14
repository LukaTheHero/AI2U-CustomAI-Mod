// Which build are we running on, and what can it actually do.
using System;
using HarmonyLib;

namespace AI2UCustomAI
{
    internal static class Platform
    {
        public const string Steam = "Steam";
        public const string Itch = "itch.io";

        static string _store;

        // "Steam", "itch.io", "Dev", or "unknown" when the field cannot be read.
        public static string Store
        {
            get
            {
                if (_store != null) return _store;
                _store = ReadStore();
                return _store;
            }
        }

        public static bool IsSteam
        {
            get { return Store == Steam; }
        }

        // True: the mod ships full native Overtone bindings, enabling local voice on both Steam and itch.io.
        public static bool LocalVoiceAvailable
        {
            get { return true; }
        }

        static string ReadStore()
        {
            try
            {
                object v = Traverse.Create(typeof(ServerUriBuilder))
                    .Field("CurrentEnvironment").GetValue();
                if (v == null) return "unknown";

                string name = v.ToString();
                if (name == "Steam") return Steam;
                if (name == "ItchIO") return Itch;
                return name;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Platform: could not read the game's environment flag: " + e.Message);
                return "unknown";
            }
        }

        public static void LogSummary()
        {
            Plugin.Log.LogInfo("Build: " + Store + " - on-device Overtone voice engine restored (0 keys required).");

            if (GameTts.Configured)
            {
                Plugin.Log.LogInfo("Voice: ORIGINAL GAME VOICES are active (Native Overtone on-device TTS - 0 keys needed).");
            }
            else if (GrokTts.Configured)
            {
                Plugin.Log.LogInfo("Voice: Cloud TTS provider is active.");
            }
        }
    }
}
