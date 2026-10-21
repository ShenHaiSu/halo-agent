/**
 * need23：子运行监控卡的数据契约（父视图 → 卡片）。
 * 注意：subId 必须是**完整**子会话 id（进入子会话要用），展示时才用 shortId。
 */
export interface SubRuntimeRow {
  /** 完整子会话 id（进入子会话用；此前被截断成 8 字符，导致 enterSub 必然找不到） */
  subId: string;
  /** 展示用短 id */
  shortId: string;
  label: string;
  status: string;
  /** 已完成轮次（后端真实统计） */
  iteration: number;
  /** 工具调用总次数（后端真实统计） */
  toolCount: number;
  /** 最近一次工具名（后端真实统计，无则空串） */
  lastTool: string;
  /** 耗时毫秒 */
  elapsedMs: number;
}

/** 拒绝记录行（"最近拒绝"告警 + "拒绝与自愈记录"卡共用）。 */
export interface SubRejectionRow {
  code: string;
  callId: string;
  atUtc: string;
}
