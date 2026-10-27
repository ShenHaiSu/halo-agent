# P1 验收证据索引

生成时间：2026-10-27 14:45:57
Git HEAD：4047baf03d762d9e1b68ae30c024fabb90e38b8d

## 文件清单
- A1-contract-tests.txt (322 bytes)
- A1-gen1.txt (40 bytes)
- A1-gen2.txt (40 bytes)
- A1-schema-inventory.txt (33 bytes)
- A1-typecheck.txt (113 bytes)
- A11-order.txt (338 bytes)
- A2-channel-tests.txt (338 bytes)
- A3-acl.txt (338 bytes)
- A4-backpressure.txt (2544 bytes)
- A5-concurrency.txt (338 bytes)
- A6-timeout.txt (338 bytes)
- A9-engine-tests-only.txt (335 bytes)
- INDEX.md (507 bytes)

## 自动化验收结论（本机实测）
- A-1 契约守卫：Contract.Tests 17/17 全绿；gen幂等（未变化，跳过写入）；typecheck 0错误；54方法/24事件/15码
- A-2 三通道一致：27/27 全绿（8用例x3实现 + ACL + JSON往返 + 管道名）
- A-3 ACL：Pipe_security_denies_everyone 通过
- A-4 背压：EventBusPressureTests 3/3 全绿（120帧/秒x10秒，内存<20MB，状态帧100%送达，丢弃计数>0）
- A-5 并发100请求配对：通过
- A-6 超时不泄漏：通过
- A-9 后端独立：Engine.* 无UI引用（Core.csproj命中仅为注释）；Server套件33/33全绿
- A-11 事件有序：Mixed_events有序性通过
- A-12 冻结：CHANGELOG [1] 冻结 + schema frozen 2025-01-01
- B-1 构建零警告：全解 Release x86 0警告0错误
- B-2 测试总时长：<60秒（Core约25秒，Server约3秒，其余秒级）

## 手工项（待整机联调）
- A-7/A-8 重连与状态恢复：EngineSupervisor已实现（退避200→5000ms/10次/排队重放/snapshot重建），待宿主接线（Program/MainForm切supervisor）后整机验证
- A-10 前端Mock：Transport/Mock/5场景已就绪且typecheck通过；待P5真实UI接入后走查6场景
