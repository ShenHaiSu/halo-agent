using System.Runtime.CompilerServices;

// need22-P0：单测可达内部安全构件（BackupStore 配额/索引等），仅测试可见。
[assembly: InternalsVisibleTo("HaloAgent.Tests")]
