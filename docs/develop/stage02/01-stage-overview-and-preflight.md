# Stage 02 - 01：统一网络代理中枢、多 Provider/Model 体系与动态请求头引擎（前置检查与当期目标）

> **所属阶段**：Stage 02（网络底座与模型配置中心）
> **依赖前置**：Stage 01 已通过全部验收项（`S01-AC01` ~ `S01-AC06`）
> **直接覆盖核心需求**：需求 #1（统一代理）、需求 #7（多 Provider 与 Model 配置）、需求 #8（自定义 Header 与 `${session-hash}` 预设变量）

---

## 一、 前置检查（Preflight Checklist）

在进入 Stage 02 研发前，需确认以下前置条件已就绪：

1. **Stage 01 基线状态检查**：
   * [x] `StoragePathManager` 已可正常定位 `<DataRootPath>/config/` 目录。
   * [x] `AtomicFileWriter` 原子 JSON 落盘工具已就绪。
   * [x] `HaloLogger.App` 与 `HaloLogger.Llm` 日志通道可正常写入。
   * [x] `Program.cs` 中已启用兼容 Win7 SP1 的 `SecurityProtocolType.Tls12`。
2. **NuGet 依赖确认**：
   * [x] `System.Security.Cryptography.ProtectedData` (`6.0.0`) 已在 `Host.Desktop.csproj` 中引用（用于跨 Win7/10/11 的 API Key 本机加密）。
   * [x] `.NET 6.0` 原生 `SocketsHttpHandler` 支持 HTTP/1.1、HTTP/2 以及 `http://`、`https://`、`socks4://`、`socks5://` 代理协议，无需额外引入第三方 Socks5 代理库。

---

## 二、 当期阶段目标（Stage 02 Objectives）

1. **构建全局统一的网络请求代理中枢（`ProxyHub` & `HaloHttpClientFactory`）**：
   * 所有对外的 HTTP/HTTPS 请求（LLM 推理、模型列表拉取、连通性探测）**强制收敛到唯一入口 `HaloHttpClientFactory`**，严禁在业务代码中私自 `new HttpClient()`。
   * 全局代理支持三种模式：
     * **`System`（自动检测当前系统代理）**：自动读取 Windows WinINet 注册表与系统环境代理配置，并实时展示检测到的系统代理地址。
     * **`Custom`（手动配置代理）**：支持手动输入代理协议（HTTP / HTTPS / SOCKS5）、主机、端口、可选账号密码认证以及 Bypass 忽略列表。
     * **`None`（不使用代理/强制直连）**：显式禁用代理（`UseProxy = false`），即使当前操作系统开启了全局系统代理也强制直连目标服务器。
2. **构建多 Provider 与多 Model 精细化配置体系**：
   * 支持添加、编辑、复制、启停、删除任意数量的大模型服务商（如 OpenAI、DeepSeek、OpenRouter、硅基流动、本地 Ollama/vLLM 等）。
   * 每个 Provider 独立维护：
     * `baseUrl`（自动规范化末尾斜杠与版本路径）
     * `apiKey`（落盘采用 Windows DPAPI 加密，前端展示脱敏掩码）
     * `proxyOverride`（默认 **`Inherit` 继承全局代理配置**，也可单独为某 Provider 指定 `None` 直连或 `Custom` 独立代理，例如本地 Ollama 强制 `None` 直连而云端 Provider 走代理）
     * `customHeaders`（自定义请求头开关与模板列表）
     * `models`（该 Provider 下的模型列表）
   * 每个 Model 独立维护：
     * `id`（API 实际调用的 model 名称，如 `gpt-4o`、`o3-mini`、`deepseek-chat`）
     * `protocol`（枚举锁定：**`chat`** 对应 `/v1/chat/completions`，**`resp`** 对应 `/v1/responses`）
     * `maxContextTokens`（最大上下文窗口，如 `128000`）
     * `maxOutputTokens`（最大输出 Token，如 `4096` / `8192` / `16384`）
3. **实现自定义请求头（Custom Headers）与预设变量解析引擎（`HeaderVariableResolver`）**：
   * 支持为每个 Provider 开启或关闭自定义请求头，并配置多组 Key-Value 模板。
   * 内置 **`${session-hash}`** 等核心预设变量，在每次发起请求时根据当前会话上下文动态注入，帮助上游网关实现高命中率的 Prompt Caching 黏性路由与计费归因。

---

## 三、 Stage 02 交付产物清单

| 模块分类 | 文件路径 | 核心职责说明 |
| :--- | :--- | :--- |
| **领域模型** | `src/Host.Desktop/Core/Models/ProxySettings.cs` | 全局代理与 Provider 代理覆盖模型定义 |
| **领域模型** | `src/Host.Desktop/Core/Models/ProviderModels.cs` | `ProviderConfig`、`ModelConfig` (`chat`/`resp`)、`CustomHeaderEntry` |
| **安全加密** | `src/Host.Desktop/Infrastructure/Storage/SecretProtector.cs` | 基于 Windows DPAPI (`DataProtectionScope.CurrentUser`) 的密钥加解密 |
| **网络中枢** | `src/Host.Desktop/Infrastructure/Network/SystemProxyInspector.cs` | 读取 Win7/10/11 注册表与 `WebRequest.GetSystemWebProxy()` 实时检测系统代理 |
| **网络中枢** | `src/Host.Desktop/Infrastructure/Network/HaloHttpClientFactory.cs` | 统一网络请求入口，按生效代理指纹池化管理 `SocketsHttpHandler` |
| **Header引擎** | `src/Host.Desktop/Infrastructure/Network/HeaderVariableResolver.cs` | 解析 `${session-hash}`、`${session-id}`、`${workspace-hash}` 等预设变量 |
| **配置服务** | `src/Host.Desktop/Services/ProviderConfigService.cs` | Provider/Model CRUD、配置持久化与连通性探测 |
| **前端状态** | `src/Web.Client/src/stores/configStore.ts` | 全局代理与多 Provider/Model 前端响应式状态管理 |
| **前端界面** | `src/Web.Client/src/views/settings/NetworkProxySettings.vue` | 全局代理配置、系统代理实时检测状态与一键测速面板 |
| **前端界面** | `src/Web.Client/src/views/settings/ProviderManagerView.vue` | 多 Provider、Model 协议/Token 参数、自定义 Header 变量胶囊配置界面 |

---

## 四、 本阶段文档导航

* 下一篇：[`02-unified-proxy-hub.md`](./02-unified-proxy-hub.md) —— 详细设计统一网络代理中枢（系统代理探测、手动配置、强制直连、Provider 继承与连接池）。
* 第三篇：[`03-provider-model-and-header-engine.md`](./03-provider-model-and-header-engine.md) —— 详细设计多 Provider/Model 配置结构、DPAPI 加密及 `${session-hash}` 自定义请求头模板引擎。
* 第四篇：[`04-settings-ui-and-acceptance.md`](./04-settings-ui-and-acceptance.md) —— 详细设计前端配置中心交互与 Stage 02 验收标准。
