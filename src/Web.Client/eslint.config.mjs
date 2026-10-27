// src/Web.Client/eslint.config.mjs
import js from '@eslint/js';
import ts from 'typescript-eslint';
import vue from 'eslint-plugin-vue';

export default [
  js.configs.recommended,
  ...ts.configs.recommended,
  ...vue.configs['flat/recommended'],
  {
    rules: {
      'no-restricted-properties': [
        'error',
        {
          object: 'Object',
          property: 'groupBy',
          message: 'Object.groupBy 需 Chrome 117+。请用 reduce 手写。',
        },
        {
          object: 'Map',
          property: 'groupBy',
          message: 'Map.groupBy 需 Chrome 117+。请用 reduce 手写。',
        },
        {
          object: 'Promise',
          property: 'withResolvers',
          message: 'Promise.withResolvers 需 Chrome 119+。',
        },
        { object: 'Array', property: 'fromAsync', message: 'Array.fromAsync 需 Chrome 121+。' },
        { object: 'navigator', property: 'gpu', message: 'WebGPU 需 Chrome 113+ 且 Win7 不支持。' },
      ],
      'no-restricted-syntax': [
        'error',
        {
          selector: "CallExpression[callee.property.name='toSorted']",
          message: 'Array.prototype.toSorted 需 Chrome 110+。请用 [...arr].sort()。',
        },
        {
          selector: "CallExpression[callee.property.name='toReversed']",
          message: 'Array.prototype.toReversed 需 Chrome 110+。',
        },
        {
          selector: "CallExpression[callee.property.name='toSpliced']",
          message: 'Array.prototype.toSpliced 需 Chrome 110+。',
        },
        {
          selector: "CallExpression[callee.name='fetch']",
          message: '铁律 F-10：前端不得直接 fetch。请走 transport/ 层。',
        },
        {
          selector: "NewExpression[callee.name='XMLHttpRequest']",
          message: '铁律 F-10：不得使用 XMLHttpRequest。请走 transport/ 层。',
        },
      ],
      'no-console': ['warn', { allow: ['error', 'warn', 'debug'] }],
      eqeqeq: ['error', 'always'],
      'prefer-const': 'error',
      'no-var': 'error',
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
      ],
      '@typescript-eslint/no-explicit-any': 'error',
      '@typescript-eslint/consistent-type-imports': ['error', { prefer: 'type-imports' }],
      'vue/multi-word-component-names': 'off',
      'vue/require-default-prop': 'off',
      'vue/no-v-html': 'error',
      'vue/component-api-style': ['error', ['script-setup']],
      'vue/block-lang': ['error', { script: { lang: 'ts' } }],
      'vue/block-order': ['error', { order: ['script', 'template', 'style'] }],
      'vue/max-attributes-per-line': 'off',
      'vue/singleline-html-element-content-newline': 'off',
      'vue/multiline-html-element-content-newline': 'off',
      'vue/html-self-closing': 'off',
      'vue/html-closing-bracket-newline': 'off',
      'vue/html-closing-bracket-spacing': 'off',
      'vue/html-indent': 'off',
      'vue/script-indent': 'off',
    },
  },
  {
    files: [
      'src/transport/**',
      'src/bridge/**',
      'src/utils/errors/**',
      'scripts/**',
      '*.config.ts',
      '*.config.mjs',
    ],
    rules: { 'no-restricted-syntax': 'off' },
  },
  {
    files: ['scripts/**'],
    languageOptions: { globals: { process: 'readonly', console: 'readonly', URL: 'readonly' } },
    rules: {
      '@typescript-eslint/no-unused-vars': 'off',
      'no-undef': 'off',
      'no-console': 'off',
      'no-misleading-character-class': 'off',
    },
  },
  {
    files: ['**/*.vue'],
    languageOptions: {
      parserOptions: {
        parser: ts.parser,
        ecmaVersion: 2022,
        sourceType: 'module',
        extraFileExtensions: ['.vue'],
      },
    },
    rules: {
      '@typescript-eslint/consistent-type-imports': 'off',
      '@typescript-eslint/no-unused-vars': 'off',
    },
  },
  {
    files: ['src/components/chat/MessageContent.vue', 'src/**/*.test.ts'],
    rules: { 'vue/no-v-html': 'off' },
  },
  {
    ignores: [
      'node_modules/**',
      'dist/**',
      '../Host.Desktop/wwwroot/**',
      '.pnpm-store-local/**',
      'config-shims.d.ts',
    ],
  },
];
