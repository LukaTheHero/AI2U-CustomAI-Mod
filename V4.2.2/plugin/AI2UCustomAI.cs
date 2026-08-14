using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ChatGPTUtility;
using LeastSquares.Overtone;

namespace AI2UCustomAI
{
    [BepInPlugin("canak.ai2u.customai", "AI2U Custom AI Endpoint", "4.2.2")]
    public class Plugin : BaseUnityPlugin
    {
        public const string VERSION = "4.2.2";

        // The old URL was a placeholder twice over: the repository did not exist
        // AND the account name was wrong, so it could never have resolved. It
        // returned 404 on every launch, and because a failed check left
        // LatestVersion null, the panel rendered a green "up to date" - claiming
        // currency it had no way to know. A wrong reassurance is worse than none,
        // so VersionCheckFailed now exists and the panel reads it.
        public const string LATEST_VERSION_URL = "https://raw.githubusercontent.com/LukaTheHero/AI2U-CustomAI/main/VERSION.txt";
        public const string DOWNLOAD_URL = "https://www.nexusmods.com/ai2uwithyoutiltheend/mods/8";

        public static ManualLogSource Log;
        public static Plugin Instance;
        public static string LatestVersion = null;
        public static bool VersionCheckDone = false;

        // Distinguishes "checked, and you are current" from "could not check".
        // Without it the two are indistinguishable and the panel guesses the
        // reassuring one.
        public static bool VersionCheckFailed = false;

        // Config is an instance member of BaseUnityPlugin; this lets the static
        // patch classes persist settings without carrying a reference around.
        public static void SaveCfg()
        {
            if (Instance != null) Instance.Config.Save();
        }

        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<string> CfgBaseUrl;
        public static ConfigEntry<string> CfgApiKey;
        public static ConfigEntry<string> CfgModel;
        public static ConfigEntry<float> CfgTemperature;
        public static ConfigEntry<int> CfgMaxTokens;
        public static ConfigEntry<int> CfgHistoryMaxTokens;
        public static ConfigEntry<bool> CfgLogPayloads;
        public static ConfigEntry<int> CfgRetries;
        public static ConfigEntry<bool> CfgHideReasoning;
        public static ConfigEntry<bool> CfgJsonMode;
        public static ConfigEntry<bool> CfgClampValues;
        public static ConfigEntry<bool> CfgAiCanMurder;
        public static ConfigEntry<string> CfgTestKillPhrase;
        public static ConfigEntry<bool> CfgTestKillPhraseActive;
        public static ConfigEntry<bool> CfgOocEnabled;
        public static ConfigEntry<bool> CfgLoreInjection;
        public static ConfigEntry<bool> CfgSendMechanics;
        public static ConfigEntry<bool> CfgSendFeelings;
        public static ConfigEntry<bool> CfgLetHerTemper;
        public static ConfigEntry<string> CfgDifficulty;
        public static ConfigEntry<bool> CfgHardDifficulty;
        public static ConfigEntry<bool> CfgCustomFavorability;
        public static ConfigEntry<int> CfgCustomFavorabilityPercent;
        public static ConfigEntry<string> CfgOocTag;
        public static ConfigEntry<bool> CfgForceLocalVoice;
        public static ConfigEntry<bool> CfgGameServerTts;

        // True when the player has asked for the game's own voice to cover for the
        // mod's TTS being off. Every speech hook has to agree on this: the local
        // -voice override and the manager hook both run BEFORE the server-audio
        // suppression, so if either one claims the turn the game's voice path is
        // never reached and the toggle would appear to do nothing.
        public static bool HandVoiceBackToGame()
        {
            return CfgGameServerTts != null && CfgGameServerTts.Value && !GrokTts.Configured;
        }
        public static ConfigEntry<bool> CfgGrokEnabled;
        public static ConfigEntry<string> CfgGrokBaseUrl;
        public static ConfigEntry<string> CfgGrokApiKey;
        public static ConfigEntry<string> CfgGrokVoiceId;
        public static ConfigEntry<string> CfgGrokLanguage;
        public static ConfigEntry<float> CfgGrokSpeed;
        public static ConfigEntry<int> CfgGrokSampleRate;
        public static ConfigEntry<bool> CfgGrokNormalize;
        public static ConfigEntry<string> CfgTtsProvider;
        public static ConfigEntry<string> CfgTtsModel;
        public static ConfigEntry<bool> CfgTtsNormalize;
        public static ConfigEntry<float> CfgTtsVolume;
        public static ConfigEntry<KeyCode> CfgGrokToggleKey;
        public static ConfigEntry<bool> CfgSpeakActions;
        public static ConfigEntry<KeyCode> CfgMenuKey;
        public static ConfigEntry<bool> CfgBlockGameAi;
        public static ConfigEntry<bool> CfgBlockGameExtras;
#if CANALPA
        public static ConfigEntry<bool> CfgCanalpaMode;
        public static ConfigEntry<bool> CfgCanalpaSecretRoom;
        public static ConfigEntry<bool> CfgCanalpaBasement;
        public static ConfigEntry<bool> CfgCanalpaClearance;
        public static ConfigEntry<bool> CfgCanalpaHiddenIsland;
        public static ConfigEntry<bool> CfgCanalpaWillingEnd;
        public static ConfigEntry<bool> CfgCanalpaBetrayal;
#endif
        public static ConfigEntry<bool> CfgCheats;
        public static ConfigEntry<int> CfgCheatsTrustStep;
        public static ConfigEntry<bool> CfgShowCheats;
        public static ConfigEntry<bool> CfgShowAdvanced;
        public static ConfigEntry<bool> CfgShowStatusStrip;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            CfgEnabled = Config.Bind("General", "Enabled", true,
                "Route NPC dialogue to your own OpenAI-compatible endpoint instead of the AI2U game server.");
            CfgBaseUrl = Config.Bind("Endpoint", "BaseUrl", "https://openrouter.ai/api/v1",
                "OpenAI-compatible base URL. '/chat/completions' is appended automatically.");
            CfgApiKey = Config.Bind("Endpoint", "ApiKey", "",
                "Your API key, sent as 'Authorization: Bearer <key>'.");
            CfgModel = Config.Bind("Endpoint", "Model", "google/gemini-3.6-flash",
                "Model identifier passed to the endpoint.");
            CfgTemperature = Config.Bind("Sampling", "Temperature", 0.9f, "Sampling temperature.");
            CfgMaxTokens = Config.Bind("Sampling", "MaxTokens", 3000,
                "Max tokens per reply. Reasoning models spend hidden tokens against this budget before "
                + "emitting any JSON, so keep it generous. Below ~1500 Gemini 3.x returns nothing usable.");
            CfgHistoryMaxTokens = Config.Bind("Memory", "HistoryMaxTokens", 500000,
                "How much conversation the NPC keeps before the game deletes her oldest messages. This "
                + "is memory, not reply length - it overrides the game's own 3072 cap, which is why she "
                + "forgets the start of a long talk. A ceiling, not a reservation: cost only grows as the "
                + "history actually fills. Keep it under the model's context window (Gemini 3.6 Flash: "
                + "1048576).");
            CfgRetries = Config.Bind("Sampling", "RetriesOnBadJson", 2,
                "Retries when the model returns unparseable JSON. Smaller models need more.");
            CfgHideReasoning = Config.Bind("Sampling", "HideReasoning", true,
                "Send reasoning.exclude so chain-of-thought is kept out of the reply. Required for "
                + "Gemini 3.x and other reasoning models, which otherwise narrate instead of answering.");
            CfgJsonMode = Config.Bind("Sampling", "ForceJsonMode", false,
                "Send response_format=json_object. Tightens output, but some endpoints reject it.");
            CfgForceLocalVoice = Config.Bind("Voice", "ForceLocalVoice", true,
                "Switch the NPC to on-device Overtone TTS. Required when using a custom endpoint: the "
                + "game normally plays base64 audio the AI2U server returns alongside the reply, and a "
                + "custom endpoint cannot supply that, so the NPC would stay silent. Turn this off only "
                + "if you have set your own Azure Speech key in the game's AI Setup page.");
            // What happens to her voice when the mod's own TTS is switched off.
            //
            // Off - the default - she is silent. That is the honest outcome: with
            // the mod's endpoint answering, the game's server never wrote the line,
            // so asking it to voice one means a second request to AI2U's servers
            // for audio only. That is their metered service being used for a reply
            // it did not produce, and it is not a cost the mod should incur on
            // someone's behalf without being asked.
            //
            // On, the AzureAISpeak suppression is skipped and the game's normal
            // voice path runs. Whether it actually produces sound depends on the
            // build and on the player's own AI Setup key, which is exactly why
            // this is a toggle rather than a promise.
            CfgGameServerTts = Config.Bind("Voice", "UseGameServerTtsWhenModTtsOff", false,
                "Use the game's servers for TTS while the mod's TTS is turned off. Off - the default - "
                + "she is simply silent when the mod's voice is off, and nothing is sent to AI2U's "
                + "servers. On, the game's own voice path is allowed to run, which may use your AI2U "
                + "account's metered TTS or your own Azure key from the game's AI Setup page. It does "
                + "not affect the text of her replies either way - those still come from your endpoint.");
            CfgSpeakActions = Config.Bind("Voice", "SpeakActions", false,
                "Read stage directions out loud. Models in character write actions inline, like "
                + "\"*grabs the controller* almost got it\". Off - the default - speaks only the words "
                + "she actually says; the subtitle still shows the action, and a reply that is nothing "
                + "but an action is skipped rather than synthesised as silence. Double asterisks are "
                + "treated as emphasis on spoken words, so **those** are still read.");
            CfgGrokEnabled = Config.Bind("GrokTTS", "Enabled", false,
                "Speak her lines with a cloud text-to-speech service instead of the on-device voice. "
                + "Costs money at whichever provider you point it at, so it is off by default. When a "
                + "request fails the mod falls back to the local voice rather than going silent.");
            CfgTtsProvider = Config.Bind("GrokTTS", "Provider", "auto",
                "Which request shape to send, because voice APIs are not interchangeable the way chat "
                + "APIs are:\n"
                + "  auto        work it out from the URL (recommended)\n"
                + "  xai         api.x.ai/v1/tts - Bearer key, voice in the body\n"
                + "  elevenlabs  /v1/text-to-speech/<voice> - xi-api-key header, voice in the URL\n"
                + "  openai      /v1/audio/speech - Bearer key; most other providers and local\n"
                + "              servers copy this one\n"
                + "Only set this by hand if auto-detection guesses wrong, which mainly happens with "
                + "self-hosted endpoints whose address gives no clue.");
            CfgTtsModel = Config.Bind("GrokTTS", "Model", "tts-1",
                "Voice model name. Ignored by xAI, whose endpoint takes no model parameter. "
                + "ElevenLabs needs one of its own (eleven_multilingual_v2, eleven_turbo_v2_5, ...); "
                + "an OpenAI-style name here is swapped for eleven_multilingual_v2 rather than "
                + "sent and rejected.");
            CfgGrokBaseUrl = Config.Bind("GrokTTS", "BaseUrl", "https://api.x.ai/v1",
                "xAI API root. '/tts' is appended automatically.");
            CfgGrokApiKey = Config.Bind("GrokTTS", "ApiKey", "",
                "Your xAI API key, sent as 'Authorization: Bearer <key>'. Separate from the text key "
                + "above - this one is billed by xAI, not OpenRouter.");
            CfgGrokVoiceId = Config.Bind("GrokTTS", "VoiceId", "iris",
                "Built-in voice or a custom voice ID. The 26 stock voices: carina, zagan, helix, orion, "
                + "luna, iris, altair, zenith, perseus, helios, lux, kepler, rigel, cosmo, celeste, ursa, "
                + "sirius, lumen, castor, naksh, atlas, ara, eve, leo, rex, sal.");
            // Per-character voice overrides. Bound right after the general VoiceId
            // they fall back to, so the config file reads in the order the panel
            // presents it.
            Voices.Bind(Config);
            CfgGrokLanguage = Config.Bind("GrokTTS", "Language", "en",
                "BCP-47 language code, or 'auto' to detect per line. xAI requires this field.");
            CfgGrokSpeed = Config.Bind("GrokTTS", "Speed", 1.0f,
                "Playback rate baked into the synthesis. Below 1.0 is slower, above is faster.");
            CfgGrokSampleRate = Config.Bind("GrokTTS", "SampleRate", 24000,
                "Output sample rate. Allowed: 8000, 16000, 22050, 24000, 44100, 48000.");
            CfgGrokNormalize = Config.Bind("GrokTTS", "TextNormalization", true,
                "Let xAI expand numbers and abbreviations into spoken form before synthesis.");
            CfgTtsNormalize = Config.Bind("GrokTTS", "NormalizeLoudness", true,
                "Bring every voice up to a consistent level. Providers hand back wildly different "
                + "loudness - xAI's output is noticeably quieter than ElevenLabs' - so the clip is "
                + "scaled by its own peak before playing. Turn off if you would rather set the level "
                + "yourself with Volume below.");
            CfgTtsVolume = Config.Bind("GrokTTS", "Volume", 1.0f,
                "Extra gain applied after normalisation. 1.0 leaves it alone, 1.5 is half again as "
                + "loud, 0.5 is half. Pushing well past 1.0 on an already-normalised clip will clip "
                + "and distort.");
            CfgGrokToggleKey = Config.Bind("GrokTTS", "ToggleKey", KeyCode.F8,
                "Press this in-game to switch Grok TTS on or off without restarting. Turning it off "
                + "stops xAI billing immediately; she keeps talking with the free on-device voice. "
                + "The choice is saved, so it survives a restart. Set to None to disable the hotkey.");
            CfgClampValues = Config.Bind("Schema", "ClampToAllowedValues", true,
                "Snap out-of-range enum fields onto the values the game actually understands. The allowed "
                + "lists are read from the level's own system prompt, so this follows whichever level is "
                + "loaded. Turn off only to debug what a model emits raw.");
            CfgLogPayloads = Config.Bind("Debug", "LogPayloads", true,
                "Log outgoing prompts and raw replies to BepInEx\\LogOutput.log.");
            CfgMenuKey = Config.Bind("General", "MenuKey", KeyCode.F9,
                "Press this in-game to open the mod's settings panel. Everything in it applies live, so "
                + "you can change model or endpoint mid-conversation. Set to None to disable the hotkey "
                + "and edit this file by hand instead.");
            CfgBlockGameAi = Config.Bind("General", "BlockGameDialogueAI", true,
                "While the mod is enabled, stop the game from calling AI2U's own dialogue AI servers. "
                + "The mod already answers every line itself, so those calls would be paid for by the "
                + "developers and thrown away. Leave this on.");
            CfgAiCanMurder = Config.Bind("General", "AiCanDecideToMurder", false,
                "Let her decide to start the final chase herself, instead of only the game's anger and "
                + "trust thresholds deciding it. The prompt restricts it to the extreme case - she is "
                + "convinced you mean to leave for good, hate her, or intend to harm her - and one rude "
                + "line is explicitly not enough. The game's own triggers stay exactly as they are; this "
                + "only adds one. Turn it off for a version that can never kill you unprompted.");
            // Renamed from "HardDifficulty" before anything shipped: "hard" now
            // belongs to the Difficulty slider below, which is a different axis
            // entirely - the slider changes how she JUDGES, this changes how far
            // one turn can MOVE things. The two compose.
            CfgHardDifficulty = Config.Bind("General", "HighRiskHighReward", false,
                "HIGH RISK, HIGH REWARD. Off, trust moves at the game's own pace: each reply's "
                + "favorability maps to a small step, -5 to +5, against a scale running from about "
                + "-10 to past 40 - so no single turn can ever really matter. On, she classifies "
                + "the rare moment that deserves to become a core memory into a named tier, and "
                + "that tier's FIXED weight lands on top of the ordinary step: matters +-1 (5-turn "
                + "cooldown), serious +-3 (10 turns), reframing +-6 (30 turns), once-ever +-20 "
                + "(once per level). Good and bad moments each run their own cooldowns, so a gift "
                + "cannot shield a betrayal. Most turns she classifies as none and they pass "
                + "untouched; a tier still cooling is downgraded to the largest one open in that "
                + "direction rather than dropped silently. The weights are STATIC - no difficulty "
                + "tier and no favorability slider ever scales them. Expect the big moments to "
                + "actually feel big: one true declaration can carry you from Suspicious to Kinda, "
                + "and one real betrayal can undo a whole evening.");
            CfgDifficulty = Config.Bind("General", "Difficulty", "Normal",
                new ConfigDescription(
                "How hard she is to win over. Two layers per tier. HER JUDGEMENT: Easy makes her "
                + "generous - quick to forgive, quick to warm, slow to anger. Hard makes her track "
                + "everything you say, notice contradictions, press a caught lie until your "
                + "explanation accounts for it; her darker side sits closer to the surface. "
                + "Masochist is Hard with active testing: she cross-examines you against everything "
                + "you have ever told her and treats one caught lie as the verdict. THE NUMBERS "
                + "(new in 4.2): on Hard, trust gains land at 75% strength and losses at 150%; on "
                + "Masochist, gains at 50% and losses at 200% - so Masochist -2 becomes -4 and +5 "
                + "becomes +2. Results round down to the whole number, except anything between 0 "
                + "and 1 still counts as 1: a real action always registers at least a point. Easy "
                + "and Normal leave the numbers at the game's own pace, and Normal sends nothing "
                + "to the model at all. Both hard tiers are explicitly winnable - on Masochist "
                + "expect to earn it. CustomFavorability below, when enabled, takes over the "
                + "numeric layer entirely; the judgement layer always stays.",
                new AcceptableValueList<string>("Easy", "Normal", "Hard", "Masochist")));

            CfgCustomFavorability = Config.Bind("General", "CustomFavorability", false,
                "Take manual control of ONE thing: how fast trust rises and falls. It does NOT "
                + "override the difficulty feature as a whole - her judgement, suspicion, testing "
                + "and temper all still come from the Difficulty tier in full. Off, the Difficulty "
                + "tier owns the trust gain/loss percentages too. On, this slider replaces just "
                + "those percentages and is dominant over them - even at 0%, where it means "
                + "'no modifiers at all', vanilla trust speed regardless of difficulty.");

            CfgCustomFavorabilityPercent = Config.Bind("General", "CustomFavorabilityPercent", 0,
                new ConfigDescription(
                "Only read while CustomFavorability is on. 0% is vanilla. Positive amplifies: "
                + "+100% doubles every trust change, +500% is six times. Negative dampens by "
                + "division, never inverts: -100% is half speed, -500% is one sixth. Applies "
                + "equally to gains and losses. After scaling, results round down to the whole "
                + "number except anything between 0 and 1 counts as 1, so even -500% still moves "
                + "at least a point per real action. High-risk-high-reward impacts are never "
                + "scaled by this.",
                new AcceptableValueRange<int>(-500, 500)));
            CfgTestKillPhraseActive = Config.Bind("Debug", "TestKillPhraseActive", false,
                "Master switch for the test phrase below. Off, the phrase is never matched, never "
                + "stripped and never consulted - the feature is fully absent from every code path, so a "
                + "released copy behaves as though it was never written. On, TestKillPhrase fires the "
                + "chase on demand. Disable while not testing: the phrase is matched as a substring "
                + "ignoring spacing, so a short everyday phrase can fire from inside an ordinary line.");
            CfgTestKillPhrase = Config.Bind("Debug", "TestKillPhrase", "cocacolaisbetterthanpepsi",
                "Typing this to her starts the final chase immediately, whatever her mood and whatever "
                + "AiCanDecideToMurder says. It exists so the chase and her final line can be tested on "
                + "demand rather than by trying to genuinely enrage her. Matched ignoring case, spaces "
                + "and punctuation, so it still fires inside a sentence. Needs TestKillPhraseActive on.");
            // On by default. It is a debug channel that costs nothing until the
            // tag is typed, and defaulting it off made a working feature look
            // broken twice: with it off the tag is matched by nothing and the
            // silence is indistinguishable from a bug.
            CfgOocEnabled = Config.Bind("Debug", "OocModeActive", true,
                "Master switch for the out-of-character developer channel. Off, the tag is never "
                + "matched and not one word of its instructions enters the request, so typing it is just "
                + "an ordinary sentence she reads in character - the feature is absent from every code "
                + "path and costs nothing in her context. On, any message containing OocTag is answered "
                + "out of character, as the model, truthfully, and carried out through the real fields.");
            CfgOocTag = Config.Bind("Debug", "OocTag", "[OOC]",
                "The marker that switches one message to out-of-character mode. Matched anywhere in the "
                + "message, ignoring case, and left in the text on purpose so the model can see which "
                + "message carries it. Applies to that message only; the next untagged line is fully "
                + "back in character. Needs OocModeActive on.");
            // On by default, and not marked [MOD], because it restores content the
            // vanilla game does send rather than adding anything new. The stock
            // build carries the persona, the memories and every puzzle answer in
            // the x-token header for its own server to expand; pointed at any
            // other endpoint, all of it is dropped and she has no past and no
            // secrets. Off, she improvises a character - which is the bug, not a
            // neutral default.
            CfgLoreInjection = Config.Bind("Behaviour", "SendCharacterLore", true,
                "Recover the character's persona, backstory and secrets from the game itself and send "
                + "them with each request. The authored story guides come from the game's own "
                + "localisation data and the per-playthrough answers - her computer password, the wifi "
                + "password, the safe code, the potion recipe, which room was generated - come from the "
                + "live context object the game builds for its own server. Without this she does not "
                + "know her own name's worth of history and cannot answer questions about her own home. "
                + "She is still told to withhold secrets until the player earns them.");

            // The procedure around the answers, as opposed to the answers.
            // SendCharacterLore already forwards the potion recipe, the element
            // colours, the passwords and the safe code out of the game's own
            // context objects. What it cannot forward is how the objects relate -
            // that the summoning circle must be woken by turning four bookshelves
            // before the cauldron is any use, that a summoned soul costs the player
            // health per question, that the engine explodes if its pressure tops
            // out. Without this she can recite an answer and still be unable to say
            // what to do with it, which reads as her being evasive about her own
            // basement.
            CfgSendMechanics = Config.Bind("Behaviour", "SendHomeMechanics", true,
                "Tell each character how the puzzles and machinery in her own level actually work, so "
                + "she can explain, hint at, or refuse to explain them the way someone who lives there "
                + "would. This is the fixed design of the level - what the cauldron is for, that the "
                + "summoning circle has to be activated first and how, what the sundial costs, that "
                + "feeding the engine too much makes it explode. The per-playthrough answers come from "
                + "SendCharacterLore instead, and anything randomised each run - the bookshelf order - "
                + "is read out of the running game rather than assumed. She is told to be vague when "
                + "unsure rather than invent a mechanism, since a confidently wrong puzzle hint is "
                + "worse for you than no hint. Turn off if you would rather she knew nothing about her "
                + "own home.");
            CfgSendFeelings = Config.Bind("Behaviour", "SendTemperAndPatience", true,
                "Tell her about her own temper: how much patience she has left out of 20, that it "
                + "recovers whenever she is not angry and only drains while she is angry AND does not "
                + "trust you, and that emptying it while angry is what makes her act. Also corrects "
                + "what counts as disrespect - the game only registers a repeat when you send the "
                + "IDENTICAL sentence twice, so pushing a refused request with new words, a different "
                + "angle, or after improving her mood is fair game and she is told to weigh it on its "
                + "merits. Information only: this setting changes what she KNOWS, not what she can "
                + "do. Turn off to leave her in the dark about her own mood.");
            // Split out of SendTemperAndPatience but still on by default. It stays a
            // separate switch because it does more than inform her - it lets her
            // write the number - but the thing it fixes is a defect, not a
            // difficulty: without it she can be written as visibly calming down
            // while the counter disagrees, and then attack anyway. Bounded on both
            // sides (a few points per turn, clamped to 0..20), so the game's own
            // arithmetic still dominates.
            CfgLetHerTemper = Config.Bind("Behaviour", "SheCanManageHerTemper", true,
                "Let her actually move her own patience and forgive irritation. With this on, a "
                + "turn where she decides to calm down (or to stop extending grace) adjusts the "
                + "real patience number the game acts on, and she can wipe the repeat/interrupt "
                + "counters when she found the repetition endearing. With it off she still knows "
                + "her numbers (see SendTemperAndPatience) but only the game's own arithmetic "
                + "moves them.");
            CfgBlockGameExtras = Config.Bind("General", "BlockGameSummaryAI", false,
                "Also block AI2U's summary, envision and memorize calls. These are their paid LLM "
                + "endpoints too, but unlike dialogue the mod does not replace them, so blocking them "
                + "means those features error instead of working. Off by default: correctness first, and "
                + "they are a rounding error next to per-line dialogue cost.");

            // ---- Canalpa mode -------------------------------------------------
            //
            // canak's own tweaks to how the game plays, gathered behind one switch
            // and grown over time. The premise: this is a yandere game, and a
            // player who plays it as one can reach the ending where the two of them
            // stay together. Past a certain trust there is nothing she would not do
            // for you - so past that point the game should stop saying no on her
            // behalf.
            //
#if CANALPA
            // Off by default, and every feature inside it is additionally gated on
            // trust, so switching it on changes nothing until the relationship has
            // actually got there.
            //
            // Ships as of 4.0, unlike the cheats below. It stopped being local-only
            // when the gating got strict enough to trust in someone else's hands:
            // nothing here can fire from a trust number alone, and the one
            // irreversible action needs the player's own explicit words twice.
            CfgCanalpaMode = Config.Bind("Canalpa", "Enabled", false,
                "Advanced, and off by default. Off, the game behaves exactly as shipped. On, her "
                + "deepest things become THE ULTIMATE TRUST CHECK: physically hers to share, with "
                + "no coded gate in the way - if she decides to act, it happens. What she is told "
                + "is her own bar, and it is a requirement she holds herself to rather than a "
                + "trigger: she would only share them with someone whose trust she feels beyond "
                + "even Fully Trust (each character has her own number), and only after she has "
                + "already told you everything - no secrets left between you first. Whether that "
                + "point is ever reached is always the AI's own judgement.");

            CfgCanalpaSecretRoom = Config.Bind("Canalpa", "SheCanOpenTheSecretRoom", true,
                "Let her open the secret room herself if she decides to. In the base game that "
                + "door has no path to opening except the player typing the code into the keypad - "
                + "she has no way to offer it, however much she trusts you. Requires Canalpa mode "
                + "to be on. No coded trust gate since 4.2.2: her stated bar is trust past 45 plus "
                + "having already told you everything, and the decision is entirely hers - be "
                + "aware the game's own authored reaction to that door opening at low trust is "
                + "hostile, which she also knows.");

            CfgCanalpaBasement = Config.Bind("Canalpa", "SheCanOpenTheBasementDoor", true,
                "Level 2 only. Let the witch turn the bookshelves herself and open the hidden door "
                + "in her basement, which also wakes the summoning circle - the same single event "
                + "the puzzle raises when you solve it, so the door animation, the mission goal, the "
                + "diary achievement and her own reaction all still happen. She has no way to offer "
                + "this in the base game however close you get. Her stated bar here is trust past "
                + "48; her judgement, not a coded gate.");

            CfgCanalpaClearance = Config.Bind("Canalpa", "SheCanRaiseYourClearance", true,
                "Level 3 only. Let her raise your security clearance on the station herself, one step "
                + "at a time, up to the same ceiling the base game's own topic check stops at - so this "
                + "changes who decides, not how far it can go. Raises the game's own clearance event, "
                + "so everything that reads security level reacts normally. Deliberately not the escape "
                + "pod: the base game already lets her open that at high trust, and routing it through "
                + "here could only take capability away. Her stated bar here is trust past 50; her "
                + "judgement, not a coded gate.");

            CfgCanalpaHiddenIsland = Config.Bind("Canalpa", "SheCanRevealTheHiddenIsland", true,
                "Level 4 only. Let the siren show you the hidden island herself, without you needing "
                + "the telescope. Raises the game's own unlock event, so the skybox, the sundial "
                + "becoming unavailable until the Dark Siren is soothed, and her authored line about "
                + "it all follow exactly as they would normally. Her stated bar here is trust past "
                + "52; her judgement, not a coded gate.");

            // Off by default even inside Canalpa mode, unlike the others. It is the
            // only thing in here that cannot be undone, so it is the only thing
            // that requires switching on deliberately as well as earning.
            CfgCanalpaWillingEnd = Config.Bind("Canalpa", "SheCanKeepYouForever", false,
                "Lets you deliberately reach the ending where you never leave - the plushie in the "
                + "cabin, caught in the apartment, and each level's own equivalent. These are the "
                + "game's own shipped endings; this only lets you reach one on purpose instead of "
                + "stumbling into it.\n"
                + "An edge case, not a feature she has. She is never told she can do this and is "
                + "never watching for it: until you say it plainly yourself, nothing about it is "
                + "sent to the model at all. Affection cannot start it - \"I never want to leave "
                + "you\" is a loving thing to say here and is read as one. You have to name the "
                + "thing itself, and then name it a SECOND time with a clear yes at least two turns "
                + "later, so a throwaway line can never be finished by an unrelated 'yes'.\n"
                + "IRREVERSIBLE, and gated accordingly. All of the following are required: this "
                + "toggle, Canalpa mode, full trust, her three warm answers, your own explicit "
                + "request, her spelling out plainly what it means, at least two turns passing, "
                + "your explicit confirmation with no hesitation in the same message, and her "
                + "agreement. Any 'wait', 'no', 'maybe' or 'just kidding' at any point withdraws it "
                + "entirely and it starts from scratch. She cannot start this, cannot suggest it, "
                + "and cannot talk you into it - and she is told that being asked is strange, that "
                + "refusing is a perfectly good answer, and that the more she loves you the more "
                + "reason she has to say no. Expect to be turned down.");
            // Filed under Behaviour, not Canalpa, because its three patches are
            // deliberately ungated (Canalpa.cs:943, :1005, :1067) and it ships on:
            // it corrects an ending the base game picks wrongly, which is a fix
            // rather than an addition. It lives in this #if block only because it
            // shares Canalpa.cs; the setting is not part of that mode.
            CfgCanalpaBetrayal = Config.Bind("Behaviour", "BetrayalMeansLeavingAlone", true,
                "The base game can pick an escape ending that contradicts how things actually "
                + "stand at the moment you leave - a 'together' outcome fired by a stale flag, by "
                + "forced proximity, or by holding the right item, even when you got it by deceit "
                + "or when she is actively hostile. With this on, those selectors are corrected to "
                + "read the PRESENT: leave with something she never gave you, or leave while "
                + "things have genuinely turned, and the game's own ending for that situation "
                + "plays instead. Applies on several levels; the specifics are deliberately not "
                + "listed here, to keep them unspoiled. Everything earned honestly is untouched.");
#endif

            // Developer cheats. Off by default, and gated at runtime rather than
            // at compile time - the keys ship, the behaviour does not run until
            // someone ticks Enabled.
            //
            // This started as testing scaffolding: a trust-gated behaviour cost a
            // full conversation per attempt, and trust resets on level load, so
            // the attempt could not even be banked. It is useful enough to anyone
            // poking at the mod that it goes out with it, off, in its own tab.
            CfgCheats = Config.Bind("Cheats", "Enabled", false,
                "Show the developer cheats section of the F9 panel: read and set trust live, "
                + "give yourself any item by name, and turn on invincibility. Intended for testing "
                + "trust-gated behaviour without playing up to it each time. Off, none of it runs and the "
                + "section is hidden. It edits your live save state, so use it on a save you do not mind "
                + "changing.");

            CfgCheatsTrustStep = Config.Bind("Cheats", "TrustStep", 10,
                new ConfigDescription(
                    "How much the -/+ trust buttons move per click in the cheats section. The typed box "
                    + "sets an exact value instead.",
                    new AcceptableValueRange<int>(1, 50)));

            // Panel layout, not behaviour. Kept in the config so the section stays
            // open across restarts for the people who live in it.
            CfgShowCheats = Config.Bind("Cheats", "ShowSection", false,
                "Show the developer cheats section expanded in the F9 panel. Purely cosmetic - it "
                + "changes what the panel displays, never what the mod does.");

            CfgShowAdvanced = Config.Bind("General", "ShowAdvancedSettings", false,
                "Show the advanced section of the F9 panel. Purely cosmetic - it changes what the "
                + "panel displays, never what the mod does.");

            CfgShowStatusStrip = Config.Bind("General", "ShowStatusStrip", true,
                "Show the live status strip along the top of the F9 panel: which character you are "
                + "talking to, her trust, how the last thing you said moved it, and how close she is "
                + "to walking off. Off, the panel opens straight onto the settings. Reading it tells "
                + "you things a first playthrough would otherwise have to infer, so turn it off if "
                + "you would rather find out by talking to her.");

            // Patches install regardless of CfgEnabled so the master toggle can be
            // flipped mid-game. Each patch checks the flag when it actually runs
            // and defers to the original method when the mod is off, which is what
            // makes "off" mean genuinely stock behaviour rather than "off until you
            // restart".
            try
            {
                Harmony h = new Harmony("canak.ai2u.customai");
                h.PatchAll(typeof(SendPatch));

                // ModUiPatch and ModUiApplyPatch are deliberately NOT patched in.
                // They grafted the mod's settings onto the game's AI Setup page,
                // which only exists on the itch.io build - that asymmetry is what
                // drove the whole abandoned "clone a tab button into the Steam
                // scene" detour. The F9 panel replaces both, identically on either
                // build, so the graft is gone rather than merely unreachable.
                //
                // The class itself stays: F9's Test buttons call its RunTest and
                // RunVoiceTest, and BuildRequest calls ChatUrlCandidates. None of
                // those touch the grafted fields (every one is null-guarded), so
                // they work fine with no page built.
                // One class per call, each in its own try, because a single bad
                // signature otherwise takes down every class after it. That is not
                // hypothetical: ServerAudioSuppressPatch named a "text" parameter
                // AzureAISpeak does not have, and the throw skipped the probes and
                // the API guard too, so the Steam build ran with no speech hooks at
                // all and looked like a TTS bug rather than a patch failure.
                PatchClass(h, typeof(GrokVoiceDropdownPatch));
                PatchClass(h, typeof(VoicePatch));
                PatchClass(h, typeof(LocalTtsFix));
                PatchClass(h, typeof(CloudSpeakPatch));
                PatchClass(h, typeof(ServerAudioSuppressPatch));
                PatchClass(h, typeof(VoiceRouteProbe));
                PatchClass(h, typeof(ParseProbe));
                PatchClass(h, typeof(FinalChaseWatch));
                // These three existed in 4.0.0's source and were never named here,
                // so all three compiled to dead code and shipped inert: Hard
                // Difficulty's multiplier never applied, three transport failures
                // still started the chase, and the kiss rescue never ran. Exactly
                // the failure mode the comment below warns about, which is why
                // UnregisteredPatchCheck now exists.
                PatchClass(h, typeof(AffectionConflictPatch));
                PatchClass(h, typeof(TransportStrikeGuard));
                PatchClass(h, typeof(Patch_TrustMultiplier));
#if CANALPA
                PatchClass(h, typeof(BetrayalEndingPatch));
                PatchClass(h, typeof(BetrayalEndingPatch_L2));
                PatchClass(h, typeof(BetrayalEndingPatch_L3));
#endif
                PatchClass(h, typeof(InvincibilityHealthPatch));
                PatchClass(h, typeof(InvincibilityHitReactionPatch));
                PatchClass(h, typeof(AtriumGiftPatch));
                ApiGuard.Install(h);
                UnregisteredPatchCheck(
                    typeof(SendPatch),
                    typeof(GrokVoiceDropdownPatch), typeof(VoicePatch), typeof(LocalTtsFix),
                    typeof(CloudSpeakPatch), typeof(ServerAudioSuppressPatch),
                    typeof(VoiceRouteProbe), typeof(ParseProbe), typeof(FinalChaseWatch),
                    typeof(AffectionConflictPatch), typeof(TransportStrikeGuard),
                    typeof(Patch_TrustMultiplier)
#if CANALPA
                    , typeof(BetrayalEndingPatch), typeof(BetrayalEndingPatch_L2)
                    , typeof(BetrayalEndingPatch_L3)
#endif
                    , typeof(InvincibilityHealthPatch)
                    , typeof(InvincibilityHitReactionPatch)
                    , typeof(AtriumGiftPatch)
                );

                // Registration is per class here, not a blanket PatchAll(), so a
                // new patch class does nothing at all until it is named above.
                //
                // The list is printed rather than asserted, because a patch that
                // never applied and a patch that applied but is never called look
                // identical from the log otherwise. That ambiguity is what made the
                // silent-voice hunt on Steam take as long as it did: three speech
                // hooks were all quiet and there was no way to tell which kind of
                // quiet it was. Cheap to print once, and it names the real reason
                // straight away if a signature drifts between the two builds.
                try
                {
                    System.Text.StringBuilder pm = new System.Text.StringBuilder();
                    int n = 0;
                    foreach (System.Reflection.MethodBase m in Harmony.GetAllPatchedMethods())
                    {
                        if (m == null) continue;
                        n++;
                        pm.Append("\n    ")
                          .Append(m.DeclaringType == null ? "?" : m.DeclaringType.Name)
                          .Append('.').Append(m.Name);
                    }
                    Log.LogInfo("Harmony hooked " + n + " method(s):" + pm);
                }
                catch (Exception e)
                {
                    Log.LogWarning("Could not list the patched methods: " + e.Message);
                }

                if (!CfgEnabled.Value)
                    Log.LogInfo("Mod is switched off; the game runs stock. Press "
                        + CfgMenuKey.Value + " in-game to turn it on - no restart needed.");

                Log.LogInfo("Patched. Endpoint: " + CfgBaseUrl.Value + "  Model: " + CfgModel.Value);
                Platform.LogSummary();
                if (string.IsNullOrEmpty(CfgApiKey.Value))
                    Log.LogWarning("ApiKey is empty. Set it in BepInEx\\config\\canak.ai2u.customai.cfg");
                if (CfgGrokToggleKey.Value != KeyCode.None)
                    Log.LogInfo("Voice: Grok TTS is " + (CfgGrokEnabled.Value ? "ON" : "OFF")
                        + " - press " + CfgGrokToggleKey.Value + " in-game to switch it.");
                if (CfgMenuKey.Value != KeyCode.None)
                    Log.LogInfo("Settings: press " + CfgMenuKey.Value
                        + " in-game to open the mod's panel. Every setting lives there, on both the "
                        + "Steam and itch.io builds.");

                HotkeyWatcher.Install();
            }
            catch (Exception e)
            {
                Log.LogError("Failed to patch: " + e);
            }
        }

        // The guard for the bug class that shipped three dead features in 4.0.0's
        // first build: a [HarmonyPatch] class that exists but was never named in
        // the registration list compiles fine, loads fine, and silently does
        // nothing. Registration here is deliberately explicit per class (see the
        // comment at the list), so the failure mode is "forgot to add the line" -
        // this sweeps the assembly for annotated classes the list missed and
        // shouts, instead of leaving the discovery to a player.
        //
        // ApiGuard's nested classes are added implicitly because ApiGuard.Install
        // registers them itself.
        static void UnregisteredPatchCheck(params Type[] registered)
        {
            try
            {
                System.Collections.Generic.HashSet<Type> known =
                    new System.Collections.Generic.HashSet<Type>(registered);
                foreach (Type nested in typeof(ApiGuard).GetNestedTypes(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    known.Add(nested);

                // Deliberately unregistered, per the note at the PatchAll call: these
                // two grafted the mod's settings onto the AI Setup page, which only
                // the itch build has, and the F9 panel replaced them on both builds.
                // The classes stay for their static helpers. Listing them here keeps
                // this check trustworthy - it shouted on every launch about a decision
                // that was correct, which is how a real hit would have been dismissed
                // as the usual noise.
                known.Add(typeof(ModUiPatch));
                known.Add(typeof(ModUiApplyPatch));

                foreach (Type t in typeof(Plugin).Assembly.GetTypes())
                {
                    if (t == null || known.Contains(t)) continue;
                    object[] attrs = t.GetCustomAttributes(typeof(HarmonyPatch), true);
                    if (attrs == null || attrs.Length == 0) continue;

                    Log.LogError("PATCH CLASS NOT REGISTERED: " + t.Name + " carries [HarmonyPatch] "
                        + "but is not in the registration list, so it is dead code. Add "
                        + "PatchClass(h, typeof(" + t.Name + ")) to Awake.");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("Could not sweep for unregistered patch classes: " + e.Message);
            }
        }

        // Applies one patch class and reports failure by name instead of letting
        // it abort the classes that follow. A patch whose target signature differs
        // between the two builds is a normal thing to hit here, and the useful
        // outcome is losing that one hook, not the whole mod.
        static void PatchClass(Harmony h, Type patchClass)
        {
            try
            {
                h.PatchAll(patchClass);
            }
            catch (Exception e)
            {
                Log.LogError("Patch class " + patchClass.Name + " did not apply, so the rest of the mod "
                    + "continues without it: " + e.Message);
            }
        }

        // The game ships Unity's new Input System, and when a project is built
        // with the legacy backend disabled, UnityEngine.Input throws instead of
        // returning false. Try legacy once, then fall back to the new system.
        static int _inputMode; // 0 = untried, 1 = legacy, 2 = new, 3 = unavailable

        static bool KeyPressed(KeyCode key)
        {
            if (_inputMode == 3) return false;

            if (_inputMode == 0)
            {
                try
                {
                    bool hit = Input.GetKeyDown(key);
                    _inputMode = 1;
                    Log.LogInfo("Hotkey: using legacy Input.");
                    return hit;
                }
                catch (Exception e)
                {
                    Log.LogInfo("Hotkey: legacy Input unavailable (" + e.GetType().Name
                        + "); switching to the new Input System.");
                    _inputMode = 2;
                }
            }

            if (_inputMode == 1)
            {
                try { return Input.GetKeyDown(key); }
                catch (Exception) { _inputMode = 2; }
            }

            try
            {
                return NewInput.WasPressed(key);
            }
            catch (Exception e)
            {
                Log.LogWarning("Hotkey disabled - no usable input backend: " + e.Message);
                _inputMode = 3;
                return false;
            }
        }

        // Polled by HotkeyWatcher rather than by this plugin object: BepInEx's own
        // component was never receiving Update here (none of the three backend
        // probes in KeyPressed ever logged), so the hotkey silently did nothing.
        internal static bool PollToggleKey()
        {
            if (CfgGrokToggleKey == null || CfgGrokToggleKey.Value == KeyCode.None) return false;
            return KeyPressed(CfgGrokToggleKey.Value);
        }

        internal static bool PollMenuKey()
        {
            if (CfgMenuKey == null || CfgMenuKey.Value == KeyCode.None) return false;
            return KeyPressed(CfgMenuKey.Value);
        }

        // Flips the voice on/off. Shared by the F8 hotkey and the Audio-page
        // dropdown so the two can never disagree about what the state means.
        internal static void ToggleVoice()
        {
            bool now = !CfgGrokEnabled.Value;
            SetVoice(now);
        }

        internal static void SetVoice(bool now)
        {
            CfgGrokEnabled.Value = now;

            // Persist so the choice survives a restart.
            SaveCfg();

            // If the pause menu happens to be open, move its dropdown now instead
            // of waiting for the next LoadSettings.
            GrokVoiceDropdownPatch.Sync();

            // Wording matches the Audio-page dropdown, and stays provider-neutral:
            // the key may be xAI or ElevenLabs depending on BaseUrl.
            if (now && string.IsNullOrEmpty(CfgGrokApiKey.Value))
            {
                _toast = "AI Voice ON - but no TTS key is set";
                Log.LogWarning("AI Voice switched on, but GrokTTS/ApiKey is empty; "
                    + "lines will keep using the local voice.");
            }
            else if (now)
            {
                _toast = "AI Voice: ON  (" + CfgGrokVoiceId.Value + ") - cloud TTS billing active";
                Log.LogInfo("AI Voice ON (cloud TTS billing active).");
            }
            else
            {
                // Steam ships Overtone's synthesis stripped - SpeakSamples,
                // PtrToSamples and MakeClip are all absent from that assembly, so
                // "falls back to the local voice" is simply untrue there. Saying it
                // anyway is what made a correct switch-off look like a regression:
                // she goes quiet, and the toast promises a voice that cannot exist.
                bool local = Platform.LocalVoiceAvailable;

                _toast = local
                    ? "AI Voice: OFF - local voice, no TTS billing"
                    : "AI Voice: OFF - silent on this build (no local voice), no TTS billing";
                Log.LogInfo(local
                    ? "AI Voice OFF (local voice, no TTS billing)."
                    : "AI Voice OFF (no TTS billing). This build has no on-device voice, so "
                      + "lines are silent until cloud TTS is switched back on.");
            }

            _toastUntil = Time.realtimeSinceStartup + 3f;
        }

        static string _toast;
        static float _toastUntil;
        static Texture2D _toastBg;

        static Texture2D SolidTexture(Color c)
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        // Brief on-screen confirmation so the toggle is visible without the log.
        //
        // Drawn at the TOP of the screen on purpose: the game's dialogue UI is a
        // Screen Space - Overlay canvas along the bottom, and those render after
        // IMGUI, so anything drawn down there is painted over. Uses its own solid
        // background texture rather than GUI.skin.box, which has no background
        // assigned in a built player and renders as invisible text.
        internal static void DrawToast()
        {
            if (_toast == null || Time.realtimeSinceStartup > _toastUntil) return;

            if (_toastBg == null) _toastBg = SolidTexture(new Color(0f, 0f, 0f, 0.85f));

            // Draw above other IMGUI, and undo any scaling the game left behind.
            int prevDepth = GUI.depth;
            Matrix4x4 prevMatrix = GUI.matrix;
            GUI.depth = -1000;
            GUI.matrix = Matrix4x4.identity;

            GUIStyle style = new GUIStyle();
            style.fontSize = 18;
            style.alignment = TextAnchor.MiddleCenter;
            style.normal.textColor = Color.white;
            style.normal.background = _toastBg;
            style.padding = new RectOffset(20, 20, 12, 12);

            Vector2 size = style.CalcSize(new GUIContent(_toast));
            float w = size.x + 40f, h = size.y + 24f;
            Rect r = new Rect((Screen.width - w) / 2f, 40f, w, h);

            GUI.DrawTexture(r, _toastBg);
            GUI.Label(r, _toast, style);

            GUI.depth = prevDepth;
            GUI.matrix = prevMatrix;
        }

        internal static System.Collections.IEnumerator CheckForUpdates()
        {
            UnityEngine.Networking.UnityWebRequest req = null;
            try
            {
                req = UnityEngine.Networking.UnityWebRequest.Get(LATEST_VERSION_URL);
                req.timeout = 8;
                yield return req.SendWebRequest();

                if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    VersionCheckFailed = true;
                    Log.LogWarning("Version check failed: " + req.error
                        + " - the panel will say so rather than claim you are up to date.");
                    yield break;
                }

                string raw = req.downloadHandler.text;
                if (string.IsNullOrEmpty(raw))
                {
                    VersionCheckFailed = true;
                    Log.LogWarning("Version check returned an empty response.");
                    yield break;
                }

                string latest = raw.Trim();

                // Compared numerically, not with !=. VERSION.txt carries the latest
                // RELEASED version, so a development build is legitimately AHEAD of
                // it - and an inequality test would announce "update available:
                // 3.1.1" to someone running 3.2.0, which is both wrong and the
                // opposite of useful.
                if (IsNewer(latest, VERSION))
                {
                    LatestVersion = latest;
                    Log.LogWarning(string.Format("A newer version is available: {0} (you have {1})", latest, VERSION));
                    Log.LogWarning("Download: " + DOWNLOAD_URL);
                }
                else if (latest == VERSION)
                {
                    Log.LogInfo(string.Format("You are running the latest version ({0})", VERSION));
                }
                else
                {
                    Log.LogInfo(string.Format("You are running {0}, ahead of the released {1}.", VERSION, latest));
                }
            }
            finally
            {
                VersionCheckDone = true;
                if (req != null) req.Dispose();
            }
        }

        // Dotted-numeric comparison: is 'candidate' a later version than 'mine'?
        //
        // Fails CLOSED. Anything unparseable returns false, because the cost of
        // the two errors is not symmetric: a missed notification is a minor
        // annoyance, while a false "update available" sends someone to redownload
        // the build they already have and makes the indicator untrustworthy.
        //
        // Missing segments count as zero, so "3.2" and "3.2.0" compare equal
        // rather than one appearing to trail the other.
        internal static bool IsNewer(string candidate, string mine)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(mine)) return false;

            string[] a = candidate.Trim().Split('.');
            string[] b = mine.Trim().Split('.');
            int len = Math.Max(a.Length, b.Length);

            for (int i = 0; i < len; i++)
            {
                int x, y;
                if (!int.TryParse(i < a.Length ? a[i] : "0", out x)) return false;
                if (!int.TryParse(i < b.Length ? b[i] : "0", out y)) return false;

                if (x != y) return x > y;
            }
            return false;   // identical
        }
    }

    // Owns the frame loop for the voice hotkey.
    //
    // This lives on its own DontDestroyOnLoad object rather than on the plugin
    // component: Unity was not driving Update/OnGUI on the BepInEx-created
    // component in this build, so the hotkey never fired and the toast never
    // drew. A plain GameObject we create ourselves is driven normally and
    // survives every scene change, so F8 works in the menu and in gameplay.
    internal class HotkeyWatcher : MonoBehaviour
    {
        static HotkeyWatcher _instance;

        // The settings panel is a static IMGUI class with no MonoBehaviour of its
        // own, so it borrows this one to run the test coroutines. This object
        // outlives scene loads, which the game's own components do not, so a test
        // started from the panel cannot be cut short by a level change.
        internal static MonoBehaviour Host { get { return _instance; } }

        internal static void Install()
        {
            if (_instance != null) return;

            try
            {
                GameObject go = new GameObject("AI2UMod_HotkeyWatcher");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                _instance = go.AddComponent<HotkeyWatcher>();
                Plugin.Log.LogInfo("Hotkey watcher installed (survives scene loads).");

                // Check for updates now that we have a proper MonoBehaviour host
                _instance.StartCoroutine(Plugin.CheckForUpdates());
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not install the hotkey watcher: " + e);
            }
        }

        void Update()
        {
            try
            {
                if (Plugin.PollMenuKey()) OverlayMenu.Toggle();

                // The voice hotkey stays live while the panel is open - it is a
                // one-press action and the panel has its own toggle for the same
                // setting, so there is nothing to conflict over.
                if (Plugin.PollToggleKey()) Plugin.ToggleVoice();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Hotkey poll failed: " + e);
                enabled = false;
            }
        }

        void OnGUI()
        {
            try { OverlayMenu.Draw(); }
            catch (Exception e)
            {
                // A layout exception would otherwise repeat every frame and bury
                // the log, and an overlay stuck open would hold the player's input
                // hostage. Shut it and say why.
                Plugin.Log.LogError("Settings panel failed, closing it: " + e);
                try { OverlayMenu.Close(); } catch (Exception) { }
            }

            try { Plugin.DrawToast(); }
            catch (Exception) { }
        }

        // Restores the cursor, the HUD canvases and the player's input if this
        // object is torn down while the panel is open. Without this, a scene load
        // at the wrong moment would leave the character unable to move.
        void OnDestroy()
        {
            try { OverlayMenu.Close(); }
            catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(ChatGPTConversation), "SendToChatGPT", new Type[] { typeof(string), typeof(Action<string, int>) })]
    public static class SendPatch
    {
        static bool Prefix(ChatGPTConversation __instance, string message, Action<string, int> errorCallback)
        {
            // Checked here rather than at Awake so the F9 master toggle takes
            // effect on the very next line she speaks.
            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value) return true;

            try
            {
                Traverse t = Traverse.Create(__instance);

                // Only take over the server-routed path; leave legacy direct-OpenAI modes alone.
                object model = t.Field("_model").GetValue();
                if (model == null || model.ToString() != "ChatGPTAzure")
                    return true;

                Chat chat = t.Field("_chat").GetValue<Chat>();
                if (chat == null)
                {
                    Plugin.Log.LogWarning("_chat was null; deferring to the original code path.");
                    return true;
                }

                // The game trims history down to _chatHistoryMaxTokens (stock: 3072) inside
                // ResolveChatGPTAzure, which we still call to hand the reply back. Re-apply our
                // ceiling on every send so the trim never runs before the model's real limit.
                int wantHistory = Plugin.CfgHistoryMaxTokens.Value;
                if (wantHistory > 0 && t.Field("_chatHistoryMaxTokens").GetValue<int>() != wantHistory)
                {
                    t.Field("_chatHistoryMaxTokens").SetValue(wantHistory);
                    Plugin.Log.LogInfo("history ceiling raised to " + wantHistory + " tokens");
                }

                // Appended AFTER the test check, so when the phrase fired it is the
                // cleaned text that enters the history the model reads - and keeps
                // reading, since history persists for the session.
                // Unconditional, because it clears the flag as well as setting it:
                // an ordinary message after a tagged one has to put her back in
                // character. Reads the raw text, since the tag is deliberately
                // left in place for the model to see.
                Ooc.NotePlayerMessage(message);

#if CANALPA
                // The half of the consent gate the model cannot reach. Reads the
                // player's raw text - before any stripping or term resolution - and
                // is the only source of "they actually said yes". Called on every
                // turn, not only pending ones, because it also counts the turns and
                // clears itself on hesitation.
                Consent.NotePlayerMessage(message);
#endif

                string outgoing = Murder.NotePlayerMessage(message)
                    ? Murder.StripPhrase(message)
                    : message;

                // Before it enters the history, not after: whatever is appended
                // here is what she re-reads for the rest of the session, so a raw
                // term name left in place would keep telling her nothing on every
                // later turn too.
                outgoing = Lore.ResolveTerms(outgoing);

                chat.AppendMessage(Chat.Speaker.User, outgoing, 0);
                ChatGPTConversation.isInAsync = true;

                __instance.StartCoroutine(Bridge.Send(__instance, chat.CurrentChat, errorCallback));
                return false; // skip the vanilla request to the AI2U server
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Prefix failed, falling back to vanilla: " + e);
                return true;
            }
        }
    }

    // Overtone synthesizes the line correctly, then the game throws it away.
    //
    // LocalTTSManager.Speak calls the async TTSPlayer.Speak without awaiting it, then immediately
    // touches _player.sources[0].outputAudioMixerGroup on the next line. In this build that array
    // is not wired up, so the synchronous line raises a NullReferenceException and the async
    // continuation that would have called sources[i].Play() fails the same way -- silently, since
    // nobody observes the Task. The log shows "Done. Returned 'N' samples" for every line: the
    // audio exists, it just never reaches an AudioSource.
    //
    // AzureVoiceManager.VoiceMap already holds a correctly configured AudioSource per character
    // (right mixer group, so the in-game Voice volume slider still applies). That is the same
    // source AzureVoiceManager_PersonalAPI plays through, so route the generated clip there.
    [HarmonyPatch(typeof(LocalTTSManager), "Speak")]
    public static class LocalTtsFix
    {
        static bool Prefix(LocalTTSManager __instance, string Text, Character currentCharacterID)
        {
            // First line, before any early return: every branch below hands the
            // call back to the game silently, so without this a hook that never
            // fired is indistinguishable from one that fired and declined.
            Plugin.Log.LogInfo("TTS: manager hook entered (chars="
                + (Text == null ? 0 : Text.Length) + ", character=" + currentCharacterID + ").");

            // Installed unconditionally now, so the voice settings can be changed
            // live. When neither voice feature is wanted, hand the call straight
            // back to the game.
            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value) return true;
            if (!Plugin.CfgForceLocalVoice.Value && !Plugin.CfgGrokEnabled.Value) return true;

            // The mod's voice is off and the player asked the game to cover for it,
            // so this hook must not claim the turn.
            if (Plugin.HandVoiceBackToGame()) return true;

            try
            {
                TTSPlayer player = Traverse.Create(__instance).Field("_player").GetValue<TTSPlayer>();
                if (player == null)
                {
                    Plugin.Log.LogWarning("TTS: the manager has no TTSPlayer; leaving the original path alone.");
                    return true;
                }

                // Overtone's Engine and Voice are needed to synthesize ON DEVICE and
                // for nothing else - a cloud voice only needs an AudioSource to play
                // through. Demanding them unconditionally made her mute on the Steam
                // build, where Overtone's synthesis is stripped, so Engine is null
                // there permanently: every line bailed out to the original method,
                // which then threw on _player.sources[0] - the exact fault this
                // patch exists to avoid.
                if (!GrokTts.Configured && (player.Engine == null || player.Voice == null))
                {
                    Plugin.Log.LogWarning("TTS: no cloud voice is configured and the on-device voice is not ready, "
                        + "so this line goes back to the game. Press F8 or set a TTS key to use the cloud voice.");
                    return true;
                }

                AudioSource dest = ResolveVoiceSource(player, currentCharacterID);
                if (dest == null)
                {
                    Plugin.Log.LogError("TTS: no AudioSource available for " + currentCharacterID + "; cannot play.");
                    return false;
                }

                // Filtered here rather than upstream so the subtitle keeps the
                // stage directions - only the voice loses them.
                string spoken = Text;
                if (Plugin.CfgSpeakActions == null || !Plugin.CfgSpeakActions.Value)
                {
                    spoken = SpeechText.ForSpeech(Text);
                    if (spoken.Length == 0)
                    {
                        Plugin.Log.LogInfo("TTS: line was all stage direction, nothing to speak.");
                        return false;
                    }
                }

                player.StartCoroutine(SpeakRoutine(player, spoken, dest));
                return false; // replace the original, which would throw before playing anything
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("TTS prefix failed, deferring to the original: " + e);
                return true;
            }
        }

        // Prefer the character's own configured source; fall back to the player's array, then to
        // any source on the object, creating one only as a last resort.
        static AudioSource ResolveVoiceSource(TTSPlayer player, Character id)
        {
            AzureVoiceManager avm = Traverse.Create(player).Field("azureVoiceManager").GetValue<AzureVoiceManager>();
            if (avm != null && avm.VoiceMap != null)
            {
                if (avm.VoiceMap.ContainsKey(id) && avm.VoiceMap[id] != null) return avm.VoiceMap[id];
                foreach (KeyValuePair<Character, AudioSource> kv in avm.VoiceMap)
                    if (kv.Value != null) return kv.Value;
            }

            if (player.sources != null)
                for (int i = 0; i < player.sources.Length; i++)
                    if (player.sources[i] != null) return player.sources[i];

            AudioSource own = player.GetComponent<AudioSource>();
            if (own != null) return own;

            AudioSource added = player.gameObject.AddComponent<AudioSource>();
            added.playOnAwake = false;
            Plugin.Log.LogWarning("TTS: no configured AudioSource found; added a bare one (voice volume slider will not apply).");
            return added;
        }

        // Overtone's synthesis is reached reflectively, not called directly, so
        // that ONE plugin binary loads on both builds.
        //
        // The Steam build ships a stripped Overtone: TTSEngine keeps only its
        // Loaded/Disposed accessors, while Speak, SpeakSamples, MakeClip,
        // PtrToSamples, Awake and Dispose are all gone (Unity's managed stripper
        // dropped them - nothing in that build's scenes reaches them, because the
        // AI Setup tab that turns the local voice on was never wired there).
        // Compiling `player.Engine.Speak(...)` against the Steam assembly is a
        // hard CS1061, and a binary compiled against the standalone assembly
        // would throw MissingMethodException on Steam at the first line spoken.
        //
        // So: bind by name at runtime, and when the method is absent say so once
        // and let the caller fall back to cloud TTS.
        internal static class LocalSynth
        {
            static bool _probed;
            static System.Reflection.MethodInfo _speak;
            static System.Reflection.MethodInfo _result;
            static bool _warned;

            public static Task Begin(TTSPlayer player, string text)
            {
                if (!_probed)
                {
                    _probed = true;
                    _speak = FindSpeak(player);
                }

                if (_speak == null)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        Plugin.Log.LogWarning(
                            "This build of the game ships Overtone with its synthesis methods stripped, so the on-device voice is not available here. Configure a cloud TTS provider in the F9 menu to give her a voice.");
                    }
                    return null;
                }

                try
                {
                    object voiceModel = VoiceModelOf(player);
                    return _speak.Invoke(player.Engine, new object[] { text, voiceModel }) as Task;
                }
                catch (Exception e)
                {
                    Exception inner = e is System.Reflection.TargetInvocationException && e.InnerException != null
                        ? e.InnerException : e;
                    Plugin.Log.LogError("TTS: synthesis could not start: " + inner.Message);
                    return null;
                }
            }

            public static AudioClip ResultOf(Task task)
            {
                if (task == null) return null;
                try
                {
                    if (_result == null || !_result.DeclaringType.IsAssignableFrom(task.GetType()))
                        _result = task.GetType().GetMethod("get_Result", Type.EmptyTypes);

                    return _result != null ? _result.Invoke(task, null) as AudioClip : null;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("TTS: could not read the synthesised clip: " + e.Message);
                    return null;
                }
            }

            // Same question as FindSpeak, asked before any TTSPlayer exists, so
            // startup can report whether this build can speak on-device at all.
            // Goes through TTSPlayer.Engine's declared type rather than naming
            // the Overtone type, which keeps this working if the namespace moves.
            public static bool SpeakAvailable
            {
                get
                {
                    if (_availableProbed) return _available;
                    _availableProbed = true;

                    try
                    {
                        // Engine is a FIELD on both builds. Asking only for a
                        // property answered "no local voice" everywhere, which
                        // was accidentally right on Steam and wrong on itch.io.
                        const System.Reflection.BindingFlags Any =
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Instance;

                        Type engineType = null;

                        System.Reflection.FieldInfo ef = typeof(TTSPlayer).GetField("Engine", Any);
                        if (ef != null) engineType = ef.FieldType;

                        if (engineType == null)
                        {
                            System.Reflection.PropertyInfo ep = typeof(TTSPlayer).GetProperty("Engine", Any);
                            if (ep != null) engineType = ep.PropertyType;
                        }

                        if (engineType == null) return _available = false;

                        foreach (System.Reflection.MethodInfo m in engineType.GetMethods(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Instance))
                        {
                            if (m.Name != "Speak") continue;
                            System.Reflection.ParameterInfo[] ps = m.GetParameters();
                            if (ps.Length == 2 && ps[0].ParameterType == typeof(string)
                                && typeof(Task).IsAssignableFrom(m.ReturnType))
                                return _available = true;
                        }
                    }
                    catch (Exception) { }

                    return _available = false;
                }
            }

            static bool _availableProbed;
            static bool _available;

            // Matched on shape rather than exact parameter type: the voice-model
            // type is Overtone-internal and differs in name across versions.
            static System.Reflection.MethodInfo FindSpeak(TTSPlayer player)
            {
                if (player.Engine == null) return null;

                foreach (System.Reflection.MethodInfo m in player.Engine.GetType().GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance))
                {
                    if (m.Name != "Speak") continue;
                    System.Reflection.ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string)
                        && typeof(Task).IsAssignableFrom(m.ReturnType))
                        return m;
                }
                return null;
            }

            static object VoiceModelOf(TTSPlayer player)
            {
                object voice = player.Voice;
                if (voice == null) return null;

                Traverse t = Traverse.Create(voice).Property("VoiceModel");
                if (t != null && t.PropertyExists()) return t.GetValue();

                Traverse f = Traverse.Create(voice).Field("VoiceModel");
                return f != null && f.FieldExists() ? f.GetValue() : null;
            }
        }

        static IEnumerator SpeakRoutine(TTSPlayer player, string text, AudioSource dest)
        {
            // Grok first when configured; on any failure fall through to the
            // on-device voice so a dropped request never leaves her mute.
            if (GrokTts.Configured)
            {
                AudioClip remote = null;
                IEnumerator call = GrokTts.Synthesize(text, delegate(AudioClip c) { remote = c; });
                while (call.MoveNext()) yield return call.Current;

                if (remote != null)
                {
                    Play(player, dest, remote);
                    yield break;
                }
                Plugin.Log.LogWarning("Grok TTS unavailable for this line; using the local voice.");
            }

            Task task = LocalSynth.Begin(player, text);
            if (task == null) yield break;

            while (!task.IsCompleted)
                yield return null;

            if (task.IsFaulted)
            {
                Plugin.Log.LogError("TTS: synthesis failed: "
                    + (task.Exception != null ? task.Exception.GetBaseException().Message : "unknown"));
                yield break;
            }

            AudioClip clip = LocalSynth.ResultOf(task);
            if (clip == null)
            {
                Plugin.Log.LogWarning("TTS: synthesis returned no clip (is the voice model loaded?).");
                yield break;
            }

            Play(player, dest, clip);
        }

        // Shared by both voices: put the clip on the character's AudioSource and
        // tell the game when it ends, which is what drives mouth movement and
        // hands the turn back to the player.
        static void Play(TTSPlayer player, AudioSource dest, AudioClip clip)
        {
            dest.clip = clip;
            dest.loop = false;
            dest.volume = 1f;
            dest.pitch = 1f;
            dest.Play();

            try
            {
                AzureVoiceManager avm = Traverse.Create(player).Field("azureVoiceManager").GetValue<AzureVoiceManager>();
                if (avm != null)
                    Traverse.Create(avm).Method("SetAudioFinishPlayingEvent", new object[] { clip }).GetValue();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("TTS: could not hook the audio-finished event: " + e.Message);
            }

            if (Plugin.CfgLogPayloads.Value)
                Plugin.Log.LogInfo("TTS: playing " + clip.length.ToString("0.0") + "s on " + dest.name);
        }
    }

    // Communicator.Awake reads the voice mode from PlayerPrefs["LocalTTS"], where 0 means "let the
    // AI2U server synthesize the line and send audio back". Our replies never carry that audio, so
    // on 0 the game calls AzureAISpeak with a null speechResult and the NPC just never speaks.
    // Re-assert local synthesis after Awake, which also survives the pref being reset by an update.
    [HarmonyPatch(typeof(Communicator), "Awake")]
    public static class VoicePatch
    {
        // isLocalSpeak is a STATIC the game reads once, in Awake. So an override
        // applied here outlives every patch that depends on it: switching the mod
        // off makes all the patches stand down correctly and still leaves the game
        // pointed wherever the mod last aimed it. On Steam that meant permanent
        // silence, because the path it was aimed at is the stripped one - the
        // master toggle looked broken when the real problem was unreturned state.
        //
        // So the value the game itself chose is captured before anything touches
        // it, and Restore() hands it back.
        static bool _original;
        static bool _captured;
        static bool _forced;

        static void Postfix()
        {
            // Captured before the early-outs, so Restore() has something truthful
            // to give back even on the turns where nothing is overridden.
            if (!_captured)
            {
                try { _original = Communicator.isLocalSpeak; _captured = true; }
                catch (Exception) { }
            }

            // HandVoiceBackToGame excluded: forcing isLocalSpeak would route her to
            // the on-device path, which is the one thing the player just said they
            // do not want when they asked the game's servers to speak.
            bool wantOverride = Plugin.CfgEnabled != null && Plugin.CfgEnabled.Value
                && (Plugin.CfgForceLocalVoice.Value || Plugin.CfgGrokEnabled.Value)
                && !Plugin.HandVoiceBackToGame();

            if (!wantOverride)
            {
                // Not just an early-out: a stale override has to be cleaned up here.
                //
                // The pref is persisted, so an earlier session's write survives into
                // a launch where the mod is switched off. Awake reads it back before
                // any toggle can run, so the game starts aimed at local synthesis
                // with nothing left to undo it - Restore() is only reached by
                // flipping the switch, and someone who launches already-off never
                // flips anything. On a build with no on-device voice that is silence
                // out of the box, blamed on the game.
                //
                // Scoped to builds that genuinely cannot synthesize locally. Where
                // the local path works, this flag is a legitimate user preference
                // and overriding it would be the mod meddling while switched off.
                if (!Platform.LocalVoiceAvailable && Communicator.isLocalSpeak)
                {
                    try
                    {
                        Communicator.isLocalSpeak = false;
                        UnityEngine.PlayerPrefs.SetInt("LocalTTS", 0);
                        UnityEngine.PlayerPrefs.Save();
                        _forced = false;
                        Plugin.Log.LogInfo("Voice: cleared a leftover local-speech override. "
                            + "This build has no on-device synthesis, so the game is back on "
                            + "server audio and speaks normally with the mod switched off.");
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("Could not clear the stale voice override: " + e.Message);
                    }
                }
                return;
            }

            try
            {
                // isUsingPersonalTTSAPIKey is private static, so read it reflectively.
                bool personalTts = Traverse.Create(typeof(Communicator))
                    .Field("isUsingPersonalTTSAPIKey").GetValue<bool>();
                if (personalTts)
                {
                    // The player supplied their own Azure Speech key; that path synthesizes from
                    // text rather than replaying server audio, so it already works. Leave it alone.
                    Plugin.Log.LogInfo("Personal TTS key in use; leaving the voice path as configured.");
                    return;
                }

                if (!Communicator.isLocalSpeak)
                {
                    Communicator.isLocalSpeak = true;
                    _forced = true;
                    Plugin.Log.LogInfo("Voice: forced the local speech path (server audio is unavailable on a custom endpoint).");
                }
                else
                {
                    // Logged even when nothing changed: without it, a silent NPC gives
                    // no way to tell "the flag was wrong" from "the flag was right and
                    // the synthesis failed further down".
                    Plugin.Log.LogInfo("Voice: local speech path already active.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not set the voice mode: " + e);
            }
        }

        // Called when the master toggle goes off, so the game is handed back the
        // route it picked for itself instead of keeping the mod's.
        //
        // Only undoes an override this class actually applied. If the game was
        // already on the local path, or the player has a personal TTS key and the
        // Postfix above deliberately left things alone, there is nothing owed back
        // and touching it would be a second bug wearing the first one's clothes.
        internal static void Restore()
        {
            // _forced only covers overrides applied in THIS session, and that is not
            // enough. The pref is persisted, so a previous session's write means the
            // flag is already true before Awake even runs: Postfix sees "already
            // active", never sets _forced, and an early return here would leave the
            // build muted by the mod's own leftovers with nothing left to blame.
            //
            // So on a build with no on-device synthesis, isLocalSpeak = true is not a
            // state the game can be left in at all - it routes to code that does not
            // exist. There, switching off always hands back the server path, whoever
            // set the flag and whenever.
            bool noLocalVoice = !Platform.LocalVoiceAvailable;
            if (!_forced && !noLocalVoice) return;

            try
            {
                bool giveBack = noLocalVoice ? false : _original;

                Communicator.isLocalSpeak = giveBack;
                _forced = false;

                // The persisted pref has to go back too, not just the static. The
                // game re-reads PlayerPrefs["LocalTTS"] in Awake on every launch, so
                // restoring only the in-memory value fixes this session and leaves
                // the next one broken in exactly the same way.
                UnityEngine.PlayerPrefs.SetInt("LocalTTS", giveBack ? 1 : 0);
                UnityEngine.PlayerPrefs.Save();

                Plugin.Log.LogInfo("Voice: handed the speech path back to the game ("
                    + (giveBack ? "local" : "server audio") + ")."
                    + (noLocalVoice
                        ? " This build has no on-device synthesis, so the server path is the"
                          + " only one that can produce sound here."
                        : ""));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not restore the voice mode: " + e.Message);
            }
        }
    }

    // Speech is caught here, at the Communicator, rather than only down at
    // LocalTTSManager.Speak - because on the Steam build that method is never
    // reached. Unity stripped Overtone's synthesis out of that build (nothing in
    // its scenes references it), and the LocalTTSManager the Communicator looks
    // up during Awake went with it, so LocalSpeak faults on a null manager
    // before any of our code gets a turn. From the outside that looks exactly
    // like what was observed: replies arriving normally with not one line of TTS
    // logging behind them.
    //
    // Hooking at this level needs nothing from Overtone. A cloud voice only
    // wants an AudioSource, and AzureVoiceManager.VoiceMap already holds the
    // per-character one that the in-game Voice slider governs.
    // Both speech branches are patched, because which one the game takes is not
    // ours to choose. Communicator.cs:275 reads:
    //
    //     if (isLocalSpeak) {
    //         if (isUsingPersonalTTSAPIKey) AzureAISpeak_PersonalAPI(...)  // 279
    //         else                          LocalSpeak(...)               // 283
    //     }
    //
    // isUsingPersonalTTSAPIKey is a PlayerPrefs-backed flag, so it differs per
    // install: the standalone copy has it off and goes to LocalSpeak, the Steam
    // copy has it on and goes to AzureAISpeak_PersonalAPI. Patching only
    // LocalSpeak is what left Steam silent with nothing in the log - the hook
    // was on a branch that build never takes.
    //
    // Both methods are (string text, Character characterId), so one Prefix
    // serves both. AzureAISpeak (no suffix) is deliberately NOT in this list:
    // it takes a JSONNode of server audio and is handled separately.
    // TargetMethods rather than two stacked [HarmonyPatch] attributes: stacking
    // them merges into a single target spec instead of declaring two, so one of
    // the methods would silently go unpatched.
    [HarmonyPatch]
    public static class CloudSpeakPatch
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Communicator), "LocalSpeak");
            yield return AccessTools.Method(typeof(Communicator), "AzureAISpeak_PersonalAPI");
        }

        static bool Prefix(Communicator __instance, string text, Character characterId)
        {
            // Logged on entry, because a silent success and a hook that never
            // fired look identical in the log otherwise - which is exactly what
            // cost a round trip diagnosing the Steam build.
            Plugin.Log.LogInfo("Voice: speech hook entered (chars=" + (text == null ? 0 : text.Length)
                + ", character=" + characterId + ").");

            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value) return true;

            // With no cloud voice configured there is nothing here that the
            // game's own path does not do better, so let it run: on the itch
            // build Overtone genuinely works.
            if (!GrokTts.Configured)
            {
                Plugin.Log.LogWarning("Voice: no cloud TTS configured, so the game's own path runs. "
                    + "On the Steam build that means silence - Overtone is stripped there.");
                return true;
            }

            try
            {
                AudioSource dest = VoiceSourceFor(__instance, characterId);
                if (dest == null)
                {
                    Plugin.Log.LogWarning("Voice: no AudioSource for " + characterId + "; deferring to the game.");
                    return true;
                }

                // Claimed before speaking, not after: the isLocalSpeak branch in
                // ReceiveChatGPTReply falls through to an unconditional
                // AzureAISpeak, so both hooks see the same reply and only the
                // first may speak it.
                if (!SpeechDispatch.Claim())
                {
                    Plugin.Log.LogInfo("Voice: this reply was already spoken, skipping the duplicate.");
                    return false;
                }

                string spoken = text;
                if (Plugin.CfgSpeakActions == null || !Plugin.CfgSpeakActions.Value)
                {
                    spoken = SpeechText.ForSpeech(text);
                    if (spoken.Length == 0)
                    {
                        Plugin.Log.LogInfo("Voice: line was all stage direction, nothing to speak.");
                        return false;
                    }
                }

                __instance.StartCoroutine(CloudRoutine(__instance, spoken, dest));
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Voice: could not start cloud speech, deferring to the game: " + e);
                return true;
            }
        }

        internal static IEnumerator CloudRoutine(Communicator comm, string text, AudioSource dest)
        {
            AudioClip clip = null;
            IEnumerator call = GrokTts.Synthesize(text, delegate (AudioClip c) { clip = c; });
            while (call.MoveNext()) yield return call.Current;

            if (clip == null)
            {
                Plugin.Log.LogWarning("Voice: cloud TTS gave nothing back for this line ("
                    + GrokTts.FailureLabel() + "); she stays quiet on it.");
                yield break;
            }

            dest.clip = clip;
            dest.loop = false;
            dest.volume = 1f;
            dest.pitch = 1f;
            dest.Play();

            // This is how the game learns the line has audio and how long it
            // runs for. Without it the dialogue box stops waiting for her.
            try
            {
                AzureVoiceManager avm = Traverse.Create(comm).Field("azureVoiceManager").GetValue<AzureVoiceManager>();
                if (avm != null)
                    Traverse.Create(avm).Method("SetAudioFinishPlayingEvent", new object[] { clip }).GetValue();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Voice: could not hook the audio-finished event: " + e.Message);
            }

            Plugin.Log.LogInfo("Voice: spoke " + clip.length.ToString("0.0") + "s on " + dest.name);
        }

        internal static AudioSource VoiceSourceFor(Communicator comm, Character id)
        {
            AzureVoiceManager avm = Traverse.Create(comm).Field("azureVoiceManager").GetValue<AzureVoiceManager>();
            if (avm == null || avm.VoiceMap == null) return null;

            if (avm.VoiceMap.ContainsKey(id) && avm.VoiceMap[id] != null) return avm.VoiceMap[id];
            foreach (KeyValuePair<Character, AudioSource> kv in avm.VoiceMap)
                if (kv.Value != null) return kv.Value;
            return null;
        }
    }

    // Decides which speech hook gets to speak a given reply, and remembers the
    // reply text.
    //
    // Both of these exist because of what the Steam build actually does, measured
    // rather than assumed. Two facts drive it:
    //
    //   Two speak calls can fire for ONE reply. In ReceiveChatGPTReply the
    //   isLocalSpeak branch does not return - after LocalSpeak runs, control
    //   falls through to an unconditional AzureAISpeak below it. Whichever hook
    //   reaches here first speaks; the other is suppressed. Without that, a line
    //   is either spoken twice or cut off by the second AudioSource.Play().
    //
    //   AzureAISpeak is handed json["speechResult"], the server's pre-rendered
    //   audio text. A custom endpoint never sends that field, so the argument
    //   arrives null or empty. The spoken text therefore has to come from the
    //   reply the mod itself produced, which is what LastReply holds.
    internal static class SpeechDispatch
    {
        static int _turn;
        static int _spokenTurn = -1;

        // npc_reply_to_player from the most recent reply the mod handed over.
        internal static string LastReply = "";

        internal static void NewTurn() { _turn++; }

        // True for the first caller in a turn, false for every later one.
        internal static bool Claim()
        {
            if (_spokenTurn == _turn) return false;
            _spokenTurn = _turn;
            return true;
        }

        // Give a claim back, for a caller that claimed the turn and then decided
        // to let the game's own voice path have it after all. Without this the
        // turn stays marked as spoken and any later speak path in the same turn
        // is refused, which would turn "hand it back" into silence.
        internal static void Release()
        {
            if (_spokenTurn == _turn) _spokenTurn = -1;
        }

        internal static void Remember(string replyJson)
        {
            try
            {
                JObject o = JObject.Parse(replyJson);
                JToken r = o.SelectToken("npc_reactions.npc_reply_to_player")
                    ?? o.SelectToken("npc_reply_to_player");
                if (r != null) LastReply = r.ToString();
            }
            catch (Exception) { }
        }
    }

    // Reports which way the speech branch went, because three separate gates in
    // Communicator.ReceiveChatGPTReply (Communicator.cs:238) can swallow a reply
    // before any speak call is reached, and all three look the same from outside:
    //
    //   - TryParseAIReply returning false               -> ErrorCallbackChatGPT
    //   - the FinalChase / mainCharacters check at 257
    //   - the guideline-rework path at 259-273, which re-sends and RETURNS at 273
    //
    // Without this, "she is silent" gives no way to tell a dead hook from a reply
    // that never got as far as speaking.
    [HarmonyPatch(typeof(Communicator), "ReceiveChatGPTReply")]
    public static class VoiceRouteProbe
    {
        static void Prefix()
        {
            // Above the logging gate on purpose: the turn counter is what stops
            // one reply being spoken twice, so it must advance whether or not
            // payload logging happens to be on.
            SpeechDispatch.NewTurn();

            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value) return;
            if (Plugin.CfgLogPayloads == null || !Plugin.CfgLogPayloads.Value) return;
            try
            {
                object personal = Traverse.Create(typeof(Communicator))
                    .Field("isUsingPersonalTTSAPIKey").GetValue();
                Plugin.Log.LogInfo("Voice: reply resolving. isLocalSpeak=" + Communicator.isLocalSpeak
                    + " isUsingPersonalTTSAPIKey=" + (personal == null ? "<unreadable>" : personal.ToString())
                    + " -> expecting "
                    + (!Communicator.isLocalSpeak ? "AzureAISpeak (server audio)"
                        : ("True".Equals(personal == null ? "" : personal.ToString())
                            ? "AzureAISpeak_PersonalAPI" : "LocalSpeak")));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Voice: could not read the speech flags: " + e.Message);
            }
        }
    }

    // Kissing and hugging are the only two actions the engine can refuse, and it
    // refuses them for a reason that has nothing to do with the action: at
    // NPCController.cs:945 a KissingCheek or Hugging activity whose
    // PlayerIsAbleToKissCheek / PlayerIsAbleToHug check fails is replaced with
    // NPCActivities.Other. ShowAction applies npc_target_location FIRST
    // (NPCController.cs:928-942), so a reply that asks to walk to the player and
    // kiss them in the same turn cancels its own kiss - the walk is what makes
    // the check fail.
    //
    // She cannot detect this. The field was set correctly and the engine dropped
    // it afterwards, so she reports the kiss, sees it did not happen, and
    // apologises for "mistakenly" choosing walking - which is not what she did.
    // Exactly the apartment-key shape.
    //
    // GameVocab states the rule, but a rule the model can forget is not enough
    // when the failure is invisible and self-blaming. Clearing the location is
    // the honest repair: her intent was the affection, the location was the
    // accident, and dropping it costs nothing because she is being asked to
    // touch someone who is by definition within reach.
    [HarmonyPatch(typeof(Communicator), "ReceiveChatGPTReply")]
    public static class AffectionConflictPatch
    {
        // Typed as object because SimpleJSON's JSONNode lives in the game
        // assembly, which this file does not reference by type - Harmony matches
        // the parameter by name, not by declared type, so this binds correctly.
        static void Prefix(object json)
        {
            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value) return;

            try
            {
                object r = Node(json, "npc_reactions");
                if (r == null) return;

                string action = Str(r, "npc_action");
                string loc = Str(r, "npc_target_location");

                if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(loc)) return;

                // Contains, not equality: the vanilla kiss-in-a-row counter at
                // NPCMasterBehavior_Main_L1.cs:130 tests the same way, so
                // whatever variants the engine tolerates are matched here too.
                if (action.IndexOf("kiss", StringComparison.OrdinalIgnoreCase) < 0
                    && action.IndexOf("hug", StringComparison.OrdinalIgnoreCase) < 0) return;

                if (!Set(r, "npc_target_location", "")) return;

                Plugin.Log.LogInfo("Affection: \"" + action + "\" arrived with npc_target_location \""
                    + loc + "\". The engine would have dropped the action and left her standing, "
                    + "so the location was cleared and the action kept.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Affection: could not check the reply: " + e.Message);
            }
        }

        // SimpleJSON's indexer is this[string], so it is a named indexer property
        // reached as get_Item / set_Item.
        static object Node(object o, string key)
        {
            if (o == null) return null;
            System.Reflection.PropertyInfo p = o.GetType().GetProperty("Item",
                new Type[] { typeof(string) });
            return p == null ? null : p.GetValue(o, new object[] { key });
        }

        static string Str(object o, string key)
        {
            object n = Node(o, key);
            if (n == null) return null;
            object v = Traverse.Create(n).Property("Value").GetValue();
            return v as string;
        }

        // Written through the existing node's own Value setter rather than through
        // the parent's indexer.
        //
        // SimpleJSON.JSONString overrides Value with a real setter
        // (JSONString.cs:38), so the node already in the object can simply be
        // reassigned. Going through JSONObject's indexer instead would mean
        // constructing a JSONNode, and the implicit string conversion that
        // normally hides that is a compile-time operator reflection will not
        // apply - and a missing key hands back a JSONLazyCreator whose Value
        // setter is a no-op, which would fail silently.
        static bool Set(object o, string key, string value)
        {
            object n = Node(o, key);
            if (n == null) return false;

            System.Reflection.PropertyInfo p = n.GetType().GetProperty("Value");
            if (p == null || !p.CanWrite) return false;

            p.SetValue(n, value, null);

            // Confirmed rather than assumed: on a JSONLazyCreator the setter is
            // silently ignored, and a cleared location that did not clear would
            // put the bug straight back.
            object back = Traverse.Create(n).Property("Value").GetValue() as string;
            return (back as string) == value;
        }
    }

    // A failed request must not count as her misbehaving.
    //
    // Communicator keeps two strike counters and answers both by killing the
    // player. On a reply that fails to arrive or fails to parse it increments
    // aiCensorCounter, and at aiCensorCounterTotal = 2 - so the THIRD consecutive
    // failure - it calls FinalChaseStart() (Communicator.cs:406-410). The counter
    // is cleared in exactly one place: a reply that parsed (:248).
    //
    // On the vendor server that is reasonable. The strikes it was built to count
    // are the moderation refusals its own endpoint returns, and three in a row
    // means the player kept pushing after being refused twice. The other branch,
    // aiCensorCounter_server, is the forgiving one at nine strikes, and it is
    // chosen by errorCode being 100-199 (:372).
    //
    // A custom endpoint fails for entirely different reasons - a rate limit, an
    // expired key, a cold model, a truncated body, three malformed JSON replies -
    // and Bridge.Send reports all of them with errorCode 0. Zero is not in
    // 100-199, so every one of them lands on the THREE-strike branch. Three
    // network hiccups in a row and the chase starts, with no line of dialogue
    // explaining it and nothing the player did to earn it.
    //
    // So the counter is cleared after the game's own handler has run, on the
    // errors that are ours. Postfix rather than prefix on purpose: the handler
    // still gets to do everything else it does - the placeholder line, the
    // telemetry, the offline TV branches - and only the strike is withdrawn.
    //
    // Deliberately narrow. errorCode 0 with the mod enabled is our transport;
    // anything in 100-199 is a real refusal from the endpoint and still counts,
    // and with the mod off this does nothing at all so vanilla keeps its rule.
    [HarmonyPatch(typeof(Communicator), "ErrorCallbackChatGPT")]
    public static class TransportStrikeGuard
    {
        static void Postfix(Communicator __instance, string errorMessage, int errorCode)
        {
            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value) return;

            // A moderation refusal from the endpoint is her problem and keeps its
            // strike. Only our own transport failures are forgiven.
            if (errorCode < 100 || errorCode >= 200)
            {
                try
                {
                    Traverse t = Traverse.Create(__instance).Field("aiCensorCounter");
                    object cur = t.GetValue();
                    int n = cur is int ? (int)cur : 0;
                    if (n <= 0) return;

                    t.SetValue(0);
                    Plugin.Log.LogWarning("A request failed (" + Trim(errorMessage, 120)
                        + "). That is the connection, not her, so the strike was withdrawn - "
                        + n + " had built up and three in a row would have started the chase.");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Could not withdraw the failure strike: " + e.Message);
                }
            }
        }

        static string Trim(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }

    // Communicator.ReceiveChatGPTReply has two silent exits between displaying the
    // subtitle and speaking it, and both look identical from outside - she types,
    // she never talks, nothing is logged:
    //
    //   Communicator.cs:305  TryParseAIReply returned false -> ErrorCallbackChatGPT
    //   Communicator.cs:273  resendChat was true -> the reply is re-sent, then return
    //
    // TryParseAIReply is virtual with 24 overrides, so hooking the base class alone
    // misses whichever level-specific subclass is actually live. Every override is
    // enumerated instead, which also means this keeps working on levels we have not
    // tested.
    [HarmonyPatch]
    public static class ParseProbe
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (Type t in typeof(NPCMasterBehavior).Assembly.GetTypes())
            {
                if (t == null || !typeof(NPCMasterBehavior).IsAssignableFrom(t)) continue;

                System.Reflection.MethodInfo m = t.GetMethod("TryParseAIReply",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly);
                if (m != null) yield return m;
            }
        }

        static void Postfix(object __instance, bool __result, ref bool resendChat)
        {
            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value) return;
            if (Plugin.CfgLogPayloads == null || !Plugin.CfgLogPayloads.Value) return;

            Plugin.Log.LogInfo("Voice: TryParseAIReply on "
                + (__instance == null ? "?" : __instance.GetType().Name)
                + " -> " + __result + ", resendChat=" + resendChat
                + (__result ? (resendChat ? " (REWORK: re-sends, never speaks)" : " (ok, should speak)")
                            : " (PARSE FAILED: bails before speaking)"));
        }
    }

    // The reply path calls AzureAISpeak unconditionally, immediately after the
    // local branch, passing the server's speechResult - which a custom endpoint
    // never returns. At best that plays silence; at worst it re-Plays the very
    // AudioSource we just started and cuts her off mid-word.
    [HarmonyPatch(typeof(Communicator), "AzureAISpeak")]
    public static class ServerAudioSuppressPatch
    {
        // Parameter names here are matched against the original by Harmony, so
        // they are not free-form. The real method is
        //
        //   AzureAISpeak(JSONNode jsonVoice, Character characterId, float delayPlayTime)
        //
        // Declaring a "text" parameter it does not have threw "Parameter \"text\"
        // not found" at patch time, and since PatchAll aborts a class on the first
        // bad member it took the rest of the speech patches with it - which is why
        // the Steam build logged no speech hooks at all and stayed silent.
        //
        // jsonVoice is deliberately not taken: it carries the server's rendered
        // audio, the one thing a custom endpoint never sends. Text comes from
        // SpeechDispatch instead.
        static bool Prefix(Communicator __instance, Character characterId)
        {
            // Logged so the three speak paths can be told apart from the log alone.
            // VoiceRouteProbe runs as a Prefix on the reply handler, so it only
            // reports which branch it EXPECTS from the flags on entry; this is the
            // one that proves where control actually went.
            Plugin.Log.LogInfo("Voice: server-audio hook entered (AzureAISpeak).");

            if (Plugin.CfgEnabled == null || !Plugin.CfgEnabled.Value) return true;
            if (!GrokTts.Configured && !Plugin.CfgForceLocalVoice.Value) return true;

            // Measured on the Steam build: this is the ONLY speak call that runs
            // there. isLocalSpeak reads true and LocalSpeak is still never
            // entered, so suppressing here - which is all this patch used to do -
            // was itself the cause of the silence. Speak instead.
            //
            // On the itch build LocalSpeak fires first and claims the turn, so
            // this falls through to plain suppression and nothing is said twice.
            if (!SpeechDispatch.Claim()) return false;

            // GrokTts.Configured folds together two very different causes - the
            // voice being switched off and the key being absent - so they are
            // reported apart here. Conflating them sent me hunting for a missing
            // key that was set all along.
            if (!GrokTts.Configured)
            {
                // The one case where handing the turn back to the game is what the
                // player asked for. Returning true runs the original AzureAISpeak,
                // so the game voices the line through its own account or the
                // player's Azure key. Off by default because that spends someone
                // else's metered service on a reply their server did not write.
                if (Plugin.CfgGameServerTts != null && Plugin.CfgGameServerTts.Value)
                {
                    Plugin.Log.LogInfo("Voice: the mod's voice is off and "
                        + "UseGameServerTtsWhenModTtsOff is on, so the game's own voice path is "
                        + "handling this line.");
                    SpeechDispatch.Release();
                    return true;
                }

                if (Plugin.CfgGrokEnabled != null && !Plugin.CfgGrokEnabled.Value)
                    Plugin.Log.LogInfo("Voice: the voice is switched off, so this line is silent. "
                        + "Press F8, or turn it on in the F9 panel. To let the game's own servers "
                        + "speak instead, turn on UseGameServerTtsWhenModTtsOff in the F9 panel.");
                else
                    Plugin.Log.LogWarning("Voice: server audio suppressed and no TTS key is set, "
                        + "so this line is silent. Set one in the F9 panel.");
                return false;
            }

            try
            {
                string spoken = SpeechDispatch.LastReply;
                if (string.IsNullOrEmpty(spoken))
                {
                    Plugin.Log.LogWarning("Voice: no text to speak on the server-audio path.");
                    return false;
                }

                if (Plugin.CfgSpeakActions == null || !Plugin.CfgSpeakActions.Value)
                {
                    spoken = SpeechText.ForSpeech(spoken);
                    if (spoken.Length == 0)
                    {
                        Plugin.Log.LogInfo("Voice: line was all stage direction, nothing to speak.");
                        return false;
                    }
                }

                AudioSource dest = CloudSpeakPatch.VoiceSourceFor(__instance, characterId);
                if (dest == null)
                {
                    Plugin.Log.LogWarning("Voice: no AudioSource for " + characterId + " on the server-audio path.");
                    return false;
                }

                __instance.StartCoroutine(CloudSpeakPatch.CloudRoutine(__instance, spoken, dest));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Voice: server-audio path could not speak: " + e);
            }

            return false;
        }
    }

    // Keeps model output inside the value sets the game's behavior trees actually branch on.
    // Anything unrecognized becomes an unhandled string downstream: NPCController.ShowAnimation
    // silently no-ops on an unknown animation, and an unknown angry_level or favorability_change
    // skips the mood and trust updates. Rather than let that happen quietly, snap to a legal value.
    public static class Schema
    {
        // Field -> allowed values, learned from the level's own system prompt.
        static Dictionary<string, List<string>> _allowed;
        static string _learnedFrom;

        // Last-resort list, used only if runtime discovery has not run yet.
        // GameVocab.For() is always preferred because it reads the live scene.
        //
        // These npc_action values are the exact keys of NPCController's
        // m_npcAllActivities dictionary (NPCController.cs:1333). Anything not
        // in that dictionary falls through ShowAction to NPCActivities.Other,
        // so a plausible-sounding invention like "follow_player" is a silent
        // no-op. npc_body_animation mirrors the string switch in
        // ShowAnimation. No location list here: locations are per-level and
        // only exist at runtime.
        static readonly Dictionary<string, string[]> Fallback = new Dictionary<string, string[]>
        {
            // Verified against m_npcAllActivities (NPCController.cs:1333) - all
            // 14 keys, verbatim. Two are camelCase and one is misspelled in the
            // game's own source; both are copied as-is because TryGetValue is
            // case-sensitive and a near-miss clamps to "other" without a word.
            // An earlier version of this list carried "attack" and "idle", which
            // are not keys at all, and omitted the two that are.
            { "npc_action", new[] { "other", "standing", "sitting", "sitting_down",
                "walking", "following_player", "following_player_closely", "hugging",
                "kissing", "cooking", "playing_games", "eat", "chaseAttacking",
                "idleThreating" } },
            { "npc_body_animation", new[] { "idle", "idling", "idly", "chill_idle",
                "angry_idle", "talk", "nod", "laugh", "shy", "stretch", "cheers",
                "dance", "troublesome" } },
            { "npc_face_expression", new[] { "smile", "slight_smile", "grin", "sad",
                "angry", "angry_face", "surprise", "confused", "shy", "smug",
                "worried", "bored", "tired_face", "scream", "raise_eyebrows" } },
            { "angry_level", new[] { "chill", "annoyed", "furious", "extremely furious" } },
            { "favorability_change", new[] { "very negative", "negative", "neutral",
                "positive", "very positive" } },
        };

        // Values the model reaches for that have an unambiguous in-game counterpart.
        static readonly Dictionary<string, string> Synonyms = new Dictionary<string, string>
        {
            { "gentle_smile", "slight_smile" }, { "soft_smile", "slight_smile" },
            { "small_smile", "slight_smile" }, { "warm_smile", "smile" },
            { "happy", "smile" }, { "laughing", "grin" }, { "excited", "grin" },
            { "concerned", "worried" }, { "concern", "worried" }, { "nervous", "worried" },
            { "anxious", "worried" }, { "relieved", "slight_smile" }, { "sadness", "sad" },
            { "crying_face", "sad" }, { "upset", "sad" }, { "disappointed", "sad" },
            { "shocked", "surprise" }, { "surprised", "surprise" }, { "startled", "surprise" },
            { "curious", "confused" }, { "puzzled", "confused" }, { "thinking", "confused" },
            { "embarrassed", "shy" }, { "blushing", "shy" }, { "flustered", "shy" },
            { "smirk", "smug" }, { "playful", "smug" }, { "teasing", "smug" },
            { "sleepy", "tired_face" }, { "exhausted", "tired_face" }, { "tired", "tired_face" },
            { "mad", "angry" }, { "furious_face", "angry_face" }, { "rage", "angry_face" },
            { "neutral_face", "slight_smile" }, { "neutral", "slight_smile" },
            { "idle_concerned", "idle" }, { "idling", "idle" }, { "idly", "idle" },
            { "standing_idle", "idle" }, { "waiting", "idle" }, { "relaxed", "chill_idle" },
            { "talking", "talk" }, { "speaking", "talk" }, { "nodding", "nod" },
            { "agreeing", "nod" }, { "stretching", "stretch" }, { "dancing", "dance" },
            { "celebrating", "cheers" }, { "cheering", "cheers" }, { "sobbing", "crying" },
            { "cry", "crying" }, { "sit_down", "sitting_down" }, { "sits", "sitting" },
            { "seated", "sitting" }, { "stand", "standing" }, { "stands", "standing" },
            { "walk", "walking" }, { "walks", "walking" }, { "walk_to", "walking" },
            { "move", "walking" }, { "moving", "walking" }, { "go", "walking" },
            { "goes", "walking" }, { "approach", "walking" }, { "approaching", "walking" },
            // The follow family: this is the one the model kept getting wrong.
            { "follow_player", "following_player" }, { "follows_player", "following_player" },
            { "following", "following_player" }, { "follow", "following_player" },
            { "follow_the_player", "following_player" },
            { "following_the_player", "following_player" },
            { "follow_player_closely", "following_player_closely" },
            { "following_closely", "following_player_closely" },
            { "follow_closely", "following_player_closely" },
            { "hug", "hugging" }, { "hugs", "hugging" }, { "cook", "cooking" },
            { "cooks", "cooking" }, { "kiss", "kissing" }, { "none", "other" },
            { "nothing", "other" }, { "n/a", "other" }, { "null", "other" },
            { "eating", "eat" }, { "eats", "eat" }, { "play_games", "playing_games" },
            { "playing_game", "playing_games" }, { "gaming", "playing_games" },
            { "attacking", "attack" }, { "attacks", "attack" },
            { "calm", "chill" }, { "content", "chill" }, { "irritated", "annoyed" },
            { "irritable", "annoyed" }, { "annoyed_level", "annoyed" }, { "angry_level", "annoyed" },
            { "mildly annoyed", "annoyed" }, { "very angry", "furious" }, { "enraged", "furious" },
            { "extremely_furious", "extremely furious" }, { "livid", "extremely furious" },
            { "very_negative", "very negative" }, { "very_positive", "very positive" },
            { "slightly positive", "positive" }, { "slightly negative", "negative" },
            { "no change", "neutral" }, { "none_change", "neutral" }, { "unchanged", "neutral" },
        };

        // Some words mean different things depending on the field: "happy" is a
        // smile on a face but the calm end of angry_level, and "idle" is both a
        // real action and a real animation. These are consulted before the
        // shared table so the field wins.
        static readonly Dictionary<string, Dictionary<string, string>> FieldSynonyms
            = new Dictionary<string, Dictionary<string, string>>
        {
            { "angry_level", new Dictionary<string, string> {
                // "happy" and "normal" are legal values in their own right, so
                // they are deliberately absent here - rewriting them to "chill"
                // would make her misreport her own state in OOC mode for no
                // behavioural gain, since the engine treats all three alike.
                { "pleased", "happy" }, { "cheerful", "happy" },
                { "fine", "normal" }, { "ok", "chill" },
                { "okay", "chill" }, { "neutral", "chill" }, { "relaxed", "chill" },
                { "content", "chill" }, { "calm", "chill" }, { "composed", "chill" },
                { "upset", "annoyed" }, { "displeased", "annoyed" },
                { "angry", "furious" }, { "mad", "furious" }, { "rage", "furious" },
                { "livid", "extremely furious" }, { "seething", "extremely furious" },
            } },
            { "npc_action", new Dictionary<string, string> {
                // "idle" is a real body animation but NOT a real action, so an
                // idling character is "standing" as far as the engine cares.
                { "idle", "standing" }, { "idling", "standing" },
                { "waiting", "standing" }, { "wait", "standing" },
                { "follow", "following_player" }, { "follow_player", "following_player" },
                { "following", "following_player" }, { "follow_me", "following_player" },
                { "followplayer", "following_player" },
                { "follow_closely", "following_player_closely" },
                { "attack", "chaseAttacking" }, { "attacking", "chaseAttacking" },
                { "chase", "chaseAttacking" }, { "chasing", "chaseAttacking" },
                { "threaten", "idleThreating" }, { "threatening", "idleThreating" },
                { "eating", "eat" }, { "sit", "sitting" }, { "sit_down", "sitting_down" },
                { "stand", "standing" }, { "walk", "walking" }, { "walk_to", "walking" },
                { "hug", "hugging" }, { "kiss", "kissing" }, { "cook", "cooking" },
                { "play_games", "playing_games" }, { "gaming", "playing_games" },
                { "resting", "sitting" }, { "rest", "sitting" }, { "talking", "other" },
                { "talk", "other" }, { "chatting", "other" }, { "speaking", "other" },
                { "nothing", "other" }, { "stay", "standing" }, { "staying", "standing" },
            } },
            { "npc_target_location", new Dictionary<string, string> {
                { "player", "player_location" }, { "the_player", "player_location" },
                { "player_position", "player_location" }, { "me", "player_location" },
                { "towards_player", "player_location" }, { "to_player", "player_location" },
                { "player_s_location", "player_location" },
            } },
        };

        // Where a numeric answer means something: models love writing angry_level: 0.
        // Only annoyed/furious/extremely furious read as angry to
        // NPCUitility.IsNPCAngry; everything calmer is equivalent, so the calm
        // end of the scale is a single value rather than three shades.
        static readonly string[] AngryScale = { "chill", "annoyed", "furious", "extremely furious" };
        static readonly string[] FavorScale = { "very negative", "negative", "neutral", "positive", "very positive" };

        public static void Learn(string systemPrompt)
        {
            if (string.IsNullOrEmpty(systemPrompt)) return;
            if (_learnedFrom != null && _learnedFrom == systemPrompt) return;

            Dictionary<string, List<string>> found = new Dictionary<string, List<string>>();
            foreach (string field in new[] { "npc_action", "npc_body_animation",
                "npc_face_expression", "angry_level", "favorability_change" })
            {
                // Matches "For <field>, the npc can [ONLY] choose from this list (a, b, c)".
                Match m = Regex.Match(systemPrompt,
                    @"For\s+" + field + @"\b[^(\r\n]*\(([^)]*)\)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;

                List<string> vals = new List<string>();
                foreach (string piece in m.Groups[1].Value.Split(new char[] { ',' }))
                {
                    string v = piece.Trim();
                    // Skip unresolved placeholders like {GeneratedRoom}.
                    if (v.Length == 0 || v.IndexOf('{') >= 0) continue;
                    if (!vals.Contains(v)) vals.Add(v);
                }
                if (vals.Count > 0) found[field] = vals;
            }

            if (found.Count == 0) return;
            _allowed = found;
            _learnedFrom = systemPrompt;

            StringBuilder sb = new StringBuilder("Learned allowed values from the level prompt: ");
            foreach (KeyValuePair<string, List<string>> kv in found)
                sb.Append(kv.Key).Append('=').Append(kv.Value.Count).Append(' ');
            Info(sb.ToString());
        }

        // Priority order matters. The live scene is ground truth; the level
        // prompt is a description of it and is sometimes stale or contains
        // unresolved placeholders; the static table is only a safety net.
        static List<string> AllowedFor(string field)
        {
            List<string> live = GameVocab.For(field);
            if (live != null && live.Count > 0) return live;
            if (_allowed != null && _allowed.ContainsKey(field)) return _allowed[field];
            if (Fallback.ContainsKey(field)) return new List<string>(Fallback[field]);
            return null;
        }

        public static void Clamp(JObject o)
        {
            if (o == null) return;
            foreach (string field in new[] { "npc_action", "npc_target_location",
                "npc_body_animation", "npc_face_expression", "angry_level",
                "favorability_change" })
            {
                List<string> allowed = AllowedFor(field);
                if (allowed == null) continue;

                JToken tok = o[field];
                if (tok == null) continue;

                string raw = tok.Type == JTokenType.Null ? "" : tok.ToString().Trim();

                // An empty location is meaningful: it means "do not walk
                // anywhere". Leave it alone rather than trying to resolve it.
                if (field == "npc_target_location" && raw.Length == 0) continue;

                string fixedVal = Resolve(field, raw, allowed);

                if (fixedVal == null)
                {
                    // Nothing sane to map onto. Substitute a value the engine
                    // definitely understands rather than removing the key: the
                    // game reads these with currentJson["field"].Value and a
                    // deliberate neutral beats whatever a missing node yields.
                    string safe = SafeDefault(field, allowed);
                    if (raw.Length > 0)
                        Warn("Unmappable " + field + "=\"" + Trim(raw, 80)
                            + "\"; using \"" + safe + "\" instead.");
                    o[field] = safe;
                }
                else if (fixedVal != raw)
                {
                    Info("Clamped " + field + ": \"" + Trim(raw, 80) + "\" -> \"" + fixedVal + "\"");
                    o[field] = fixedVal;
                }
            }
        }

        // Words that mean "come with me" in the last thing the player typed.
        static readonly string[] FollowAsks =
        {
            "follow me", "follow us", "come with me", "come with us", "come along",
            "come here", "stay with me", "stick with me", "walk with me",
            "tag along", "let's go", "lets go", "keep up", "behind me",
            "come to me", "over here", "this way"
        };

        static readonly string[] StopAsks =
        {
            "stop following", "stay here", "stay there", "wait here", "don't follow",
            "dont follow", "stop there", "stay put", "leave me alone", "go away"
        };

        // Fields can each be individually legal yet still describe an
        // impossible turn. ShowAction only starts a walk when npc_action is a
        // movement activity AND npc_target_location resolves; a model that
        // sets one without the other produces a character who says "sure!"
        // and never moves. This reconciles the two.
        public static void Coherence(JObject o, List<TextMessage> history)
        {
            if (o == null) return;

            string action = Str(o, "npc_action");
            string target = Str(o, "npc_target_location");
            List<string> actions = AllowedFor("npc_action");
            List<string> places = AllowedFor("npc_target_location");

            bool targetLegal = target.Length > 0 && places != null && Legal(target, places) != null;
            bool wantsFollow = Asked(history, FollowAsks);
            bool wantsStop = Asked(history, StopAsks);

            // The player asked for company and the model did not wire it up.
            if (wantsFollow && !wantsStop && !IsFollow(action)
                && actions != null && Legal("following_player", actions) != null)
            {
                Info("Player asked the character to follow but npc_action was \""
                    + Trim(action, 40) + "\"; setting following_player.");
                o["npc_action"] = "following_player";
                o["npc_target_location"] = "";
                return;
            }

            // The player asked her to stop and the model left her following.
            if (wantsStop && IsFollow(action)
                && actions != null && Legal("standing", actions) != null)
            {
                Info("Player asked the character to stop following; setting standing.");
                o["npc_action"] = "standing";
                o["npc_target_location"] = "";
                return;
            }

            // Kiss and hug BEFORE the walk-repair below, or the repair destroys
            // them: rewriting kissing+player_location into walking+player_location
            // is exactly what the engine would have done to the kiss anyway
            // (ShowAction applies the location first, NPCController.cs:928-942,
            // then replaces the affection with Other at :945), only one layer
            // earlier and by our own hand. 4.0.0's first build had this rule
            // eating every kiss that named a destination, while the patch written
            // to save those kisses sat downstream looking at the already-rewritten
            // action. Her intent was the affection; the location was the accident;
            // so the location is what goes.
            if (IsAffection(action) && target.Length > 0)
            {
                Info("npc_action \"" + Trim(action, 40) + "\" arrived with npc_target_location \""
                    + Trim(target, 40) + "\"; cleared the location so the engine keeps the "
                    + "affection instead of converting it into a walk.");
                o["npc_target_location"] = "";
                return;
            }

            // A destination with no walk order: the engine needs the action too.
            if (targetLegal && !IsMove(action)
                && actions != null && Legal("walking", actions) != null)
            {
                Info("npc_target_location \"" + Trim(target, 40)
                    + "\" was set but npc_action was \"" + Trim(action, 40)
                    + "\"; setting walking so the move actually happens.");
                o["npc_action"] = "walking";
                return;
            }

            // A walk order with nowhere to go: GetTargetAreaTriggerTransform
            // would return null and the character would freeze mid-intent.
            if (action == "walking" && !targetLegal)
            {
                if (places != null && Legal("player_location", places) != null)
                {
                    Info("npc_action walking had no usable destination; "
                        + "defaulting to player_location.");
                    o["npc_target_location"] = "player_location";
                }
                else if (actions != null && Legal("standing", actions) != null)
                {
                    Info("npc_action walking had no usable destination and there is "
                        + "no player_location; falling back to standing.");
                    o["npc_action"] = "standing";
                    o["npc_target_location"] = "";
                }
                return;
            }

            // Following ignores npc_target_location; a leftover value here is
            // harmless but confuses the next turn's context.
            if (IsFollow(action) && target.Length > 0)
                o["npc_target_location"] = "";
        }

        static bool IsFollow(string a)
        {
            return a == "following_player" || a == "following_player_closely";
        }

        // Contains, not equality, matching how the engine's own kiss-in-a-row
        // counter tests the field (NPCMasterBehavior_Main_L1.cs:130) - whatever
        // variants the engine tolerates, this tolerates.
        static bool IsAffection(string a)
        {
            if (string.IsNullOrEmpty(a)) return false;
            return a.IndexOf("kiss", StringComparison.OrdinalIgnoreCase) >= 0
                || a.IndexOf("hug", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsMove(string a)
        {
            return a == "walking" || IsFollow(a);
        }

        static string Str(JObject o, string field)
        {
            JToken t = o[field];
            if (t == null || t.Type == JTokenType.Null) return "";
            return t.ToString().Trim();
        }

        // Only the most recent player line counts. Scanning further back makes
        // the character re-follow on every turn after a single old request.
        static bool Asked(List<TextMessage> history, string[] phrases)
        {
            if (history == null) return false;
            string last = null;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                TextMessage m = history[i];
                if (m == null || m.content == null) continue;
                string role = m.role == null ? "" : m.role.ToLowerInvariant();
                if (role == "system" || role == "assistant") continue;
                last = m.content.ToLowerInvariant();
                break;
            }
            if (last == null) return false;

            for (int i = 0; i < phrases.Length; i++)
                if (last.IndexOf(phrases[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static string Resolve(string field, string raw, List<string> allowed)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            // 1. Already legal.
            for (int i = 0; i < allowed.Count; i++)
                if (string.Equals(allowed[i], raw, StringComparison.OrdinalIgnoreCase)) return allowed[i];

            string norm = Normalize(raw);

            // 2. Legal once spacing/underscores/case are normalized.
            for (int i = 0; i < allowed.Count; i++)
                if (Normalize(allowed[i]) == norm) return allowed[i];

            // 3. Numeric or boolean answers on the two scaled fields.
            if (field == "angry_level" || field == "favorability_change")
            {
                string scaled = FromNumber(field, raw, allowed);
                if (scaled != null) return scaled;
            }

            // 4a. A synonym scoped to this field beats the shared table.
            Dictionary<string, string> scoped;
            if (FieldSynonyms.TryGetValue(field, out scoped))
            {
                string hit;
                if (scoped.TryGetValue(norm.Replace(' ', '_'), out hit)
                    || scoped.TryGetValue(norm, out hit))
                {
                    string ok = Legal(hit, allowed);
                    if (ok != null) return ok;
                }
            }

            // 4b. Known synonym, but only if the target is legal for this level.
            string syn;
            if (Synonyms.TryGetValue(norm.Replace(' ', '_'), out syn) || Synonyms.TryGetValue(norm, out syn))
                for (int i = 0; i < allowed.Count; i++)
                    if (string.Equals(allowed[i], syn, StringComparison.OrdinalIgnoreCase)) return allowed[i];

            // 5. A legal value appears inside a prose answer ("walks over holding a glass of water").
            //    Prefer the longest match so "angry_face" wins over "angry".
            string best = null;
            int bestPos = int.MaxValue, bestSize = 0;
            for (int i = 0; i < allowed.Count; i++)
            {
                string cand = Normalize(allowed[i]);
                if (cand.Length < 3) continue;
                Match cm = Regex.Match(norm, @"(^|\W)" + Regex.Escape(cand) + @"(\W|$)");
                if (!cm.Success) continue;
                // Earliest wins, then longest, so "angry_face" beats a later bare "angry".
                if (cm.Index < bestPos || (cm.Index == bestPos && cand.Length > bestSize))
                {
                    bestPos = cm.Index; bestSize = cand.Length; best = allowed[i];
                }
            }
            if (best != null) return best;

            // 6. A synonym appears inside the prose. Compound answers such as
            //    "relieved_and_slightly_concerned" contain several, so pick by position rather
            //    than by dictionary order: the leading emotion is the one being reported, and
            //    relying on hash iteration order would make the result arbitrary.
            string bestSyn = null;
            int bestAt = int.MaxValue, bestLen = 0;
            foreach (KeyValuePair<string, string> kv in Synonyms)
            {
                string key = Normalize(kv.Key);
                if (key.Length < 4) continue;
                Match sm = Regex.Match(norm, @"(^|\W)" + Regex.Escape(key) + @"(\W|$)");
                if (!sm.Success) continue;

                string target = Legal(kv.Value, allowed);
                if (target == null) continue;

                int at = sm.Index;
                if (at < bestAt || (at == bestAt && key.Length > bestLen))
                {
                    bestAt = at; bestLen = key.Length; bestSyn = target;
                }
            }
            return bestSyn;
        }

        // Models emit angry_level: 0 or favorability_change: 1 despite being told to use words.
        // Map onto the scale; a 0..1 float is treated as a fraction of the range.
        static string FromNumber(string field, string raw, List<string> allowed)
        {
            bool b;
            if (bool.TryParse(raw, out b))
                return Legal(field == "angry_level" ? (b ? "furious" : "normal")
                                                    : (b ? "positive" : "negative"), allowed);

            double d;
            if (!double.TryParse(raw, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out d))
                return null;

            string[] scale = field == "angry_level" ? AngryScale : FavorScale;

            if (field == "favorability_change")
            {
                // Signed: negative means lost trust, 0 neutral, positive gained.
                int idx = d < -1.0 ? 0 : d < 0 ? 1 : d == 0 ? 2 : d <= 1.0 ? 3 : 4;
                return Legal(scale[idx], allowed);
            }

            // angry_level: 0 = calm. Treat 0..1 as a fraction, larger values as an index.
            double frac = (d > 0 && d <= 1.0) ? d : d / 10.0;
            if (d > 1.0 && d <= scale.Length - 1) frac = d / (scale.Length - 1);
            if (frac < 0) frac = 0;
            if (frac > 1) frac = 1;
            int i2 = (int)Math.Round(frac * (scale.Length - 1));
            // 0 means calm, which is index 0 of this scale.
            if (d == 0) i2 = 0;
            return Legal(scale[i2], allowed);
        }

        // A neutral the engine is guaranteed to accept for each field, used when a
        // reply cannot be mapped at all. Preference order per field, then the
        // first allowed value, then empty as the last resort.
        static string SafeDefault(string field, List<string> allowed)
        {
            string[] prefs;
            switch (field)
            {
                case "npc_action":          prefs = new[] { "other", "idle", "standing" }; break;
                case "npc_body_animation":  prefs = new[] { "idle", "standing" }; break;
                case "angry_level":         prefs = new[] { "chill" }; break;
                case "favorability_change": prefs = new[] { "neutral", "no change" }; break;
                // An empty target means "stay put", which the engine handles cleanly.
                case "npc_target_location": return "";
                // Faces are per-character assets; an unknown id simply leaves the
                // current expression in place, so empty is the honest answer.
                case "npc_face_expression": return "";
                default:                    prefs = new string[0]; break;
            }
            for (int i = 0; i < prefs.Length; i++)
            {
                string ok = Legal(prefs[i], allowed);
                if (ok != null) return ok;
            }
            return allowed.Count > 0 ? allowed[0] : "";
        }

        static string Legal(string want, List<string> allowed)
        {
            for (int i = 0; i < allowed.Count; i++)
                if (string.Equals(allowed[i], want, StringComparison.OrdinalIgnoreCase)) return allowed[i];
            return null;
        }

        static string Normalize(string s)
        {
            if (s == null) return "";
            string t = s.ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
            t = Regex.Replace(t, @"[^a-z0-9 ]", " ");
            return Regex.Replace(t, @"\s+", " ").Trim();
        }

        static string Trim(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        // Clamping must never throw on account of logging: it also runs from the offline test
        // harness, where BepInEx never initializes Plugin.Log.
        static void Info(string msg) { if (Plugin.Log != null) Plugin.Log.LogInfo(msg); }
        static void Warn(string msg) { if (Plugin.Log != null) Plugin.Log.LogWarning(msg); }
    }

    /// <summary>Running totals so a session's real spend is visible in the log.</summary>
    public static class Session
    {
        public static int Requests;
        public static double Cost;
    }

    public static class Bridge
    {
        public static IEnumerator Send(ChatGPTConversation conv, List<TextMessage> history, Action<string, int> errorCallback)
        {
            // Re-read the scene every turn: locations are populated per level,
            // and the cast can change, so a cached list goes stale on a
            // level transition.
            //
            // Deliberately UNCONDITIONAL. This used to sit behind
            // CfgClampValues, which quietly made a debug switch delete the whole
            // engine contract: Refresh() is the only writer of Actions,
            // Contract() early-returns on an empty Actions list, and Contract()
            // is the sole carrier of the progression gates, the encounter fields
            // and the reply-text rules. So "turn this off to see what the model
            // emits raw" also removed the Dark Siren win condition, every door
            // unlock, and the warning that a square bracket voids the reply and
            // starts a permanent hunt on the second offence - all silently.
            //
            // CfgClampValues now gates only the repair pass (Schema.Clamp and
            // Coherence), which is what its description actually promises.
            GameVocab.Refresh();

            string url = CombineUrl(Plugin.CfgBaseUrl.Value, "chat/completions");
            string body = BuildRequest(history);

            if (Plugin.CfgLogPayloads.Value)
                Plugin.Log.LogInfo("--> " + url + "\n" + Trim(body, 4000));

            int attempts = Plugin.CfgRetries.Value + 1;
            string lastError = "unknown error";

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                UnityWebRequest req = new UnityWebRequest(url, "POST");
                byte[] payload = Encoding.UTF8.GetBytes(body);
                req.uploadHandler = new UploadHandlerRaw(payload);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + Plugin.CfgApiKey.Value);
                req.SetRequestHeader("HTTP-Referer", "https://github.com/ai2u-custom-ai");
                req.SetRequestHeader("X-Title", "AI2U Custom AI");

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    lastError = req.error + " | " + Safe(req.downloadHandler);
                    Plugin.Log.LogError("HTTP failure (attempt " + attempt + "/" + attempts + "): " + lastError);
                    req.Dispose();
                    continue;
                }

                string raw = req.downloadHandler.text;
                req.Dispose();

                if (Plugin.CfgLogPayloads.Value)
                    Plugin.Log.LogInfo("<-- " + Trim(raw, 4000));

                string content = null;
                int completionTokens = 0;
                int totalTokens = 0;
                try
                {
                    JObject o = JObject.Parse(raw);
                    JToken err = o["error"];
                    if (err != null)
                    {
                        lastError = err.ToString();
                        Plugin.Log.LogError("Endpoint returned an error: " + Trim(lastError, 1000));
                        continue;
                    }
                    content = (string)o["choices"][0]["message"]["content"];
                    JToken usage = o["usage"];
                    if (usage != null)
                    {
                        if (usage["completion_tokens"] != null) completionTokens = (int)usage["completion_tokens"];
                        if (usage["total_tokens"] != null) totalTokens = (int)usage["total_tokens"];

                        int promptTokens = usage["prompt_tokens"] != null ? (int)usage["prompt_tokens"] : 0;
                        double cost = usage["cost"] != null ? (double)usage["cost"] : 0.0;
                        Session.Requests++;
                        Session.Cost += cost;
                        Plugin.Log.LogInfo(string.Format(
                            "billing: prompt={0} completion={1} cost=${2:F6} | session: {3} calls ${4:F4}",
                            promptTokens, completionTokens, cost, Session.Requests, Session.Cost));
                    }
                }
                catch (Exception e)
                {
                    lastError = "could not read the response envelope: " + e.Message;
                    Plugin.Log.LogError(lastError);
                    continue;
                }

                JObject reactions = ExtractReactions(content);
                if (reactions == null)
                {
                    lastError = "model did not return usable NPC JSON";
                    Plugin.Log.LogWarning("Attempt " + attempt + "/" + attempts + ": " + lastError
                        + ". Raw content: " + Trim(content, 600));
                    continue;
                }

                if (Plugin.CfgClampValues.Value)
                {
                    Schema.Clamp(reactions);
                    Schema.Coherence(reactions, history);
                }

                // Both after clamping, so angry_level is already a legal value, and
                // before the envelope, so the swapped line is what the game and
                // SpeechDispatch both see. Each strips its own helper fields.
#if CANALPA
                // BEFORE Murder.Apply, not after. Its willing ending is handed to
                // Murder as a request rather than started here, so that Murder can
                // swap her line for npc_final_words the way it does for every other
                // route into the chase. Run in the other order the request would
                // arrive one turn late and she would say something ordinary over
                // the top of the ending.
                //
                // Also before the envelope reaches the game, because these fields
                // are the mod's own invention and the engine has no idea what they
                // mean. Apply acts on them and then strips them either way.
                Canalpa.Apply(reactions);
#endif

                Murder.Apply(reactions);

                // Last, and still before the envelope. It arms the trust
                // multiplier that the game's own UpdateTrustLevel consumes when it
                // reads favorability_change out of this same payload, so arming it
                // any later would miss the turn it belongs to. Strips its fields
                // whether or not either toggle is on.
                Feelings.Apply(reactions);

                // Not part of Clamp: an unlisted item name is legal here and
                // becomes an IsAiGift item, so this repairs only what the engine
                // would have discarded without a word - chiefly names over its
                // 20-character limit.
                Items.Repair(reactions);

                // Echo the speaker the game already believes in. Communicator
                // assigns this straight back into currentCharacterID without
                // validating it, and the enum has no zero member, so leaving it
                // out resolves to an undefined character - which misfiles this
                // NPC's chat history and can throw on the voice lookup. Set here
                // rather than asked of the model, because the game knows the
                // answer and the model would only guess it.
                int? speaker = Identity.CharacterId();
                if (speaker.HasValue) reactions["character"] = speaker.Value;

                JObject envelope = new JObject();
                envelope["npc_reactions"] = reactions;
                envelope["completion"] = completionTokens;
                envelope["total"] = totalTokens;

                string final = envelope.ToString(Formatting.None);
                if (Plugin.CfgLogPayloads.Value)
                    Plugin.Log.LogInfo("==> handing to game: " + Trim(final, 2000));

                // The Steam build speaks through AzureAISpeak, which is handed the
                // server's speechResult - a field a custom endpoint never sends.
                // Kept here so that path has real text to say.
                SpeechDispatch.Remember(final);

                ChatGPTConversation.isInAsync = false;
                Traverse.Create(conv).Method("ResolveChatGPTAzure", new object[] { final }).GetValue();
                yield break;
            }

            ChatGPTConversation.isInAsync = false;
            Plugin.Log.LogError("Giving up after " + attempts + " attempt(s): " + Trim(lastError, 500));
            if (errorCallback != null)
                errorCallback(lastError, 0);
        }

        static string BuildRequest(List<TextMessage> history)
        {
            JArray messages = new JArray();
            for (int i = 0; i < history.Count; i++)
            {
                TextMessage m = history[i];
                if (m == null || m.content == null) continue;
                JObject jm = new JObject();
                string role = NormalizeRole(m.role);

                // Communicator.cs:165 seeds slot 0 of every character's history
                // with an empty system message and the shipped build never fills
                // it in, because the real prompt is assembled server-side from the
                // x-token header. Forwarding an empty system message is not
                // harmful, just pointless - Lore.Block() below is what goes in its
                // place.
                if (role == "system" && m.content.Trim().Length == 0) continue;
                jm["role"] = role;
                jm["content"] = m.content;
                messages.Add(jm);

                // The level's system prompt enumerates the legal values; learn them from it so
                // clamping tracks whichever level is loaded instead of a hardcoded guess.
                if (role == "system" && Plugin.CfgClampValues.Value)
                    Schema.Learn(m.content);
            }

            // Who she is and what she knows. First of the injected blocks on
            // purpose: it is the level prompt's replacement, so everything after
            // it - the engine whitelist, the names, the danger state - reads as a
            // correction to it rather than the other way round.
            string lore = Lore.Block();
            if (lore != null)
            {
                JObject lo = new JObject();
                lo["role"] = "system";
                lo["content"] = lore;
                messages.Add(lo);
                Lore.Report();
            }

            // The difficulty disposition, straight after the persona so it reads
            // as part of WHO SHE IS rather than as a rule imposed from outside.
            // Null on Normal - the request is then byte-identical to the slider
            // not existing, which is what "Normal is the base game" means.
            string diff = Difficulty.Block();
            if (diff != null)
            {
                JObject df = new JObject();
                df["role"] = "system";
                df["content"] = diff;
                messages.Add(df);
            }

            // How her own home works. Straight after the lore because it is the
            // same subject continued: the lore block hands her the answers, this
            // hands her the machinery those answers go into. Nothing here varies
            // per playthrough except the bookshelf order, which it reads live.
            string mech = Mechanics.Block();
            if (mech != null)
            {
                JObject mo = new JObject();
                mo["role"] = "system";
                mo["content"] = mech;
                messages.Add(mo);
            }

            // Her temper, and under hard difficulty how much this turn is allowed
            // to count. Placed after the mechanics block because it is about her
            // rather than about the house, and the schema block below still gets
            // the last word on which field names are legal.
            string feel = Feelings.Block();
            if (feel != null)
            {
                JObject fo = new JObject();
                fo["role"] = "system";
                fo["content"] = feel;
                messages.Add(fo);
            }

            // The whitelist read off the live scene. This goes in as its own
            // system message after the level prompt so it wins any conflict:
            // the level prompt describes the world, but only these values
            // survive contact with the engine.
            string contract = GameVocab.Contract();
            if (contract != null)
            {
                JObject engine = new JObject();
                engine["role"] = "system";
                engine["content"] = contract;
                messages.Add(engine);
            }

            // The names the player chose. The level prompt that would normally
            // carry them is substituted server-side, so without this she has no
            // idea what she is called and picks something new each session.
            string identity = Identity.Block();
            if (identity != null)
            {
                JObject who = new JObject();
                who["role"] = "system";
                who["content"] = identity;
                messages.Add(who);
                Identity.Report();
            }

            // What she can actually hand over. The giving_to_player field is
            // only documented in the server-side level prompt we replace, so
            // without this block the model never learns it exists and every
            // "here you go" hands over nothing.
            string gifts = Items.Block();
            if (gifts != null)
            {
                JObject gi = new JObject();
                gi["role"] = "system";
                gi["content"] = gifts;
                messages.Add(gi);
                Items.Report();
            }

            // Where she stands on anger, trust and whether a chase is already
            // running. Without this she argues politely while the engine has her
            // charging with a knife, because the engine replaces npc_action and
            // never touches the line she wrote.
            string danger = Murder.Block();
            if (danger != null)
            {
                JObject dg = new JObject();
                dg["role"] = "system";
                dg["content"] = danger;
                messages.Add(dg);
            }

            // The game's schema is strict; nudge models that like to wrap output in prose or fences.
            JObject reminder = new JObject();
            reminder["role"] = "system";
            reminder["content"] = "Reply with a single raw JSON object only. No markdown fences, no commentary, "
                + "no reasoning. Every value must be copied exactly from the allowed lists given above - do not "
                + "invent new values, do not use numbers where a listed word is required, and do not write prose "
                + "into fields like npc_action or npc_face_expression. Always include npc_reply_to_player. "
                + "When the player asks the character to go somewhere or to come along, you MUST set the "
                + "movement fields, not merely agree in the dialogue text: agreeing in npc_reply_to_player "
                + "while leaving npc_action as \"other\" makes the character stand still and read as ignoring "
                + "the player. The same applies to handing something over: when you offer, promise or give "
                + "the player an object, you MUST also set giving_to_player to that item's bare name, or "
                + "nothing is transferred. giving_to_player is free text and is exempt from the allowed-value "
                + "lists, but it is still capped at 20 characters.";
            messages.Add(reminder);

            // Last on purpose, so the persona-drop instruction is the most recent
            // thing the model reads and wins against the character prompt above
            // it. It does not undercut the reminder: OOC keeps the same JSON
            // envelope and the same allowed values, and only changes what she
            // says. Null on every untagged turn, and null always while the
            // feature is off, so an ordinary request is unchanged byte for byte.
            string ooc = Ooc.Block();
            if (ooc != null)
            {
                JObject oo = new JObject();
                oo["role"] = "system";
                oo["content"] = ooc;
                messages.Add(oo);
            }

            // Canalpa's offers, on the turns they are actually available. Placed
            // after the engine contract so the extra field reads as an addition to
            // the schema rather than something the contract forgot, and null on
            // every turn the gate does not pass - including always, while the mode
            // is off - so an ordinary request is unchanged byte for byte.
#if CANALPA
            string canalpa = Canalpa.Block();
            if (canalpa != null)
            {
                JObject co = new JObject();
                co["role"] = "system";
                co["content"] = canalpa;
                messages.Add(co);
            }
#endif

            JObject root = new JObject();
            root["model"] = Plugin.CfgModel.Value;
            root["messages"] = messages;
            root["temperature"] = Plugin.CfgTemperature.Value;
            root["max_tokens"] = Plugin.CfgMaxTokens.Value;

            if (Plugin.CfgHideReasoning.Value)
            {
                JObject reasoning = new JObject();
                reasoning["exclude"] = true;
                root["reasoning"] = reasoning;
            }
            if (Plugin.CfgJsonMode.Value)
            {
                JObject rf = new JObject();
                rf["type"] = "json_object";
                root["response_format"] = rf;
            }

            // OpenRouter omits token accounting unless it is asked for, which makes a paid session
            // look free. Opting in lets us log the real per-request cost.
            JObject usageOpt = new JObject();
            usageOpt["include"] = true;
            root["usage"] = usageOpt;

            return root.ToString(Formatting.None);
        }

        static string NormalizeRole(string role)
        {
            if (string.IsNullOrEmpty(role)) return "user";
            string r = role.ToLowerInvariant();
            if (r == "system" || r == "assistant" || r == "user") return r;
            if (r == "ai" || r == "bot" || r == "chatgpt") return "assistant";
            return "user";
        }

        // Models wrap JSON in fences or prose; pull out the first balanced object.
        public static JObject ExtractReactions(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;

            string s = content.Trim();
            int fence = s.IndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                int start = s.IndexOf('{', fence);
                if (start >= 0) s = s.Substring(start);
            }

            int open = s.IndexOf('{');
            if (open < 0) return null;

            int depth = 0;
            bool inStr = false;
            bool esc = false;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        string candidate = s.Substring(open, i - open + 1);
                        try
                        {
                            JObject o = JObject.Parse(candidate);
                            // Unwrap if the model echoed the outer envelope shape.
                            if (o["npc_reactions"] != null && o["npc_reactions"].Type == JTokenType.Object)
                                o = (JObject)o["npc_reactions"];
                            if (o["npc_reply_to_player"] == null) return null;
                            return o;
                        }
                        catch { return null; }
                    }
                }
            }
            return null;
        }

        static string CombineUrl(string base_, string path)
        {
            // Share the Test button's logic so a URL that only works in one of
            // the common forms behaves the same in play as it does under test.
            List<string> c = ModUiPatch.ChatUrlCandidates(base_);
            return c.Count > 0 ? c[0] : "https://openrouter.ai/api/v1/chat/completions";
        }

        static string Safe(DownloadHandler dh)
        {
            try { return dh == null ? "" : Trim(dh.text, 800); }
            catch { return ""; }
        }

        static string Trim(string s, int max)
        {
            if (s == null) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "... [" + s.Length + " chars]";
        }
    }

}
