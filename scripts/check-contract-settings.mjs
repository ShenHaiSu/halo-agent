#!/usr/bin/env node
// scripts/check-contract-settings.mjs
// P10 三方一致性：agent.schema.json 与 SettingsContracts.cs 与 agent.d.ts（settings.* 新契约）。
import { readFileSync, existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');
const schema = JSON.parse(readFileSync(resolve(root, 'contract/agent.schema.json'), 'utf8'));
const cs = readFileSync(resolve(root, 'src/Engine.Abstractions/Methods/SettingsContracts.cs'), 'utf8');
const dts = readFileSync(resolve(root, 'src/Web.Client/src/contract/agent.d.ts'), 'utf8');

const mustMethods = ['settings.get', 'settings.saveProviders', 'settings.setActive', 'settings.saveNetwork', 'settings.probeModels'];
const mustNot = ['settings.saveModel', 'settings.saveExtraHeaders', 'state.setProvider'];
let fail = 0;
for (const m of mustMethods) {
  if (!schema.methods[m]) { console.error(`schema 缺 ${m}`); fail = 1; }
  if (!dts.includes(`'${m}'`)) { console.error(`d.ts 缺 ${m}`); fail = 1; }
}
for (const m of mustNot) {
  if (schema.methods[m]) { console.error(`schema 不应含 ${m}`); fail = 1; }
  if (dts.includes(`'${m}'`)) { console.error(`d.ts 不应含 ${m}`); fail = 1; }
}
// cs 侧新契约类型
for (const t of ['ProviderView', 'ModelView', 'SettingsSaveProvidersParams', 'SettingsSetActiveParams']) {
  if (!cs.includes(t)) { console.error(`cs 缺 ${t}`); fail = 1; }
}
// d.ts 由 gen-contract-types 从 schema 生成：校验 settings.get 结果含整树关键键
const getResult = JSON.stringify(schema.methods['settings.get']?.result ?? {});
for (const k of ['activeProvider', 'activeModel', 'migrationRequired', 'providers', 'proxy']) {
  if (!getResult.includes(k)) { console.error(`schema settings.get 缺 ${k}`); fail = 1; }
}
if (!dts.includes('activeProvider')) { console.error('d.ts settings.get 缺 activeProvider'); fail = 1; }
if (!existsSync(resolve(root, 'src/Web.Client/src/components/settings/providerEditing.ts'))) {
  console.error('缺 providerEditing.ts');
  fail = 1;
}
if (fail === 0) console.log('[check-contract-settings] 三方一致');
process.exit(fail);
