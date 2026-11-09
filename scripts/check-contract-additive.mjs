// Contract freeze gate: the schema may only grow by additive steps.
// Fails when a frozen name is removed or renamed, when a required envelope
// field changes, or when a rejected payload name appears. New methods must
// be listed under freeze.addedMethods. Used by CI before any build.
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const schemaPath = resolve(here, '../contract/agent.schema.json');

const BASELINE_VERSION = '0.1.0';
const BASELINE_METHODS = [
  'system.ping',
  'system.info',
  'session.new',
  'session.resume',
  'session.rename',
  'session.delete',
  'session.clear',
  'session.list',
  'session.get',
  'session.history',
  'session.compact',
  'session.setCwd',
  'agent.prompt',
  'agent.queue',
  'agent.cancel',
  'agent.retry',
  'agent.drain',
  'agent.answer',
  'agent.unqueue',
  'state.snapshot',
  'state.mode',
  'state.persona',
  'state.maxTurns',
  'tool.undoRollback',
  'settings.saveProviders',
  'settings.setActive',
  'settings.saveNetwork',
  'settings.saveBehavior',
  'asset.skills.list',
  'asset.skills.get',
  'asset.memory.list',
  'asset.memory.save',
  'asset.personas.list',
  'asset.personas.get',
  'usage.query',
  'usage.clear',
  'host.pickDirectory',
  'host.openPath',
];
const BASELINE_EVENTS = [
  'turn.started',
  'text.delta',
  'text.done',
  'tool.start',
  'tool.delta',
  'tool.end',
  'tool.confirm',
  'turn.done',
  'turn.failed',
  'turn.cancelled',
  'session.updated',
  'session.compacted',
  'state.changed',
  'usage.tick',
];
const BASELINE_CODES = [
  'INVALID_PARAMS',
  'NOT_FOUND',
  'TURN_IN_PROGRESS',
  'COMPACTING',
  'RATE_LIMITED',
  'PROVIDER_AUTH',
  'NETWORK_FAIL',
  'TOOL_DENIED',
  'TOOL_TIMEOUT',
  'CANCELLED',
  'INTERNAL',
];

const failures = [];
const schema = JSON.parse(readFileSync(schemaPath, 'utf8'));

function sameList(actual, expected) {
  return (
    actual.length === expected.length &&
    actual.every((v, i) => v === expected[i])
  );
}

if (schema.contractVersion === BASELINE_VERSION) {
  failures.push(
    'contractVersion is still the baseline; an additive freeze step must bump it (for example 0.1.1).',
  );
}

const methods = schema.methods ?? [];
const missing = BASELINE_METHODS.filter((m) => !methods.includes(m));
if (missing.length > 0)
  failures.push(`methods removed or renamed: ${missing.join(', ')}`);
const added = methods.filter((m) => !BASELINE_METHODS.includes(m));
const declared = schema.freeze?.addedMethods ?? [];
for (const name of added) {
  if (!declared.includes(name))
    failures.push(
      `new method ${name} is not listed under freeze.addedMethods.`,
    );
}
for (const name of declared) {
  if (!methods.includes(name))
    failures.push(
      `freeze.addedMethods lists ${name}, which is missing from methods.`,
    );
}

if (!sameList(schema.events ?? [], BASELINE_EVENTS)) {
  failures.push(
    'events must stay exactly the frozen list for this freeze step.',
  );
}
if (!sameList(schema.errorCodes ?? [], BASELINE_CODES)) {
  failures.push(
    'errorCodes must stay exactly the frozen list for this freeze step.',
  );
}

const defs = schema.definitions ?? {};
const enumOf = (name) => defs[name]?.enum ?? [];
if (!sameList(enumOf('methodName'), methods))
  failures.push(
    'definitions.methodName.enum must equal the top-level methods list.',
  );
if (!sameList(enumOf('eventKind'), schema.events ?? []))
  failures.push(
    'definitions.eventKind.enum must equal the top-level events list.',
  );
if (!sameList(enumOf('errorCode'), schema.errorCodes ?? []))
  failures.push(
    'definitions.errorCode.enum must equal the top-level errorCodes list.',
  );
const req = defs.requestFrame?.required ?? [];
const res = defs.responseFrame?.required ?? [];
const evt = defs.eventFrame?.required ?? [];
if (!sameList(req, ['t', 'id', 'm']))
  failures.push('requestFrame.required changed; envelope fields are frozen.');
if (!sameList(res, ['t', 'id', 'ok']))
  failures.push('responseFrame.required changed; envelope fields are frozen.');
if (!sameList(evt, ['t', 'seq', 'k']))
  failures.push('eventFrame.required changed; envelope fields are frozen.');
const codes = schema.errorCodes ?? [];
for (const name of ['BACKPRESSURE', 'UNKNOWN_TOOL']) {
  if (codes.includes(name))
    failures.push(
      `${name} must never be an error code; use the frozen mapping instead.`,
    );
}
const optionalText = JSON.stringify(schema.freeze?.optionals ?? []);
for (const name of ['argsDelta', 'argsJson', 'BACKPRESSURE']) {
  if (methods.includes(name) || optionalText.includes(name)) {
    failures.push(`rejected payload name present: ${name}.`);
  }
}
const routing = schema.freeze?.routing ?? {};
if (!String(routing.backpressure ?? '').includes('RATE_LIMITED')) {
  failures.push('freeze.routing.backpressure must point at RATE_LIMITED.');
}
if (!String(routing.unknownTool ?? '').includes('TOOL_DENIED')) {
  failures.push('freeze.routing.unknownTool must point at TOOL_DENIED.');
}

if (failures.length > 0) {
  console.error('contract freeze gate failed (additive-only):');
  for (const line of failures) console.error(`  - ${line}`);
  process.exit(1);
}
console.log(
  `contract freeze gate passed: ${BASELINE_VERSION} -> ${schema.contractVersion}, +${added.length} method(s).`,
);
