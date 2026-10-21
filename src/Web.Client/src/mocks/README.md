# need21-F02：生产 mock 隔离区（仅 DEV&&?mock 经 @vite-ignore 动态加载，禁止静态 import 进正式包）

- `mock.ts`：`createBrowserMockBridge()` 装配入口（`MockTransport` 经 `shared/bridge/hostBridge.ts#loadMockApi` 动态注入）。
- `mockState.ts`：Mock 共享状态（`MockState` + `createMockState` + `buildMockPreview` + `fireMockAskUser`）。
- `mockSystem.ts`：系统域 Mock 方法（system/storage/network/provider/llm/run）。
- `mockWorkspace.ts`：业务域 Mock 方法（workspace/session/history/goal/sub/skill/usage）。
- 旧 `src/bridge/mock*.ts` 已物理搬入此处，原路径 0 文件残留；生产构建后 `scripts/assert-no-mock.mjs` 断言 wwwroot 无 `mockWorkspace`。
