// need19 P2/P4：目录纯函数自检（node --experimental-strip-types scripts/toc.selfcheck.mjs）
import { toDisplayText, bubbleLabel, buildHistoryToc } from '../src/features/session/utils/historyToc.ts';
import { buildTimelineEntries } from '../src/features/session/utils/turnGroup.ts';

let pass = 0;
let fail = 0;
function eq(name, got, want) {
  const a = JSON.stringify(got);
  const b = JSON.stringify(want);
  if (a === b) {
    pass++;
    console.log(`ok ${name}`);
  } else {
    fail++;
    console.log(`FAIL ${name}\n  got:  ${a}\n  want: ${b}`);
  }
}

// U1：超长中文截 15 + …
eq('U1', bubbleLabel('你好世界，这是一段超过十五个字的中文文本'), '你好世界，这是一段超过十五个字…');
// U2：短文本无省略号
eq('U2', bubbleLabel('短文本'), '短文本');
// U4：markdown 清洗
eq('U4', toDisplayText('# 标题\n\n正文 **加粗** [链接](http://a)'), '标题 正文 加粗 链接');
// U5：纯图
eq('U5', toDisplayText('![图](a.png)'), '[图片]');
// U6：代码围栏
eq('U6', toDisplayText('```ts\nconst a = 1;\n```'), 'const a = 1;');
// U7：HTML 标签
eq('U7', toDisplayText('<script>alert(1)</script>hi').includes('hi'), true);

// U8/U9：buildHistoryToc 计数与轮次
const msgs = [
  { id: 'u1', role: 'user', content: '第一问是什么', createdAtUtc: '' },
  { id: 'a1', role: 'assistant', content: '第一答内容', createdAtUtc: '' },
  { id: 'u2', role: 'user', content: '第二问是什么', createdAtUtc: '' },
  { id: 'a2', role: 'assistant', content: '第二答内容', createdAtUtc: '' },
  { id: 'a3', role: 'assistant', content: '第三答内容', createdAtUtc: '' },
  { id: 's1', role: 'system', content: '系统提示', createdAtUtc: '' },
];
const entries = buildTimelineEntries(msgs, [
  {
    key: 'a1',
    assistantId: 'a1',
    reasoning: null,
    content: '第一答内容',
    toolCalls: [],
    toolMessages: [],
    telemetry: null,
    createdAtUtc: '',
  },
  {
    key: 'a2',
    assistantId: 'a2',
    reasoning: null,
    content: '第二答内容',
    createdAtUtc: '',
    toolCalls: [],
    toolMessages: [],
    telemetry: null,
  },
  {
    key: 'a3',
    assistantId: 'a3',
    reasoning: null,
    content: '第三答内容',
    createdAtUtc: '',
    toolCalls: [],
    toolMessages: [],
    telemetry: null,
  },
]);
eq('U8-count', buildHistoryToc(entries, { role: 'all', roundHeadsOnly: false }).length, 5);
const heads = buildHistoryToc(entries, { role: 'all', roundHeadsOnly: true });
eq(
  'U9-heads-no-system',
  heads.every((t) => t.role !== 'system'),
  true
);
eq('U9-heads-rounds', heads.filter((t) => t.role === 'assistant').length, 2);

// U10：孤立 tool 组
const toolEntries = buildTimelineEntries(
  [{ id: 't1', role: 'tool', content: 'out', createdAtUtc: '' }],
  [
    {
      key: 'tool-t1',
      assistantId: '',
      reasoning: null,
      content: null,
      toolCalls: [],
      toolMessages: [{ id: 't1', role: 'tool', content: 'out', createdAtUtc: '' }],
      telemetry: null,
      createdAtUtc: '',
    },
  ]
);
const toolToc = buildHistoryToc(toolEntries, { role: 'all', roundHeadsOnly: false });
eq('U10-role', toolToc[0]?.role, 'assistant');
eq('U10-label', String(toolToc[0]?.label || '').startsWith('🔧'), true);

// U11：与旧内联语义一致（key/kind 序列）
eq(
  'U11-keys',
  entries.map((e) => `${e.key}:${e.kind}`),
  ['u-u1:user', 'a1:turn', 'u-u2:user', 'a2:turn', 'a3:turn', 's-s1:system']
);

console.log(`\nselfcheck: pass=${pass} fail=${fail}`);
process.exit(fail ? 1 : 0);
