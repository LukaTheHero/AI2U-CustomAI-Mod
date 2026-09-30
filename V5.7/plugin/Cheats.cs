// Developer cheats: read and write the two numbers that gate everything else,
// hand yourself any item, and switch off dying.
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
using System.Collections.Generic;
using System.Reflection;
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
        // True only while the cheat trust editor is mid-write. Read by the
        // trust pipeline prefix in Feelings.cs, which stands down so the raw
        // delta passes through unscaled.
        public static bool BypassTrustPipeline;

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

                // Bypass the difficulty/favorability pipeline for this one call.
                // "Set trust to exactly 30" has to mean 30 - on Masochist the
                // scaled delta would land somewhere else entirely, and a testing
                // instrument that misses its target is worse than none.
                BypassTrustPipeline = true;
                try { m.GetValue(); }
                finally { BypassTrustPipeline = false; }
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

        // The gem cheat was removed in 4.2.0. It only ever edited a local mirror:
        // gems are server-backed and the hub reloads them from the PlayFab
        // inventory on every load (LevelManager_HubWorld.cs:334), silently wiping
        // the edit. Not fixable from this side, so it is gone rather than broken.

        // ---- give an item --------------------------------------------------

        // Type a name, get one. This used to be done by a separate mod that read
        // the chat box: you held Ctrl and pressed Enter and the line you had typed
        // became an item instead of a message. That worked, but it put a cheat on
        // the same keystroke as talking to her, so a mistimed Ctrl turned a
        // sentence into an item and the sentence was gone. It is a button here
        // instead, and the chat box is only ever the chat box.
        //
        // Matching is the game's own Item.Equals(string) (Item.cs:452), which
        // accepts the internal name case-insensitively, the localised display
        // name, and any alias the item library lists for it. Reimplementing that
        // comparison would mean a name that works in one place and not another.
        // True when there is a player inventory to put something into. The panel
        // greys the row out on this rather than letting the button fail: in the main
        // menu there is no inventory at all, and a button that reports an error is a
        // worse answer than a button that visibly is not available yet.
        public static bool ItemsReady
        {
            get
            {
                try { return Inventory.FindInventory("PlayerInventory") != null; }
                catch (Exception) { return false; }
            }
        }

        // Returns the internal name of what was actually granted, or null on failure.
        // The name matters to the caller: "teddy bear" grants TeddyBear, and the panel
        // says so, which is how you learn the real spelling for next time.
        public static string GiveItem(string typed)
        {
            if (string.IsNullOrEmpty(typed) || typed.Trim().Length == 0)
            {
                Plugin.Log.LogInfo("Cheats: no item name typed.");
                return null;
            }

            typed = typed.Trim();

            try
            {
                Inventory inv = Inventory.FindInventory("PlayerInventory");
                if (inv == null)
                {
                    Plugin.Log.LogInfo("Cheats: no player inventory in this scene.");
                    return null;
                }

                Item found = FindItem(typed) ?? Placeholder(typed);
                if (found == null) return null;

                // isNewItem: true, so this goes down the same path as picking the
                // item up off the floor - the pickup sound, the "new item" flash
                // and any event module attached to it all fire. false would drop
                // it into the grid silently, which reads as the button not working.
                inv.AddItem(found, true);

                string name = found.GetInternalName();
                if (string.IsNullOrEmpty(name)) name = typed;
                Plugin.Log.LogInfo("Cheats: gave 1x " + name + ".");
                return name;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Cheats: could not give \"" + typed + "\": " + e.Message);
                return null;
            }
        }

        // An item the game does not have, built at runtime. This is the same shape the
        // game itself uses when the AI offers you something that is not a real asset:
        // IsAiGift plus the generic gift sprite. Without it, typing a name with a
        // typo in it looks identical to the button being broken.
        static Item Placeholder(string typed)
        {
            try
            {
                Item made = ScriptableObject.CreateInstance<Item>();
                made.ItemName = typed;
                made.ItemDesc = "Default description for " + typed;
                made.IsAiGift = true;

                Sprite s = GiftSprite();
                if (s != null) made.ItemSprite = s;

                Plugin.Log.LogInfo("Cheats: no asset named \"" + typed
                    + "\", granting a placeholder.");
                return made;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Cheats: could not build a placeholder for \""
                    + typed + "\": " + e.Message);
                return null;
            }
        }

        // The sprite the game shows for an AI-given item, read off whichever NPC
        // behaviour is in the scene. Reflected rather than accessed directly because
        // the field is protected, and a null answer only costs us a blank icon.
        static Sprite GiftSprite()
        {
            try
            {
                NPCMasterBehavior_MainCharacter npc =
                    UnityEngine.Object.FindObjectOfType<NPCMasterBehavior_MainCharacter>();
                if (npc == null) return null;

                return Traverse.Create(npc).Field("itemSprite_AIGiven").GetValue() as Sprite;
            }
            catch (Exception) { return null; }
        }

        // Every Item asset the game has loaded, plus anything sitting in a
        // Resources/items folder that has not been touched yet. FindObjectsOfTypeAll
        // rather than FindObjectsOfType because these are ScriptableObjects, not
        // scene objects - the ordinary find would return none of them.
        static Item[] AllItems()
        {
            List<Item> all = new List<Item>();

            try { all.AddRange(Resources.FindObjectsOfTypeAll<Item>()); }
            catch (Exception) { }

            try
            {
                Item[] loose = Resources.LoadAll<Item>("items");
                for (int i = 0; i < loose.Length; i++)
                    if (!all.Contains(loose[i])) all.Add(loose[i]);
            }
            catch (Exception) { }

            return all.ToArray();
        }

        // Internal names are run together - TeddyBear, RedCandle - so a person typing
        // what they see in the inventory types two words. Stripping spaces and
        // punctuation on both sides is what makes "teddy bear" land.
        static string Squash(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            char[] buf = new char[s.Length];
            int n = 0;
            for (int i = 0; i < s.Length; i++)
                if (char.IsLetterOrDigit(s[i])) buf[n++] = char.ToLowerInvariant(s[i]);

            return new string(buf, 0, n);
        }

        static Item FindItem(string typed)
        {
            Item[] all = AllItems();

            // Exact internal name first. Item.Equals also matches aliases and the
            // localised name, so going straight to it would let a loose alias win
            // over the item whose real name was typed.
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                if (string.Equals(all[i].GetInternalName(), typed,
                        StringComparison.OrdinalIgnoreCase))
                    return all[i];
            }

            string want = Squash(typed);
            if (want.Length > 0)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    if (Squash(all[i].GetInternalName()) == want) return all[i];
                }
            }

            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                try { if (all[i].Equals(typed)) return all[i]; }
                catch (Exception) { }
            }

            return null;
        }

        // Names for the panel to offer, so nobody has to guess at spelling. Sorted
        // and de-duplicated: the same asset can be reachable twice.
        static float _namesAt = -99f;
        static string[] _names = new string[0];

        public static string[] ItemNames()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _namesAt < 5f) return _names;
            _namesAt = now;

            try
            {
                Item[] all = AllItems();
                List<string> names = new List<string>();

                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    string n = all[i].GetInternalName();
                    if (string.IsNullOrEmpty(n)) continue;
                    if (!names.Contains(n)) names.Add(n);
                }

                names.Sort(StringComparer.OrdinalIgnoreCase);
                _names = names.ToArray();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Cheats: could not list items: " + e.Message);
            }

            return _names;
        }

        // ---- invincibility -------------------------------------------------

        // Deliberately not persisted to the config: it is a testing state, and one
        // that quietly survived a restart would make a later "why is nothing killing
        // me" very hard to explain.
        //
        // It also comes off when you change level. Checked lazily rather than from a
        // scene-load hook, because the only moment the answer matters is when
        // something is about to take health away, and that is where it is read.
        static bool _invincOn;
        static int _invincLevel;

        public static bool InvincibleOn
        {
            get
            {
                if (!_invincOn) return false;

                int now;
                try { now = GameManager.CurrentLevel; }
                catch (Exception) { return _invincOn; }

                if (now != _invincLevel)
                {
                    _invincOn = false;
                    Plugin.Log.LogInfo("Cheats: invincibility cleared on level change.");
                    return false;
                }

                return true;
            }
        }


        public static bool SetInvincible(bool on)
        {
            _invincOn = on;
            try { _invincLevel = GameManager.CurrentLevel; }
            catch (Exception) { _invincLevel = 0; }

            // Heal on the way in, so switching it on part-way through a fight is
            // not "invincible at one heart". Going off leaves health where it is.
            if (on)
            {
                try
                {
                    PlayerProperty p = UnityEngine.Object.FindObjectOfType<PlayerProperty>();
                    if (p != null && p.Health < p.MaxHealth) p.Health = p.MaxHealth;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Cheats: invincibility could not top up health: " + e.Message);
                }
            }

            Plugin.Log.LogInfo("Cheats: invincibility " + (on ? "ON" : "off") + ".");
            return true;
        }
    }

    // Invincibility, enforced in one place: refuse any write to Health that would
    // lower it.
    //
    // Every damage path in the game ends at this one public setter. The level
    // controllers do their own arithmetic and then read the result back to decide
    // whether you died - PlayerController_L2.cs:98,110,124,141,154,169 and the L1,
    // L3, L4 equivalents all do "Health = Health - 1" and then test "Health <= 0" -
    // so refusing the decrease makes the death branch unreachable without having to
    // know about any of them. That includes the ones that skip the arithmetic and
    // assign zero outright (PlayerController_L2.cs:311, the forest during the final
    // chase).
    //
    // This is why it is the setter and not the five GetHit overrides the mod this
    // replaced patched: those are reached through decreaseHealthEvent listeners
    // (PlayerController.cs:230), and patching listeners means finding all of them
    // and finding the next one somebody adds. There is only ever one setter.
    //
    // Healing, and anything else that raises health, is left alone.
    [HarmonyPatch(typeof(PlayerProperty), "Health", MethodType.Setter)]
    public static class InvincibilityHealthPatch
    {
        public static bool Prefix(PlayerProperty __instance, int value)
        {
            if (!Cheats.InvincibleOn) return true;
            return value >= __instance.Health;
        }
    }

    // One level's hit reaction is not health-gated: it closes the UI, takes input
    // away and moves the player, all before looking at health, so blocking the
    // damage above does not stop it (PlayerController_L99.cs:66-90 - it never
    // touches Health at all). That one needs suppressing directly.
    //
    // Resolved by name rather than typeof so that a build without this controller
    // skips the patch instead of failing to load. Prepare returning false is the
    // way to say "nothing to patch here" - a null TargetMethod is treated as a
    // failed patch and logged as an error.
    [HarmonyPatch]
    public static class InvincibilityHitReactionPatch
    {
        static Type Target()
        {
            return AccessTools.TypeByName("PlayerController_L99");
        }

        public static bool Prepare()
        {
            Type t = Target();
            return t != null && AccessTools.Method(t, "GetHit") != null;
        }

        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(Target(), "GetHit");
        }

        public static bool Prefix()
        {
            return !Cheats.InvincibleOn;
        }
    }

    // Gifts work everywhere except the atrium, where they are parsed and then
    // dropped on the floor.
    //
    // NPCMasterBehavior_Main_Config.ReceiveItem (NPCMasterBehavior_Main_Config.cs:434)
    // is an override with an empty body. The reply pipeline does call it - the
    // "giving_to_player" field is read and handed over at :341 - so she decides to
    // give you something, says she has, and nothing arrives. Every other level
    // inherits the base implementation or writes a working one.
    //
    // The fix restores the base behaviour (NPCMasterBehavior_MainCharacter.cs:632).
    // It cannot simply call the base method: reflection dispatches virtually, so
    // invoking it would land back on the empty override. The body is short and is
    // reproduced here, including the part that takes the item out of her own
    // inventory - leaving that out means she can gift the same object forever.
    //
    // Not gated on the cheats toggle. This is a bug fix, not a cheat.
    [HarmonyPatch]
    public static class AtriumGiftPatch
    {
        static Type Target()
        {
            return AccessTools.TypeByName("NPCMasterBehavior_Main_Config");
        }

        public static bool Prepare()
        {
            Type t = Target();
            return t != null
                && AccessTools.Method(t, "ReceiveItem", new Type[] { typeof(string) }) != null;
        }

        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(Target(), "ReceiveItem", new Type[] { typeof(string) });
        }

        public static bool Prefix(object __instance, string itemName)
        {
            try
            {
                Traverse t = Traverse.Create(__instance);

                // Delivering an item fires OnAddingItem, which adds the name to a
                // ten-second justGotItems window and sends her an "item get" prompt
                // (NPCMasterBehavior_Main_Config.cs:445,456). If that reply names the
                // same object again, without this check it arrives twice. The working
                // level overrides guard exactly this way, on the raw name, before the
                // cleaning step (NPCMasterBehavior_Main_L1.cs:316).
                List<string> justGot = t.Field("justGotItems").GetValue<List<string>>();
                if (justGot != null && justGot.Contains(itemName)) return false;

                string cleaned = t.Method("ReceiveItemNameCheck", new object[] { itemName })
                                  .GetValue<string>();
                if (string.IsNullOrEmpty(cleaned)) return false;

                t.Method("ReceiveItemUINoticeMessage", new object[] { cleaned }).GetValue();

                Inventory npcInv = t.Field("npcProperty").Property<Inventory>("NPCInventory").Value;
                if (npcInv != null && npcInv.ContainsItem(cleaned)) npcInv.RemoveItem(cleaned);

                Plugin.Log.LogInfo("Atrium gift: delivered \"" + cleaned + "\".");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Atrium gift: could not deliver \"" + itemName + "\": " + e.Message);
            }

            // The original body is empty either way; skipping it just avoids a
            // pointless call.
            return false;
        }
    }

}

