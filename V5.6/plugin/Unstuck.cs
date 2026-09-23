// The hill freeze, and the watchdog that ends it.
//
// A real Siren session: the player walked her up the hill, playfully shoved
// her down it, and she never walked - or did anything - again. Replies kept
// parsing, TTS kept speaking, TryParseAIReply kept returning True, and her
// npc_location told the model "hilltop" for the rest of the session while
// even the fishing minigame's teleport (which visibly warps her next to the
// player) could not bring her back. Only quitting the level could.
//
// Mechanism, read from the decompiled game (NPCController.cs, all line
// numbers 0.1.46):
//
//   - Every walk is MoveTo(), which parks a coroutine in the shared field
//     currentMoveCoroutine (:209) and reports arrival ONLY from the
//     coroutine's final lines: Stop(); arrivedCallback?.Invoke() (:488).
//
//   - The behavior tree's FollowTargetObjects task waits on that callback:
//     OnUpdate returns Running until moveCompleted flips, the callback is
//     the field's only writer, and while the node is Running the tree never
//     returns to the selector that reads NextAction. New actions land in
//     the blackboard and are never looked at.
//
//   - PushDetection (:721) - ticked every frame by a parallel branch of the
//     SAME tree - reacts to a mid-size shove (0.45m..1.5m of displacement
//     while the player stands within ~1.4m) by calling MoveTo for a
//     cosmetic half-step away (:740). MoveTo begins with
//     StopCoroutine(currentMoveCoroutine): the walk in flight dies BEFORE
//     its callback line ever runs, moveCompleted stays false forever, and
//     the tree is parked for good. She answers, she emotes in text, she
//     never moves or acts again. The game ships no watchdog of any kind
//     (there is no "stuck" anywhere in its source), so the wedge is
//     permanent by construction.
//
//   - Teleport() repairs the AGENT (nav.Warp re-snaps her to the NavMesh)
//     but touches nothing in the tree - which is why the fishing teleport
//     moved her body and changed nothing else. Stop() and Teleport() orphan
//     callbacks the same way MoveTo does, so the shove is merely the
//     easiest trigger, not the only one.
//
//   - All of this lives in the shared NPCController base and the shared
//     task pool: every character on every level can freeze like this. The
//     Siren's island just makes it easy - open slopes, an ocean/land mesh
//     split, and long walks beside the player.
//
// Two layers here, one config toggle:
//
//   1. Prevention. PushDetection is re-implemented verbatim with a single
//      guard: the cosmetic side-step only runs when no walk is in flight.
//      A walk in flight means she is already moving away from an overlap,
//      so the step is redundant exactly when it is deadly. Her
//      being-pushed reaction (getPushedByEvent -> dialogue) is untouched.
//
//   2. Watchdog. Once per AI reply: if the tree's NextAction has diverged
//      from CurrentAction on two consecutive replies while she has not
//      moved a centimeter, the tree is parked. Recovery is the engine's
//      own supported reset - DisableBehavior + EnableBehavior rebuilds the
//      task stack, which is literally what Behavior Designer's own
//      RestartBehaviorTree task does - plus re-seating the agent on the
//      NavMesh if the shove knocked her off it, and re-deriving
//      current_location, which is written only by zone TriggerEnter (there
//      is no TriggerExit handler in the game) and so had frozen on
//      "hilltop", misinforming the model every turn. The check costs two
//      reflected string reads per reply; the recovery runs only on a
//      confirmed wedge and is loud in the log when it does.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace AI2UCustomAI
{
    internal static class Unstuck
    {
        // ---------------- handles into the live scene ----------------

        // Same route as Items.Property(): Communicator's plain field first,
        // because the _MainCharacter field is only reassigned on the hub.
        static NPCController Controller()
        {
            try
            {
                Communicator c = UnityEngine.Object.FindObjectOfType<Communicator>();
                if (c == null) return null;

                Traverse t = Traverse.Create(c);
                object master = t.Field("npcMasterBehavior").GetValue();
                if (master == null) master = t.Field("npcMasterBehavior_MainCharacter").GetValue();
                if (master == null) return null;

                return Traverse.Create(master).Field("npcController").GetValue<NPCController>();
            }
            catch { return null; }
        }

        // The tree is reached through the public NormalBehavior property, but
        // via reflection so the mod does not take a compile-time reference to
        // BehaviorDesigner.Runtime for four cold-path calls.
        static object Tree(NPCController ctrl)
        {
            try { return Traverse.Create(ctrl).Property("NormalBehavior").GetValue(); }
            catch { return null; }
        }

        // GetVariable("X").GetValue().ToString() - the same shape the game's
        // own cheat console uses (CheatCommandNPC.cs:519,576), which is what
        // makes "NextAction"/"CurrentAction" safe names to rely on.
        static string Var(object tree, string name)
        {
            try
            {
                MethodInfo get = tree.GetType().GetMethod("GetVariable", new Type[] { typeof(string) });
                if (get == null) return null;
                object sv = get.Invoke(tree, new object[] { name });
                if (sv == null) return null;
                MethodInfo val = sv.GetType().GetMethod("GetValue", Type.EmptyTypes);
                if (val == null) return null;
                object v = val.Invoke(sv, null);
                return v == null ? null : v.ToString();
            }
            catch { return null; }
        }

        // ---------------- layer 1: the push guard ----------------

        public static class PushGuardPatch
        {
            static readonly FieldInfo FPlayer    = AccessTools.Field(typeof(NPCController), "playerProperty");
            static readonly FieldInfo FPushing   = AccessTools.Field(typeof(NPCController), "isPlayerPushing");
            static readonly FieldInfo FStartPos  = AccessTools.Field(typeof(NPCController), "playerPushingNPCStartPos");
            static readonly FieldInfo FDistance  = AccessTools.Field(typeof(NPCController), "m_pushDetectionDis");
            static readonly FieldInfo FListener  = AccessTools.Field(typeof(NPCController), "_npcActionListener");
            static readonly FieldInfo FCoroutine = AccessTools.Field(typeof(NPCController), "currentMoveCoroutine");

            [HarmonyPatch(typeof(NPCController), "PushDetection")]
            [HarmonyPrefix]
            static bool Prefix(NPCController __instance)
            {
                // Toggle off, or a game patch renamed a field: run the stock
                // method, stock bug included. Never trade a known freeze for
                // an unknown throw in a per-frame path.
                if (!Plugin.CfgStuckRecovery.Value) return true;
                if (FPlayer == null || FPushing == null || FStartPos == null
                    || FDistance == null || FListener == null || FCoroutine == null) return true;

                PlayerProperty player = FPlayer.GetValue(__instance) as PlayerProperty;
                if (player == null) return true;

                // Verbatim game logic (NPCController.PushDetection, :721-747)
                // except the one guarded line.
                Vector3 v = player.transform.position - __instance.transform.position;
                v.y = 0f;
                bool pushing = (bool)FPushing.GetValue(__instance);

                if (v.sqrMagnitude < 2f)
                {
                    float threshold = (float)FDistance.GetValue(__instance);
                    Vector3 start = (Vector3)FStartPos.GetValue(__instance);
                    float moved = Vector3.Distance(start, __instance.transform.position);

                    if (!pushing)
                    {
                        FPushing.SetValue(__instance, true);
                        FStartPos.SetValue(__instance, __instance.transform.position);
                    }
                    else if (moved > threshold)
                    {
                        FPushing.SetValue(__instance, false);
                        NPCActionListener listener = FListener.GetValue(__instance) as NPCActionListener;
                        if (listener != null && listener.getPushedByEvent != null)
                            listener.getPushedByEvent.Invoke(__instance.gameObject);
                    }
                    else if (moved > threshold * 0.3f)
                    {
                        // THE GUARD. The stock line calls MoveTo
                        // unconditionally, and MoveTo opens by killing
                        // whatever walk is in flight - callback and all,
                        // which is the entire freeze. The side-step is
                        // cosmetic and she is already in motion when a walk
                        // exists, so it only runs on a standing character.
                        if (FCoroutine.GetValue(__instance) == null)
                            __instance.MoveTo(__instance.transform.position - v.normalized * 1.5f, 0.5f);
                    }
                }
                else if (pushing)
                {
                    FPushing.SetValue(__instance, false);
                }

                return false;
            }
        }

        // ---------------- layer 2: the watchdog ----------------

        // One observation per AI reply. Two consecutive observations of the
        // same divergence, with her standing dead still between them, is the
        // signature of a parked tree - a healthy tree consumes NextAction
        // within a tick, and a healthy long walk moves her.
        static string _lastSig;
        static Vector3 _lastPos;
        static float _lastWhen;
        static int _lastCtrl;

        public static void Observe()
        {
            if (!Plugin.CfgStuckRecovery.Value) return;
            try { ObserveCore(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Unstuck: watchdog error (skipped this turn): " + e.Message);
            }
        }

        static void ObserveCore()
        {
            NPCController ctrl = Controller();
            if (ctrl == null || !ctrl.gameObject.activeInHierarchy) { _lastSig = null; return; }

            // New character or new level: yesterday's evidence is about
            // someone else.
            if (ctrl.GetInstanceID() != _lastCtrl) { _lastCtrl = ctrl.GetInstanceID(); _lastSig = null; }

            // Scripted sequences own her on purpose: endings, minigames, and
            // the final chase all stop or steer her outside the normal tree,
            // and "not obeying npc_action" is correct behaviour there.
            if (GameManager.curAIGameStatus != CurrentAIGameStatus.Normal) { _lastSig = null; return; }
            if (Murder.InFinalChase()) { _lastSig = null; return; }

            object tree = Tree(ctrl);
            if (tree == null) { _lastSig = null; return; }

            string next = Var(tree, "NextAction");
            string cur = Var(tree, "CurrentAction");
            NavMeshAgent nav = ctrl.nav;
            Vector3 pos = ctrl.transform.position;

            bool diverged = next != null && cur != null
                && !string.Equals(next, cur, StringComparison.Ordinal);
            bool still = nav != null
                && nav.velocity.sqrMagnitude < 0.01f
                && (pos - _lastPos).sqrMagnitude < 0.0025f;

            string sig = diverged ? next + "|" + cur : null;
            bool wedged = diverged && still
                && sig == _lastSig
                && Time.realtimeSinceStartup - _lastWhen > 3f;

            _lastSig = sig;
            _lastPos = pos;
            _lastWhen = Time.realtimeSinceStartup;

            if (!wedged) return;

            Plugin.Log.LogWarning("Unstuck: her decision loop is parked (she wants \"" + next
                + "\" but is frozen mid-\"" + cur + "\" and has not moved between two replies)."
                + " Restarting her behavior tree.");
            Recover(ctrl, nav, tree);
            _lastSig = null;
        }

        static void Recover(NPCController ctrl, NavMeshAgent nav, object tree)
        {
            // 1. Her feet. A shove down a slope can leave the agent off the
            // mesh entirely, where SetDestination fails silently forever -
            // nav.Warp through the nearest sampled point is the same repair
            // the game's own Teleport() uses.
            try
            {
                if (nav != null && !nav.isOnNavMesh)
                {
                    NavMeshHit hit;
                    if (NavMesh.SamplePosition(ctrl.transform.position, out hit, 10f, NavMesh.AllAreas))
                    {
                        nav.Warp(hit.position);
                        Plugin.Log.LogInfo("Unstuck: she was off the NavMesh; re-seated her on it.");
                    }
                }
            }
            catch { }

            // 2. The dangling walk. Stop() is the game's own reset: it kills
            // the coroutine, clears the walking animation and nulls
            // currentMoveCoroutine. Orphaning a callback here is fine - the
            // tree that owned it is about to be rebuilt.
            try { ctrl.Stop(); } catch { }
            try { if (nav != null && nav.isOnNavMesh) nav.ResetPath(); } catch { }

            // 3. The tree itself - the actual fix. Disable + Enable with no
            // arguments destroys and rebuilds the task stack (Behavior
            // Designer's own RestartBehaviorTree does exactly this pair), so
            // every parked node - FollowTargetObjects, FaceTo, a held Sit -
            // starts over. SharedVariables live on the Behavior component
            // and survive, and the reply that triggered this observation is
            // handed to the game right after, so ReceiveChatGPT primes the
            // fresh tree with the action she just chose.
            try
            {
                Type bt = tree.GetType();
                MethodInfo dis = bt.GetMethod("DisableBehavior", Type.EmptyTypes);
                MethodInfo en = bt.GetMethod("EnableBehavior", Type.EmptyTypes);
                if (dis == null || en == null)
                {
                    Plugin.Log.LogWarning("Unstuck: this Behavior Designer build hides its restart "
                        + "methods, so the tree was left alone.");
                    return;
                }
                dis.Invoke(tree, null);
                en.Invoke(tree, null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Unstuck: tree restart failed: " + e.Message);
                return;
            }

            // 4. Her sense of place. current_location is written only by
            // AreaTriggerDetector.OnTriggerEnter - nothing ever clears it, so
            // a frozen girl reports her last zone to the model forever (the
            // session that found this bug told it "hilltop" for half an
            // hour). Re-derive it from the nearest registered zone so the
            // next prompt tells the truth.
            try
            {
                System.Collections.Generic.List<AreaTriggerDetector> areas =
                    AreaTriggerDetector.RegisteredAreas;
                NPCProperty prop = ctrl.NpcProperty;
                if (areas != null && prop != null)
                {
                    AreaTriggerDetector best = null;
                    float bestD = float.MaxValue;
                    for (int i = 0; i < areas.Count; i++)
                    {
                        AreaTriggerDetector a = areas[i];
                        if (a == null) continue;
                        float d = (a.transform.position - ctrl.transform.position).sqrMagnitude;
                        if (d < bestD) { bestD = d; best = a; }
                    }
                    if (best != null && !string.IsNullOrEmpty(best.areaName))
                    {
                        prop.current_location = best.areaName;
                        prop.current_room = best.roomName;
                    }
                }
            }
            catch { }

            Plugin.Log.LogWarning("Unstuck: recovery complete. Her next action executes normally.");
        }
    }
}
