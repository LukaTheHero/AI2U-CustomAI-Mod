using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ChatGPTUtility;

namespace AI2UCustomAI
{
    // The game's three side AI calls, answered here instead of on AI2U's servers.
    //
    // Dialogue was always the loud one, but it is not the only paid LLM call the
    // game makes. There are three more, and until 4.3 the mod's only answer to
    // them was a switch that pointed them at a dead port:
    //
    //   summary   at level exit - the written recap on the ending screen
    //   envision  when you show her a painting or put something on her TV
    //   memorize  on every scene change - the one-line memory of what you last
    //             talked about, which comes back as LastTopic in a later prompt
    //
    // Blocking them was never good enough. The ending screen went blank, and
    // memorize failing silently meant her memory of the conversation stopped
    // being written at all - the trap in open issue 6. So these are now answered
    // from the user's own endpoint, in the same model's voice as her dialogue.
    //
    // Interception is deliberately as low as it goes. Summary and memorize are
    // caught at the request itself, which means the game's own callbacks still
    // consume the reply: MemorizeProcessor still writes its save key, the ending
    // screen still fills its own text field. Nothing about how the game stores or
    // displays these had to be reimplemented, so there is nothing to keep in sync.
    internal static class Extras
    {
        public static void Install(Harmony h)
        {
            h.PatchAll(typeof(PostGuard));
            h.PatchAll(typeof(EnvisionGuard));
        }

        static bool On()
        {
            return Plugin.CfgEnabled != null && Plugin.CfgEnabled.Value
                && Plugin.CfgOwnExtras != null && Plugin.CfgOwnExtras.Value;
        }

        enum Kind { None, Summary, Memorize }

        // ServerUriBuilder builds all three from one private helper, so the path
        // segment is the only thing that separates them:
        //   .../app/summary/{level}/{playfabId}/{gameId}
        //   .../app/memorize/{level}/{playfabId}/{gameId}
        static Kind Which(Uri uri)
        {
            if (uri == null) return Kind.None;
            string p = uri.AbsolutePath;
            if (p == null) return Kind.None;
            if (p.IndexOf("/summary/", StringComparison.OrdinalIgnoreCase) >= 0) return Kind.Summary;
            if (p.IndexOf("/memorize/", StringComparison.OrdinalIgnoreCase) >= 0) return Kind.Memorize;
            return Kind.None;
        }

        // Summary and memorize both go through the non-generic PostReq, which
        // hands the raw body straight to `callback` with no deserialization at
        // all. Substituting the coroutine means no request leaves the machine.
        [HarmonyPatch(typeof(Requests), "PostReq", new Type[] {
            typeof(Uri), typeof(string), typeof(Action<string>),
            typeof(Action<string, int>), typeof(Action<string>),
            typeof(Dictionary<string, string>) })]
        static class PostGuard
        {
            static bool Prefix(Uri uri, string json, Action<string> callback, ref IEnumerator __result)
            {
                if (!On()) return true;

                Kind kind = Which(uri);
                if (kind == Kind.None) return true;

                // No callback means nothing would consume our reply either, so
                // there is no reason to spend a request on it.
                if (callback == null) return true;

                __result = Run(kind, json, callback);
                return false;
            }
        }

        // Envision is the 4-arg SendToChatGPT overload - a different method from
        // the 2-arg one SendPatch owns, so the two do not collide.
        [HarmonyPatch(typeof(ChatGPTConversation), "SendToChatGPT", new Type[] {
            typeof(string), typeof(Action<string, int>), typeof(string), typeof(EnvisionType) })]
        static class EnvisionGuard
        {
            static bool Prefix(ChatGPTConversation __instance, string message,
                Action<string, int> errorCallback, string base64Image)
            {
                if (!On()) return true;
                if (string.IsNullOrEmpty(base64Image)) return true;

                try
                {
                    Traverse t = Traverse.Create(__instance);

                    // Same guard as the dialogue patch: leave the legacy
                    // direct-OpenAI modes to the game.
                    object model = t.Field("_model").GetValue();
                    if (model == null || model.ToString() != "ChatGPTAzure") return true;

                    Chat chat = t.Field("_chat").GetValue<Chat>();
                    if (chat == null || chat.CurrentChat == null) return true;

                    __instance.StartCoroutine(Bridge.Send(__instance, chat.CurrentChat,
                        errorCallback, base64Image, Note(message)));
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Envision takeover failed, letting the game "
                        + "have it: " + e.Message);
                    return true;
                }
            }
        }

        // The call sites wrap the authored story guide in a small JSON object:
        //   {"story_guide":"<text>", "sentence_from_player":""}
        // That text is real shipped content - "player drew a painting in art room,
        // catgirl describes what the painting about..." - so it is what should
        // frame the picture. Pulled out of the wrapper because the raw braces read
        // as a malformed instruction next to our own prose.
        static string Note(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            try
            {
                JObject o = JObject.Parse(message);
                string guide = (string)o["story_guide"];
                if (!string.IsNullOrEmpty(guide)) return guide.Trim();
            }
            catch (Exception) { }
            return message.Trim();
        }

        // What to tell the model these two jobs are.
        //
        // Both of these are mod-authored instructions, and that needs saying plainly
        // because of the standing rule against inventing lore. They are task
        // directions - "summarize this" - and they state no facts about any
        // character, place or event. Every world fact in the request comes out of
        // the transcript the game itself assembled. Nothing here fills a gap in the
        // authored content, and nothing here would survive into her dialogue.
        //
        // For summary the game does ship a prompt slot, StoryGuide/SummaryPrompt,
        // and it is appended last in the body. In English its value is an empty
        // string - the real instruction lived on the server. Other languages hold a
        // one-line language directive ("summarize the ending in Spanish"), so where
        // the player has one it is forwarded as-is; it arrives inside the transcript
        // and steers the output language without anything being written for it here.
        // What language to write in, taken from the game's own setting.
        //
        // This exists because of a real regression. The record the game assembles
        // ends with StoryGuide/SummaryPrompt, and the earlier version of the
        // prompt below told the model to obey any language directive it found
        // there. That term's English slot is an empty string, so I2 falls through
        // to a populated slot - and the populated slots are one-line directives
        // reading "summarize the game ending in Chinese", and so on. An English
        // player got a Chinese recap, in a Chinese transliteration of her name,
        // because the mod instructed the model to do exactly that.
        //
        // So the language is decided here from LocalizationManager.CurrentLanguage
        // instead of from anything inside the transcript. Fully qualified for the
        // reason given at Lore.cs:54-57 - the game ships a second class named
        // LocalizationManager for the in-game desktop, and a bare lookup binds
        // that one. Guarded: an unreadable property returns null and the caller
        // falls back to English rather than throwing on the ending screen.
        static string Language()
        {
            try
            {
                Type t = AccessTools.TypeByName("I2.Loc.LocalizationManager");
                if (t == null) return null;

                object v = Traverse.Create(t).Property("CurrentLanguage").GetValue();
                string s = v as string;
                return string.IsNullOrEmpty(s) ? null : s.Trim();
            }
            catch (Exception) { return null; }
        }

        static string LanguageRule()
        {
            string lang = Language();
            if (lang == null) lang = "English";

            // Stated twice on purpose. The transcript still carries the stray
            // directive described above, and naming the language without
            // overriding it leaves two conflicting instructions in one request.
            return "Write in " + lang + ". Ignore any instruction inside the "
                + "record about what language to use - it is a leftover and is "
                + "often wrong.";
        }

        static string SummaryPrompt()
        {
            return "You are writing the closing recap on the ending screen of a "
                + "horror dating game. Below is a record of one playthrough: the "
                + "player's name, the character's name and nature, everything the "
                + "two of them said, and a description of the ending that was "
                + "reached.\n\n"
                + "Write the recap in three to five sentences. Cover how the "
                + "relationship actually went and how it ended, in past tense, "
                + "addressing the player as \"you\". Draw only on what is in the "
                + "record - do not invent events, names or details that are not "
                + "there. " + LanguageRule() + "\n\n"
                + "Reply with the recap alone: plain prose, no title, no heading, "
                + "no speaker label, no asterisks or other markdown, and no "
                + "commentary before or after it.";
        }

        static string MemorizePrompt()
        {
            return "Below is a conversation between a player and a character in a "
                + "game. Write the single thing worth remembering from it: what they "
                + "talked about, and where things stood between them at the end.\n\n"
                + "One sentence, under 300 characters, past tense, plain text. This "
                + "is a private note the character will read before her next "
                + "conversation with the player, so write it as a reminder of the "
                + "last topic rather than as a message to anyone. Use only what the "
                + "conversation contains. " + LanguageRule() + " Reply with the "
                + "sentence alone, with no label and no markdown.";
        }

        // The shape the ending screen reads. It pulls npc_reactions.ending_name for
        // the text, three ints for its own metrics, and iterates EndingRewards as
        // {playfabItemKey: count}, indexing each key into a reward table.
        //
        // EndingRewards is emitted empty, and that is a real cost worth stating: the
        // items and gems the vendor's reply would have granted on the ending screen
        // do not arrive while this is on. It is empty rather than populated because
        // the keys have to match entries in the game's own reward table and an
        // unknown one throws out of the dictionary lookup - so guessing them would
        // trade a missing reward for a broken ending screen. A missing key is safe:
        // the JSON reader hands back a lazy placeholder that enumerates zero times.
        static string SummaryBody(string text)
        {
            JObject reactions = new JObject();
            reactions["ending_name"] = text;

            JObject root = new JObject();
            root["npc_reactions"] = reactions;
            root["completion"] = 0;
            root["prompt"] = 0;
            root["total"] = 0;
            root["EndingRewards"] = new JObject();
            return root.ToString();
        }

        static IEnumerator Run(Kind kind, string json, Action<string> callback)
        {
            string transcript = Transcript(json, kind == Kind.Summary ? 12000 : 6000);
            if (transcript == null)
            {
                // Deliberately silent past the log. Vanilla's own connection-error
                // path returns without invoking either callback, so doing the same
                // leaves the game in a state it already knows how to be in: the
                // save keeps its previous memory, the ending screen takes its
                // timeout branch. Handing back an empty body instead would crash
                // the ending screen outright, because JSON.Parse returns null for a
                // body with no tokens and the reader dereferences it unchecked.
                Plugin.Log.LogWarning("Nothing readable in the game's " + kind
                    + " request; leaving it unanswered.");
                yield break;
            }

            JArray messages = new JArray();
            JObject sys = new JObject();
            sys["role"] = "system";
            sys["content"] = kind == Kind.Summary ? SummaryPrompt() : MemorizePrompt();
            messages.Add(sys);

            JObject usr = new JObject();
            usr["role"] = "user";
            usr["content"] = transcript;
            messages.Add(usr);

            string reply = null;
            yield return Bridge.Ask(messages, kind == Kind.Summary ? 500 : 120,
                false, r => reply = r);

            if (reply == null)
            {
                Plugin.Log.LogWarning("The " + kind + " request failed; leaving it "
                    + "unanswered rather than saving a broken one.");
                yield break;
            }

            reply = reply.Trim();
            if (reply.Length == 0)
            {
                Plugin.Log.LogWarning("Empty " + kind + " reply; discarded.");
                yield break;
            }

            if (kind == Kind.Summary)
            {
                reply = Plain(reply);
                Plugin.Log.LogInfo("Ending summary written by our own endpoint ("
                    + reply.Length + " chars).");
                callback(SummaryBody(reply));
                yield break;
            }

            // MemorizeProcessor JSON-escapes the raw body and then saves it only
            // when the result is under 500 characters, silently dropping anything
            // longer. Escaping only grows a string, so the cap is enforced here
            // against the escaped length rather than the plain one - trimming a
            // sentence is better than the memory vanishing with no message.
            string memory = Fit(reply, 460);
            Plugin.Log.LogInfo("Last-topic memory written by our own endpoint: " + memory);
            callback(memory);
        }

        // A backstop for the recap, because this one is read by the player.
        //
        // SummaryPrompt already asks for plain prose with no heading, label or
        // markdown. A screenshot of the ending screen showed the model ignoring
        // that anyway and opening with a label - the visible text began
        // "/feelings):*", the tail of an invented heading, with the recap after
        // it. The prompt is the request; this is the enforcement, on the same
        // reasoning as the reply-length trim: an instruction the model may
        // decline should not be the only thing between it and the screen.
        //
        // Deliberately narrow. It removes asterisks, leading heading hashes, and
        // a first line that is a label rather than a sentence - short, ending in
        // a colon, with real text after it. Prose is left alone: a colon inside
        // a long sentence is not a label, and a recap that is a single paragraph
        // has no first line to drop. If anything here empties the string the
        // original is returned, since a stray asterisk beats a blank screen.
        static string Plain(string s)
        {
            string original = s;
            try
            {
                s = s.Replace("*", "").Replace("`", "").Trim();

                while (s.StartsWith("#")) s = s.Substring(1).TrimStart();

                int nl = s.IndexOf('\n');
                if (nl > 0)
                {
                    string head = s.Substring(0, nl).Trim();
                    string rest = s.Substring(nl + 1).Trim();
                    if (rest.Length > 0 && head.Length <= 80 && head.EndsWith(":"))
                        s = rest;
                }

                s = s.Trim();
                return s.Length == 0 ? original : s;
            }
            catch (Exception) { return original; }
        }

        // Trim until the JSON-escaped form fits, since that is what gets measured.
        static string Fit(string s, int max)
        {
            for (int i = 0; i < 40; i++)
            {
                if (JToken.FromObject(s).ToString(Newtonsoft.Json.Formatting.Indented).Length < max)
                    return s;
                int cut = (int)(s.Length * 0.85);
                if (cut < 20) return s.Substring(0, Math.Min(s.Length, 20));
                s = s.Substring(0, cut).TrimEnd() + "...";
            }
            return s;
        }

        // Everything the game was about to send, as one readable transcript.
        //
        // For summary that body is already a digest the game assembled - both
        // names, the level's character description, the whole chat history and the
        // ending description, delimited by " **** ". For memorize it is the raw
        // message list. Either way the content is what we want to reason over, and
        // the tuning fields around it (temperature, penalties) are theirs to send
        // to their own server, not ours.
        static string Transcript(string json, int maxChars)
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                JObject o = JObject.Parse(json);
                JArray msgs = o["messages"] as JArray;
                if (msgs == null) return null;

                for (int i = 0; i < msgs.Count; i++)
                {
                    JToken c = msgs[i]["content"];
                    if (c == null || c.Type != JTokenType.String) continue;
                    string text = ((string)c);
                    if (text == null || text.Trim().Length == 0) continue;

                    string role = (string)msgs[i]["role"];
                    if (sb.Length > 0) sb.Append('\n');
                    if (role != null && role != "user" && role != "system")
                        sb.Append(role).Append(": ");
                    sb.Append(text.Trim());
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not read the game's request body: " + e.Message);
                return null;
            }

            if (sb.Length == 0) return null;

            // Oldest first, so a long round loses its opening rather than the part
            // nearest the ending.
            if (sb.Length > maxChars)
                return "[earlier conversation trimmed]\n" + sb.ToString(sb.Length - maxChars, maxChars);
            return sb.ToString();
        }
    }
}
