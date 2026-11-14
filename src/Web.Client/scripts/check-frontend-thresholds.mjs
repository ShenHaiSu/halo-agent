// 前端可维护阈限门禁 —— 消息主链拆解后的增量守护。
//
// 背景：消息主链曾是神对象 + 上帝 props + 重复拷贝；拆解后容器/视图/副作用三权分立，
// 本脚本把"拆完不反弹"变成可执行的门禁：文件行数、props 个数、import 个数、
// 内部函数长度、空 catch，一项超限即失败。
// 例外必须登记在 EXCEPTIONS 并写清设计理由，不接受临时放行。
//
// 口径（与立项时的目标架构一致）：
// - SFC（含 template+script+style）≤250L；composable/工具模块 ≤200L；
// - defineProps 字段 ≤8，其中函数型 ≤2（超限一律改 provide/inject）；
// - import 语句 ≤10（类型导入同样计数，职责多了自然会超）；
// - 内部函数体 ≤60L：豁免每个文件唯一的对外装配入口（如 useXxx 工厂），
//   它是文件级装配作用域而非业务逻辑函数；其余嵌套函数一律受限；
// - catch 块禁止为空，至少 console.warn 上报。
//
// 用法：node scripts/check-frontend-thresholds.mjs
// 退出码：0 = 通过；1 = 有超限（打印文件/项/实测值）。
import { readFileSync, existsSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
// typescript 仅借用其解析器做函数长度度量，不引入运行时依赖。
const ts = require('typescript');

const here = dirname(fileURLToPath(import.meta.url));
const agentRoot = join(here, '..', 'src', 'features', 'agent');

const SFC_LIMIT = 250;
const MODULE_LIMIT = 200;
const PROPS_LIMIT = 8;
const FN_PROPS_LIMIT = 2;
const IMPORT_LIMIT = 10;
const FN_BODY_LIMIT = 60;

const SFC_FILES = [
  'components/MessageTimeline.vue',
  'components/TimelineView.vue',
  'components/TimelineRow.vue',
  'components/UserTurnBlock.vue',
  'components/ToolCallCard.vue',
  'components/timeline/TimelineEmptyState.vue',
  'components/timeline/TimelineJumpButton.vue',
  'components/timeline/TimelineAlerts.vue',
  'components/userturn/UserBubble.vue',
  'components/userturn/UserTurnLiveBody.vue',
  'components/userturn/UserTurnSteady.vue',
  'components/userturn/UserTurnProcessBody.vue',
  'components/toolcard/ToolCardHeader.vue',
  'components/toolcard/ToolDiffView.vue',
  'components/toolcard/ToolExecView.vue',
  'components/toolcard/ToolViewImageView.vue',
  'components/toolcard/ToolBytesRow.vue',
];

const MODULE_FILES = [
  'composables/useTimeline.ts',
  'composables/useTimelineProjection.ts',
  'composables/useTimelineWindow.ts',
  'composables/useTimelineExpansion.ts',
  'composables/useTimelineCards.ts',
  'composables/useTimelineImages.ts',
  'composables/useTimelineViewModel.ts',
  'composables/useImageThumb.ts',
  'composables/useToolCardActions.ts',
  'composables/useTimelineRowActions.ts',
  'composables/timelineViewKey.ts',
  'composables/useTimelineBehavior.ts',
  'composables/useTimelineScene.ts',
  'utils/toolArgs.ts',
  'utils/toolDiff.ts',
  'utils/toolHeadSummary.ts',
];

// import 超限例外：设计上就是扇出/扇入点的装配器，拆分只会制造第二套钻孔。
// key 为相对 agentRoot 的路径，value 为允许上限与理由。
const IMPORT_EXCEPTIONS = {
  // 唯一的跨 store 装配扇出点：聚合 6 个子 composable + turn/stream/cards/telemetry/menu/nav，
  // 容器只允许在这里一次抓多个 store（职责即如此，不可再拆）。
  'composables/useTimelineViewModel.ts': { limit: 22, reason: 'designated cross-store assembly fan-out' },
  // 投影唯一真相源：按拆分映射扇入 turn/stream/store + 会话纯函数，11 个来源模块是下限。
  'composables/useTimelineProjection.ts': { limit: 14, reason: 'projection fan-in over session utils and stores' },
  // 卡片派生 + 记忆化 + 遥测行：扇入卡片/错误文本/图片工具 + 三个 store。
  'composables/useTimelineCards.ts': { limit: 14, reason: 'card derivation fan-in with telemetry' },
  // 滚动 pin/窗口/顶部锚定/事件绑定：扇入导航 + 窗口算法 + 跟随模式。
  'composables/useTimelineWindow.ts': { limit: 12, reason: 'scroll and window assembly over nav utils' },
};

const failures = [];
const notes = [];

function fail(file, item, actual, limit) {
  failures.push(`  - ${file} → ${item} 实测 ${actual}，上限 ${limit}`);
}

function lineCount(text) {
  return text.split('\n').length;
}

function countImports(text) {
  const m = text.match(/^import\s/gm);
  return m ? m.length : 0;
}

function scriptOfVue(source) {
  const m = source.match(/<script setup[^>]*>([\s\S]*?)<\/script>/);
  return m ? m[1] : '';
}

// 提取 defineProps<...> 泛型体：顶层分号切分，过滤空项。
function propsOf(script) {
  const start = script.indexOf('defineProps<');
  if (start < 0) return null;
  let i = script.indexOf('<', start) + 1;
  let depth = 1;
  let body = '';
  let prev = '';
  for (; i < script.length && depth > 0; i++) {
    const ch = script[i];
    if (ch === '<') depth++;
    // `=>` 中的 `>` 不是泛型闭合，不参与深度计数
    else if (ch === '>' && prev !== '=') depth--;
    if (depth > 0) body += ch;
    prev = ch;
  }
  if (depth !== 0) return null;
  // 顶层按分号切分（忽略对象/函数类型内部的分号与换行）
  const fields = [];
  let cur = '';
  let d = 0;
  for (const ch of body) {
    if (ch === '{' || ch === '(') d++;
    else if (ch === '}' || ch === ')') d--;
    if (ch === ';' && d === 0) {
      if (cur.trim()) fields.push(cur.trim());
      cur = '';
    } else {
      cur += ch;
    }
  }
  if (cur.trim()) fields.push(cur.trim());
  return fields;
}

function checkSfc(rel) {
  const abs = join(agentRoot, rel);
  if (!existsSync(abs)) {
    fail(rel, '文件缺失', 'missing', 'exists');
    return;
  }
  const source = readFileSync(abs, 'utf8');
  const lines = lineCount(source);
  if (lines > SFC_LIMIT) fail(rel, 'SFC 行数', lines, SFC_LIMIT);
  const script = scriptOfVue(source);
  const imports = countImports(script);
  const impLimit = IMPORT_EXCEPTIONS[rel]?.limit ?? IMPORT_LIMIT;
  if (imports > impLimit) fail(rel, 'import 语句数', imports, impLimit);
  else if (IMPORT_EXCEPTIONS[rel]) notes.push(`  = ${rel} import ${imports}（例外上限 ${impLimit}：${IMPORT_EXCEPTIONS[rel].reason}）`);
  const fields = propsOf(script);
  if (fields) {
    if (fields.length > PROPS_LIMIT) fail(rel, 'props 字段数', fields.length, PROPS_LIMIT);
    const fnProps = fields.filter((f) => f.includes('=>') || f.includes('Function')).length;
    if (fnProps > FN_PROPS_LIMIT) fail(rel, '函数型 props', fnProps, FN_PROPS_LIMIT);
  }
  checkCatch(rel, script);
  checkFunctionBodies(rel, script, /* isVue */ true);
}

function checkModule(rel) {
  const abs = join(agentRoot, rel);
  if (!existsSync(abs)) {
    fail(rel, '文件缺失', 'missing', 'exists');
    return;
  }
  const source = readFileSync(abs, 'utf8');
  const lines = lineCount(source);
  if (lines > MODULE_LIMIT) fail(rel, '模块行数', lines, MODULE_LIMIT);
  const imports = countImports(source);
  const impLimit = IMPORT_EXCEPTIONS[rel]?.limit ?? IMPORT_LIMIT;
  if (imports > impLimit) fail(rel, 'import 语句数', imports, impLimit);
  else if (IMPORT_EXCEPTIONS[rel]) notes.push(`  = ${rel} import ${imports}（例外上限 ${impLimit}：${IMPORT_EXCEPTIONS[rel].reason}）`);
  checkCatch(rel, source);
  checkFunctionBodies(rel, source, /* isVue */ false);
}

function checkCatch(rel, code) {
  const m = code.match(/catch\s*(\([^)]*\))?\s*\{\s*\}/);
  if (m) fail(rel, '空 catch 块', m[0].slice(0, 30), '至少 console.warn');
}

function checkFunctionBodies(rel, code, isVue) {
  const fileName = isVue ? rel + '.ts' : rel;
  const sf = ts.createSourceFile(fileName, code, ts.ScriptTarget.Latest, true);
  // 豁免本文件顶层的对外装配入口（export function）：它是文件级装配作用域，
  // 不是可抽的业务函数；其内部嵌套函数仍逐个受限（继续向下遍历）。
  function visit(node) {
    const isFn =
      ts.isFunctionDeclaration(node) || ts.isFunctionExpression(node) || ts.isArrowFunction(node) || ts.isMethodDeclaration(node);
    if (isFn && node.body && ts.isBlock(node.body)) {
      const name = node.name && ts.isIdentifier(node.name) ? node.name.text : '(anon)';
      const start = sf.getLineAndCharacterOfPosition(node.getStart(sf)).line + 1;
      const end = sf.getLineAndCharacterOfPosition(node.getEnd()).line + 1;
      const len = end - start + 1;
      const exempt =
        !!node.parent &&
        ts.isSourceFile(node.parent) &&
        ts.isFunctionDeclaration(node) &&
        (ts.getCombinedModifierFlags(node) & ts.ModifierFlags.Export) !== 0;
      if (!exempt && len > FN_BODY_LIMIT) {
        fail(rel, `函数 ${name} 体长度`, `${len}L`, `${FN_BODY_LIMIT}L`);
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(sf);
}

for (const f of SFC_FILES) checkSfc(f);
for (const f of MODULE_FILES) checkModule(f);

if (notes.length) {
  console.log('[check-frontend-thresholds] 已登记例外：');
  for (const n of notes) console.log(n);
}

if (failures.length > 0) {
  console.error(`[check-frontend-thresholds] FAIL: ${failures.length} 项超限：`);
  for (const f of failures) console.error(f);
  console.error('  修法：拆分子组件/composable，或改 provide 收敛 props；例外须先登记再合入。');
  process.exit(1);
}

console.log(
  '[check-frontend-thresholds] OK: 阈限门禁通过（SFC≤250L / 模块≤200L / props≤8 / 函数型≤2 / import≤10 / 函数体≤60L / 无空catch）'
);
