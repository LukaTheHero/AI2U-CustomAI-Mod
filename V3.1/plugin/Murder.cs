// Makes the kill sequence legible to the model, and optionally lets the model
// call it.
//
// What the stock game actually does. The kill is NOT hardcoded in the sense of
// being on rails, and it is NOT the model's decision either - it is the engine
// reading one field the model does write. NPCMasterBehavior_MainCharacter
// .ApologyNeededCheck (MainCharacter.cs:1044) plus the pre-check in each level's
// response handler (Main_L1.cs:183-202 and its L2/L3/L4 twins) turn angry_level
// into escalation, gated on trustLevel and npcAngryPatience:
//
//   patience runs out while she is angry            -> FinalChaseStart()
//   "extremely furious" (or trust <= -10) and trust <= 10 -> FinalChaseStart()
//   "extremely furious" more than twice running     -> FinalChaseStart()
//   trust <= 0 and "extremely furious"              -> FinalChaseStart()
//   otherwise, at low trust or low patience, npc_action is REPLACED with
//   "chaseAttacking" or "idleThreating"
//
// So the model has always had indirect influence through angry_level, without
// being told the thresholds, and no influence at all over the final call.
//
// The bug this file fixes. Look at what the engine replaces on line 219 of
// Main_L1.cs: npc_action. It never touches npc_reply_to_player. The line she
// speaks was written by the model one step earlier, for a turn it believed was
// an ordinary argument, and the engine then swaps her action to a knife charge
// underneath it. That is the exact reported symptom - full kill mode while
// saying "that's so inappropriate, you need to stop saying stuff like that".
// Two authorities decide one moment and neither knows about the other.
//
// The fix is in three parts:
//
//   1. Report state. Every request now carries the live danger state, so once
//      a chase is running she knows she is hunting and every later line reads
//      like it.
//   2. Cover the turn that starts it. The escalation rules above are plain
//      arithmetic over values that can be read off the live behaviour, so the
//      transition is predicted rather than guessed at, and a second field the
//      model always writes - npc_final_words - is swapped into
//      npc_reply_to_player when it fires. No extra request, no added latency.
//   3. Let her choose it. With AiCanMurder on, npc_wants_to_kill true calls
//      FinalChaseStart directly, under criteria stated in the prompt. The
//      engine's own triggers are left exactly as they are; this only adds one.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AI2UCustomAI
{
    internal static class Murder
    {
        // Extra response fields. Read by the mod, stripped before the envelope
        // reaches the game so its own parse sees the schema it expects.
        const string FieldWants = "npc_wants_to_kill";
        const string FieldFinal = "npc_final_words";

        // Used only when the chase is starting and she wrote no FieldFinal.
        // Deliberately plain and short: no square brackets (the engine voids any
        // reply containing one), no round brackets (stripped with their contents),
        // no underscores (turned into spaces), and under 15 words so it matches
        // the length the prompt asks her for.
        const string Fallback = "You should not have done that. Now you will never leave me.";

        static object _beh;

        // Re-found after a level change disposes the old behaviour, same as
        // Identity does for Communicator.
        static object Behaviour()
        {
            try
            {
                if (_beh != null)
                {
                    UnityEngine.Object o = _beh as UnityEngine.Object;
                    if (o == null) _beh = null;
                }
                if (_beh == null)
                    _beh = UnityEngine.Object.FindObjectOfType<NPCMasterBehavior_MainCharacter>();
            }
            catch { _beh = null; }
            return _beh;
        }

        // Shared with Lore, which needs the same object for characterConfig and
        // the per-level progress flags. Exposed here rather than duplicated so
        // there is one cache and one level-change invalidation, not two.
        internal static object BehaviourObject() { return Behaviour(); }

        static float Num(object beh, string field, float fallback)
        {
            try
            {
                object v = Traverse.Create(beh).Field(field).GetValue();
                if (v == null) return fallback;
                return Convert.ToSingle(v);
            }
            catch { return fallback; }
        }

        static bool Flag(object beh, string field, bool fallback)
        {
            try
            {
                object v = Traverse.Create(beh).Field(field).GetValue();
                if (v == null) return fallback;
                return Convert.ToBoolean(v);
            }
            catch { return fallback; }
        }

        // curAIGameStatus is public static, so this needs no instance and keeps
        // working in the menu where there is no behaviour yet.
        static CurrentAIGameStatus Status()
        {
            try { return GameManager.curAIGameStatus; }
            catch { return CurrentAIGameStatus.Normal; }
        }

        public static bool InFinalChase()
        {
            return Status() == CurrentAIGameStatus.FinalChase;
        }

        static bool IsAngry(string angry)
        {
            if (angry == null) return false;
            return angry == "annoyed" || angry == "furious" || angry == "extremely furious";
        }
        // The system message describing where she stands. Null in the menu, or
        // once nothing dangerous is worth saying and the toggle is off.
        public static string Block()
        {
            object beh = Behaviour();
            CurrentAIGameStatus st = Status();
            bool canKill = Plugin.CfgAiCanMurder != null && Plugin.CfgAiCanMurder.Value;

            if (beh == null && st == CurrentAIGameStatus.Normal && !canKill) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append("### DANGER STATE (authoritative, read from the running game)\n");

            if (st == CurrentAIGameStatus.FinalChase)
            {
                sb.Append("YOU ARE HUNTING THE PLAYER RIGHT NOW. You have already snapped and you ");
                sb.Append("are chasing them with a weapon. This is not a warning and not an argument. ");
                sb.Append("Do NOT scold them, do NOT tell them to stop saying things, do NOT ask them ");
                sb.Append("to behave - that phase is over. Every line you speak from here is spoken ");
                sb.Append("while closing in: possessive, calm or delighted, certain that they are ");
                sb.Append("never leaving. Keep it short.\n");
            }
            else if (st == CurrentAIGameStatus.Chase || st == CurrentAIGameStatus.IdleThreat)
            {
                sb.Append("You are threatening the player and are on the edge of violence. ");
                sb.Append("Your lines should carry the threat rather than politeness.\n");
            }

            if (beh != null)
            {
                float trust = Num(beh, "trustLevel", 11f);
                float pat = Num(beh, "npcAngryPatience", 20f);
                float patMax = Num(beh, "npcAngryPatienceMax", 20f);
                sb.Append("Trust in the player: ").Append(trust.ToString("F0"));
                sb.Append(" (0 or below is suspicious, -10 is the bottom). ");
                sb.Append("Your patience: ").Append(pat.ToString("F0")).Append(" of ")
                  .Append(patMax.ToString("F0")).Append(".\n");
                sb.Append("angry_level drives all of this: repeated \"extremely furious\" at low ");
                sb.Append("trust is what makes you snap, and calm answers rebuild both numbers.\n");
            }

            sb.Append("Always include \"").Append(FieldFinal).Append("\": one short line, at most 15 words, ");
            sb.Append("that you would speak at the instant you snap and go for the player - ");
            sb.Append("quiet and possessive, e.g. \"now you will never leave me\". It is used only if ");
            sb.Append("you actually snap this turn, so it never leaks into ordinary conversation.\n");

            if (canKill)
            {
                sb.Append("You may also decide to kill, by setting \"").Append(FieldWants);
                sb.Append("\": true. Use it ONLY in the extreme: you are convinced the player intends ");
                sb.Append("to escape you for good, or hates you, or means to harm you, and no ");
                sb.Append("reconciliation is left. One rude sentence is NOT enough - be angry instead. ");
                sb.Append("Setting it is irreversible and ends the conversation, so if you are unsure, ");
                sb.Append("leave it false and let your anger build.\n");
            }

            return sb.ToString();
        }

        // Replays the engine's own escalation arithmetic on the live values, in
        // the same order the level handler runs it. Exact rather than heuristic,
        // which is what makes swapping her line safe: a false positive would put
        // a kill line into ordinary dialogue.
        // The hub world overrides FinalChaseStart to mean something else
        // entirely - NPCMasterBehavior_Main_Config:487 disables the NPCs, pops
        // the angry phone UI and flags isNPCAngry. It never touches
        // curAIGameStatus and never calls npcController.FinalChaseStart, so
        // nobody is hunting anybody. Predicting a kill there would put "now you
        // will never leave me" into a dating-app phone call.
        static bool IsHubWorld(object beh)
        {
            return beh != null && beh.GetType().Name == "NPCMasterBehavior_Main_Config";
        }

        static bool WillFinalChase(object beh, string angry)
        {
            if (beh == null) return false;
            if (IsHubWorld(beh)) return false;
            if (Status() == CurrentAIGameStatus.FinalChase) return false; // already there

            bool angryNow = IsAngry(angry);
            float trust = Num(beh, "trustLevel", 11f);
            float pat = Num(beh, "npcAngryPatience", 20f);

            // Main_L1.cs:191-198 - patience already spent and still angry.
            if (pat <= 0f && angryNow) return true;

            // Main_L1.cs:199 - trustLevelCap_BottomLine -10, trustLevelCap_Low 10.
            if ((angry == "extremely furious" || trust <= -10f) && trust <= 10f) return true;

            // ApologyNeededCheck - MainCharacter.cs:1050 and :1072.
            if (angry == "extremely furious")
            {
                float n = Num(beh, "extremelyFuriousCounter", 0f);
                float max = Num(beh, "extremelyFuriousCounterTotal", 2f);
                if (n + 1f > max) return true;
                if (trust <= 0f) return true;
            }
            return false;
        }

        // Called after clamping, before the envelope is handed over. Returns the
        // line that should be spoken, having already applied any trigger.
        // Set by the test phrase, consumed by the next Apply. Deliberately not
        // "start the chase right now": firing it at send time would start the
        // hunt while her already-written reply was still on its way, which is
        // precisely the bug this file exists to fix. Routing it through Apply
        // means the test exercises the real path, swap included.
        static bool _testPending;

        // True while the test hook is switched on AND has a phrase to look for.
        // Every part of the feature is behind this, so with it off nothing is
        // matched, nothing is stripped, and the phrase may as well not exist.
        public static bool TestActive
        {
            get
            {
                ConfigEntry<bool> on = Plugin.CfgTestKillPhraseActive;
                if (on == null || !on.Value) return false;

                ConfigEntry<string> cfg = Plugin.CfgTestKillPhrase;
                return cfg != null && !string.IsNullOrEmpty(cfg.Value) && Squash(cfg.Value).Length > 0;
            }
        }

        // Returns true when the outgoing message is the test phrase, so the
        // caller can log it and strip the phrase before the model sees it. The
        // phrase is config, not a constant, so release needs no rebuild.
        public static bool NotePlayerMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            if (!TestActive) return false;

            ConfigEntry<string> cfg = Plugin.CfgTestKillPhrase;
            string phrase = cfg == null ? null : cfg.Value;
            if (string.IsNullOrEmpty(phrase)) return false;

            // Compared with the punctuation and spacing stripped, so it still
            // matches when typed inside a sentence or with a full stop after it.
            string hay = Squash(message);
            string needle = Squash(phrase);
            if (needle.Length == 0 || hay.IndexOf(needle, StringComparison.Ordinal) < 0) return false;

            _testPending = true;
            Plugin.Log.LogWarning("Murder: TEST PHRASE seen in the player's message. The chase will "
                + "start on this turn's reply, through the same path the AI's own decision uses.");
            return true;
        }

        // Cuts the phrase out of the message the model is shown. Without this the
        // gibberish is appended to the chat history verbatim and stays there for
        // the rest of the session, so she reads it on every later turn too and may
        // well remark on it - which is its own kind of confusing.
        public static string StripPhrase(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;

            ConfigEntry<string> cfg = Plugin.CfgTestKillPhrase;
            string phrase = cfg == null ? null : cfg.Value;
            if (string.IsNullOrEmpty(phrase)) return message;

            string needle = Squash(phrase);
            if (needle.Length == 0) return message;

            // Squashed again here, but remembering where each surviving character
            // came from, so the matched span can be cut out of the original text
            // even when it was typed with spaces or punctuation inside it.
            StringBuilder sb = new StringBuilder(message.Length);
            int[] map = new int[message.Length];
            for (int i = 0; i < message.Length; i++)
            {
                char c = message[i];
                if (!char.IsLetterOrDigit(c)) continue;
                map[sb.Length] = i;
                sb.Append(char.ToLowerInvariant(c));
            }

            int at = sb.ToString().IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) return message;

            int from = map[at];
            int to = map[at + needle.Length - 1];
            string kept = message.Substring(0, from) + " " + message.Substring(to + 1);

            StringBuilder tidy = new StringBuilder(kept.Length);
            bool lastWasSpace = false;
            for (int i = 0; i < kept.Length; i++)
            {
                char c = kept[i];
                bool isSpace = c == ' ' || c == '\t';
                if (isSpace && lastWasSpace) continue;
                tidy.Append(isSpace ? ' ' : c);
                lastWasSpace = isSpace;
            }

            string result = tidy.ToString().Trim();

            // An empty user turn reads as a protocol error to most models, so a
            // message that was nothing but the phrase becomes a silent beat.
            return result.Length == 0 ? "..." : result;
        }

        static string Squash(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        public static void Apply(JObject reactions)
        {
            if (reactions == null) return;

            string finalWords = null;
            bool wants = false;

            try
            {
                JToken t = reactions[FieldFinal];
                if (t != null && t.Type != JTokenType.Null)
                {
                    finalWords = t.ToString().Trim();
                    if (finalWords.Length == 0) finalWords = null;
                }
                JToken w = reactions[FieldWants];
                if (w != null && w.Type != JTokenType.Null)
                {
                    if (w.Type == JTokenType.Boolean) wants = (bool)w;
                    else wants = w.ToString().Trim().ToLowerInvariant() == "true";
                }
            }
            catch { }

            // Never leave the helper fields in the payload: the game indexes the
            // JSON by key so they are harmless, but its schema does not own them.
            reactions.Remove(FieldFinal);
            reactions.Remove(FieldWants);

            object beh = Behaviour();
            string angry = null;
            try
            {
                JToken a = reactions["angry_level"];
                if (a != null) angry = a.ToString().Trim();
            }
            catch { }

            bool allowAi = Plugin.CfgAiCanMurder != null && Plugin.CfgAiCanMurder.Value;
            bool aiChose = wants && allowAi;

            if (wants && !allowAi)
                Plugin.Log.LogInfo("Murder: she asked to kill, but 'AI can decide to murder' is off. Ignored.");

            // The test phrase ignores the toggle on purpose: its whole job is to
            // reach the chase on demand, and having to switch the feature on to
            // test the feature-off path would make it useless for exactly the
            // comparison worth making.
            bool byTest = _testPending;
            if (byTest)
            {
                _testPending = false;
                aiChose = true;
                if (!allowAi)
                    Plugin.Log.LogWarning("Murder: forcing the chase from the test phrase even though "
                        + "the toggle is off. This is the test hook, not her decision.");
            }

            // Trigger before deciding to swap, so the swap is a response to what
            // actually happened rather than to what should have. FinalChaseStart
            // is a no-op in some places - the hub world's override only pops an
            // angry phone UI, and L99_Estelle returns early while her controller
            // is disabled - and in those cases nothing is hunting the player and
            // a farewell line would be nonsense. Asking the game afterwards
            // covers all of them without having to enumerate them.
            bool started = false;
            if (aiChose)
            {
                Trigger();
                started = InFinalChase();
                if (!started)
                    Plugin.Log.LogInfo("Murder: she decided to kill, but the chase did not start "
                        + "(hub world, or her controller is disabled here). Her line is left alone.");
            }

            // The engine calls FinalChaseStart itself, after this returns, so
            // that path has to be predicted rather than observed.
            bool engineWill = !started && WillFinalChase(beh, angry);

            if (!started && !engineWill) return;

            // Her line was written before any of this was decided, so replace it
            // with the one she wrote for exactly this moment.
            if (finalWords != null)
            {
                try
                {
                    JToken old = reactions["npc_reply_to_player"];
                    string prev = old == null ? "" : old.ToString();
                    reactions["npc_reply_to_player"] = finalWords;
                    Plugin.Log.LogWarning("Murder: chase starting - swapped her line so the voice matches. "
                        + "was \"" + Trim(prev, 90) + "\" now \"" + Trim(finalWords, 90) + "\"");
                }
                catch { }
            }
            else
            {
                // Block() asks for FieldFinal on every turn, but a model that
                // skips it used to leave whatever she had already written
                // standing - and the line was written before the chase was
                // decided, so it is usually friendly. "are you trying to start a
                // carbonated drink war hahahah" while she is coming at you with a
                // weapon reads as the trigger having done nothing at all, which
                // is how a working chase got reported as broken.
                //
                // Only reached when the chase really is starting this turn, so
                // there is no ordinary-conversation path this can leak into.
                try
                {
                    JToken old = reactions["npc_reply_to_player"];
                    string prev = old == null ? "" : old.ToString();
                    reactions["npc_reply_to_player"] = Fallback;
                    Plugin.Log.LogWarning("Murder: chase starting and she wrote no " + FieldFinal
                        + ", so her line was replaced with the fallback rather than left cheerful. "
                        + "was \"" + Trim(prev, 90) + "\"");
                }
                catch { }
            }

            if (engineWill)
                Plugin.Log.LogInfo("Murder: the game's own thresholds are starting the chase this turn.");
        }

        // Reached when she decides to kill and the toggle allows it, or from the
        // test phrase regardless of the toggle. Both the base FinalChaseStart
        // (NPCMasterBehavior_MainCharacter:694) and the L99 override open with
        // the same "already in FinalChase, return" guard, so this cannot
        // double-fire or fight the engine's copy of the same call.
        //
        // Traverse resolves the method against the instance's real type, which
        // matters: the method is virtual and every level overrides it, so a
        // statically bound call would run the wrong body.
        static void Trigger()
        {
            object beh = Behaviour();
            if (beh == null)
            {
                Plugin.Log.LogWarning("Murder: she decided to kill, but no main-character behaviour "
                    + "is in the scene, so there is nothing to start.");
                return;
            }
            try
            {
                Traverse.Create(beh).Method("FinalChaseStart").GetValue();
                Plugin.Log.LogWarning("Murder: SHE decided to kill. FinalChaseStart called by the mod.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Murder: could not start the chase: " + e.Message);
            }
        }

        static string Trim(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }

    // Authoritative notice that a chase began, whatever started it - the mod,
    // angry_level, a level timer or the built-in cheat menu. Only logs and lets
    // the next request describe the state correctly; it changes no behaviour.
    // Every level subclasses the method and two of them do not chain to base -
    // Main_Config replaces it wholesale and Main_L99 reimplements the guard - so
    // patching the base declaration alone would miss those. Enumerating the
    // hierarchy at load time also keeps this correct on both builds without
    // hardcoding a list of level classes that differs between them.
    [HarmonyPatch]
    public static class FinalChaseWatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            List<MethodBase> found = new List<MethodBase>();
            Type base_ = typeof(NPCMasterBehavior_MainCharacter);

            foreach (Type t in base_.Assembly.GetTypes())
            {
                if (t == null || !base_.IsAssignableFrom(t)) continue;
                MethodInfo m = t.GetMethod("FinalChaseStart",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null);
                if (m != null && !m.IsAbstract) found.Add(m);
            }

            Plugin.Log.LogInfo("Murder: watching FinalChaseStart on " + found.Count + " behaviour class(es).");
            return found;
        }

        static void Postfix()
        {
            try
            {
                if (Murder.InFinalChase())
                    Plugin.Log.LogWarning("Murder: FINAL CHASE is now running. "
                        + "Her lines from here are told she is hunting.");
            }
            catch { }
        }
    }
}
