// need22-P5 J2：ESLint 平面配置（eslint 10）。
// 存量报错先 warn（一周后收紧为 error）；未使用变量以 tsconfig noUnusedLocals/Parameters 为准，此处关闭避免重复。
// 与 .editorconfig 对齐：缩进 2 空格（indentation 仅 warn，不阻断提交）。
import js from '@eslint/js';
import globals from 'globals';
import tseslint from 'typescript-eslint';
import pluginVue from 'eslint-plugin-vue';
import vueParser from 'vue-eslint-parser';
import prettierConfig from 'eslint-config-prettier';
import boundaries from 'eslint-plugin-boundaries';

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
  {
    plugins: { boundaries },
    settings: {
      // need21-F01：别名解析（与 vite.config.ts / tsconfig.json 三处同源；F05 定 @/* 去留时同步改）。
      'import/resolver': {
        alias: {
          map: [
            ['@features', './src/features'],
            ['@shared', './src/shared'],
            ['@', './src'],
          ],
          extensions: ['.js', '.jsx', '.ts', '.tsx', '.vue', '.json', '.mjs', '.cjs'],
        },
        node: { extensions: ['.js', '.jsx', '.ts', '.tsx', '.vue', '.json', '.mjs', '.cjs'] },
      },
      'boundaries/elements': [
        { type: 'feature-agent', pattern: 'src/features/agent' },
        { type: 'feature-session', pattern: 'src/features/session' },
        { type: 'feature-workspace', pattern: 'src/features/workspace' },
        { type: 'feature-settings', pattern: 'src/features/settings' },
        { type: 'feature-shell', pattern: 'src/features/shell' },
        { type: 'shared', pattern: 'src/shared' },
        { type: 'mocks', pattern: 'src/mocks' },
      ],
    },
    rules: {
      'boundaries/dependencies': [
        'warn',
        {
          default: 'allow',
          policies: [
            {
              from: { element: { type: 'feature-agent' } },
              disallow: {
                to: { element: { types: { anyOf: ['feature-session', 'feature-workspace', 'feature-settings', 'feature-shell'] } } },
              },
            },
            {
              from: { element: { type: 'feature-session' } },
              disallow: {
                to: { element: { types: { anyOf: ['feature-agent', 'feature-workspace', 'feature-settings', 'feature-shell'] } } },
              },
            },
            {
              from: { element: { type: 'feature-workspace' } },
              disallow: {
                to: { element: { types: { anyOf: ['feature-agent', 'feature-session', 'feature-settings', 'feature-shell'] } } },
              },
            },
            {
              from: { element: { type: 'feature-settings' } },
              disallow: {
                to: { element: { types: { anyOf: ['feature-agent', 'feature-session', 'feature-workspace', 'feature-shell'] } } },
              },
            },
            {
              from: { element: { type: 'feature-shell' } },
              disallow: {
                to: { element: { types: { anyOf: ['feature-agent', 'feature-session', 'feature-workspace', 'feature-settings'] } } },
              },
            },
          ],
        },
      ],
    },
  },
  {
    files: ['src/features/agent/stores/**/*.ts'],
    rules: {
      // need21-F03：新 stores 单向数据流卡死（error）：禁止跨 feature 直连 + 禁止直 import 其他 store。
      'boundaries/dependencies': [
        'error',
        {
          default: 'allow',
          policies: [
            {
              from: { element: { type: 'feature-agent' } },
              disallow: {
                to: { element: { types: { anyOf: ['feature-session', 'feature-workspace', 'feature-settings', 'feature-shell'] } } },
              },
            },
            {
              from: { element: { type: 'feature-session' } },
              disallow: {
                to: { element: { types: { anyOf: ['feature-agent', 'feature-workspace', 'feature-settings', 'feature-shell'] } } },
              },
            },
            {
              from: { element: { type: 'feature-workspace' } },
              disallow: {
                to: { element: { types: { anyOf: ['feature-agent', 'feature-session', 'feature-settings', 'feature-shell'] } } },
              },
            },
            {
              from: { element: { type: 'feature-settings' } },
              disallow: {
                to: { element: { types: { anyOf: ['feature-agent', 'feature-session', 'feature-workspace', 'feature-shell'] } } },
              },
            },
            {
              from: { element: { type: 'feature-shell' } },
              disallow: {
                to: { element: { types: { anyOf: ['feature-agent', 'feature-session', 'feature-workspace', 'feature-settings'] } } },
              },
            },
          ],
        },
      ],
      'no-restricted-imports': [
        'error',
        {
          patterns: [
            {
              group: [
                '@/stores/workspaceStore',
                '@/stores/sessionStore',
                '@/stores/configStore',
                '@/stores/askUserStore',
                '@/stores/agentStore',
              ],
              message: 'need21-F03: agent 新 store 禁止直调其他 store，请走 runtime 参数或 haloEventBus 事件。',
            },
          ],
        },
      ],
    },
  },
  prettierConfig
);
