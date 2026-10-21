import type { GlobalThemeOverrides } from 'naive-ui';

export const haloDarkThemeOverrides: GlobalThemeOverrides = {
  common: {
    fontFamily: '"Microsoft YaHei UI", "Microsoft YaHei", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
    fontFamilyMono: 'Consolas, "JetBrains Mono", "Courier New", monospace',
    primaryColor: '#3b82f6',          // 科技蓝主色（替代 Naive 默认绿色）
    primaryColorHover: '#60a5fa',
    primaryColorPressed: '#2563eb',
    primaryColorSuppl: '#1d4ed8',
    infoColor: '#38bdf8',
    successColor: '#22c55e',
    warningColor: '#f59e0b',
    errorColor: '#ef4444',
    bodyColor: '#0d1117',
    cardColor: '#161c24',
    modalColor: '#181f29',
    popoverColor: '#212a36',
    tableColor: '#161c24',
    inputColor: '#0f141b',
    borderColor: '#283241',
    borderRadius: '6px',              // 桌面端克制圆角
    borderRadiusSmall: '4px',
    fontSize: '13px',                 // 桌面端标准紧凑字号
    fontSizeMedium: '13px',
    fontSizeSmall: '12px',
    heightMedium: '30px',             // 紧凑型控件高度
    heightSmall: '26px',
  },
  Button: {
    fontWeight: '500',
    paddingMedium: '0 12px',
  },
  Input: {
    color: '#0d1117',
    colorFocus: '#090d12',
    border: '1px solid #283241',
    borderHover: '1px solid #3b82f6',
    borderFocus: '1px solid #3b82f6',
  },
  Modal: {
    boxShadow: '0 20px 50px rgba(0, 0, 0, 0.65)',
  },
  Tabs: {
    tabFontSizeMedium: '13px',
    tabGapMediumBar: '20px',
  },
};

export const haloLightThemeOverrides: GlobalThemeOverrides = {
  common: {
    fontFamily: '"Microsoft YaHei UI", "Microsoft YaHei", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
    fontFamilyMono: 'Consolas, "JetBrains Mono", "Courier New", monospace',
    primaryColor: '#2563eb',
    primaryColorHover: '#3b82f6',
    primaryColorPressed: '#1d4ed8',
    primaryColorSuppl: '#1e40af',
    infoColor: '#0284c7',
    successColor: '#16a34a',
    warningColor: '#d97706',
    errorColor: '#dc2626',
    bodyColor: '#f8fafc',
    cardColor: '#ffffff',
    modalColor: '#ffffff',
    popoverColor: '#ffffff',
    tableColor: '#ffffff',
    inputColor: '#f8fafc',
    borderColor: '#e2e8f0',
    borderRadius: '6px',
    borderRadiusSmall: '4px',
    fontSize: '13px',
    fontSizeMedium: '13px',
    fontSizeSmall: '12px',
    heightMedium: '30px',
    heightSmall: '26px',
  },
  Button: {
    fontWeight: '500',
    paddingMedium: '0 12px',
  },
  Tabs: {
    tabFontSizeMedium: '13px',
    tabGapMediumBar: '20px',
  },
};
