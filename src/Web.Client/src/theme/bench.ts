// src/Web.Client/src/theme/bench.ts（仅开发用）
import { applyThemeTokens, clearInlineThemeTokens } from './apply';

/**
 * 主题切换性能测量。
 * 在浏览器 Console 里：await __benchTheme()
 */
export async function benchThemeSwitch(iterations = 20): Promise<void> {
  const { pendingThemeSnapshot } = await import('./builtin');
  const themes = Object.keys(pendingThemeSnapshot);
  const durations: number[] = [];

  for (let i = 0; i < iterations; i++) {
    const id = themes[i % themes.length]!;
    const theme = pendingThemeSnapshot[id]!;
    const t0 = performance.now();
    clearInlineThemeTokens();
    applyThemeTokens(theme.tokens);
    // 强制样式重算（读取一个布局属性）
    void document.body.offsetHeight;
    durations.push(performance.now() - t0);
  }

  durations.sort((a, b) => a - b);
  const p50 = durations[Math.floor(durations.length * 0.5)];
  const p95 = durations[Math.floor(durations.length * 0.95)];
  console.debug(
    `主题切换 ${iterations} 次：p50=${p50?.toFixed(2)}ms p95=${p95?.toFixed(2)}ms ` +
      `max=${durations[durations.length - 1]?.toFixed(2)}ms`
  );
}
