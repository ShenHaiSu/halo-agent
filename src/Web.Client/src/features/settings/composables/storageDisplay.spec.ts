import { describe, expect, it } from 'vitest';
import {
  canConfirmStorageMigration,
  computeDiskUsage,
  computeStorageHealth,
  describeStorageEvent,
  eventDisplayMeta,
  formatEventTime,
  formatLastWrite,
  healthLabel,
  healthTagType,
  isLogChannelEmpty,
  parseAttachmentReport,
} from './storageDisplay';

describe('磁盘占用率口径', () => {
  it('总量为 0 时按容量未知兜底，除零不出 NaN', () => {
    const view = computeDiskUsage(0, 0);
    expect(view.usedPercent).toBe(0);
    expect(view.capacityUnknown).toBe(true);
    expect(Number.isNaN(view.usedPercent)).toBe(false);
  });

  it('free=50、total=100 时占用率为 50%', () => {
    const view = computeDiskUsage(100, 50);
    expect(view.usedPercent).toBe(50);
    expect(view.capacityUnknown).toBe(false);
    expect(view.usedGbText).toBe('0.0');
  });

  it('free 大于 total 等异常数据占用率归零，不出现负数', () => {
    expect(computeDiskUsage(100, 200).usedPercent).toBe(0);
  });

  it('负数与非有限值被归零', () => {
    const view = computeDiskUsage(-10, Number.NaN);
    expect(view.usedPercent).toBe(0);
    expect(view.capacityUnknown).toBe(true);
  });
});

describe('最后写入时间格式化', () => {
  it('null / undefined → 尚未写入', () => {
    expect(formatLastWrite(null)).toBe('尚未写入');
    expect(formatLastWrite(undefined)).toBe('尚未写入');
  });

  it('非法字符串 → 未知', () => {
    expect(formatLastWrite('not-a-date')).toBe('未知');
  });

  it('合法 UTC → 本地化非空字符串', () => {
    const text = formatLastWrite('2026-11-19T10:00:00Z');
    expect(text.length).toBeGreaterThan(0);
    expect(text).not.toBe('未知');
  });
});

describe('存储健康度三态', () => {
  it('不可写 → readonly，文案权限受限 / error 标签', () => {
    const health = computeStorageHealth(false, false);
    expect(health).toBe('readonly');
    expect(healthTagType(health)).toBe('error');
    expect(healthLabel(health)).toBe('权限受限');
  });

  it('可写但处于兜底 → degraded', () => {
    const health = computeStorageHealth(true, true);
    expect(health).toBe('degraded');
    expect(healthTagType(health)).toBe('warning');
    expect(healthLabel(health)).toBe('降级兜底中');
  });

  it('两者正常 → healthy', () => {
    const health = computeStorageHealth(true, false);
    expect(health).toBe('healthy');
    expect(healthTagType(health)).toBe('success');
    expect(healthLabel(health)).toBe('健康');
  });
});

describe('空文件判定', () => {
  it('存在且 0 字节 → 空文件', () => {
    expect(isLogChannelEmpty(true, 0)).toBe(true);
  });

  it('不存在或非空 → 非 空文件', () => {
    expect(isLogChannelEmpty(false, 0)).toBe(false);
    expect(isLogChannelEmpty(true, 128)).toBe(false);
  });
});

describe('迁移确认按钮可用性', () => {
  const okValidation = {
    isValid: true,
    normalizedPath: 'D:\\Data',
    message: 'ok',
    availableFreeSpaceMb: 1024,
    requiredSpaceMb: 0,
    targetNonEmpty: false,
    riskCode: null,
  };

  it('空路径 / 校验中禁用', () => {
    expect(canConfirmStorageMigration('', false, okValidation)).toBe(false);
    expect(canConfirmStorageMigration('D:\\Data', true, okValidation)).toBe(false);
  });

  it('预检未返回时不阻塞（兼容老宿主）', () => {
    expect(canConfirmStorageMigration('D:\\Data', false, null)).toBe(true);
  });

  it('预检无效 / 空间不足风险码时禁用', () => {
    expect(canConfirmStorageMigration('D:\\Data', false, { ...okValidation, isValid: false })).toBe(false);
    expect(canConfirmStorageMigration('D:\\Data', false, { ...okValidation, riskCode: 'INSUFFICIENT_FOR_MIGRATE' })).toBe(false);
  });

  it('迁移所需空间大于可用空间时禁用', () => {
    expect(canConfirmStorageMigration('D:\\Data', false, { ...okValidation, requiredSpaceMb: 2048 })).toBe(false);
  });
});

describe('事件时间线映射', () => {
  it('路径变更映射为 success / 存储路径变更', () => {
    expect(eventDisplayMeta('storage:changed')).toEqual({ timelineType: 'success', title: '存储路径变更' });
  });

  it('迁移进度映射为 info / 迁移进度', () => {
    expect(eventDisplayMeta('storage:migration-progress')).toEqual({ timelineType: 'info', title: '迁移进度' });
  });

  it('摘要容错缺失字段', () => {
    expect(describeStorageEvent('storage:changed', null)).toBe('? -> ? (0 files)');
    expect(describeStorageEvent('storage:migration-progress', { percent: 50, message: '复制中' })).toBe('[50%] 复制中');
  });

  it('非法时间戳回退短横线', () => {
    expect(formatEventTime(0)).toBe('-');
    expect(formatEventTime(Number.NaN)).toBe('-');
  });
});

describe('附件清理报告解析', () => {
  it('解析 camelCase 报告字段', () => {
    const report = parseAttachmentReport(
      JSON.stringify({
        scannedFiles: 10,
        scannedBytes: 2048,
        expiredFiles: 3,
        expiredBytes: 512,
        deletedFiles: 0,
        dryRun: true,
        retentionDays: 30,
      })
    );
    expect(report.scannedFiles).toBe(10);
    expect(report.expiredFiles).toBe(3);
    expect(report.expiredBytes).toBe(512);
    expect(report.dryRun).toBe(true);
  });

  it('坏 JSON 回退空报告，不抛异常', () => {
    expect(parseAttachmentReport('{broken')).toEqual({
      scannedFiles: 0,
      scannedBytes: 0,
      expiredFiles: 0,
      expiredBytes: 0,
      deletedFiles: 0,
      dryRun: true,
      retentionDays: 0,
    });
  });
});
