# Frame contract (single source of truth)

`agent.schema.json` is the only authority for frame envelopes (`req` / `res` / `evt`),
method names, event names and error codes. The backend catalog
(`src/HaloAgent.Bridge.Contracts/AgentContract.cs`) and the frontend types
(`src/Web.Client/src/contract/agent.d.ts`) are mirrors, never authorities.

## Files

| File | Role |
|---|---|
| `contract/agent.schema.json` | Source of truth: version, name lists, envelope shapes, frozen `freeze` appendix (0.1.1: +`session.title`) |
| `scripts/gen-contract-types.mjs` | Generates `agent.d.ts` plus both C# mirrors from the schema (CRLF output, `--check` is newline-insensitive) |
| `scripts/check-contract-additive.mjs` | Additive-only gate: version bump, no removals, envelope fixed, rejected names absent |
| `contract/freeze-review.md` | Per-row review: supplier / default / replay for every frozen optional field |
| `contract/parity-template.json` | Empty parity checklist (all unchecked); stations tick items as they land |
| `src/Web.Client/src/contract/agent.d.ts` | Machine output (header says `generated`, hand edits forbidden) |
| `src/HaloAgent.Bridge.Contracts/AgentContract.cs` | C# mirror plus `TryClassifyFrame` envelope check (lists synced by the generator) |
| `src/HaloAgent.Engine.Abstractions/ContractCatalog.cs` | Engine-side C# mirror (lists synced by the generator) |
| `tests/Contract.Tests/` | Three-way consistency + envelope behavior + additive-only freeze guard |

## Workflow

```bash
# regenerate after editing the schema (updates agent.d.ts and both C# mirrors)
node scripts/gen-contract-types.mjs

# fail when any generated mirror is stale (CI gate)
node scripts/gen-contract-types.mjs --check

# fail when the schema breaks the additive-only freeze rule (CI gate)
node scripts/check-contract-additive.mjs
```

`tests/Contract.Tests` fails on any drift between the three sides, so the
`--check` gate and `dotnet test` enforce the same freeze.

Frontend `typecheck` covers `src/contract/agentSmoke.ts`, which references the
generated surface — a stale generation breaks the web build as well.

## Freeze rule

After the freeze only **optional** payload fields may be added. Method names,
event names, error codes, required fields and their semantics must not change;
a breaking change needs a new major contract version plus a migration note.
