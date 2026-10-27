/**
 * 未自带类型声明的 PostCSS 插件 shim（仅供 vite.config.ts / 编辑器使用）。
 * postcss-preset-env / cssnano / autoprefixer / tailwindcss 均自带类型，无需声明。
 */
declare module 'postcss-import' {
  import type { PluginCreator } from 'postcss'
  const plugin: PluginCreator<Record<string, unknown>>
  export default plugin
}

declare module 'tailwindcss/nesting' {
  import type { PluginCreator } from 'postcss'
  const plugin: PluginCreator<string | Record<string, unknown>>
  export default plugin
}
