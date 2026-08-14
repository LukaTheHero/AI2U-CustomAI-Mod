// Developer cheats: read and write the two numbers that gate everything else,
// and drive two third-party mods from the same panel.
//
// Why this exists at all: testing a trust-gated feature meant holding a real
// conversation until trust crossed 40, every single time, for every rebuild.
// Trust is a runtime float that resets on level load, so it could not even be
// saved between attempts. That made the most interesting features the most
// expensive ones to check, which is backwards.
//
// Nothing here is on by default and nothing here is sent to the model. The
// panel section is collapsed, its master toggle is off, and with it off not one
// line below runs.
//
// Trust is changed through the game's own UpdateTrustLevel(int) rather than by
// writing the field. That method also sets the indicator string, plays her
// reaction emoji, fires the FullyTrust/heartBreaker achievements and invokes
// both listener events (NPCMasterBehavior_MainCharacter.cs:1227-1276). Poking
// the float leaves the HUD showing "Suspicious" at trust 96 and skips all of
// it, which is a worse lie than not having the cheat.
using System;
using HarmonyLib;
using UnityEngine;

namespace AI2UCustomAI
{
    internal static class Cheats
    {
        public static bool Active
        {
            get { return Plugin.CfgCheats != null && Plugin.CfgCheats.Value; }
        }

        // ---- trust ---------------------------------------------------------

        // The live trust value, or null when there is no NPC to read it from.
        // Null is a real answer here (the hub, a menu, a loading screen) and the
        // panel prints it as "no character in this scene" rather than 0, because
        // 0 is also a legitimate trust level.
        public static float? Trust()
        {
            try
            {
                object beh = Murder.BehaviourObject();
                if (beh == null) return null;

                object v = Traverse.Create(beh).Field("trustLevel").GetValue();
                return v is float ? (float?)(float)v : null;
            }
            catch (Exception) { return null; }
        }

        public static string Indicator()
        {
            try
            {
                object beh = Murder.BehaviourObject();
                if (beh == null) return null;

                return Traverse.Create(beh).Field("trustLevelIndicator").GetValue() as string;
            }
            catch (Exception) { return null; }
        }

        // Set trust to an absolute value by asking the game for the difference.
        //
        // UpdateTrustLevel takes a delta and is protected, so it needs Traverse
        // either way; passing (target - current) means the game recomputes the
        // indicator, emoji, achievements and events exactly as it would after a
        // real conversation beat.
        //
        // The delta is an int because that is the method's signature, so the
        // reachable targets are whole numbers away from wherever trust sits -
        // fine for crossing a threshold at 40, which is the entire point.
        public static bool SetTrust(float target)
        {
            try
            {
                object beh = Murder.BehaviourObject();
                if (beh == null)
                {
                    Plugin.Log.LogWarning("Cheats: no character in this scene, so trust was not changed.");
                    return false;
                }

                float? cur = Trust();
                if (!cur.HasValue)
                {
                    Plugin.Log.LogWarning("Cheats: could not read trust, so it was not changed.");
                    return false;
                }

                int delta = Mathf.RoundToInt(target - cur.Value);
                if (delta == 0) return true;

                Traverse m = Traverse.Create(beh).Method("UpdateTrustLevel", new object[] { delta });
                if (!m.MethodExists())
                {
                    Plugin.Log.LogWarning("Cheats: UpdateTrustLevel is missing, so trust was not changed.");
                    return false;
                }

                m.GetValue();
                Plugin.Log.LogInfo("Cheats: trust " + cur.Value.ToString("0.#") + " -> "
                    + Trust().GetValueOrDefault().ToString("0.#") + " (delta " + delta + ").");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Cheats: could not change trust: " + e.Message);
                return false;
            }
        }

        // ---- message count -------------------------------------------------

        // chatTimes is the gate behind the front door and the apartment key, and
        // it is a message count rather than trust: the engine deletes
        // giving_to_player for the key while chatTimes <= 10
        // (NPCMasterBehavior_Main_L1.cs:123), and suppresses even the pickup
        // notice below the same threshold (:335-340). So testing either one meant
        // typing eleven real messages first.
        //
        // It is declared private on each level subclass, not on the shared base -
        // L1:828, L2:1013, L4:592, L99_Rorre:340 - which is the same split that
        // made the secret room silently fail. Walk the subclasses and use
        // whichever one this scene actually has.
        static readonly string[] ChatLevels =
        {
            "NPCMasterBehavior_Main_L1",
            "NPCMasterBehavior_Main_L2",
            "NPCMasterBehavior_Main_L4",
            "NPCMasterBehavior_Main_L99_Rorre",
        };

        // The subclass component holding chatTimes, or null outside a level.
        static object ChatHost()
        {
            object beh = Murder.BehaviourObject();
            UnityEngine.Component c = beh as UnityEngine.Component;
            if (c == null) return null;

            for (int i = 0; i < ChatLevels.Length; i++)
            {
                Type t = AccessTools.TypeByName(ChatLevels[i]);
                if (t == null) continue;

                UnityEngine.Component sub = c.GetComponent(t);
                if (sub == null) continue;

                if (Traverse.Create(sub).Field("chatTimes").FieldExists()) return sub;
            }
            return null;
        }

        public static int? Messages()
        {
            try
            {
                object host = ChatHost();
                if (host == null) return null;

                object v = Traverse.Create(host).Field("chatTimes").GetValue();
                return v is int ? (int?)(int)v : null;
            }
            catch (Exception) { return null; }
        }

        // The threshold this level gates on, so the panel can say what the number
        // needs to beat instead of hardcoding 10. Every level ships 10 today, but
        // they are four separate fields and L2's is named after the necklace.
        public static int? MessageGate()
        {
            try
            {
                object host = ChatHost();
                if (host == null) return null;

                string[] names =
                {
                    "chatTimesThreshold_OpenDoorLimitation",
                    "chatTimesThreshold_GivingNecklace",
                };

                for (int i = 0; i < names.Length; i++)
                {
                    Traverse f = Traverse.Create(host).Field(names[i]);
                    if (!f.FieldExists()) continue;

                    object v = f.GetValue();
                    if (v is int) return (int)v;
                }
                return null;
            }
            catch (Exception) { return null; }
        }

        // Written directly, unlike trust: nothing recomputes from chatTimes, it is
        // only ever compared against a threshold, so there is no game method to
        // route through and no derived state to leave stale.
        public static bool SetMessages(int value)
        {
            try
            {
                if (value < 0) value = 0;

                object host = ChatHost();
                if (host == null)
                {
                    Plugin.Log.LogWarning("Cheats: no level behaviour in this scene, so the message "
                        + "count was not changed. Open the panel during a conversation.");
                    return false;
                }

                int before = Messages().GetValueOrDefault();
                Traverse.Create(host).Field("chatTimes").SetValue(value);

                Plugin.Log.LogInfo("Cheats: message count " + before + " -> " + value
                    + " on " + host.GetType().Name + ".");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Cheats: could not change the message count: " + e.Message);
                return false;
            }
        }

        // ---- gems ----------------------------------------------------------

        // Gems live in PlayerPrefs under "gems" - every writer in the game goes
        // through it (Shop.cs:126, UIManager_Gacha.cs:636, UIManager_Ending.cs:617),
        // and the hub seeds it from the PlayFab inventory on load
        // (LevelManager_HubWorld.cs:334). So PlayerPrefs is the local truth, but
        // it is a mirror: the hub overwrites it from the server next time it
        // loads, which is worth saying in the panel rather than letting someone
        // think a cheat purchase is permanent.
        public static int Gems()
        {
            try { return PlayerPrefs.GetInt("gems", 0); }
            catch (Exception) { return 0; }
        }

        public static bool SetGems(int value)
        {
            try
            {
                if (value < 0) value = 0;

                PlayerPrefs.SetInt("gems", value);
                PlayerPrefs.Save();

                // The HUD and shop only redraw on this event; without it the
                // number is correct in memory and stale on screen.
                // EventManager.GemUpdated() is the game's own one-line wrapper
                // (EventManager.cs:7-13).
                Type t = AccessTools.TypeByName("EventManager");
                if (t != null)
                {
                    Traverse ev = Traverse.Create(t).Method("GemUpdated");
                    if (ev.MethodExists()) ev.GetValue();
                }

                Plugin.Log.LogInfo("Cheats: gems set to " + value + ".");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Cheats: could not set gems: " + e.Message);
                return false;
            }
        }

        // ---- third-party mods ----------------------------------------------

        // Three mods by Luigirocks900, driven rather than bundled.
        //
        // They are separate BepInEx plugins with their own GUIDs, so BepInEx
        // loads them on its own and this panel only reflects into whatever is
        // already there. That is deliberate: their DLLs are not ours to
        // redistribute, and a hard reference would make this mod fail to load
        // for everyone who does not have them. Absent, the rows say so and
        // explain where to get them.
        //
        // The capitalisation of the GUIDs really is inconsistent between them -
        // Invincibility uses a lowercase l, the other two a capital L. These
        // strings are copied from each assembly's own BepInPlugin attribute, so
        // do not "tidy" them: the lookup is case-sensitive.
        internal const string GiftGuid = "com.Luigirocks900.GiftYourselfAnything";
        internal const string InvincGuid = "com.luigirocks900.InvincibilityMod";
        internal const string AtriumGuid = "com.Luigirocks900.RestoreAtriumGifts";

        static bool? _gift, _invinc, _atrium;

        public static bool GiftInstalled
        {
            get
            {
                if (!_gift.HasValue) _gift = HasPlugin(GiftGuid);
                return _gift.Value;
            }
        }

        public static bool InvincInstalled
        {
            get
            {
                if (!_invinc.HasValue) _invinc = HasPlugin(InvincGuid);
                return _invinc.Value;
            }
        }

        // RestoreAtriumGifts has nothing to drive: it is a Harmony patch that
        // restores gifting in the Atrium and has no state and no keybind. The
        // panel only reports whether it is loaded, so a missing gift in the hub
        // can be told apart from a missing mod.
        public static bool AtriumInstalled
        {
            get
            {
                if (!_atrium.HasValue) _atrium = HasPlugin(AtriumGuid);
                return _atrium.Value;
            }
        }

        static bool HasPlugin(string guid)
        {
            try
            {
                return BepInEx.Bootstrap.Chainloader.PluginInfos != null
                    && BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(guid);
            }
            catch (Exception) { return false; }
        }

        // Invincibility keeps its state in a static bool on its own plugin type
        // (isInvulnerable), and toggling it also swaps the HUD health icon. Read
        // and write it through the live instance so the icon follows, rather
        // than duplicating its UI work here.
        // Reflected over the shipped assembly to confirm the layout rather than
        // guess it: isInvulnerable is a public static bool on
        // InvincibilityMod.InvincibilityMod, and UpdateIndicatorVisibility is an
        // *instance* method on InvincibilityMod.InvincibilityModActual, which is
        // the MonoBehaviour that owns the HUD icon. Its Update() also polls the
        // same static every frame, so writing the field is enough on its own and
        // the repaint below only makes the icon change on the same frame instead
        // of the next one.
        //
        // Fully-qualified names come first because the namespace and the plugin
        // type share the name "InvincibilityMod", and an unqualified lookup can
        // resolve to the namespace and find no field. The short names stay as a
        // fallback in case the author renames the namespace, since this is
        // someone else's binary and we get no say in its layout.
        static readonly string[] InvincTypes =
        {
            "InvincibilityMod.InvincibilityMod",
            "InvincibilityMod.InvincibilityModActual",
            "InvincibilityMod", "InvincibilityModActual", "InvincibilityModObject",
        };

        static Traverse InvincField()
        {
            object inst = PluginInstance(InvincGuid);
            if (inst != null)
            {
                Traverse t = Traverse.Create(inst).Field("isInvulnerable");
                if (t.FieldExists()) return t;
            }

            for (int i = 0; i < InvincTypes.Length; i++)
            {
                Type ty = AccessTools.TypeByName(InvincTypes[i]);
                if (ty == null) continue;

                Traverse t = Traverse.Create(ty).Field("isInvulnerable");
                if (t.FieldExists()) return t;
            }
            return null;
        }

        public static bool? Invincible()
        {
            if (!InvincInstalled) return null;

            try
            {
                Traverse f = InvincField();
                if (f == null) return null;

                object v = f.GetValue();
                return v is bool ? (bool?)(bool)v : null;
            }
            catch (Exception) { return null; }
        }

        public static bool SetInvincible(bool on)
        {
            if (!InvincInstalled) return false;

            try
            {
                Traverse f = InvincField();
                if (f == null) return false;
                f.SetValue(on);

                // Its own code repaints the health icon in UpdateIndicatorVisibility,
                // which lives on the scene component rather than the plugin - so it
                // has to be found through the live object, not the plugin instance.
                // Skipped silently if absent: the state change is the part that
                // matters and a stale icon is not worth failing the toggle over.
                RepaintInvincIndicator();

                Plugin.Log.LogInfo("Cheats: invincibility " + (on ? "ON" : "off") + ".");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Cheats: could not toggle invincibility: " + e.Message);
                return false;
            }
        }

        static void RepaintInvincIndicator()
        {
            try
            {
                for (int i = 0; i < InvincTypes.Length; i++)
                {
                    Type ty = AccessTools.TypeByName(InvincTypes[i]);
                    if (ty == null) continue;

                    // Static form first, then any live instance in the scene.
                    Traverse st = Traverse.Create(ty).Method("UpdateIndicatorVisibility");
                    if (st.MethodExists()) { st.GetValue(); return; }

                    if (!typeof(UnityEngine.Object).IsAssignableFrom(ty)) continue;

                    UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(ty);
                    for (int j = 0; found != null && j < found.Length; j++)
                    {
                        Traverse m = Traverse.Create(found[j]).Method("UpdateIndicatorVisibility");
                        if (m.MethodExists()) { m.GetValue(); return; }
                    }
                }
            }
            catch (Exception) { }
        }

        static object PluginInstance(string guid)
        {
            try
            {
                BepInEx.PluginInfo info;
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(guid, out info)) return null;
                return info.Instance;
            }
            catch (Exception) { return null; }
        }
    }
}
