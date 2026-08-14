# Server-Side Cost Impact

Why this mod does not add inference or bandwidth cost to AlterStaff's
infrastructure, and where it strictly reduces it.

Findings below come from the decompiled game assembly (`Assembly-CSharp.dll`,
Unity 2022.3.62, Mono backend) and from this repository's plugin source.
Line references are to `plugin/AI2UCustomAI.cs` unless stated otherwise.

## Summary

The mod **cancels** the vanilla dialogue request rather than post-processing its
result. The expensive per-turn call to the game's inference backend is never
issued, so the dominant recurring cost is removed, not duplicated. No code path
in the mod sends anything to an AlterStaff host.

## The interception is a cancellation, not an override

`ChatGPTConversation.SendToChatGPT` is patched with a Harmony **prefix** that
returns `false`:

```csharp
__instance.StartCoroutine(Bridge.Send(__instance, chat.CurrentChat, errorCallback));
return false; // skip the vanilla request to the AI2U server
```

A prefix returning `false` skips the original method body entirely. In vanilla
that body reaches:

```csharp
this.requests.PostReq<ChatGPTRes>(this._uri, text, ..., this._reqHeaders)
```

where `_uri` is `ServerUriBuilder.GetPlayUri(...)`. Because the body never runs,
that POST is never constructed and never sent. The request headers the vanilla
path would have populated — `x-gpt-key`, `X-Authorization`, `x-token`,
`x-tts-override` — are likewise never transmitted for dialogue turns.

This distinction is the whole argument. A postfix or a result-rewrite would have
let the server call complete and then discarded the answer, which would have
cost them full price for a response nobody reads. A cancelling prefix costs them
nothing.

### ResolveChatGPTAzure does no network I/O

The mod still calls `ResolveChatGPTAzure` to hand the finished reply back to the
game's own dispatch logic. That is safe: in the decompiled source this method
appears only as a **response callback** passed into `PostReq`
(`new Action<string>(this.ResolveChatGPTAzure)`). It parses a payload it is
handed. Invoking it directly performs no request.

## Outbound hosts belong to the operator

Every endpoint reachable from the mod is configured by the person running it:

| Host | Purpose | Paid by |
|---|---|---|
| `openrouter.ai` | dialogue inference | mod operator |
| `api.x.ai` | cloud TTS | mod operator |
| `api.elevenlabs.io` | cloud TTS (alternate) | mod operator |
| `api.openai.com` | TTS (OpenAI shape) | mod operator |

Auth is `Authorization: Bearer <key>` from local config (`:1091`). No AlterStaff
hostname appears anywhere in the plugin sources.

## Voice bypasses their TTS as well

Two patches keep audio off their infrastructure:

- `Communicator.Awake` postfix forces on-device synthesis when no personal TTS
  key is set, because server-supplied audio does not exist on a custom endpoint.
- `LocalTTSManager.Speak` prefix replaces a method that would otherwise throw
  before playback, synthesizing locally through the Overtone player instead.

With the modded dropdown set to a cloud voice, synthesis is billed to the
operator's own provider. Either way, their TTS service is not invoked and
`x-tts-override` is not sent.

## What still runs on their infrastructure

Stated plainly so the claim is verifiable rather than absolute. These are
**unchanged** by the mod, not increased:

| Path | Trigger | Status |
|---|---|---|
| PlayFab session auth | login | unchanged |
| Launcher patch checks (Dropbox-backed CDN) | startup | unchanged |
| `GetSummaryUri` (`EndingProcessor.cs:36`) | playthrough ending | unchanged |
| `GetMemorizeUri` (`MemorizeProcessor.cs:124`) | memory consolidation | unchanged |
| `GetEnvisionUri` (`NPCMasterBehavior.cs:123`) | envision feature | unchanged |

Only `SendToChatGPT` is intercepted. The endpoints above are low-frequency —
per-playthrough or per-feature — whereas dialogue inference is per-utterance and
resends accumulated history each turn. Removing the per-turn call while leaving
occasional calls untouched yields a net reduction on every axis: inference
tokens, egress bandwidth, and TTS seconds.

The mod adds no new call sites to their services, and no retry, polling, or
fallback logic in it targets their hosts.

## Reproducing the audit

```bash
# 1. No AlterStaff hosts in the mod
grep -rnoE 'https?://[a-zA-Z0-9._/-]+' --include=*.cs plugin/ | sort -u

# 2. Only three patch targets
grep -hoE 'HarmonyPatch\(typeof\([A-Za-z]+\), "[A-Za-z]+"' plugin/*.cs

# 3. The dialogue prefix cancels (returns false)
grep -n 'return false' plugin/AI2UCustomAI.cs
```

For runtime confirmation, set `LogPayloads = true` in
`BepInEx\config\canak.ai2u.customai.cfg` and inspect `BepInEx\LogOutput.log`:
dialogue turns show one outbound request, to the configured base URL. A network
capture filtered to their domain shows auth and patch traffic only, with no
per-turn inference calls.

## Cost moved, not created

The workload did not disappear; it relocated to the operator's account. Because
`HistoryMaxTokens` overrides the stock ceiling, each turn resends a larger
history, so operator spend grows faster than vanilla would have. That is a cost
increase for the operator and a cost decrease for AlterStaff. The two are not
symmetric: their per-turn inference expense goes to zero regardless of how much
the operator's own bill grows.
