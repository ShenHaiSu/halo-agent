# Frozen optional-fields review (contract 0.1.1)

`contract/agent.schema.json` is the only authority. Every row below records
who supplies the field, what the default means, and how replay treats it.
All rows are optional additions on top of the 0.1.0 baseline; required
envelope fields and existing semantics are unchanged.

| Location | Optional field | Supplier | Default | Replay |
|---|---|---|---|---|
| `session.new` request | `workspaceId` | frontend sends, backend stores | `default` | persisted with the session |
| `session.list` result | `workspaceId/messageCount/providerId/modelId/titleLocked/parentSessionId/agentRole/isPinned/createdAtUtc/updatedAtUtc` | backend | absent means unknown | filter by workspace, `default` scope only |
| `session.get` result | same columns as `session.list` | backend | absent means unknown | single session read |
| `session.history` items | `id/toolCalls {id,name,arguments}/toolCallId/name/images/createdAtUtc/inputTokens/outputTokens/cachedTokens/reasoningContent/validatedWarnings/errorCode` plus page `total` | backend | absent means plain text row | paged read, cursor stable |
| `agent.prompt` request | `providerId/modelId/requestId/timeoutSec/needsConfirm/workspaceId/attachments {fileName,mime,sizeBytes,digest or relPath}` | frontend | absent means backend choice | `requestId` de-duplicates retries |
| `agent.answer` request | `toolCallId` | frontend | absent means single pending confirm | keyed by turn plus `toolCallId` when present |
| `tool.start` payload | `argsPreview/policy` | backend | absent means no preview | card preview only |
| `tool.end` payload | `success/output/exitCode/durationMs/fileBytes/errorCode/errorPayload/spilledFile/images/validatedWarnings` | backend | absent means unknown outcome | truncated output with `spilledFile` for full text |
| `tool.confirm` payload | `argsPreview/policy/timeoutSec/questions/questionYaml/ticketId/sessionId` | backend | absent means simple approve/reject | question form drives the answer |
| `tool.delta` payload | `{turnId,toolCallId,delta}` | backend | none | progress only, throttled |
| `text.delta` payload | `kind` (`text` or `reasoning`) | backend | `text` | reasoning streams separately; `text.done` length counts text only |
| `usage.tick` / `usage.query` | `cachedTokens/maxTokens/usagePercentage/occupancyState/usedIsEstimated` | backend | absent means unmeasured | budget bar reads the max series |
| `turn.failed` payload | `reason/hint` | backend | absent means plain failure | terminal state |
| `turn.cancelled` payload | `reason` (`rejected`/`timeout`/`cancelled`) plus `signaled` | backend | `cancelled` with `signaled` true | reason plus `signaled` drives the copy |
| `system.info` result | `routing {enabledBatches/defaultFrame}/bridgeVersion/contractVersion` | backend | absent means legacy chain | capability probe |
| `session.title` | `sessionId/title` plus `titleLocked` | frontend asks, backend stores | explicit call only, never automatic rename | title update event |

Frozen mappings: unknown tool maps to `TOOL_DENIED` with detail
`UNKNOWN_TOOL`; backpressure maps to `RATE_LIMITED` with hint
`backpressure`; truncation is `turn.done` with `truncated: true`, never
`turn.failed`; empty prompt is `INVALID_PARAMS` with detail `EMPTY_PROMPT`;
unknown session is `NOT_FOUND` with detail; budget overrun is `RATE_LIMITED`
only. Limits: history text 2048, staged attachment 8k, tool result 64k,
question yaml 4k, attachment paths relative only. Deprecation log sampling:
first 3 fully logged, then once per day.
