# Frame contract (single source of truth)

`agent.schema.json` is the only authority for frame envelopes (`req` / `res` / `evt`),
method names, event names and error codes. The backend catalog
(`src/HaloAgent.Bridge.Contracts/AgentContract.cs`) and the frontend types
(`src/Web.Client/src/contract/agent.d.ts`) are mirrors, never authorities.

## Files

| File | Role |
|---|---|
| `contract/agent.schema.json` | Source of truth: version, name lists, envelope shapes |
| `scripts/gen-contract-types.mjs` | Generates `agent.d.ts` from the schema |
| `src/Web.Client/src/contract/agent.d.ts` | Machine output (header says `generated`, hand edits forbidden) |
| `src/HaloAgent.Bridge.Contracts/AgentContract.cs` | C# mirror plus `TryClassifyFrame` envelope check |
| `tests/Contract.Tests/` | Three-way consistency + envelope behavior tests |

## Workflow

```bash
# regenerate after editing the schema
node scripts/gen-contract-types.mjs

# fail when the generated file is stale (CI gate)
node scripts/gen-contract-types.mjs --check
```

`tests/Contract.Tests` fails on any drift between the three sides, so the
`--check` gate and `dotnet test` enforce the same freeze.

Frontend `typecheck` covers `src/contract/agentSmoke.ts`, which references the
generated surface — a stale generation breaks the web build as well.

## Freeze rule

After the freeze only **optional** payload fields may be added. Method names,
event names, error codes, required fields and their semantics must not change;
a breaking change needs a new major contract version plus a migration note.
