// need23: 源码级断言 —— 模板里用了局部组件但忘记 import。
//
// 背景：漏 import 的组件会被 Vue 编译成 resolveComponent("<PascalCase>") 运行时解析。
//       本项目 main.ts 用 app.use(naive) 全局注册了 naive-ui，所以 kebab 的 n-* 运行时解析是合法的；
//       但 PascalCase 局部组件未注册 → 运行时解析失败 → 渲染成空标签（整块卡片消失且无任何报错）。
//       该类 bug 不会被 vue-tsc（未开 strictTemplates）捕获，eslint-plugin-vue v10 也没有 no-unresolved-components 规则。
// 做法：用 @vue/compiler-sfc 把每个 SFC 编译成 render 代码，扫出仍走 resolveComponent 的 PascalCase 名。
//
// 用法：node scripts/assert-no-unresolved-components.mjs
// 退出码：0 = 通过；1 = 有未 import 的局部组件（打印文件/组件名）。
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse, compileScript } from '@vue/compiler-sfc';

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = resolve(here, '../src');

// 编译产物中未解析组件的形态：_resolveComponent("Xxx") / resolveComponent("Xxx")
const RESOLVE_CALL = /resolveComponent\(\s*"([A-Za-z][A-Za-z0-9_$]*)"\s*\)/g;

// 允许运行时解析的全局组件（naive-ui 全量注册；其余 kebab 一律视为未注册）
const GLOBAL_COMPONENT_RE = /^n-[a-z0-9-]+$/;

function walk(dir, out) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    const st = statSync(p);
    if (st.isDirectory()) walk(p, out);
    else if (name.endsWith('.vue')) out.push(p);
  }
  return out;
}

const problems = [];

for (const file of walk(srcRoot, [])) {
  const source = readFileSync(file, 'utf8');
  let compiled;
  try {
    const { descriptor, errors } = parse(source, { filename: file });
    if (errors.length) continue; // 语法错误交给 vue-tsc 报，这里不重复
    if (!descriptor.template) continue;
    // inlineTemplate: true → 模板内联进 setup，编译器按 binding 元数据把已 import 的组件解析为直接引用；
    // 未 import 的则保留 resolveComponent("Xxx") 调用。
    compiled = compileScript(descriptor, { id: file, inlineTemplate: true }).content;
  } catch {
    continue;
  }
  for (const m of compiled.matchAll(RESOLVE_CALL)) {
    const name = m[1];
    if (GLOBAL_COMPONENT_RE.test(name)) continue; // naive-ui 全局注册，运行时可解析
    problems.push({ file: relative(srcRoot, file), name });
  }
}

if (problems.length > 0) {
  console.error(`[assert-no-unresolved-components] FAIL: ${problems.length} 处模板使用了未 import 的局部组件（将渲染为空）：`);
  for (const p of problems) console.error(`  - ${p.file} → <${p.name}>`);
  console.error('  修法：在对应 .vue 的 <script setup> 里 import 该组件。');
  process.exit(1);
}

console.log('[assert-no-unresolved-components] OK: 所有模板组件均已 import');
