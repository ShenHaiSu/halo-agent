// need22-P5 J2：ESLint 平面配置（eslint 10）。
// 存量报错先 warn（一周后收紧为 error）；未使用变量以 tsconfig noUnusedLocals/Parameters 为准，此处关闭避免重复。
// 与 .editorconfig 对齐：缩进 2 空格（indentation 仅 warn，不阻断提交）。
import js from '@eslint/js';
import globals from 'globals';
import tseslint from 'typescript-eslint';
import pluginVue from 'eslint-plugin-vue';
import vueParser from 'vue-eslint-parser';
import prettierConfig from 'eslint-config-prettier';

export default tseslint.config(
  {
    ignores: ['node_modules/**', 'dist/**', '../Host.Desktop/wwwroot/**', '*.tsbuildinfo', '.husky/**'],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  ...pluginVue.configs['flat/recommended'],
  {
    files: ['**/*.{ts,vue,mjs}'],
    languageOptions: {
      globals: {
        ...globals.browser,
      },
    },
  },
  {
    files: ['**/*.vue'],
    languageOptions: {
      parser: vueParser,
      parserOptions: {
        parser: tseslint.parser,
        ecmaVersion: 2022,
        sourceType: 'module',
      },
    },
  },
  {
    files: ['**/*.{ts,vue}'],
    rules: {
      // 存量宽限（warn；P5+1 周收紧为 error）
      'no-empty': 'warn',
      '@typescript-eslint/no-explicit-any': 'warn',
      '@typescript-eslint/no-unused-vars': 'off',
      '@typescript-eslint/no-non-null-assertion': 'warn',
      'vue/no-unused-vars': 'warn',
      'vue/require-v-for-key': 'error',
      'vue/no-v-html': 'warn',
      'vue/multi-word-component-names': 'off',
      'no-debugger': 'warn',
      'no-console': 'off',
      'prefer-const': 'warn',
      // 以下为本次全量扫描出的存量 error，先降 warn（一周后收紧；对应文件见 pnpm lint 输出）
      'no-control-regex': 'warn',
      'no-useless-escape': 'warn',
      'no-useless-assignment': 'warn',
    },
  },
  {
    files: ['scripts/**/*.mjs', 'eslint.config.mjs'],
    languageOptions: {
      globals: {
        console: 'readonly',
        process: 'readonly',
      },
    },
  },
  prettierConfig,
);
