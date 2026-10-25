using Xunit;

namespace HaloAgent.Tests;

// 并行隔离：TokenBudgetService.ImageTokensPerImage 是进程级静态可变，
// Stage09ImageEstimateTests / Stage09StablePromptTests 以 set-1000/finally-restore 方式复用。
// 并行交错读写会导致估算 delta flake，故这两个类共享本串行集合
// （集合内串行、不与其它集合并行）。其它 50+ 类不受影响，保持默认并行。
// 注：xunit 2.4.2 的 CollectionDefinition 属性名为 DisableParallelization
//（DisableTestParallelization 只在 assembly 级 CollectionBehavior 上）。
[CollectionDefinition("Serial-ImageCost", DisableParallelization = true)]
public sealed class SerialImageCostCollection { }

// 注：HaloLogger 是进程静态单例（Initialize 切全局目录 + 全局 Channel），
// Stage01FoundationTests 的日志断言/读文件在并行下会被其它 Initialize 切走目录。
// 故 Stage01 独占本串行集合（集合内串行、不与其它集合并行）。
[CollectionDefinition("Serial-HaloLogger", DisableParallelization = true)]
public sealed class SerialHaloLoggerCollection { }

// 注：BackupStore.GlobalQuotaBytes 是进程静态可调配额，
// Stage15P0AttackSamples.A5_Quota_Rejected 会设为 10 字节，并行下污染其它备份用例。
// 故该类独占本串行集合（集合内串行、不与其它集合并行）。
[CollectionDefinition("Serial-BackupQuota", DisableParallelization = true)]
public sealed class SerialBackupQuotaCollection { }

// 注：PythonAvailabilitySnapshot.OverrideForTests 是进程级静态注入（Current 全局可见），
// Stage17PythonFallbackTests 多个用例 Override/Clear 交错在并行下会互相覆盖。
// 为保断言不 flake，本类整体串行（集合内串行、不与其它集合并行），
// 与 Serial-ImageCost 的进程静态隔离策略一致。
[CollectionDefinition("Serial-PythonSnapshot", DisableParallelization = true)]
public sealed class SerialPythonSnapshotCollection { }
