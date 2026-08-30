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

        static bool _loggedDialogue;

        public static void Install(Harmony h)
        {
            h.PatchAll(typeof(PlayGuard));
            h.PatchAll(typeof(SandboxPlayGuard));
            h.PatchAll(typeof(FetchAsyncGuard));
        }

        static bool BlockDialogue()
        {
            return Plugin.CfgEnabled != null && Plugin.CfgEnabled.Value
                && Plugin.CfgBlockGameAi != null && Plugin.CfgBlockGameAi.Value;
        }

        static void Redirect(ref Uri result, string what)
        {
            result = Blocked;

            // Once per session: reaching here means something bypassed SendPatch,
            // which is worth knowing about.
            if (_loggedDialogue) return;
            _loggedDialogue = true;
            Plugin.Log.LogWarning("Guard: blocked an AI2U dialogue AI call (" + what
                + "). The mod normally replaces this before it gets here, so if her replies still "
                + "work you can ignore this; if they do not, SendToChatGPT was bypassed.");
        }

        [HarmonyPatch(typeof(ServerUriBuilder), "GetPlayUri")]
        static class PlayGuard
        {
            static void Postfix(ref Uri __result)
            {
                if (BlockDialogue()) Redirect(ref __result, "play");
            }
        }

        [HarmonyPatch(typeof(ServerUriBuilder), "GetSandBoxPlayUri")]
        static class SandboxPlayGuard
        {
            static void Postfix(ref Uri __result)
            {
                if (BlockDialogue()) Redirect(ref __result, "sandbox play");
            }
        }

        [HarmonyPatch(typeof(ServerUriBuilder), "GetFetchAsyncUri")]
        static class FetchAsyncGuard
        {
            static void Postfix(ref Uri __result)
            {
                if (BlockDialogue()) Redirect(ref __result, "fetchAsync");
            }
        }

        // Summary, envision and memorize used to be redirected to the dead port
        // here. They are not any more, and the difference matters.
        //
        // A redirect only ever suppressed them: the ending screen lost its written
        // summary, and - the trap recorded as open issue 6 - her memory of what you
        // last talked about silently stopped being saved, because MemorizeProcessor
        // writes the _lastTopic_ save key from the reply body and a failed request
        // never produces one. Turning the switch on to save the developers money
        // quietly cost the player a feature, with nothing in the log saying so.
        //
        // Extras.cs now intercepts these three one level lower, at the request
        // itself, and answers them from the user's own endpoint. That never sends
        // anything to AI2U either, so there is nothing left here to block - and if
        // a future game update routes one of them through a sender Extras does not
        // recognise, falling through to their server is a working fallback where a
        // dead port would have been a broken feature.
    }
}
