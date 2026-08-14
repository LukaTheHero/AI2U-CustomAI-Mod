// Keeps AI2U's own paid AI endpoints out of the loop while the mod is driving
// the conversation.
//
// The point is not to break the game - it is that a modded session should not
// bill the developers for inference it never uses. SendPatch already returns
// false on the dialogue path, so in a healthy session GetPlayUri is never even
// reached and this guard stays silent. It exists for the paths that skip
// SendToChatGPT: retries, async polling, and anything a future game update
// wires up differently.
//
// How the block works. These are static methods returning a Uri, so rather than
// trying to cancel a UnityWebRequest mid-flight - which would mean fabricating a
// UnityWebRequestAsyncOperation for a Prefix to return - the Postfix simply
// rewrites the address to a dead loopback port. Port 1 refuses instantly, so the
// request fails fast and locally through the game's existing error handling
// instead of hanging on a timeout.
//
// What is deliberately NOT touched: Record, Heartbeat, WifiChecking,
// ServerCondition, Fake, the inbox calls (Fetch/Claim/Love/Delete/Notify/
// MultiCast/Welcome), the metrics calls (EventLog/MetricsLog/Impression/
// PlayFabError), Shop, GachaDraw, Redeem, NameCheck and Newsletter. None of
// those are LLM calls, and blocking them would break login, saves, the store
// and progression for no benefit.
using System;
using HarmonyLib;

namespace AI2UCustomAI
{
    internal static class ApiGuard
    {
        // Unroutable on purpose: a closed port on the loopback interface fails
        // immediately rather than waiting out a network timeout.
        static readonly Uri Blocked = new Uri("http://127.0.0.1:1/ai2u-blocked-by-mod");

        static bool _loggedDialogue, _loggedExtras;

        public static void Install(Harmony h)
        {
            h.PatchAll(typeof(PlayGuard));
            h.PatchAll(typeof(SandboxPlayGuard));
            h.PatchAll(typeof(FetchAsyncGuard));
            h.PatchAll(typeof(SummaryGuard));
            h.PatchAll(typeof(EnvisionGuard));
            h.PatchAll(typeof(MemorizeGuard));
        }

        static bool BlockDialogue()
        {
            return Plugin.CfgEnabled != null && Plugin.CfgEnabled.Value
                && Plugin.CfgBlockGameAi != null && Plugin.CfgBlockGameAi.Value;
        }

        static bool BlockExtras()
        {
            return Plugin.CfgEnabled != null && Plugin.CfgEnabled.Value
                && Plugin.CfgBlockGameExtras != null && Plugin.CfgBlockGameExtras.Value;
        }

        static void Redirect(ref Uri result, string what, bool dialogue)
        {
            result = Blocked;

            // Once per kind per session: the dialogue case means something
            // bypassed SendPatch and is worth knowing about, the extras case is
            // expected and would otherwise spam the log.
            if (dialogue)
            {
                if (_loggedDialogue) return;
                _loggedDialogue = true;
                Plugin.Log.LogWarning("Guard: blocked an AI2U dialogue AI call (" + what
                    + "). The mod normally replaces this before it gets here, so if her replies still "
                    + "work you can ignore this; if they do not, SendToChatGPT was bypassed.");
            }
            else
            {
                if (_loggedExtras) return;
                _loggedExtras = true;
                Plugin.Log.LogInfo("Guard: blocked AI2U's " + what + " call. This is one of their paid "
                    + "LLM endpoints. Turn off 'Also block summary / envision / memorize' in the F9 menu "
                    + "if you would rather have that feature than a zero bill on their side.");
            }
        }

        [HarmonyPatch(typeof(ServerUriBuilder), "GetPlayUri")]
        static class PlayGuard
        {
            static void Postfix(ref Uri __result)
            {
                if (BlockDialogue()) Redirect(ref __result, "play", true);
            }
        }

        [HarmonyPatch(typeof(ServerUriBuilder), "GetSandBoxPlayUri")]
        static class SandboxPlayGuard
        {
            static void Postfix(ref Uri __result)
            {
                if (BlockDialogue()) Redirect(ref __result, "sandbox play", true);
            }
        }

        [HarmonyPatch(typeof(ServerUriBuilder), "GetFetchAsyncUri")]
        static class FetchAsyncGuard
        {
            static void Postfix(ref Uri __result)
            {
                if (BlockDialogue()) Redirect(ref __result, "fetchAsync", true);
            }
        }

        [HarmonyPatch(typeof(ServerUriBuilder), "GetSummaryUri")]
        static class SummaryGuard
        {
            static void Postfix(ref Uri __result)
            {
                if (BlockExtras()) Redirect(ref __result, "summary", false);
            }
        }

        [HarmonyPatch(typeof(ServerUriBuilder), "GetEnvisionUri")]
        static class EnvisionGuard
        {
            static void Postfix(ref Uri __result)
            {
                if (BlockExtras()) Redirect(ref __result, "envision", false);
            }
        }

        [HarmonyPatch(typeof(ServerUriBuilder), "GetMemorizeUri")]
        static class MemorizeGuard
        {
            static void Postfix(ref Uri __result)
            {
                if (BlockExtras()) Redirect(ref __result, "memorize", false);
            }
        }
    }
}
