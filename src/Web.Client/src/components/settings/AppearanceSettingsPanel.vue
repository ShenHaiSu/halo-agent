<template>
  <div class="space-y-4 min-w-0 max-w-full overflow-x-hidden">
    <!-- 1. 明暗与主题色 -->
    <n-card title="明暗与主题色" size="small" data-testid="appearance-theme-card">
      <div class="space-y-3">
        <div class="flex items-center gap-2">
          <span class="text-xs text-secondary shrink-0">外观模式</span>
          <n-radio-group
            :value="settings.themeMode"
            data-testid="appearance-theme-mode"
            @update:value="onThemeModeChange"
          >
            <n-radio value="dark">
              <span class="inline-flex items-center gap-1 text-xs">
                <Moon class="w-3.5 h-3.5" />暗黑
              </span>
            </n-radio>
            <n-radio value="light">
              <span class="inline-flex items-center gap-1 text-xs">
                <Sun class="w-3.5 h-3.5" />浅色
              </span>
            </n-radio>
          </n-radio-group>
        </div>

        <div class="space-y-1.5">
          <span class="text-xs text-secondary">主题色</span>
          <div class="flex items-center gap-2 flex-wrap">
            <button
              v-for="(preset, i) in THEME_COLOR_PRESETS"
              :key="preset.value"
              type="button"
              :title="`${preset.label} ${preset.value}`"
              :data-testid="`theme-color-preset-${i}`"
              class="w-7 h-7 rounded-full border transition-transform hover:scale-110"
              :style="{
                backgroundColor: preset.value,
                borderColor: settings.themeColor.toLowerCase() === preset.value ? '#fff' : 'transparent',
                boxShadow: settings.themeColor.toLowerCase() === preset.value
                  ? `0 0 0 2px ${preset.value}` : '0 0 0 1px var(--border-subtle)',
              }"
              @click="onPresetColor(preset.value)"
            />
          </div>
          <div class="flex items-center gap-2">
            <n-input
              v-model:value="customHex"
              placeholder="#3b82f6 自定义 hex"
              size="small"
              class="font-mono max-w-[180px]"
              data-testid="appearance-custom-color"
              @change="onCustomHexApply"
              @keydown.enter="onCustomHexApply"
            />
            <span
              class="w-6 h-6 rounded border border-subtle shrink-0"
              :style="{ backgroundColor: settings.themeColor }"
              :title="`当前生效：${settings.themeColor}`"
            />
          </div>
        </div>
      </div>
    </n-card>

    <!-- 2. 字体 -->
    <n-card title="字体" size="small" data-testid="appearance-font-card">
      <div class="space-y-3">
        <div class="space-y-1.5">
          <div class="flex items-center justify-between">
            <span class="text-xs text-secondary">全局字号</span>
            <span class="text-xs font-mono" data-testid="appearance-font-size-value">{{ settings.fontSize }}px</span>
          </div>
          <n-slider
            :value="settings.fontSize"
            :min="12"
            :max="16"
            :step="1"
            :marks="fontSizeMarks"
            data-testid="appearance-font-size"
            @update:value="onFontSizeChange"
          />
        </div>

        <div class="grid grid-cols-1 gap-2.5">
          <div class="space-y-1">
            <span class="text-xs text-secondary">中文字体</span>
            <n-select
              :value="settings.chineseFont"
              :options="chineseFontSelectOptions"
              filterable
              tag
              size="small"
              data-testid="appearance-chinese-font"
              @update:value="onChineseFontChange"
            />
          </div>
          <div class="space-y-1">
            <span class="text-xs text-secondary">英文字体</span>
            <n-select
              :value="settings.englishFont"
              :options="englishFontSelectOptions"
              filterable
              tag
              size="small"
              data-testid="appearance-english-font"
              @update:value="onEnglishFontChange"
            />
          </div>
        </div>

        <n-collapse>
          <n-collapse-item title="高级：等宽字体" name="mono">
            <n-select
              :value="settings.monoFont"
              :options="monoFontSelectOptions"
              filterable
              tag
              size="small"
              data-testid="appearance-mono-font"
              @update:value="onMonoFontChange"
            />
          </n-collapse-item>
        </n-collapse>

        <div
          class="setting-inset px-3 py-2 text-sm"
          data-testid="appearance-font-preview"
          :style="{ fontSize: `${settings.fontSize}px`, fontFamily: previewFontStack }"
        >
          Aa 中文测试 English 123
        </div>
      </div>
    </n-card>

    <!-- 3. 会话主界面宽度 -->
    <n-card title="会话主界面宽度" size="small" data-testid="appearance-width-card">
      <div class="grid grid-cols-2 gap-2.5">
        <div
          role="button"
          tabindex="0"
          data-testid="chat-width-centered"
          class="setting-option p-2.5 cursor-pointer"
          :class="{ 'setting-option-active': settings.chatWidthMode === 'centered' }"
          @click="onChatWidthChange('centered')"
          @keydown.enter="onChatWidthChange('centered')"
        >
          <div class="flex justify-center py-1.5">
            <div class="w-full max-w-[120px] h-10 rounded border border-subtle flex items-center justify-center">
              <div class="w-3/5 h-6 rounded-sm" style="background-color: var(--accent-primary); opacity: 0.55;" />
            </div>
          </div>
          <div class="text-xs font-medium text-center">限定最大宽度 · 居中</div>
          <div class="text-[11px] text-secondary text-center mt-0.5">阅读友好</div>
        </div>
        <div
          role="button"
          tabindex="0"
          data-testid="chat-width-full"
          class="setting-option p-2.5 cursor-pointer"
          :class="{ 'setting-option-active': settings.chatWidthMode === 'full' }"
          @click="onChatWidthChange('full')"
          @keydown.enter="onChatWidthChange('full')"
        >
          <div class="flex justify-center py-1.5">
            <div class="w-full h-10 rounded border border-subtle flex items-center justify-center">
              <div class="w-full mx-2 h-6 rounded-sm" style="background-color: var(--accent-primary); opacity: 0.55;" />
            </div>
          </div>
          <div class="text-xs font-medium text-center">占满可用宽度</div>
          <div class="text-[11px] text-secondary text-center mt-0.5">宽屏利用</div>
        </div>
      </div>
    </n-card>

    <!-- 4. 页脚：重置 + 生效摘要 -->
    <div class="flex items-center justify-between gap-2 flex-wrap">
      <n-button secondary size="small" data-testid="appearance-reset-btn" @click="onReset">
        重置为默认
      </n-button>
      <span class="text-[11px] text-secondary font-mono break-all" data-testid="appearance-summary">
        {{ summaryText }}
      </span>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch } from 'vue';
import { useMessage } from 'naive-ui';
import { Moon, Sun } from 'lucide-vue-next';
import { useAppearanceStore } from '@/stores/appearanceStore';
import {
  THEME_COLOR_PRESETS,
  CHINESE_FONT_OPTIONS,
  ENGLISH_FONT_OPTIONS,
  buildFontStack,
  normalizeHexColor,
  type ChatWidthMode,
  type ThemeMode,
} from '@/theme/appearance';

const message = useMessage();
const appearanceStore = useAppearanceStore();

const settings = computed(() => appearanceStore.settings);

const fontSizeMarks: Record<number, string> = {
  12: '12',
  13: '13',
  14: '14',
  15: '15',
  16: '16',
};

const chineseFontSelectOptions = computed(() =>
  Array.from(new Set([settings.value.chineseFont, ...CHINESE_FONT_OPTIONS])).map((f) => ({
    label: f,
    value: f,
  }))
);
const englishFontSelectOptions = computed(() =>
  Array.from(new Set([settings.value.englishFont, ...ENGLISH_FONT_OPTIONS])).map((f) => ({
    label: f,
    value: f,
  }))
);
const monoFontSelectOptions = computed(() =>
  Array.from(new Set([settings.value.monoFont, 'Consolas', 'JetBrains Mono', 'Cascadia Mono'])).map((f) => ({
    label: f,
    value: f,
  }))
);

const customHex = ref(settings.value.themeColor);
watch(
  () => settings.value.themeColor,
  (val) => {
    customHex.value = val;
  }
);

const previewFontStack = computed(() => buildFontStack(settings.value));

const summaryText = computed(() => {
  const s = settings.value;
  const width = s.chatWidthMode === 'centered' ? '居中限宽' : '占满宽度';
  return `${s.fontSize}px · ${s.chineseFont} / ${s.englishFont} · ${s.themeColor} · ${width}`;
});

function onThemeModeChange(mode: ThemeMode): void {
  appearanceStore.setThemeMode(mode);
}

function onPresetColor(hex: string): void {
  appearanceStore.update({ themeColor: hex });
}

function onCustomHexApply(): void {
  const normalized = normalizeHexColor(customHex.value);
  if (!normalized) {
    message.warning('主题色格式非法，请输入如 #3b82f6 的 hex 颜色');
    customHex.value = settings.value.themeColor;
    return;
  }
  appearanceStore.update({ themeColor: normalized });
}

function onFontSizeChange(val: number): void {
  appearanceStore.update({ fontSize: val });
}

function onChineseFontChange(val: string): void {
  if (!val?.trim()) return;
  appearanceStore.update({ chineseFont: val.trim() });
}

function onEnglishFontChange(val: string): void {
  if (!val?.trim()) return;
  appearanceStore.update({ englishFont: val.trim() });
}

function onMonoFontChange(val: string): void {
  if (!val?.trim()) return;
  appearanceStore.update({ monoFont: val.trim() });
}

function onChatWidthChange(mode: ChatWidthMode): void {
  appearanceStore.update({ chatWidthMode: mode });
}

function onReset(): void {
  appearanceStore.reset();
  message.success('外观已重置为默认');
}
</script>
