using System;
using System.IO;
using BepInEx;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AI2UCustomAI
{
    public static class ProfileManager
    {
        public static int CurrentProfile = 1;

        public static string GetProfilePath(int profileNum)
        {
            string dir = Paths.ConfigPath;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                try { dir = Path.Combine(Application.dataPath, "..", "BepInEx", "config"); }
                catch (Exception) { dir = "."; }
            }
            return Path.Combine(dir, "AI2UCustomAI_profile" + profileNum + ".json");
        }

        public static void Init()
        {
            if (Plugin.CfgActiveProfile != null)
            {
                int p = Plugin.CfgActiveProfile.Value;
                if (p < 1 || p > 3) p = 1;
                CurrentProfile = p;
            }

            string path = GetProfilePath(CurrentProfile);
            if (!File.Exists(path))
            {
                SaveProfile(CurrentProfile);
            }
        }

        public static void SwitchToProfile(int newProfile)
        {
            if (newProfile < 1 || newProfile > 3) return;

            // Commit and save current active profile first
            OverlayMenu.CommitChanges();
            SaveProfile(CurrentProfile);

            CurrentProfile = newProfile;
            if (Plugin.CfgActiveProfile != null)
            {
                Plugin.CfgActiveProfile.Value = newProfile;
                Plugin.SaveCfg();
            }

            string path = GetProfilePath(newProfile);
            if (File.Exists(path))
            {
                LoadProfile(newProfile);
            }
            else
            {
                // New profile initialisation from current state
                SaveProfile(newProfile);
            }

            OverlayMenu.ReloadFromProfile();
            OverlayMenu.PostNote("Switched to Profile " + newProfile + ". Settings loaded!");
        }

        public static void SaveProfile(int profileNum)
        {
            try
            {
                JObject root = new JObject();
                JObject strings = new JObject();
                JObject ints = new JObject();
                JObject floats = new JObject();
                JObject bools = new JObject();

                // Strings
                if (Plugin.CfgBaseUrl != null) strings["BaseUrl"] = Plugin.CfgBaseUrl.Value ?? "";
                if (Plugin.CfgApiKey != null) strings["ApiKey"] = Plugin.CfgApiKey.Value ?? "";
                if (Plugin.CfgModel != null) strings["Model"] = Plugin.CfgModel.Value ?? "";
                if (Plugin.CfgOpenRouterProvider != null) strings["OpenRouterProvider"] = Plugin.CfgOpenRouterProvider.Value ?? "";
                if (Plugin.CfgTtsProvider != null) strings["TtsProvider"] = Plugin.CfgTtsProvider.Value ?? "";
                if (Plugin.CfgGrokBaseUrl != null) strings["GrokBaseUrl"] = Plugin.CfgGrokBaseUrl.Value ?? "";
                if (Plugin.CfgGrokApiKey != null) strings["GrokApiKey"] = Plugin.CfgGrokApiKey.Value ?? "";
                if (Plugin.CfgTtsModel != null) strings["TtsModel"] = Plugin.CfgTtsModel.Value ?? "";
                if (Plugin.CfgGrokVoiceId != null) strings["GrokVoiceId"] = Plugin.CfgGrokVoiceId.Value ?? "";
                if (Plugin.CfgGrokLanguage != null) strings["GrokLanguage"] = Plugin.CfgGrokLanguage.Value ?? "";
                if (Plugin.CfgTestKillPhrase != null) strings["TestKillPhrase"] = Plugin.CfgTestKillPhrase.Value ?? "";
                if (Plugin.CfgOocTag != null) strings["OocTag"] = Plugin.CfgOocTag.Value ?? "";
                if (Plugin.CfgTimeskipTag != null) strings["TimeskipTag"] = Plugin.CfgTimeskipTag.Value ?? "";
                if (Plugin.CfgGameVoiceKey != null) strings["GameVoiceKey"] = Plugin.CfgGameVoiceKey.Value ?? "";
                if (Plugin.CfgGameVoiceRegion != null) strings["GameVoiceRegion"] = Plugin.CfgGameVoiceRegion.Value ?? "";
                if (Plugin.CfgVoiceChoice != null) strings["VoiceChoice"] = Plugin.CfgVoiceChoice.Value ?? "";

                if (Voices.Names != null)
                {
                    JObject vObj = new JObject();
                    for (int i = 0; i < Voices.Names.Length; i++)
                    {
                        var entry = Voices.Entry(Voices.Names[i]);
                        if (entry != null) vObj[Voices.Names[i]] = entry.Value ?? "";
                    }
                    root["character_voices"] = vObj;

                    // Azure's own per-character voices (5.6), kept apart from the
                    // Custom Endpoint ones above for the reason given in Voices.cs.
                    JObject azObj = new JObject();
                    for (int i = 0; i < Voices.Names.Length; i++)
                    {
                        var az = Voices.AzureEntry(Voices.Names[i]);
                        if (az != null) azObj[Voices.Names[i]] = az.Value ?? "";
                    }
                    root["azure_character_voices"] = azObj;

                    // Her own Azure pitch and speed (5.7); "" is the default.
                    JObject tuneObj = new JObject();
                    for (int i = 0; i < Voices.Names.Length; i++)
                    {
                        JObject t = new JObject();
                        var pe = Voices.AzurePitchEntry(Voices.Names[i]);
                        var se = Voices.AzureSpeedEntry(Voices.Names[i]);
                        t["pitch"] = pe != null ? (pe.Value ?? "") : "";
                        t["speed"] = se != null ? (se.Value ?? "") : "";
                        tuneObj[Voices.Names[i]] = t;
                    }
                    root["azure_character_tuning"] = tuneObj;
                }

                // Ints
                if (Plugin.CfgMaxTokens != null) ints["MaxTokens"] = Plugin.CfgMaxTokens.Value;
                if (Plugin.CfgReplyWordLimit != null) ints["ReplyWordLimit"] = Plugin.CfgReplyWordLimit.Value;
                if (Plugin.CfgRetries != null) ints["Retries"] = Plugin.CfgRetries.Value;
                if (Plugin.CfgHistoryMaxTokens != null) ints["HistoryMaxTokens"] = Plugin.CfgHistoryMaxTokens.Value;
                if (Plugin.CfgGrokSampleRate != null) ints["GrokSampleRate"] = Plugin.CfgGrokSampleRate.Value;
                if (Plugin.CfgCustomFavorabilityPercent != null) ints["CustomFavorabilityPercent"] = Plugin.CfgCustomFavorabilityPercent.Value;

                // Floats
                if (Plugin.CfgTemperature != null) floats["Temperature"] = Plugin.CfgTemperature.Value;
                if (Plugin.CfgGrokSpeed != null) floats["GrokSpeed"] = Plugin.CfgGrokSpeed.Value;
                if (Plugin.CfgTtsVolume != null) floats["TtsVolume"] = Plugin.CfgTtsVolume.Value;

                // Bools
                if (Plugin.CfgEnabled != null) bools["Enabled"] = Plugin.CfgEnabled.Value;
                if (Plugin.CfgLogPayloads != null) bools["LogPayloads"] = Plugin.CfgLogPayloads.Value;
                if (Plugin.CfgHideReasoning != null) bools["HideReasoning"] = Plugin.CfgHideReasoning.Value;
                if (Plugin.CfgJsonMode != null) bools["JsonMode"] = Plugin.CfgJsonMode.Value;
                if (Plugin.CfgLocalModelMode != null) bools["LocalModelMode"] = Plugin.CfgLocalModelMode.Value;
                if (Plugin.CfgClampValues != null) bools["ClampValues"] = Plugin.CfgClampValues.Value;
                if (Plugin.CfgAiCanMurder != null) bools["AiCanMurder"] = Plugin.CfgAiCanMurder.Value;
                if (Plugin.CfgTestKillPhraseActive != null) bools["TestKillPhraseActive"] = Plugin.CfgTestKillPhraseActive.Value;
                if (Plugin.CfgOocEnabled != null) bools["OocEnabled"] = Plugin.CfgOocEnabled.Value;
                if (Plugin.CfgTimeskipEnabled != null) bools["TimeskipEnabled"] = Plugin.CfgTimeskipEnabled.Value;
                if (Plugin.CfgActionsAreReal != null) bools["ActionsAreReal"] = Plugin.CfgActionsAreReal.Value;
                if (Plugin.CfgLoreInjection != null) bools["LoreInjection"] = Plugin.CfgLoreInjection.Value;
                if (Plugin.CfgSendMechanics != null) bools["SendMechanics"] = Plugin.CfgSendMechanics.Value;
                if (Plugin.CfgSendFeelings != null) bools["SendFeelings"] = Plugin.CfgSendFeelings.Value;
                if (Plugin.CfgLetHerTemper != null) bools["LetHerTemper"] = Plugin.CfgLetHerTemper.Value;
                if (Plugin.CfgWarningShock != null) bools["WarningShock"] = Plugin.CfgWarningShock.Value;
                if (Plugin.CfgStuckRecovery != null) bools["StuckRecovery"] = Plugin.CfgStuckRecovery.Value;
                if (Plugin.CfgHardDifficulty != null) bools["HardDifficulty"] = Plugin.CfgHardDifficulty.Value;
                if (Plugin.CfgCustomFavorability != null) bools["CustomFavorability"] = Plugin.CfgCustomFavorability.Value;
                if (Plugin.CfgForceLocalVoice != null) bools["ForceLocalVoice"] = Plugin.CfgForceLocalVoice.Value;
                if (Plugin.CfgGameVoice != null) bools["GameVoice"] = Plugin.CfgGameVoice.Value;
                if (Plugin.CfgAzureExpressive != null) bools["AzureExpressive"] = Plugin.CfgAzureExpressive.Value;
                if (Plugin.CfgAzureOriginalTuning != null) bools["AzureOriginalTuning"] = Plugin.CfgAzureOriginalTuning.Value;
                if (Plugin.CfgGrokEnabled != null) bools["GrokEnabled"] = Plugin.CfgGrokEnabled.Value;
                if (Plugin.CfgGrokNormalize != null) bools["GrokNormalize"] = Plugin.CfgGrokNormalize.Value;
                if (Plugin.CfgTtsNormalize != null) bools["TtsNormalize"] = Plugin.CfgTtsNormalize.Value;
                if (Plugin.CfgOpenRouterAllowFallback != null) bools["OpenRouterAllowFallback"] = Plugin.CfgOpenRouterAllowFallback.Value;
                if (Plugin.CfgSpeakActions != null) bools["SpeakActions"] = Plugin.CfgSpeakActions.Value;
                if (Plugin.CfgBlockGameAi != null) bools["BlockGameAi"] = Plugin.CfgBlockGameAi.Value;
                if (Plugin.CfgOwnExtras != null) bools["OwnExtras"] = Plugin.CfgOwnExtras.Value;

                root["strings"] = strings;
                root["ints"] = ints;
                root["floats"] = floats;
                root["bools"] = bools;

                string path = GetProfilePath(profileNum);
                string parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent);

                File.WriteAllText(path, root.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null) Plugin.Log.LogError("Failed to save Profile " + profileNum + ": " + ex.Message);
            }
        }

        // A tuning value as text, whatever shape a hand-edited profile gives it;
        // anything that is not a plain value is "default" rather than an error
        // that would abandon the rest of the profile half-loaded.
        static string TuningText(JObject t, string field)
        {
            JValue v = t != null ? t[field] as JValue : null;
            if (v == null || v.Value == null) return "";
            return Convert.ToString(v.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }

        public static void LoadProfile(int profileNum)
        {
            try
            {
                string path = GetProfilePath(profileNum);
                if (!File.Exists(path)) return;

                string json = File.ReadAllText(path);
                JObject root = JObject.Parse(json);

                JObject strings = root["strings"] as JObject;
                if (strings != null)
                {
                    if (strings["BaseUrl"] != null && Plugin.CfgBaseUrl != null) Plugin.CfgBaseUrl.Value = (string)strings["BaseUrl"];
                    if (strings["ApiKey"] != null && Plugin.CfgApiKey != null) Plugin.CfgApiKey.Value = (string)strings["ApiKey"];
                    if (strings["Model"] != null && Plugin.CfgModel != null) Plugin.CfgModel.Value = (string)strings["Model"];
                    if (strings["OpenRouterProvider"] != null && Plugin.CfgOpenRouterProvider != null) Plugin.CfgOpenRouterProvider.Value = (string)strings["OpenRouterProvider"];
                    if (strings["TtsProvider"] != null && Plugin.CfgTtsProvider != null) Plugin.CfgTtsProvider.Value = (string)strings["TtsProvider"];
                    if (strings["GrokBaseUrl"] != null && Plugin.CfgGrokBaseUrl != null) Plugin.CfgGrokBaseUrl.Value = (string)strings["GrokBaseUrl"];
                    if (strings["GrokApiKey"] != null && Plugin.CfgGrokApiKey != null) Plugin.CfgGrokApiKey.Value = (string)strings["GrokApiKey"];
                    if (strings["TtsModel"] != null && Plugin.CfgTtsModel != null) Plugin.CfgTtsModel.Value = (string)strings["TtsModel"];
                    if (strings["GrokVoiceId"] != null && Plugin.CfgGrokVoiceId != null) Plugin.CfgGrokVoiceId.Value = (string)strings["GrokVoiceId"];
                    if (strings["GrokLanguage"] != null && Plugin.CfgGrokLanguage != null) Plugin.CfgGrokLanguage.Value = (string)strings["GrokLanguage"];
                    if (strings["TestKillPhrase"] != null && Plugin.CfgTestKillPhrase != null) Plugin.CfgTestKillPhrase.Value = (string)strings["TestKillPhrase"];
                    if (strings["OocTag"] != null && Plugin.CfgOocTag != null) Plugin.CfgOocTag.Value = (string)strings["OocTag"];
                    if (strings["TimeskipTag"] != null && Plugin.CfgTimeskipTag != null) Plugin.CfgTimeskipTag.Value = (string)strings["TimeskipTag"];
                    if (strings["GameVoiceKey"] != null && Plugin.CfgGameVoiceKey != null) Plugin.CfgGameVoiceKey.Value = (string)strings["GameVoiceKey"];
                    if (strings["GameVoiceRegion"] != null && Plugin.CfgGameVoiceRegion != null) Plugin.CfgGameVoiceRegion.Value = (string)strings["GameVoiceRegion"];
                    if (strings["VoiceChoice"] != null && Plugin.CfgVoiceChoice != null) Plugin.CfgVoiceChoice.Value = (string)strings["VoiceChoice"];
                }

                JObject vObj = root["character_voices"] as JObject;
                if (vObj != null && Voices.Names != null)
                {
                    for (int i = 0; i < Voices.Names.Length; i++)
                    {
                        string cName = Voices.Names[i];
                        if (vObj[cName] != null)
                        {
                            var entry = Voices.Entry(cName);
                            if (entry != null) entry.Value = (string)vObj[cName];
                        }
                    }
                }

                // Older profiles have no Azure section; leaving the fields alone is the
                // right reading of "this profile never set them".
                JObject azObj = root["azure_character_voices"] as JObject;
                if (azObj != null && Voices.Names != null)
                {
                    for (int i = 0; i < Voices.Names.Length; i++)
                    {
                        string cName = Voices.Names[i];
                        if (azObj[cName] == null) continue;
                        var az = Voices.AzureEntry(cName);
                        if (az != null) az.Value = (string)azObj[cName];
                    }
                }
                else if (Voices.Names != null)
                {
                    // A profile saved before 5.6 has no Azure section. Leaving the
                    // fields alone meant it kept the PREVIOUS profile's Azure voices,
                    // and the next save wrote them into this profile for good. Its
                    // own shared voices are the only evidence of what it wanted, so
                    // they are read the way the one-time migration reads them.
                    JObject shared = root["character_voices"] as JObject;
                    for (int i = 0; i < Voices.Names.Length; i++)
                    {
                        var az = Voices.AzureEntry(Voices.Names[i]);
                        if (az == null) continue;
                        string v = shared != null && shared[Voices.Names[i]] != null
                            ? ((string)shared[Voices.Names[i]] ?? "").Trim() : "";
                        az.Value = Voices.AzureCandidate(v) ? v : "";
                    }
                }

                // A profile saved before 5.7 has no tuning. It never asked for
                // any, so it gets the default - not whatever the previous profile
                // happened to hold, which the next save would make its own.
                if (Voices.Names != null)
                {
                    JObject tuneObj = root["azure_character_tuning"] as JObject;
                    for (int i = 0; i < Voices.Names.Length; i++)
                    {
                        JObject t = tuneObj != null ? tuneObj[Voices.Names[i]] as JObject : null;
                        var pe = Voices.AzurePitchEntry(Voices.Names[i]);
                        var se = Voices.AzureSpeedEntry(Voices.Names[i]);
                        if (pe != null) pe.Value = TuningText(t, "pitch");
                        if (se != null) se.Value = TuningText(t, "speed");
                    }
                }

                JObject ints = root["ints"] as JObject;
                if (ints != null)
                {
                    if (ints["MaxTokens"] != null && Plugin.CfgMaxTokens != null) Plugin.CfgMaxTokens.Value = (int)ints["MaxTokens"];
                    if (ints["ReplyWordLimit"] != null && Plugin.CfgReplyWordLimit != null) Plugin.CfgReplyWordLimit.Value = (int)ints["ReplyWordLimit"];
                    if (ints["Retries"] != null && Plugin.CfgRetries != null) Plugin.CfgRetries.Value = (int)ints["Retries"];
                    if (ints["HistoryMaxTokens"] != null && Plugin.CfgHistoryMaxTokens != null) Plugin.CfgHistoryMaxTokens.Value = (int)ints["HistoryMaxTokens"];
                    if (ints["GrokSampleRate"] != null && Plugin.CfgGrokSampleRate != null) Plugin.CfgGrokSampleRate.Value = (int)ints["GrokSampleRate"];
                    if (ints["CustomFavorabilityPercent"] != null && Plugin.CfgCustomFavorabilityPercent != null) Plugin.CfgCustomFavorabilityPercent.Value = (int)ints["CustomFavorabilityPercent"];
                }

                JObject floats = root["floats"] as JObject;
                if (floats != null)
                {
                    if (floats["Temperature"] != null && Plugin.CfgTemperature != null) Plugin.CfgTemperature.Value = (float)floats["Temperature"];
                    if (floats["GrokSpeed"] != null && Plugin.CfgGrokSpeed != null) Plugin.CfgGrokSpeed.Value = (float)floats["GrokSpeed"];
                    if (floats["TtsVolume"] != null && Plugin.CfgTtsVolume != null) Plugin.CfgTtsVolume.Value = (float)floats["TtsVolume"];
                }

                JObject bools = root["bools"] as JObject;
                if (bools != null)
                {
                    if (bools["Enabled"] != null && Plugin.CfgEnabled != null) Plugin.CfgEnabled.Value = (bool)bools["Enabled"];
                    if (bools["LogPayloads"] != null && Plugin.CfgLogPayloads != null) Plugin.CfgLogPayloads.Value = (bool)bools["LogPayloads"];
                    if (bools["HideReasoning"] != null && Plugin.CfgHideReasoning != null) Plugin.CfgHideReasoning.Value = (bool)bools["HideReasoning"];
                    if (bools["JsonMode"] != null && Plugin.CfgJsonMode != null) Plugin.CfgJsonMode.Value = (bool)bools["JsonMode"];
                    if (bools["LocalModelMode"] != null && Plugin.CfgLocalModelMode != null) Plugin.CfgLocalModelMode.Value = (bool)bools["LocalModelMode"];
                    if (bools["ClampValues"] != null && Plugin.CfgClampValues != null) Plugin.CfgClampValues.Value = (bool)bools["ClampValues"];
                    if (bools["AiCanMurder"] != null && Plugin.CfgAiCanMurder != null) Plugin.CfgAiCanMurder.Value = (bool)bools["AiCanMurder"];
                    if (bools["TestKillPhraseActive"] != null && Plugin.CfgTestKillPhraseActive != null) Plugin.CfgTestKillPhraseActive.Value = (bool)bools["TestKillPhraseActive"];
                    if (bools["OocEnabled"] != null && Plugin.CfgOocEnabled != null) Plugin.CfgOocEnabled.Value = (bool)bools["OocEnabled"];
                    if (bools["TimeskipEnabled"] != null && Plugin.CfgTimeskipEnabled != null) Plugin.CfgTimeskipEnabled.Value = (bool)bools["TimeskipEnabled"];
                    if (bools["ActionsAreReal"] != null && Plugin.CfgActionsAreReal != null) Plugin.CfgActionsAreReal.Value = (bool)bools["ActionsAreReal"];
                    if (bools["LoreInjection"] != null && Plugin.CfgLoreInjection != null) Plugin.CfgLoreInjection.Value = (bool)bools["LoreInjection"];
                    if (bools["SendMechanics"] != null && Plugin.CfgSendMechanics != null) Plugin.CfgSendMechanics.Value = (bool)bools["SendMechanics"];
                    if (bools["SendFeelings"] != null && Plugin.CfgSendFeelings != null) Plugin.CfgSendFeelings.Value = (bool)bools["SendFeelings"];
                    if (bools["LetHerTemper"] != null && Plugin.CfgLetHerTemper != null) Plugin.CfgLetHerTemper.Value = (bool)bools["LetHerTemper"];
                    if (bools["WarningShock"] != null && Plugin.CfgWarningShock != null) Plugin.CfgWarningShock.Value = (bool)bools["WarningShock"];
                    if (bools["StuckRecovery"] != null && Plugin.CfgStuckRecovery != null) Plugin.CfgStuckRecovery.Value = (bool)bools["StuckRecovery"];
                    if (bools["HardDifficulty"] != null && Plugin.CfgHardDifficulty != null) Plugin.CfgHardDifficulty.Value = (bool)bools["HardDifficulty"];
                    if (bools["CustomFavorability"] != null && Plugin.CfgCustomFavorability != null) Plugin.CfgCustomFavorability.Value = (bool)bools["CustomFavorability"];
                    if (bools["ForceLocalVoice"] != null && Plugin.CfgForceLocalVoice != null) Plugin.CfgForceLocalVoice.Value = (bool)bools["ForceLocalVoice"];
                    if (bools["GameVoice"] != null && Plugin.CfgGameVoice != null) Plugin.CfgGameVoice.Value = (bool)bools["GameVoice"];
                    if (bools["AzureExpressive"] != null && Plugin.CfgAzureExpressive != null) Plugin.CfgAzureExpressive.Value = (bool)bools["AzureExpressive"];
                    if (bools["AzureOriginalTuning"] != null && Plugin.CfgAzureOriginalTuning != null) Plugin.CfgAzureOriginalTuning.Value = (bool)bools["AzureOriginalTuning"];
                    if (bools["GrokEnabled"] != null && Plugin.CfgGrokEnabled != null) Plugin.CfgGrokEnabled.Value = (bool)bools["GrokEnabled"];
                    if (bools["GrokNormalize"] != null && Plugin.CfgGrokNormalize != null) Plugin.CfgGrokNormalize.Value = (bool)bools["GrokNormalize"];
                    if (bools["TtsNormalize"] != null && Plugin.CfgTtsNormalize != null) Plugin.CfgTtsNormalize.Value = (bool)bools["TtsNormalize"];
                    if (bools["OpenRouterAllowFallback"] != null && Plugin.CfgOpenRouterAllowFallback != null) Plugin.CfgOpenRouterAllowFallback.Value = (bool)bools["OpenRouterAllowFallback"];
                    if (bools["SpeakActions"] != null && Plugin.CfgSpeakActions != null) Plugin.CfgSpeakActions.Value = (bool)bools["SpeakActions"];
                    if (bools["BlockGameAi"] != null && Plugin.CfgBlockGameAi != null) Plugin.CfgBlockGameAi.Value = (bool)bools["BlockGameAi"];
                    if (bools["OwnExtras"] != null && Plugin.CfgOwnExtras != null) Plugin.CfgOwnExtras.Value = (bool)bools["OwnExtras"];
                }

                Plugin.SaveCfg();
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null) Plugin.Log.LogError("Failed to load Profile " + profileNum + ": " + ex.Message);
            }
        }
    }
}
