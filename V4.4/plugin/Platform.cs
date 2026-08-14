// Which build are we running on, and what can it actually do.
//
// One binary ships for both stores, so anything that differs between them has
// to be decided at runtime. Two separate questions are involved and conflating
// them is what caused a wrong diagnosis earlier in this project:
//
//   Which store   - answered by the game's own flag, ServerUriBuilder
//                   .CurrentEnvironment (private static, enum Dev/ItchIO/Steam).
//                   It is baked per build and is what the game itself uses to
//                   pick its API host, so it cannot drift from reality the way
//                   an install-path guess can.
//
//   What works    - answered by asking the assembly, not by inferring it from
//                   the store. The Steam build ships a stripped Overtone:
//                   TTSEngine keeps Loaded/Disposed while Speak, SpeakSamples,
//                   MakeClip and PtrToSamples are gone, dropped by Unity's
//                   managed stripper because nothing in that build's scenes
//                   reaches them. So the local voice is unavailable there.
//
// Behaviour is driven off the capability check, never off the store name. A
// future build could strip differently, or not at all, and the capability check
// would still be right where a store check would quietly be wrong.
using System;
using HarmonyLib;

namespace AI2UCustomAI
{
    internal static class Platform
    {
        public const string Steam = "Steam";
        public const string Itch = "itch.io";

        static string _store;
        static int _localVoice = -1; // -1 unknown, 0 no, 1 yes

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

        // True when this assembly still has the Overtone method that actually
        // synthesises audio. Cloud TTS is unaffected either way.
        public static bool LocalVoiceAvailable
        {
            get
            {
                if (_localVoice >= 0) return _localVoice == 1;
                _localVoice = LocalTtsFix.LocalSynth.SpeakAvailable ? 1 : 0;
                return _localVoice == 1;
            }
        }

        static string ReadStore()
        {
            try
            {
                object v = Traverse.Create(typeof(ServerUriBuilder))
                    .Field("CurrentEnvironment").GetValue();
                if (v == null) return "unknown";

                // The enum type is private, so compare on the name rather than
                // casting to a type this assembly cannot reference.
                string name = v.ToString();
                if (name == "Steam") return Steam;
                if (name == "ItchIO") return Itch;
                return name; // "Dev", or whatever a future build adds.
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Platform: could not read the game's environment flag, "
                    + "so store-specific notes are skipped: " + e.Message);
                return "unknown";
            }
        }

        // One line at startup, so a bug report says which build it came from
        // without anyone having to ask.
        public static void LogSummary()
        {
            string voice = LocalVoiceAvailable
                ? "on-device Overtone voice available"
                : "on-device Overtone voice NOT available (stripped from this build) - cloud TTS only";

            Plugin.Log.LogInfo("Build: " + Store + " - " + voice + ".");

            if (!LocalVoiceAvailable && !GrokTts.Configured)
            {
                Plugin.Log.LogWarning("This build has no on-device voice and no cloud TTS key is set, "
                    + "so she will be silent. Set a TTS key in the F9 panel to give her a voice.");
            }
        }
    }
}
