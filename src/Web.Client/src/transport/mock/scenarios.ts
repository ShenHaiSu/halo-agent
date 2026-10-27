/**
 * Mock 场景定义。
 *
 * 场景 = 一串带时间间隔的事件。用"回放真实事件序列"而不是"返回假数据"，
 * 是因为前端真正要验证的是**事件驱动的渲染逻辑**：
 *   - 流式文本如何合帧
 *   - 工具卡片如何从"执行中"变"完成"
 *   - 权限询问如何暂停与继续
 */

export interface ScenarioStep {
  afterMs: number
  kind: string
  payload: unknown
}

export interface Scenario {
  steps: ScenarioStep[]
  /** 权限询问被允许后继续回放什么 */
  onAllow?: Scenario
  /** 权限询问被拒绝后继续回放什么 */
  onDeny?: Scenario
}

// ---------- 场景一：简单对话（无工具） ----------

const SIMPLE_ANSWER =
  '好的，我来看看。首先需要确认 ~/Downloads 目录下有哪些压缩包。'

function streamText(text: string, perChunkMs = 30): ScenarioStep[] {
  const steps: ScenarioStep[] = []
  // 每 3 个字符一块，模拟模型的增量输出
  for (let i = 0; i < text.length; i += 3) {
    steps.push({
      afterMs: i === 0 ? 200 : perChunkMs,
      kind: 'reasoning.delta',
      payload: { text: i === 0 ? '用户要求归档压缩包，先列目录看清楚。' : '' },
    })
  }
  return steps.filter((s) => (s.payload as { text: string }).text !== '')
}

function streamBody(text: string, perChunkMs = 25): ScenarioStep[] {
  const steps: ScenarioStep[] = []
  for (let i = 0; i < text.length; i += 4) {
    steps.push({
      afterMs: i === 0 ? 400 : perChunkMs,
      kind: 'text.delta',
      payload: { text: text.slice(i, i + 4) },
    })
  }
  return steps
}

const simpleScenario: Scenario = {
  steps: [
    { afterMs: 0, kind: 'round.started', payload: { used: 1, limit: 200 } },
    ...streamText(SIMPLE_ANSWER),
    ...streamBody('我先列出目录看看有哪些压缩包。'),
    {
      afterMs: 500,
      kind: 'turn.finished',
      payload: { turnId: 'mock-turn-1', text: '我先列出目录看看有哪些压缩包。', truncated: false },
    },
    { afterMs: 50, kind: 'usage', payload: { prompt: 1200, completion: 80 } },
  ],
}

// ---------- 场景二：带工具调用 ----------

const toolScenario: Scenario = {
  steps: [
    { afterMs: 0, kind: 'round.started', payload: { used: 1, limit: 200 } },
    ...streamBody('我先看看目录里有什么。'),
    {
      afterMs: 400,
      kind: 'tool.started',
      payload: {
        toolCallId: 'call_1',
        name: 'list_directory',
        args: '{"path":"~/Downloads"}',
        idleMs: null,
      },
    },
    { afterMs: 350, kind: 'tool.finished', payload: {
      toolCallId: 'call_1',
      name: 'list_directory',
      ok: true,
      output: '目录 C:\\Users\\me\\Downloads:\narchive-2024.zip\nreport.zip\n截图.png\nnotes.txt\n',
    }},
    { afterMs: 300, kind: 'round.started', payload: { used: 2, limit: 200 } },
    ...streamBody('找到两个压缩包：archive-2024.zip 和 report.zip。我按日期归档。'),
    {
      afterMs: 400,
      kind: 'tool.started',
      payload: {
        toolCallId: 'call_2',
        name: 'run_shell_command',
        args: '{"command":"move archive-2024.zip archives\\\\2024\\\\","idle_ms":60000}',
        idleMs: 60000,
      },
    },
    // 模拟命令实时输出
    { afterMs: 300, kind: 'tool.outputDelta', payload: { toolCallId: 'call_2', delta: 'moving...' } },
    { afterMs: 300, kind: 'tool.outputDelta', payload: { toolCallId: 'call_2', delta: ' done\n' } },
    { afterMs: 200, kind: 'tool.finished', payload: {
      toolCallId: 'call_2',
      name: 'run_shell_command',
      ok: true,
      output: 'moving... done\n(命令退出码 0)\n',
    }},
    ...streamBody('归档完成。archive-2024.zip → archives/2024/。'),
    {
      afterMs: 500,
      kind: 'turn.finished',
      payload: { turnId: 'mock-turn-1', text: '归档完成。', truncated: false },
    },
    { afterMs: 50, kind: 'usage', payload: { prompt: 2400, completion: 210 } },
  ],
}

// ---------- 场景三：危险命令（权限询问） ----------

const permissionScenario: Scenario = {
  steps: [
    { afterMs: 0, kind: 'round.started', payload: { used: 1, limit: 200 } },
    ...streamBody('这个命令有风险，我先确认一下。'),
    {
      afterMs: 300,
      kind: 'ask.permission',
      payload: {
        askId: 'q1',
        kind: 'command',
        title: '命令执行确认',
        body: 'rm -rf /tmp/old-build',
        warning: null,
        allowAlways: true,
      },
    },
  ],
  onAllow: {
    steps: [
      { afterMs: 200, kind: 'tool.started', payload: {
        toolCallId: 'call_p1',
        name: 'run_shell_command',
        args: '{"command":"rm -rf /tmp/old-build"}',
        idleMs: null,
      }},
      { afterMs: 400, kind: 'tool.finished', payload: {
        toolCallId: 'call_p1',
        name: 'run_shell_command',
        ok: true,
        output: '(命令退出码 0)\n',
      }},
      ...streamBody('清理完成。'),
      { afterMs: 300, kind: 'turn.finished', payload: {
        turnId: 'mock-turn-1', text: '清理完成。', truncated: false },
      },
    ],
  },
  onDeny: {
    steps: [
      { afterMs: 200, kind: 'tool.finished', payload: {
        toolCallId: 'call_p1',
        name: 'run_shell_command',
        ok: false,
        output: '操作未执行: 用户拒绝了该操作',
      }},
      ...streamBody('好的，我不执行这条命令。需要我做别的吗？'),
      { afterMs: 300, kind: 'turn.finished', payload: {
        turnId: 'mock-turn-1', text: '已取消。', truncated: false },
      },
    ],
  },
}

// ---------- 场景四：高危命令（红色警告） ----------

const dangerScenario: Scenario = {
  steps: [
    { afterMs: 0, kind: 'round.started', payload: { used: 1, limit: 200 } },
    {
      afterMs: 400,
      kind: 'ask.permission',
      payload: {
        askId: 'q2',
        kind: 'command',
        title: '命令执行确认',
        body: 'rm -rf /',
        warning: '⚠ 检测到递归删除根目录 /\n解析出的目标: /',
        allowAlways: false,
      },
    },
  ],
  onAllow: {
    steps: [
      { afterMs: 200, kind: 'notice', payload: {
        text: '这只是一次演示：真实的 HaloAgent 不会真的执行这条命令。',
        level: 'warn',
      }},
      { afterMs: 200, kind: 'turn.finished', payload: {
        turnId: 'mock-turn-1', text: '', truncated: false },
      },
    ],
  },
  onDeny: {
    steps: [
      { afterMs: 200, kind: 'notice', payload: { text: '已拒绝，这是正确的选择。', level: 'info' } },
      { afterMs: 200, kind: 'turn.finished', payload: {
        turnId: 'mock-turn-1', text: '', truncated: false },
      },
    ],
  },
}

// ---------- 场景五：轮次预算用尽 ----------

const roundsScenario: Scenario = {
  steps: [
    { afterMs: 0, kind: 'round.started', payload: { used: 200, limit: 200 } },
    {
      afterMs: 300,
      kind: 'ask.rounds',
      payload: {
        askId: 'q3',
        kind: 'rounds',
        title: '轮次预算用尽',
        body: '已用 200 轮，任务可能还没完。继续会再放一批。',
        allowAlways: false,
        used: 200,
      },
    },
  ],
  onAllow: {
    steps: [
      { afterMs: 200, kind: 'round.started', payload: { used: 1, limit: 200 } },
      ...streamBody('继续处理…'),
      { afterMs: 300, kind: 'turn.finished', payload: {
        turnId: 'mock-turn-1', text: '完成。', truncated: false },
      },
    ],
  },
}

// ---------- 场景选择 ----------

export function getScenario(userText: string): Scenario {
  const t = userText.toLowerCase()

  // 用关键字触发不同场景，便于手动验证
  if (t.includes('危险') || t.includes('danger') || t.includes('rm -rf /')) {
    return dangerScenario
  }
  if (t.includes('权限') || t.includes('permission') || t.includes('归档')) {
    return permissionScenario
  }
  if (t.includes('轮次') || t.includes('rounds')) {
    return roundsScenario
  }
  if (t.includes('工具') || t.includes('tool') || t.includes('zip')) {
    return toolScenario
  }
  return simpleScenario
}
