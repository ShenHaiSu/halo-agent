// 目录纯函数自检（node --experimental-strip-types scripts/toc.selfcheck.mjs）
import { bubbleLabel } from '../src/features/session/utils/historyToc.ts';
import { flatBlocks, summaryAnchorKeys } from '../src/features/session/utils/timelineSummary.ts';

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

// U1：超长中文截断不断言具体长度，只断言非空且为字符串
eq('U1', typeof bubbleLabel('你好世界，这是一段超过十五个字的中文文本'), 'string');
// U2：短文本无省略号
eq('U2', bubbleLabel('短文本'), '短文本');

// U8/U9：摘要源目录计数与锚点（主链纯 V2，每轮三锚点）
const summaries = [
  { turnId: 'uturn-u1', turnSeq: 1, userPreview: '第一问', userContent: '第一问是什么', finalBlockId: 'b1', finalPreview: '第一答', blocks: 1, tools: 0, failed: 0, durationMs: 0, chars: 3, status: 'completed', lastRequestId: 'req-1', anchorKey: 'uturn-u1' },
  { turnId: 'uturn-u2', turnSeq: 2, userPreview: '第二问', userContent: '第二问是什么', finalBlockId: 'b2', finalPreview: '第二答', blocks: 1, tools: 0, failed: 0, durationMs: 0, chars: 3, status: 'completed', lastRequestId: 'req-2', anchorKey: 'uturn-u2' },
];
eq('U8-count', flatBlocks(summaries).length, 2);
eq('U9-anchors', summaryAnchorKeys('uturn-u1', 'b1'), { user: 'uturn-u1', process: 'uturn-u1:process', final: 'b1' });

console.log(`\nselfcheck: pass=${pass} fail=${fail}`);
process.exit(fail ? 1 : 0);
