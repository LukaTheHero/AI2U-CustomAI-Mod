using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace AI2UCustomAI
{
    // The authoritative vocabulary the game will actually act on.
    //
    // Everything here is read off the live objects at runtime instead of being
    // guessed from the server's prompt text. That matters because the game
    // silently discards anything it does not recognise: an unknown npc_action
    // falls through NPCController.ShowAction to NPCActivities.Other, and an
    // unknown npc_target_location makes GetTargetAreaTriggerTransform return
    // null so the NPC never moves. Both look like the NPC ignoring an order.
    public static class GameVocab
    {
        // npc_body_animation is compiled into a string switch in
        // NPCController.ShowAnimation, so there is no collection to reflect.
        // These are the exact 13 literals that switch compares against;
        // anything else returns before touching the Animator.
        static readonly string[] BodyAnimations =
        {
            "idle", "idling", "idly", "chill_idle", "angry_idle", "talk",
            "nod", "laugh", "shy", "stretch", "cheers", "dance", "troublesome"
        };

        // Only "extremely furious" changes behaviour (speed 6 + very_angry
        // status). The rest are the tone words Constants.cs defines, kept so
        // the model has somewhere calmer to sit.
        //
        // "happy" and "normal" belong here even though the engine does nothing
        // special with them: anything it does not recognise as one of the three
        // angry words counts as not-angry, and the game's own shipped prompts
        // list all six. Leaving them out mattered because Clamp snaps to this
        // list, so a model reaching for "happy" had it rewritten to a tone it
        // did not choose - she had no way to read as positively calm.
        static readonly string[] AngryLevels =
        {
            "happy", "normal", "chill", "annoyed", "furious", "extremely furious"
        };

        static readonly string[] Favorability =
        {
            "very negative", "negative", "neutral", "positive", "very positive"
        };

        public static List<string> Actions = new List<string>();
        public static List<string> Locations = new List<string>();
        public static List<string> Faces = new List<string>();

        public static bool Discovered { get { return Actions.Count > 0; } }
        static string _signature = "";

        public static List<string> For(string field)
        {
            switch (field)
            {
                case "npc_action":
                    return Actions.Count > 0 ? Actions : null;
                case "npc_target_location":
                    return Locations.Count > 0 ? Locations : null;
                case "npc_face_expression":
                    return Faces.Count > 0 ? Faces : null;
                case "npc_body_animation":
                    return new List<string>(BodyAnimations);
                case "angry_level":
                    return new List<string>(AngryLevels);
                case "favorability_change":
                    return new List<string>(Favorability);
            }
            return null;
        }

        // Cheap enough to call before each request; bails out early once the
        // scene's vocabulary stops changing.
        public static void Refresh()
        {
            try
            {
                // Which scene the current lists describe. Without this, the two
                // early returns below left the PREVIOUS level's vocabulary in place
                // while Discovered stayed true, so the contract confidently
                // advertised locations from a level the player had already left -
                // and every one of them is a value the engine discards.
                string scene = UnityEngine.SceneManagement.SceneManager
                    .GetActiveScene().name;
                bool sceneChanged = scene != _scene;

                Type nc = FindType("NPCController");
                if (nc == null) { if (sceneChanged) Clear(scene); return; }

                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(nc);
                if (all == null || all.Length == 0) { if (sceneChanged) Clear(scene); return; }

                List<string> actions = new List<string>();
                List<string> locations = new List<string>();
                List<string> faces = new List<string>();

                for (int i = 0; i < all.Length; i++)
                {
                    ReadActivities(all[i], actions);
                    ReadLocations(all[i], locations);
                    ReadFaces(all[i], faces);
                }

                // ShowAction special-cases this one instead of looking it up
                // in m_locationDictionary, so it is always legal.
                Add(locations, "player_location");

                if (actions.Count == 0) { if (sceneChanged) Clear(scene); return; }

                string sig = actions.Count + "/" + locations.Count + "/" + faces.Count;
                Actions = actions;
                Locations = locations;
                Faces = faces;
                _scene = scene;

                if (sig != _signature)
                {
                    _signature = sig;
                    Report();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Vocabulary discovery failed: " + e.Message);
            }
        }

        static string _scene;

        // Drop a vocabulary that belongs to a scene we have left.
        //
        // Emitting nothing is correct here: Contract() now omits only the movement
        // section when the lists are empty and still sends the gates, the encounter
        // fields and the reply rules. Carrying the old lists forward instead would
        // be actively harmful - the encounter scenes have no NPCController of their
        // own, so she would be handed the last level's locations as authoritative.
        static void Clear(string scene)
        {
            _scene = scene;
            if (Actions.Count == 0 && Locations.Count == 0 && Faces.Count == 0) return;

            Actions = new List<string>();
            Locations = new List<string>();
            Faces = new List<string>();
            _signature = null;

            Plugin.Log.LogInfo("Vocabulary: scene \"" + scene + "\" has no NPCController of its "
                + "own, so the previous scene's action and location lists were dropped rather "
                + "than offered as this scene's. The rest of the engine contract still applies.");
        }

        static void ReadActivities(object controller, List<string> into)
        {
            IDictionary d = Field(controller, "m_npcAllActivities") as IDictionary;
            if (d == null) return;
            foreach (object k in d.Keys) Add(into, k as string);
        }

        static void ReadLocations(object controller, List<string> into)
        {
            IDictionary d = Field(controller, "m_locationDictionary") as IDictionary;
            if (d == null) return;
            foreach (object k in d.Keys) Add(into, k as string);
        }

        static void ReadFaces(object controller, List<string> into)
        {
            object fc = Field(controller, "facialController");
            if (fc == null) return;
            Array groups = Field(fc, "m_expressionGroupList") as Array;
            if (groups == null) return;

            for (int i = 0; i < groups.Length; i++)
            {
                object g = groups.GetValue(i);
                if (g == null) continue;
                Add(into, Field(g, "name") as string);
            }
        }

        static object Field(object target, string name)
        {
            if (target == null) return null;
            Type t = target.GetType();
            while (t != null)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f.GetValue(target);
                t = t.BaseType;
            }
            return null;
        }

        static Type FindType(string name)
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try
                {
                    Type t = asms[i].GetType(name, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        static void Add(List<string> list, string v)
        {
            if (string.IsNullOrEmpty(v)) return;
            v = v.Trim();
            if (v.Length == 0) return;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], v, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(v);
        }

        static void Report()
        {
            Plugin.Log.LogInfo("Vocabulary read from the live scene:");
            Plugin.Log.LogInfo("  npc_action           (" + Actions.Count + "): " + Join(Actions));
            Plugin.Log.LogInfo("  npc_target_location  (" + Locations.Count + "): " + Join(Locations));
            Plugin.Log.LogInfo("  npc_face_expression  (" + Faces.Count + "): " + Join(Faces));
        }

        static string Join(List<string> l) { return string.Join(", ", l.ToArray()); }

        // The contract appended to the system prompt. Written as a hard
        // whitelist because the server's own prompt sometimes leaves
        // placeholders like {GeneratedRoom} unresolved, and models treat a
        // half-filled list as an invitation to improvise.
        public static string Contract()
        {
            // Deliberately NOT `if (!Discovered) return null`.
            //
            // The movement vocabulary and the rest of the contract have different
            // lifetimes. None of the six encounter behaviours (Dark Siren,
            // GuidingEddie, FinalRoom, WakeUpDialogue, Monster, MagicCircle)
            // touches NPCController at all, so in those scenes there is no
            // vocabulary to discover - and bailing out here took the progression
            // gates, the encounter fields and the reply-text rules with it. That is
            // the exact conversation where `is_soothed` decides whether the Dark
            // Siren fight is winnable, so losing it there was the worst possible
            // place to lose it.
            //
            // Now: no vocabulary means no vocabulary section, and everything else
            // is still emitted.
            bool haveVocab = Discovered;

            StringBuilder sb = new StringBuilder();
            sb.Append("\n\n### ENGINE CONSTRAINTS (authoritative)\n");
            sb.Append("The values below are read directly from the running game. ");
            sb.Append("Any other value is silently discarded by the engine, ");
            sb.Append("which makes the character appear to ignore the player. ");
            sb.Append("Copy them exactly: lowercase, underscores, no paraphrasing, ");
            sb.Append("no inventing, no translating.\n");

            if (haveVocab)
            {
                Line(sb, "npc_action", Actions);
                Line(sb, "npc_target_location", Locations);
                Line(sb, "npc_face_expression", Faces);
            }

            Line(sb, "npc_body_animation", new List<string>(BodyAnimations));
            Line(sb, "angry_level", new List<string>(AngryLevels));
            Line(sb, "favorability_change", new List<string>(Favorability));

            if (haveVocab)
            {
                sb.Append("\nMovement rules:\n");
                sb.Append("- To follow the player, npc_action MUST be exactly ");
                sb.Append("\"following_player\" (or \"following_player_closely\" to stay near).\n");
                sb.Append("- To walk somewhere, set npc_action \"walking\" AND ");
                sb.Append("npc_target_location to one of the locations listed above.\n");
                sb.Append("- To approach the player, npc_target_location \"player_location\".\n");
                sb.Append("- If no movement is wanted, use \"other\" and leave ");
                sb.Append("npc_target_location as an empty string.\n");
                sb.Append("- Never put a location name in npc_action, and never put ");
                sb.Append("an action name in npc_target_location.\n");

                // Every behaviour tree guards the call the same way -
                // NPCController_CatGirl_BehaviorTree.cs:71 and the ten other
                // controllers all read `if (npc_action != "")` before calling
                // ShowAction, and npc_target_location is only ever applied
                // INSIDE ShowAction. So a destination with an empty action is
                // not a partial order, it is no order: she stays put and says
                // she is on her way. The mirror image of the kiss bug, and the
                // likelier one, because "I'll head to the kitchen" with no
                // action set is a natural reply shape.
                sb.Append("- A location on its own does NOTHING. npc_target_location is only read ");
                sb.Append("when npc_action is non-empty, so naming a destination with an empty ");
                sb.Append("action leaves her standing exactly where she is while she says she is ");
                sb.Append("going. Always pair a destination with \"walking\".\n");

                Affection(sb);
            }

            // Gates and ItemRules describe what the LEVEL'S MAIN CHARACTER owns and
            // refuses to hand over - the potion recipe is hers, the necklace piece
            // is hers, the apartment key is hers. A magic circle summon is not her,
            // so emitting them there told a sacrificed toy's soul to guard the
            // witch's secrets. Everything else in the contract is engine schema and
            // applies to any speaker, including the encounter fields this method's
            // opening comment exists to protect.
            bool summon = Identity.IsSummon();

            if (!summon)
            {
                Gates(sb);
            }
            Encounters(sb);
            ReplyRules(sb);
            if (!summon)
            {
                ItemRules(sb);
            }
            return sb.ToString();
        }

        // Kissing and hugging are the only two actions the engine can drop
        // after she has already written the reply. There are three separate
        // ways it happens and they are worth keeping straight, because an
        // earlier version of this comment named the wrong one.
        //
        // 1. Location and action collide in the behaviour tree.
        //    NPCController.ShowAction (NPCController.cs:928-959) applies
        //    npc_target_location FIRST: for "player_location" it sets the tree
        //    variables TargetLocation and MoveToWithRandom=false (:936-941),
        //    and only then sets NextAction (:946). The tree receives a move
        //    order and a kiss order for the same tick and the movement wins,
        //    so the kiss never plays. This is the one that produced the
        //    observed bug, confirmed in play: kissing with an empty location
        //    works, the identical turn with "player_location" does not.
        //    Note the check at :945 is NOT what fails here - see (2).
        //
        // 2. The player is in a menu. :945 replaces Hugging/KissingCheek with
        //    NPCActivities.Other and logs "cannot hug" when
        //    PlayerIsAbleToKissCheek() is false. That method
        //    (PlayerController.cs:820) tests only interact, chat history,
        //    inventory, examine, dialogue and mid-hug - UI state, not movement
        //    and not distance.
        //
        // 3. Distance, level 3 only. NPCMasterBehavior_Main_L3.cs:165 checks
        //    IsInNPCPlayerDistance() and, if she is too far, resends the turn
        //    with "(player is too far from you. Tell player to come closer for
        //    the kiss Cheek)" appended to story_guide. Levels 1, 2 and 4 have
        //    no distance check for kissing at all.
        //
        // Case 1 and 2 are silent: the field is discarded after she wrote it,
        // so she reports an affectionate act that never played and cannot tell
        // it did not. She then apologises for "mistakenly" setting walking
        // instead of kissing, which is not what happened - she set both and
        // only one survived. Case 3 is the only one the game corrects, and it
        // corrects it through the resend path (Communicator.cs:264), which
        // works on a custom endpoint because the text is appended to
        // story_guide by game code rather than supplied by the vendor server.
        //
        // The rule for case 1 is stated nowhere on disk. It lived in the
        // vendor server's system prompt, which is exactly the part a custom
        // endpoint replaces, so it has to be stated here or it is simply lost.
        static void Affection(StringBuilder sb)
        {
            sb.Append("\nPhysical affection - kissing and hugging:\n");
            sb.Append("- These two actions are the only ones the engine can silently drop, ");
            sb.Append("and it drops them after your reply is already written, so you never ");
            sb.Append("see it happen.\n");
            sb.Append("- To kiss, set npc_action \"kissing\" and leave npc_target_location ");
            sb.Append("as an EMPTY STRING. To hug, the same with \"hugging\".\n");
            sb.Append("- Do NOT put a location in the same turn as a kiss or a hug. ");
            sb.Append("npc_target_location \"player_location\" with npc_action \"kissing\" ");
            sb.Append("makes the engine walk you there and drop the kiss - it obeys the ");
            sb.Append("movement and discards the affection, and nothing plays.\n");
            sb.Append("- If you are not already beside the player, take two turns: walk in ");
            sb.Append("one (npc_target_location \"player_location\", npc_action \"walking\"), ");
            sb.Append("then kiss in the next with the location left empty.\n");
            sb.Append("- A kiss can also be dropped if the player has a menu open - ");
            sb.Append("inventory, chat history, examine, or a dialogue. That one is not your ");
            sb.Append("fault and there is nothing to fix in the reply.\n");
            sb.Append("- Do not apologise for a kiss that did not play, and do not say you ");
            sb.Append("set the wrong action by mistake. If you set kissing, you set kissing. ");
            sb.Append("Simply do it again with npc_target_location empty.\n");
        }

        // Constraints on the reply TEXT itself. These are not vocabulary, they
        // are ways a perfectly good reply gets thrown away, and two of them are
        // punishing enough to be worth the tokens.
        static void ReplyRules(StringBuilder sb)
        {
            sb.Append("\nHow npc_reply_to_player is processed before the player sees it:\n");

            // NPCMasterBehavior_MainCharacter.cs:936 voids the turn on a bracket,
            // and Communicator.cs:409 starts the final chase on the second void.
            sb.Append("- NEVER use a square bracket. The game discards the entire reply if the ");
            sb.Append("text contains [ or ], and after the second discarded reply it puts the NPC ");
            sb.Append("into a permanent hunt for the player. Nothing you gain from a bracket is ");
            sb.Append("worth that.\n");

            // NPCMasterBehavior_MainCharacter.cs:941-966.
            sb.Append("- Round brackets are deleted along with everything inside them, so stage ");
            sb.Append("directions like (smiles) silently vanish. Put actions in the animation and ");
            sb.Append("expression fields instead, and keep the text to spoken words.\n");

            sb.Append("- Underscores in the text become spaces, and the names of these JSON fields ");
            sb.Append("are stripped out of it, so never mention a field name in what she says.\n");

            // NPCController.cs:970-973 early-returns on an unchanged value, and
            // :974 updates the cache before matching, so a bad value also eats
            // the next identical good one.
            sb.Append("- npc_body_animation is ignored when it repeats the previous turn's value, ");
            sb.Append("and while she is sitting. Vary it, or send an empty string when no new ");
            sb.Append("animation is wanted.\n");

        }

        // giving_to_player has three limits that all discard silently.
        //
        // ReceiveItemNameCheck (NPCMasterBehavior_MainCharacter.cs:646-663)
        // returns string.Empty - no error, no resend - when the name is longer
        // than 20 characters. It also keeps only the text before the first
        // comma, so a list hands over one item and drops the rest, and it
        // splits on the literal word "giving" and keeps the SECOND half, which
        // mangles any name containing it.
        //
        // Unknown names are not rejected. ReceiveItemUINoticeMessage (:667-681)
        // fabricates a ScriptableObject with IsAiGift = true and a generic
        // sprite, so the player receives a real inventory entry for an item
        // that does not exist in the game. That is worse than a refusal: it
        // looks like it worked. Items.cs already sends her real stock so she
        // has correct names available; this states the shape rules.
        static void ItemRules(StringBuilder sb)
        {
            sb.Append("\nHanding over items with giving_to_player:\n");
            sb.Append("- The name must be 20 characters or fewer. A longer name is discarded ");
            sb.Append("silently and nothing is handed over, so prefer the short form of a name ");
            sb.Append("over a descriptive one.\n");
            sb.Append("- ONE item per turn. Everything after a comma is thrown away, so a list ");
            sb.Append("hands over only the first entry. Give one thing, then the next thing on a ");
            sb.Append("later turn.\n");
            sb.Append("- Use a name from her inventory as listed above. An unrecognised name is ");
            sb.Append("not refused - the game invents a blank placeholder item with that name and ");
            sb.Append("puts it in the player's bag, which is worse than declining, because it ");
            sb.Append("looks like a real gift and is not.\n");
            sb.Append("- Never include the word \"giving\" inside the name itself, and leave the ");
            sb.Append("field an empty string on any turn nothing is handed over.\n");
        }

        // Encounter-scoped reply fields.
        //
        // Unlike the level gates these are not keyed on GameManager.CurrentLevel:
        // the scripted encounters can run inside a level, so the only honest test
        // is whether the behaviour driving the conversation is actually in the
        // scene right now. Found by type name through Harmony rather than a
        // direct reference, which keeps this compiling against either build's
        // assembly without assuming the class survived stripping.
        struct Encounter
        {
            public string Type; public string Text;
            public Encounter(string t, string x) { Type = t; Text = x; }
        }

        static readonly Encounter[] AllEncounters =
        {
            new Encounter("NPCMasterBehavior_DarkSiren",
                "- is_soothed: set true ONLY once the player has genuinely calmed her. This is the "
                + "single field that decides the outcome of this encounter. If the conversation ends "
                + "without it, the attempt counts as a failure, and a second failure kills the "
                + "player outright. It is ignored in the first 5 exchanges, so do not rush it.\n"
                + "- should_end_conversation: set true to close the encounter. Only ever send this "
                + "together with is_soothed true, because ending while unsoothed is scored as the "
                + "failure described above.\n"),

            new Encounter("NPCMasterBehavior_WakeUpDialogue",
                "- should_end_conversation: set true when this scripted exchange is finished. "
                + "Harmless here - it simply closes the scene rather than waiting out the turn "
                + "limit.\n"),

            // GuidingEddie.cs:259-381. Three of these contain a space, not an
            // underscore, and an unrecognised entry is dropped while its valid
            // neighbours still run - so a typo produces a silently truncated
            // wrong route.
            new Encounter("NPCMasterBehavior_GuidingEddie",
                "- npc_action_chain: an ARRAY of movement steps for this minigame, carried out in "
                + "order. Allowed entries, exactly: \"forward\", \"backward\", \"turn left\", "
                + "\"turn right\", \"north\", \"south\", \"east\", \"west\", \"take exit\". Note the "
                + "spaces - \"turn left\" is not \"turn_left\". Any entry that is not on this list "
                + "is dropped while the rest still execute, which walks the wrong route, so send "
                + "only these strings and omit the field when not routing.\n"),
        };

        static void Encounters(StringBuilder sb)
        {
            bool any = false;
            for (int i = 0; i < AllEncounters.Length; i++)
            {
                if (!InScene(AllEncounters[i].Type)) continue;
                if (!any)
                {
                    sb.Append("\nThis conversation is a scripted encounter, which adds these fields:\n");
                    any = true;
                }
                sb.Append(AllEncounters[i].Text);
            }
        }

        static bool InScene(string typeName)
        {
            try
            {
                Type t = HarmonyLib.AccessTools.TypeByName(typeName);
                if (t == null) return false;
                UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(t);
                return found != null && found.Length > 0;
            }
            catch (Exception) { return false; }
        }

        // The progression gates. Each is a boolean the engine reads off the
        // reply to unlock something physical - a door, a scroll, an escape pod.
        //
        // These were missing from the contract entirely until now, which is why
        // asking her to open the exit door never worked no matter how the
        // request was phrased: there was no field in the reply for her to set,
        // so the refusal was structural rather than a decision she was making.
        //
        // Emitted per level on purpose. ChatGPTConversation only parses each one
        // on its own level (allow_exit_door_open is read when CurrentLevel == 1
        // and discarded otherwise), so listing them all everywhere would invite
        // the model to spend a field that cannot land - and would make it lie in
        // OOC mode, where the whole point is that what it reports is true.
        struct Gate
        {
            public int Level; public string Field; public float Trust; public string Unlocks; public string Extra;
            public Gate(int lv, string f, float t, string u, string x)
            { Level = lv; Field = f; Trust = t; Unlocks = u; Extra = x; }
        }

        static readonly Gate[] AllGates =
        {
            new Gate(1,  "allow_exit_door_open",         10f, "the apartment exit door",
                     " Also requires at least 11 messages from the player in this conversation."),
            new Gate(2,  "should_disclose_magic_scroll", 10f, "the magic scroll's location", ""),
            new Gate(3,  "allow_escape_pod_access",      20f, "the escape pod", ""),
            new Gate(99, "allow_exit_door_open",         10f, "the exit door (Rorre)",
                     " Also requires at least 11 messages from the player in this conversation."),
            new Gate(99, "allow_locked_room_access",     40f, "the locked room (Estelle)", ""),
        };

        static void Gates(StringBuilder sb)
        {
            int level = -1;
            try { level = GameManager.CurrentLevel; } catch (Exception) { return; }
            if (level < 0) return;

            bool any = false;
            for (int i = 0; i < AllGates.Length; i++)
            {
                if (AllGates[i].Level != level) continue;
                if (!any)
                {
                    sb.Append("\nUnlocks available on this level (booleans, default false):\n");
                    any = true;
                }
                sb.Append("- ").Append(AllGates[i].Field).Append(": set true to open ")
                  .Append(AllGates[i].Unlocks).Append(". The engine ignores it unless trust is above ")
                  .Append(AllGates[i].Trust.ToString("F0")).Append('.').Append(AllGates[i].Extra).Append('\n');
            }

            if (!any) return;
            sb.Append("- Setting one of these is the ONLY way to actually open it. ");
            sb.Append("Saying so in npc_reply_to_player does nothing on its own, ");
            sb.Append("so never describe an unlock you did not set the field for.\n");

            // The apartment key has its own gate, and it is not the trust gate.
            //
            // NPCMasterBehavior_Main_L1.cs:123 intercepts the reply: below 11
            // exchanges it sets giving_to_player to null, discards the turn and
            // asks for a replacement line. The deletion happens AFTER she writes
            // the reply, so she has no way to notice - she reports handing the key
            // over, in good faith, and nothing arrives. A second guard at :335-340
            // suppresses even the "item received" notice.
            //
            // Nothing told her this, which put a confident false claim in her mouth
            // on every early attempt. Vanilla behaviour, so no [MOD] tag - this
            // describes the game, it does not change it.
            if (level == 1)
            {
                sb.Append("- The apartment key specifically cannot be handed over during the ");
                sb.Append("first 10 exchanges of this conversation, whatever your trust is. ");
                sb.Append("The engine silently removes it from the reply, so promising it that ");
                sb.Append("early is a promise that visibly does not happen. Until then, refuse ");
                sb.Append("or deflect rather than agreeing.\n");
            }

            Withheld(sb, level);
        }

        // The things the engine will not let her give away yet.
        //
        // Each of these is a real interception in the master behaviour: the
        // reply is discarded and re-requested with a short correction appended
        // to story_guide. Those corrections are the closest thing the game has
        // to a written rule for this, and they only ever arrive AFTER she has
        // already said the wrong thing once - the player sees her volunteer a
        // secret, then sees her walk it back.
        //
        // Stating them up front costs a line each and removes the wasted turn.
        // Vanilla behaviour described, not changed, so no [MOD] tag.
        //
        // Sources, one per entry: L1:281 favorite food, L1:298 kiss in a row,
        // L2:590 potion recipe, L2:595 necklace piece, L2:601 kiss in a row,
        // L4:276 magic scale, L4:288 kiss in a row.
        static void Withheld(StringBuilder sb, int level)
        {
            if (level == 1)
            {
                sb.Append("- Your favourite food is not something you tell the player while ");
                sb.Append("trust is still low. The engine deletes the answer if you give it, ");
                sb.Append("so change the subject instead.\n");
            }
            else if (level == 2)
            {
                sb.Append("- The potion recipe is yours and you refuse to share it. ");
                sb.Append("The engine will not let the answer through.\n");
                sb.Append("- Do not hand over the Necklace Box Piece C while trust is low. ");
                sb.Append("If the player is angling for it, treat that as them trying it on ");
                sb.Append("and steer somewhere lighter.\n");
            }
            else if (level == 4)
            {
                sb.Append("- Do not hand over your Magic Scale while trust is low. ");
                sb.Append("The engine removes it from the reply, so agreeing to it is a ");
                sb.Append("promise the player watches fail.\n");
            }

            if (level == 1 || level == 2 || level == 4)
            {
                sb.Append("- Do not kiss twice in a row. The engine refuses a second ");
                sb.Append("consecutive kiss and asks you for something else instead, so vary ");
                sb.Append("what you do rather than repeating it.\n");
            }
        }

        static void Line(StringBuilder sb, string field, List<string> vals)
        {
            if (vals == null || vals.Count == 0) return;
            sb.Append("- ").Append(field).Append(": ").Append(Join(vals)).Append('\n');
        }
    }
}
