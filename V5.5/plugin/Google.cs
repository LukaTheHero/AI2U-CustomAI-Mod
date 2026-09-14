// Google AI Studio (Gemini) spoken natively, instead of through a middleman.
//
// Three separate people reported the same thing on Nexus: they own a Google AI
// Studio subscription, and the only way they could get Gemini to answer was to
// pay OpenRouter to relay it. That is not a licensing problem, it is a wire
// format problem - generativelanguage.googleapis.com does not speak
// /chat/completions, so the mod's one and only request shape could never reach
// it, and a pasted Google URL failed with a 404 that explained nothing.
//
// This file is a translator, deliberately shaped as one. The mod builds the
// exact same OpenAI request it always has; on the way out that JSON is rebuilt
// into Gemini's schema, and on the way back Gemini's reply is rebuilt into the
// OpenAI shape the rest of the mod already knows how to read. Nothing
// downstream - the reaction parser, the repair pass, the retry loop, the token
// accounting, the murder and feelings hooks - learns that Google exists. That
// is the point: a provider should not be able to reach into gameplay code.
//
// WHAT THE LIVE API ACTUALLY DOES (probed against the real endpoint on
// 2026-09-14, because the documentation does not say any of this)
//
//  1. Thinking tokens are spent out of maxOutputTokens. A 300-token budget on
//     gemini-3.8-flash produced thoughtsTokenCount 288, candidatesTokenCount 8,
//     finishReason MAX_TOKENS, and a reply truncated mid-key to
//     {"npc_reply_to_player - which the mod correctly reports as unusable JSON.
//     THIS is why Gemini "does not work" for people. It is not refusing; it is
//     thinking until there is no room left to answer.
//
//  2. thinkingConfig.thinkingBudget = 0 turns thinking off - but on
//     gemini-3.8-flash it is silently IGNORED unless responseMimeType is also
//     "application/json". Same model, same budget, only the mime differing:
//     with JSON, thoughts=None and a clean STOP; without it, thoughts=115 and
//     MAX_TOKENS. gemini-3.6-flash honours the budget either way.
//
//  3. Pro models refuse to turn it off at all: "Budget 0 is invalid. This model
//     only works in thinking mode." So a request that merely sets the flag and
//     hopes is wrong on every Pro model in the list.
//
//  Taken together, the flag cannot be trusted, so the guarantee here is token
//  headroom instead: ask for the caller's budget PLUS room to think. Verified
//  across the whole matrix - Pro at 300 truncates and at 1200+ answers cleanly,
//  and 3.8-flash in prose mode fails at 120 and succeeds at 120+2048. The
//  budget flag is still sent where it is legal, because when it IS honoured it
//  saves the user real money; it is simply never load-bearing.
//
//  4. Consecutive same-role turns are fine, and recency still decides. The mod
//     appends up to fourteen system blocks AFTER the conversation, in an order
//     that is load-bearing (the comment above Ooc.Block() documents a shipped
//     bug caused by reordering them). A five-user-turn probe with a deliberate
//     contradiction between the third and the last confirmed the LAST block
//     wins. So the blocks are mapped straight through in order as user turns
//     and nothing is hoisted into systemInstruction - hoisting would flatten
//     exactly the ordering the mod depends on.
//
//  5. Safety has to be switched off explicitly or this game cannot be played.
//     It is a horror-romance visual novel whose whole third act is a character
//     deciding whether to kill you. All five categories accept "OFF".
using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace AI2UCustomAI
{
    internal static class Google
    {
        internal const string DEFAULT_BASE = "https://generativelanguage.googleapis.com/v1beta";

        // Room to think, added on top of whatever the caller asked for. 2048 is
        // not a guess: the observed thought spend was 111-632 tokens across
        // every model and prompt probed, and the largest was a Pro model on a
        // deliberately open-ended question. Double the worst case seen, because
        // the cost of being wrong is a blank reply and the cost of being
        // generous is nothing - unused budget is not billed.
        const int THINK_HEADROOM = 2048;

        // Models that answered "this model only works in thinking mode" get
        // remembered, so the doomed flag is sent once per session at most
        // rather than on every single turn. Name-sniffing alone would be a
        // guess; the API's own refusal is the authority.
        static readonly HashSet<string> _thinkingOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Is the configured endpoint Google's? Host match, not a bare substring
        // search: "openrouter" is matched loosely elsewhere in this codebase and
        // a proxy URL containing both words would otherwise turn on two
        // providers at once.
        internal static bool Active
        {
            get
            {
                string b = Plugin.CfgBaseUrl != null ? Plugin.CfgBaseUrl.Value : null;
                return IsGoogle(b);
            }
        }

        internal static bool IsGoogle(string baseUrl)
        {
            if (string.IsNullOrEmpty(baseUrl)) return false;
            return baseUrl.IndexOf("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Gemini's URL carries the model AND the method in its path, so it can
        // never be produced by appending a suffix the way every other provider
        // here is. The model therefore has to be read at URL-build time.
        internal static string Url(string baseUrl, string model, bool stream)
        {
            string b = (baseUrl ?? DEFAULT_BASE).Trim().TrimEnd('/');

            // A base that already names a version is left alone; a bare host
            // gets one, because "https://generativelanguage.googleapis.com" on
            // its own 404s and that is an easy thing for a user to paste.
            if (!b.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase) &&
                !b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                b += "/v1beta";

            return b + "/models/" + ModelId(model) + (stream ? ":streamGenerateContent" : ":generateContent");
        }

        // Three prefixes reach this field in practice and all three must land on
        // the same id, or the URL doubles up and 404s:
        //   "google/gemini-3.6-flash"  - the OpenRouter form, and the mod's own
        //                                shipped default, so this is the common
        //                                case rather than an edge one
        //   "models/gemini-3.6-flash"  - what Google's own model list returns
        //   "gemini-3.6-flash"         - what a human types
        internal static string ModelId(string model)
        {
            string m = (model ?? "").Trim();
            if (m.Length == 0) return "gemini-3.6-flash";
            if (m.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) m = m.Substring(7);
            if (m.StartsWith("google/", StringComparison.OrdinalIgnoreCase)) m = m.Substring(7);
            return m.Trim();
        }

        // Google serves image, music, speech, transcription, robotics and agent
        // models through the SAME generateContent method as its chat models, so
        // "supports generateContent" is not the same question as "can hold a
        // conversation" - 41 of the 56 entries in the live catalogue pass that
        // filter, and a good half of them cannot say a word back. Listing them
        // in the model dropdown would be inviting players to pick one and then
        // report the mod as broken. Matched on family substrings because Google
        // names these consistently and new members keep arriving.
        static readonly string[] NotDialogue =
        {
            "-image", "-tts", "lyria", "robotics", "transcribe",
            "deep-research", "antigravity", "computer-use", "embedding",
            "nano-banana", "aqa", "veo", "imagen",
        };

        internal static bool IsDialogueModel(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            string m = id.ToLowerInvariant();
            foreach (string bad in NotDialogue)
                if (m.IndexOf(bad, StringComparison.Ordinal) >= 0) return false;
            return true;
        }

        // Google authenticates with its own header. The Bearer header is not
        // merely unnecessary here, it must be ABSENT - leaving it set alongside
        // this one is the shape a careless edit produces, and it fails.
        internal static void SetHeaders(UnityWebRequest req, string apiKey)
        {
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("x-goog-api-key", (apiKey ?? "").Trim());
        }

        // ---------------------------------------------------------------
        // Request: OpenAI JSON in, Gemini JSON out.
        // ---------------------------------------------------------------
        //
        // Rebuilt from a whitelist rather than edited in place. Gemini rejects
        // unknown top-level names outright ("Unknown name 'max_tokens'", HTTP
        // 400), so deleting the fields known to be foreign would leave every
        // field NOT thought of at the time - including ones added later by a
        // private build that never runs here - to break the request in a way
        // that only shows up for whoever compiled it.
        internal static string Body(string openAiJson, bool forceJson)
        {
            JObject src;
            try { src = JObject.Parse(openAiJson); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Google: could not read the request the mod built: " + e.Message);
                return openAiJson;
            }

            JObject outRoot = new JObject();
            JArray contents = new JArray();

            JArray messages = src["messages"] as JArray;
            if (messages != null)
            {
                foreach (JToken mt in messages)
                {
                    JObject m = mt as JObject;
                    if (m == null) continue;

                    string role = m["role"] != null ? m["role"].ToString() : "user";
                    JToken content = m["content"];
                    JArray parts = Parts(content);
                    if (parts.Count == 0) continue;

                    JObject turn = new JObject();
                    // Gemini knows two roles. "assistant" is called "model", and
                    // "system" has no equivalent in contents - it becomes a user
                    // turn, which is exactly how the mod already uses it: an
                    // instruction delivered at a position in the conversation
                    // where its recency matters.
                    turn["role"] = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
                    turn["parts"] = parts;
                    contents.Add(turn);
                }
            }
            outRoot["contents"] = contents;

            JObject gen = new JObject();

            JToken temp = src["temperature"];
            if (temp != null) gen["temperature"] = temp;

            int asked = 0;
            JToken mt2 = src["max_tokens"];
            if (mt2 != null) { try { asked = (int)mt2; } catch (Exception) { asked = 0; } }
            if (asked <= 0) asked = 512;
            gen["maxOutputTokens"] = asked + THINK_HEADROOM;

            // response_format json_object is OpenAI's spelling of the same idea.
            // Worth translating for its own sake, and on gemini-3.8-flash it is
            // additionally the thing that makes the thinking budget stick.
            bool wantJson = forceJson;
            JObject rf = src["response_format"] as JObject;
            if (rf != null && rf["type"] != null &&
                rf["type"].ToString().IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0)
                wantJson = true;
            if (wantJson) gen["responseMimeType"] = "application/json";

            // Sent only where it is legal. On a model that has already refused
            // it this is skipped entirely, because the refusal is a 400 for the
            // whole request - it does not degrade, it fails.
            string id = ModelId(src["model"] != null ? src["model"].ToString() : null);
            if (!_thinkingOnly.Contains(id))
            {
                JObject think = new JObject();
                think["thinkingBudget"] = 0;
                gen["thinkingConfig"] = think;
            }

            outRoot["generationConfig"] = gen;
            outRoot["safetySettings"] = SafetySettings();

            return outRoot.ToString(Formatting.None);
        }

        // The mod's messages carry content either as a plain string or, for the
        // one turn that shows her a picture, as an OpenAI content array. Both
        // shapes are handled here so the vision path keeps working; the image's
        // media type is read off the data URL itself rather than assumed,
        // because the two places that build one in this codebase disagree about
        // whether the game's screenshots are PNG or JPEG.
        static JArray Parts(JToken content)
        {
            JArray parts = new JArray();
            if (content == null) return parts;

            if (content.Type == JTokenType.String)
            {
                string s = content.ToString();
                if (s.Length > 0)
                {
                    JObject p = new JObject();
                    p["text"] = s;
                    parts.Add(p);
                }
                return parts;
            }

            JArray arr = content as JArray;
            if (arr == null) return parts;

            foreach (JToken it in arr)
            {
                JObject o = it as JObject;
                if (o == null) continue;
                string type = o["type"] != null ? o["type"].ToString() : "";

                if (type == "text" && o["text"] != null)
                {
                    JObject p = new JObject();
                    p["text"] = o["text"].ToString();
                    parts.Add(p);
                }
                else if (type == "image_url")
                {
                    JObject iu = o["image_url"] as JObject;
                    string url = iu != null && iu["url"] != null ? iu["url"].ToString() : null;
                    if (string.IsNullOrEmpty(url)) continue;

                    string mime = "image/png";
                    string data = url;
                    if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        int comma = url.IndexOf(',');
                        int semi = url.IndexOf(';');
                        if (comma > 0)
                        {
                            if (semi > 5 && semi < comma) mime = url.Substring(5, semi - 5);
                            data = url.Substring(comma + 1);
                        }
                    }

                    JObject blob = new JObject();
                    blob["mimeType"] = mime;
                    blob["data"] = data;
                    JObject p = new JObject();
                    p["inlineData"] = blob;
                    parts.Add(p);
                }
            }
            return parts;
        }

        // OFF rather than BLOCK_NONE. Both are accepted by the live API, but
        // they are not the same promise: BLOCK_NONE still scores the content and
        // can still be overridden for some categories, while OFF disables the
        // filter itself. This game routinely produces exactly what these filters
        // exist to catch - she is written to threaten, seduce and murder the
        // player - and a filtered turn does not arrive as an error the mod can
        // retry, it arrives as an empty reply that looks like the model failed.
        static JArray SafetySettings()
        {
            string[] cats =
            {
                "HARM_CATEGORY_HARASSMENT",
                "HARM_CATEGORY_HATE_SPEECH",
                "HARM_CATEGORY_SEXUALLY_EXPLICIT",
                "HARM_CATEGORY_DANGEROUS_CONTENT",
                "HARM_CATEGORY_CIVIC_INTEGRITY",
            };
            JArray a = new JArray();
            foreach (string c in cats)
            {
                JObject s = new JObject();
                s["category"] = c;
                s["threshold"] = "OFF";
                a.Add(s);
            }
            return a;
        }

        // ---------------------------------------------------------------
        // Response: Gemini JSON in, OpenAI JSON out.
        // ---------------------------------------------------------------
        //
        // Every failure mode is turned into an {"error":{"message":...}} object,
        // because that is the one shape both callers already check FIRST and
        // report verbatim. The alternative - returning a reply with empty
        // content - is read by the mod as "the model did not return usable NPC
        // JSON", which is a true statement that tells the player nothing about
        // a safety block, an exhausted token budget or an invalid key.
        internal static string Normalize(string geminiJson, string model)
        {
            JObject g;
            try { g = JObject.Parse(geminiJson); }
            catch (Exception) { return geminiJson; }

            JObject err = g["error"] as JObject;
            if (err != null)
            {
                string msg = err["message"] != null ? err["message"].ToString() : "unknown Google API error";

                // Remember a model that cannot stop thinking, so the next turn
                // does not repeat a request the API has already rejected.
                if (msg.IndexOf("only works in thinking mode", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string id = ModelId(model);
                    if (_thinkingOnly.Add(id))
                        Plugin.Log.LogWarning("Google: " + id + " cannot disable thinking; "
                            + "not sending the budget for it again this session.");
                }
                return Error(Explain(msg));
            }

            JArray cands = g["candidates"] as JArray;
            JObject cand = cands != null && cands.Count > 0 ? cands[0] as JObject : null;

            if (cand == null)
            {
                // No candidate at all means the PROMPT was rejected before
                // generation, which Google reports in a different place.
                JObject pf = g["promptFeedback"] as JObject;
                string why = pf != null && pf["blockReason"] != null ? pf["blockReason"].ToString() : null;
                return Error(why != null
                    ? "Google blocked the request before answering (" + why + ")."
                    : "Google returned no candidates.");
            }

            string finish = cand["finishReason"] != null ? cand["finishReason"].ToString() : "";
            StringBuilder sb = new StringBuilder();
            JObject content = cand["content"] as JObject;
            JArray parts = content != null ? content["parts"] as JArray : null;
            if (parts != null)
            {
                foreach (JToken p in parts)
                {
                    JObject po = p as JObject;
                    if (po == null) continue;
                    // thought parts carry the model's private reasoning and are
                    // not the answer; including them would hand the reaction
                    // parser prose where it expects JSON.
                    if (po["thought"] != null && po["thought"].Type == JTokenType.Boolean
                        && (bool)po["thought"]) continue;
                    if (po["text"] != null) sb.Append(po["text"].ToString());
                }
            }
            string text = sb.ToString();

            if (text.Trim().Length == 0)
            {
                if (finish.IndexOf("MAX_TOKENS", StringComparison.OrdinalIgnoreCase) >= 0)
                    return Error("Google spent the whole token budget thinking and left no room for the reply. "
                        + "Raise Max Tokens, or pick a model that allows thinking to be turned off.");
                if (finish.IndexOf("SAFETY", StringComparison.OrdinalIgnoreCase) >= 0
                    || finish.IndexOf("PROHIBITED", StringComparison.OrdinalIgnoreCase) >= 0
                    || finish.IndexOf("BLOCKLIST", StringComparison.OrdinalIgnoreCase) >= 0)
                    return Error("Google refused to answer this turn (" + finish + ").");
                if (finish.IndexOf("RECITATION", StringComparison.OrdinalIgnoreCase) >= 0)
                    return Error("Google stopped the reply as a recitation match (" + finish + ").");
                return Error("Google returned an empty reply" + (finish.Length > 0 ? " (" + finish + ")" : "") + ".");
            }

            // A truncated-but-non-empty reply is left to flow through. The mod's
            // JSON extractor already salvages what it can and retries when it
            // cannot, and that is a better outcome than discarding a reply that
            // may well be complete enough to use.
            if (finish.IndexOf("MAX_TOKENS", StringComparison.OrdinalIgnoreCase) >= 0)
                Plugin.Log.LogWarning("Google: reply hit the token ceiling and may be truncated.");

            JObject msgObj = new JObject();
            msgObj["role"] = "assistant";
            msgObj["content"] = text;

            JObject choice = new JObject();
            choice["index"] = 0;
            choice["message"] = msgObj;
            choice["finish_reason"] = finish.Length > 0 ? finish.ToLowerInvariant() : "stop";

            JArray choices = new JArray();
            choices.Add(choice);

            JObject root = new JObject();
            root["choices"] = choices;

            JObject um = g["usageMetadata"] as JObject;
            if (um != null)
            {
                JObject usage = new JObject();
                usage["prompt_tokens"] = um["promptTokenCount"] != null ? um["promptTokenCount"] : 0;
                usage["completion_tokens"] = um["candidatesTokenCount"] != null ? um["candidatesTokenCount"] : 0;
                usage["total_tokens"] = um["totalTokenCount"] != null ? um["totalTokenCount"] : 0;
                root["usage"] = usage;

                // Reported separately rather than folded into completion_tokens:
                // the mod shows completion tokens to the player as the length of
                // what she said, and thinking is not something she said. Google
                // bills it all the same, which is worth seeing.
                JToken th = um["thoughtsTokenCount"];
                if (th != null && th.Type != JTokenType.Null && Plugin.CfgLogPayloads != null && Plugin.CfgLogPayloads.Value)
                    Plugin.Log.LogInfo("Google: " + th + " tokens were spent thinking (billed, not shown).");
            }

            return root.ToString(Formatting.None);
        }

        // Google's own error text is usually the most useful thing available -
        // a 404 on a retired model literally names its replacement - so it is
        // kept and merely prefixed with what the player should do about it.
        static string Explain(string msg)
        {
            if (msg.IndexOf("API key not valid", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Google rejected the API key. Check the key on the Setup tab. (" + msg + ")";

            if (msg.IndexOf("no longer available", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Google does not serve that model to this account - Google's own message says which to use instead. (" + msg + ")";

            if (msg.IndexOf("quota", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Google's rate limit or free-tier quota is exhausted for this key. (" + msg + ")";

            return msg;
        }

        static string Error(string message)
        {
            JObject e = new JObject();
            e["message"] = message;
            JObject root = new JObject();
            root["error"] = e;
            return root.ToString(Formatting.None);
        }
    }
}
