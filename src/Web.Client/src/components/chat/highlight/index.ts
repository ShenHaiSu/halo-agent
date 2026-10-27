// src/Web.Client/src/components/chat/highlight/index.ts
import type { LangDef } from './tokenizer';
import { typescript } from './langs/typescript';
import { javascript } from './langs/javascript';
import { csharp } from './langs/csharp';
import { json } from './langs/json';
import { sql } from './langs/sql';
import { html } from './langs/html';
import { css } from './langs/css';
import { python } from './langs/python';
import { bash } from './langs/bash';
import { powershell } from './langs/powershell';
import { markdown } from './langs/markdown';

/**
 * ============ 别名表 ============
 *  模型输出的 ```cs / ```c# / ```C# 都要能映射到 csharp。
 *  别名全部小写比较。
 * ==============================
 */
const REGISTRY: Record<string, LangDef> = {
  typescript,
  javascript,
  csharp,
  json,
  sql,
  html,
  css,
  python,
  bash,
  powershell,
  markdown,
};

const ALIAS: Record<string, string> = {
  ts: 'typescript',
  tsx: 'typescript',
  mts: 'typescript',
  js: 'javascript',
  jsx: 'javascript',
  mjs: 'javascript',
  cjs: 'javascript',
  cs: 'csharp',
  'c#': 'csharp',
  sh: 'bash',
  shell: 'bash',
  zsh: 'bash',
  console: 'bash',
  ps: 'powershell',
  ps1: 'powershell',
  pwsh: 'powershell',
  py: 'python',
  python3: 'python',
  jsonc: 'json',
  json5: 'json',
  md: 'markdown',
  htm: 'html',
  xml: 'html',
  svg: 'html',
  vue: 'html',
  scss: 'css',
  less: 'css',
};

export function resolveLang(raw: string | null | undefined): LangDef | null {
  if (!raw) return null;
  const k = raw.trim().toLowerCase();
  const name = ALIAS[k] ?? k;
  return REGISTRY[name] ?? null;
}

/** 下拉里可显示的显示名 */
export function displayLang(raw: string | null): string {
  if (!raw) return '纯文本';
  const k = raw.trim().toLowerCase();
  const name = ALIAS[k] ?? k;
  switch (name) {
    case 'typescript':
      return 'TypeScript';
    case 'javascript':
      return 'JavaScript';
    case 'csharp':
      return 'C#';
    case 'json':
      return 'JSON';
    case 'sql':
      return 'SQL';
    case 'html':
      return 'HTML';
    case 'css':
      return 'CSS';
    case 'python':
      return 'Python';
    case 'bash':
      return 'Shell';
    case 'powershell':
      return 'PowerShell';
    case 'markdown':
      return 'Markdown';
    default:
      return raw;
  }
}

export const LANG_NAMES = Object.keys(REGISTRY);
export const ALL_LANGS: LangDef[] = Object.values(REGISTRY);
