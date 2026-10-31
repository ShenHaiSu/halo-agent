<!-- src/Web.Client/src/components/chat/MessageBody.vue -->
<!-- P5 D5-1 Markdown 渲染入口：全仓唯一允许渲染模型内容的组件。 @allow-innerhtml -->
<!-- 本文件无动态 HTML 注入，用 h() 把 AST 递归构建为 VNode，XSS 物理免疫。 -->
<script setup lang="ts">
 import { computed, getCurrentInstance, h, popScopeId, pushScopeId, type VNode } from 'vue';
import type { MdInline, MdNode } from './MarkdownRenderer';
import { classifyLink } from './sanitize';
import CodeBlock from './CodeBlock.vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';

const props = withDefaults(
  defineProps<{
    nodes: MdNode[];
    /** 流式中的半行（不解析，纯文本） */
    pending?: string;
  }>(),
  { pending: '' }
);

/** 链接点击三档策略：外部走宿主打开；mailto 放行默认行为；
 * 相对路径阻止导航 + toast；非安全协议解析层已降级为纯文本，到不了这里 */
async function openLink(e: MouseEvent, href: string): Promise<void> {
  const kind = classifyLink(href);
  if (kind === 'mailto') return;
  e.preventDefault();
  if (kind === 'unsafe') return;
  if (kind === 'external') {
    try {
      await transport.host.openExternal(href);
    } catch {
      /* 宿主不可用时忽略（已阻止默认导航） */
    }
    return;
  }
  useToast().info('历史中的相对链接不可跳转');
}

function renderInline(nodes: MdInline[]): VNode[] {
  return nodes.map((n, i) => {
    switch (n.type) {
      case 'text':
        return h('span', { key: i }, n.value);
      case 'br':
        return h('br', { key: i });
      case 'code':
        return h('code', { key: i, class: 'md-inline-code' }, n.value);
      case 'strong':
        return h('strong', { key: i }, renderInline(n.children));
      case 'em':
        return h('em', { key: i }, renderInline(n.children));
      case 'del':
        return h('del', { key: i }, renderInline(n.children));
      case 'link': {
        const isImg = n.isImage === true;
        if (isImg) {
          return h('img', {
            key: i,
            src: n.href,
            alt: n.alt ?? n.title ?? '',
            title: n.alt ?? undefined,
            class: 'md-img',
            loading: 'lazy',
            // 图片加载失败不要破图 icon
            onError: (ev: Event) => {
              (ev.target as HTMLImageElement).style.display = 'none';
            },
          });
        }
        return h(
          'a',
          {
            key: i,
            href: n.href,
            title: n.title,
            class: 'md-link',
            rel: 'noopener noreferrer',
            onClick: (e: MouseEvent) => void openLink(e, n.href),
          },
          renderInline(n.children)
        );
      }
    }
  });
}

function renderBlocks(nodes: MdNode[]): VNode[] {
  return nodes.map((n, i) => {
    switch (n.type) {
      case 'paragraph':
        return h('p', { key: i, class: 'md-p' }, renderInline(n.children));

      case 'heading':
        return h(
          'h' + String(n.level),
          { key: i, class: 'md-h md-h' + String(n.level) },
          renderInline(n.children)
        );

      case 'fence':
        return h(CodeBlock, {
          key: i,
          lang: n.lang,
          code: n.value,
          closed: n.closed,
        });

      case 'hr':
        return h('hr', { key: i, class: 'md-hr' });

      case 'blockquote':
        return h('blockquote', { key: i, class: 'md-quote' }, renderBlocks(n.children));

      case 'list': {
        const tag = n.ordered ? 'ol' : 'ul';
        const listProps: Record<string, unknown> = { key: i, class: 'md-list' };
        if (n.ordered && n.start !== 1) listProps['start'] = String(n.start);
        return h(
          tag,
          listProps,
          n.items.map((it, k) => {
            const body = it.inline !== null ? renderInline(it.inline) : renderBlocks(it.children);
            if (it.checked !== null) {
              return h('li', { key: k, class: 'md-li md-li-task' }, [
                h('input', {
                  type: 'checkbox',
                  checked: it.checked,
                  disabled: true,
                  class: 'md-task',
                }),
                h('span', { class: 'md-task-text' }, body),
              ]);
            }
            return h('li', { key: k, class: 'md-li' }, body);
          })
        );
      }

      case 'table': {
        const alignOf = (k: number): 'left' | 'center' | 'right' | undefined =>
          n.align[k] ?? undefined;
        return h('div', { key: i, class: 'md-table-wrap' }, [
          h('table', { class: 'md-table' }, [
            h('thead', {}, [
              h(
                'tr',
                {},
                n.head.map((cell, k) =>
                  h(
                    'th',
                    {
                      key: k,
                      style: alignOf(k) ? { textAlign: alignOf(k) } : undefined,
                    },
                    renderInline(cell)
                  )
                )
              ),
            ]),
            h(
              'tbody',
              {},
              n.rows.map((row, r) =>
                h(
                  'tr',
                  { key: r },
                  row.map((cell, k) =>
                    h(
                      'td',
                      {
                        key: k,
                        style: alignOf(k) ? { textAlign: alignOf(k) } : undefined,
                      },
                      renderInline(cell)
                    )
                  )
                )
              )
            ),
          ]),
        ]);
      }
    }
  });
}

 // setup 顶层捕获：__scopeId 是 SFC 类型级常量（多实例共享同一值）。
 // computed 求值常发生在 render 上下文之外（流式 finalize 即此类），此时
 // currentRenderingInstance 为空，故不用 withScopeId（其 3.5 实现忽略传入 id、
 // 转而依赖渲染实例，包 computed 不可靠）；改用 push/popScopeId 按值绑定。
 const scopeId = (getCurrentInstance()?.type as { __scopeId?: string } | undefined)?.__scopeId ?? null;
 const blocks = computed(() => {
   if (scopeId) pushScopeId(scopeId);
   try {
     return renderBlocks(props.nodes);
   } finally {
     if (scopeId) popScopeId();
   }
 });

function renderAll(): VNode[] {
  return blocks.value;
}
</script>

<template>
  <div class="md-body">
    <component :is="renderAll" />
    <!-- 流式半行：纯文本，不解析（避免半行的 ** 被误渲染） -->
    <span v-if="pending" class="md-pending">{{ pending }}</span>
  </div>
</template>

<style scoped>
.md-body {
  font-size: var(--text-base);
  line-height: 1.65;
  color: hsl(var(--fg));
  overflow-wrap: anywhere; /* 长 URL / 长单词不撑破容器 */
  word-break: break-word;
}
 /* 渲染体 VNode 在 computed 内经 h() 创建：已用 pushScopeId 绑定本 SFC 作用域，内层自带 data-v，裸写 scoped 即可。 */
 /* 勿在 computed 外手搓 VNode，新增手搓点须进 pushScopeId 包裹。 */
 .md-p{margin:0 0 var(--space-3) 0;}
 .md-p:last-child{margin-bottom:0;}
 .md-h{font-weight:600;line-height:1.35;margin:var(--space-5) 0 var(--space-2);}
 .md-h1{font-size:var(--text-xl);}
 .md-h2{font-size:var(--text-lg);}
 .md-h3{font-size:var(--text-base);}
 .md-h4,.md-h5,.md-h6{font-size:var(--text-sm);}
 .md-list{margin:0 0 var(--space-3);padding-inline-start:1.6em;list-style:disc outside none;}
 ol.md-list{list-style-type:decimal;}
 .md-list .md-list{margin-block:var(--space-1);padding-inline-start:1.6em;}
 .md-list .md-list{list-style-type:circle;}
 .md-list .md-list .md-list{list-style-type:square;}
 ol.md-list ol.md-list{list-style-type:lower-alpha;}
 ol.md-list ol.md-list ol.md-list{list-style-type:lower-roman;}
 /* ul⊂ol / ol⊂ul 混排回落：后代命中已有，只显式托底二级 ol 在 ul 下的形态 */
 ul.md-list ol.md-list{list-style-type:lower-alpha;}
 .md-li{margin:var(--space-1) 0;}
 .md-li::marker{color:hsl(var(--fg-muted));}
 .md-li-task{list-style:none;margin-inline-start:-1.6em;display:flex;gap:var(--space-2);align-items:flex-start;}
 .md-task{margin-top:0.3em;accent-color:hsl(var(--accent));}
 .md-quote{margin:0 0 var(--space-3);padding:var(--space-2) var(--space-3);border-left:3px solid hsl(var(--border-strong));color:hsl(var(--fg-muted));background:hsl(var(--bg-subtle));border-radius:0 var(--radius-sm) var(--radius-sm) 0;}
 .md-inline-code{font-family:var(--font-mono);font-size:0.92em;padding:0.1em 0.35em;border-radius:var(--radius-xs);background:hsl(var(--bg-muted));color:hsl(var(--fg));}
 .md-hr{border:0;border-top:1px solid hsl(var(--border));margin:var(--space-4) 0;}
 .md-link{color:hsl(var(--accent));text-decoration:underline;text-underline-offset:2px;cursor:pointer;}
 .md-link:hover{color:hsl(var(--accent-hover));}
 .md-img{max-width:100%;border-radius:var(--radius-sm);border:1px solid hsl(var(--border-subtle));}
 .md-table-wrap{overflow-x:auto;margin:0 0 var(--space-3);max-width:100%;border:1px solid hsl(var(--border));border-radius:var(--radius-md);}
 .md-table{border-collapse:collapse;width:100%;min-width:100%;font-size:var(--text-sm);line-height:1.55;border:0;}
 .md-table th,.md-table td{border:1px solid hsl(var(--border));padding:var(--space-2) var(--space-3);text-align:left;vertical-align:top;}
 .md-table thead tr:first-child th{border-top:0;}
 .md-table tr th:first-child,.md-table tr td:first-child{border-left:0;}
 .md-table tr th:last-child,.md-table tr td:last-child{border-right:0;}
 .md-table tbody tr:last-child td{border-bottom:0;}
 .md-table th{background:hsl(var(--bg-subtle));font-weight:600;}
 .md-pending{white-space:pre-wrap;}
</style>
