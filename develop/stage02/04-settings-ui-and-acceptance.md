# Stage 02 - 04：基于 Naive UI + Tailwind 的配置中心交互设计及 Stage 02 验收标准

> **所属阶段**：Stage 02
> **核心目标**：基于 **Naive UI 全量组件 + Tailwind CSS v3.4** 打造桌面级高密度、零歧义的“全局网络代理”与“多 Provider / Model / 自定义 Header”配置中心，并确立 Stage 02 验收基准。

---

## 一、 配置中心整体容器与布局架构（`SettingsCenterModal.vue`）

配置中心采用 **桌面应用经典的左导右表双栏模态窗**（尺寸：`width: 960px; height: 680px`），底层由 `<n-modal>` + `<n-tabs placement="left">` 构建：

```text
+---------------------------------------------------------------------------------------------------+
| [SVG: Settings] Halo-Agent 系统配置中心                                                     [X]    |
+----------------------+----------------------------------------------------------------------------+
| 左侧导航栏 (180px)   | 右侧主配置区 (Flex-1, 内部独立 NScrollbar 滚动)                             |
| (<n-tabs left>)      |                                                                            |
|                      |  [当前激活页内容：模型服务商 / 网络代理 / 存储与日志 / 终端环境 / 关于]     |
| * 模型与服务商       |                                                                            |
|   网络与代理中枢     |                                                                            |
|   存储与日志路径     |                                                                            |
|   命令执行与安全     |                                                                            |
|   系统诊断与关于     |                                                                            |
+----------------------+----------------------------------------------------------------------------+
```

---

## 二、 全局网络代理设置页交互与组件设计（`NetworkProxySettings.vue`）

### 1. 顶部：系统代理实时侦测横幅（System Proxy Live Banner）
* 使用 Tailwind 渐变边框卡片包裹 `<n-space justify="space-between" align="center">`：
  * 左侧：Lucide `Radar` 图标 + 实时状态文案：
    * 若系统开启了代理：显示 `<n-tag type="info" size="small">系统代理已启用</n-tag>` + 等宽字体显示检测到的地址（如 `http://127.0.0.1:7890`）及 Bypass 规则。
    * 若系统未开启代理：显示 `<n-tag type="default" size="small">系统未开启代理 (DIRECT)</n-tag>`。
  * 右侧：`<n-button size="small" secondary @click="refreshSystemProxy">`（带 `RefreshCw` 旋转加载动画）：`重新检测系统代理`。

### 2. 中部：全局代理三态选择矩阵（3-Card Radio Matrix）
摒弃简陋的普通单选圆点，采用 **Tailwind 定制的三列可点击交互卡片**（配合 `<n-radio>` 状态绑定，选中态高亮 `border-blue-500 bg-blue-500/10`）：

| 卡片 1：`System`（默认推荐） | 卡片 2：`Custom`（手动指定） | 卡片 3：`None`（强制直连） |
| :--- | :--- | :--- |
| **图标**：`Globe` (Lucide)<br>**标题**：自动跟随系统代理<br>**徽标**：`<n-tag type="success" size="tiny">推荐</n-tag>`<br>**说明**：实时读取 Windows Internet 代理设置，切换系统代理软件无需重启应用。 | **图标**：`Sliders` (Lucide)<br>**标题**：手动配置固定代理<br>**说明**：独立指定 HTTP / HTTPS / SOCKS5 代理服务器、端口及认证凭据。 | **图标**：`ZapOff` (Lucide)<br>**标题**：不使用任何代理<br>**说明**：强制底层 `SocketsHttpHandler` 绕过系统全局代理直接连接目标服务器。 |

* **当选中 `Custom`（手动配置）时**，通过 `<n-collapse-transition>` 平滑展开 `<n-form label-placement="left" label-width="100px" size="small">`：
  * **代理协议与地址**：`<n-input-group>` 组合：
    * 左侧 `<n-select style="width: 110px" :options="['http://', 'https://', 'socks5://']" />`
    * 中间 `<n-input placeholder="主机地址，如 127.0.0.1" />`
    * 右侧 `<n-input-number :min="1" :max="65535" placeholder="端口，如 7890" style="width: 120px" />`
  * **本地地址绕过**：`<n-switch>` `绕过本地地址 (localhost / 127.0.0.1 / 内网段)` + `<n-input placeholder="自定义绕过列表，分号分隔，如 *.internal.corp;192.168.*" />`
  * **代理身份认证**：`<n-switch v-model:value="enableProxyAuth" />`，开启后双列展示 `<n-input placeholder="用户名" />` 与 `<n-input type="password" show-password-on="click" placeholder="密码 (DPAPI 加密存储)" />`。

### 3. 底部：一键代理连通性探测舱（Connectivity Tester）
* 置于底部固定 `<n-card size="small" embedded>` 内：
  * `<n-input-group>`：`<n-input v-model:value="testUrl" placeholder="测试目标 URL，默认 https://api.openai.com/v1/models" />` + `<n-button type="primary" :loading="testing" @click="runProxyTest">测试代理连通性</n-button>`。
  * 测试结果反馈条：实时展示 `<n-tag type="success">HTTP 200 / 401 (网络已通) · 耗时 186ms · 实际出口: http://127.0.0.1:7890</n-tag>` 或详细异常诊断原因（如 `TLS 握手失败` / `代理端口拒绝连接`）。

---

## 三、 多 Provider / 多 Model 与自定义 Header 交互设计（`ProviderManagerView.vue`）

在“模型与服务商”页签内，采用 **`<n-split :default-size="0.28">` 主从双栏布局**：

```text
+----------------------------+----------------------------------------------------------------------+
| 左栏：Provider 列表 (28%)  | 右栏：当前选中 Provider 详情配置区 (72%)                              |
|                            |                                                                      |
| [+ 新增 Provider (NButton)]| [Provider 名称: DeepSeek Official]          [启用开关 NSwitch] [删除] |
| [搜索服务商...   (NInput)] | -------------------------------------------------------------------- |
| -------------------------- | 1. 连接与认证配置 (NForm)                                            |
| * [●] OpenAI Official      |    Base URL: [ https://api.deepseek.com               ] [连通性测试] |
|       3 Models · SystemPrx |    端点预览: Chat -> /v1/chat/completions | Resp -> /v1/responses    |
|                            |    API Key : [ ********************************** [眼] ] (DPAPI加密) |
| * [●] DeepSeek (当前选中)  |    代理策略: [ 继承全局代理配置 (System: 127.0.0.1:7890)          v] |
|       2 Models · Header[1] | -------------------------------------------------------------------- |
|                            | 2. 自定义请求头与预设变量 (Custom Headers)           [x] 启用 Header |
| * [○] Local Ollama         |    快捷插入变量: [+ ${session-hash} (推荐)] [+ ${session-id}]        |
|       1 Model  · 直连(None)|                  [+ ${workspace-hash}] [+ ${model-id}] [+ ${req-id}] |
|                            |    [Header Key: X-Session-Hash ] = [Value: halo_${session-hash} ] [x]|
|                            |    实时预览: X-Session-Hash: halo_a94f8c2d1e07b35a                   |
|                            | -------------------------------------------------------------------- |
|                            | 3. 模型列表与协议/Token配额矩阵                      [+ 添加模型]     |
|                            |    +------------------+--------------+-------------+---------------+ |
|                            |    | 模型 ID          | 协议类型     | 最大上下文  | 最大输出Token | |
|                            |    +------------------+--------------+-------------+---------------+ |
|                            |    | deepseek-chat    | [CHAT|resp]  | 128,000     | 8,192    [编] | |
|                            |    | deepseek-reasoner| [CHAT|resp]  | 128,000     | 16,384   [编] | |
|                            |    +------------------+--------------+-------------+---------------+ |
+----------------------------+----------------------------------------------------------------------+
```

### 1. Provider 连接与代理级联配置
* **Base URL 智能端点回显**：当用户输入 `https://api.openai.com` 时，输入框下方实时以小号等宽灰字渲染自动归一化后的双协议完整地址：
  * `Chat 协议地址：https://api.openai.com/v1/chat/completions`
  * `Resp 协议地址：https://api.openai.com/v1/responses`
* **Provider 代理策略下拉框（`<n-select>`）**：
  * `Inherit`：`继承全局代理设置（当前生效：系统代理 127.0.0.1:7890）` *(默认)*
  * `None`：`不使用代理（强制直连，适合内网 vLLM / Ollama）`
  * `Custom`：`为此 Provider 单独指定代理服务器`（选中后在下方内嵌展开独立代理主机与端口输入框）

### 2. 自定义请求头与 `${session-hash}` 预设变量胶囊交互（核心亮点）
* **启用开关**：卡片标题栏右侧配置 `<n-switch v-model:value="provider.enableCustomHeaders" />`。
* **预设变量胶囊工具栏（Variable Quick-Insert Bar）**：
  * 使用 `<n-tooltip>` 包裹一组高辨识度 Tailwind 胶囊按钮：
    * **`${session-hash}`**（特殊高亮蓝紫边框 + `Sparkles` SVG 图标）：悬停提示 *“当前会话固定 16 位 SHA-256 哈希值。部分网关/厂商要求携带会话哈希以实现 Sticky Routing 与 Prompt Cache 命中，大幅降低成本”*。
    * 其余变量胶囊：`${session-id}`、`${workspace-hash}`、`${model-id}`、`${provider-id}`、`${request-id}`、`${timestamp-ms}`、`${os-version}`。
  * **光标精准插入交互**：记录当前最后聚焦的 Header Value `<n-input>` 实例与光标 `selectionStart` 位置；点击任意变量胶囊时，自动将该变量插入到光标处，无需用户手动拼写。
* **动态键值对编辑器（`<n-dynamic-input>`）与实时编译预览**：
  * 每行包含：`启用勾选框 (NCheckbox)` + `Header 名称 (NAutoComplete，提供 X-Session-Hash / X-Client-Session-Id / HTTP-Referer 等常用补全)` + `Header 值模板 (NInput)` + `删除按钮`。
  * **底部实时解析预览框**：以深色代码块样式实时展示代入当前激活会话后的真实 HTTP Header 报文（例如 `X-Session-Hash: a94f8c2d1e07b35a`），让用户配置完立刻确认无误。

### 3. Model 列表与（协议 / 最大上下文 / 最大输出 Token）配置交互
* **模型表格与行内/抽屉编辑**：
  * 支持点击 `[+ 添加模型]` 打开 `<n-modal preset="card" title="配置模型参数" style="width: 520px">`，也支持在列表行内直接快速切换协议：
  * **协议选择器（`<n-radio-group size="small">` 分段按钮）**：
    * `<n-radio-button value="chat">Chat 协议 (/v1/chat/completions)</n-radio-button>`
    * `<n-radio-button value="resp">Responses 协议 (/v1/responses)</n-radio-button>`
  * **最大上下文 (`maxContextTokens`) 组合控件**：
    * `<n-input-number :step="1024" :min="2048" :max="2097152" />`
    * 下方紧跟快捷预设胶囊组（点击一键填入）：`[32K (32,768)]` `[64K (65,536)]` `[128K (131,072)]` `[200K (204,800)]` `[1M (1,048,576)]`。
  * **最大输出 Token (`maxOutputTokens`) 组合控件**：
    * `<n-input-number :step="512" :min="256" :max="131072" />`
    * 下方紧跟快捷预设胶囊组：`[4K (4,096)]` `[8K (8,192)]` `[16K (16,384)]` `[32K (32,768)]` `[64K (65,536)]`。
    * 实时校验防呆：若用户输入 `maxOutputTokens >= maxContextTokens`，`<n-form-item>` 立即给出红色校验提示：“最大输出 Token 不能大于或等于最大上下文窗口”。

---

## 四、 Stage 02 验收标准（Acceptance Criteria）

| 验收编号 | 验收维度 | 具体测试步骤 | 预期通过标准 |
| :--- | :--- | :--- | :--- |
| **S02-AC01** | **系统代理自动检测** | 1. 在系统中开启代理软件（如端口 `7890`），点击前端“重新检测系统代理”<br>2. 关闭系统代理软件后再次点击刷新 | 1. 准确识别并显示 `已启用 (http://127.0.0.1:7890)`<br>2. 关闭后准确显示 `系统未开启代理 (DIRECT)` |
| **S02-AC02** | **三种全局代理与 Provider 级联覆盖** | 1. 系统开启代理，但全局模式设为 `None`（不使用代理）发起探测<br>2. 全局设为 `System`，某本地 Provider 设为 `None` | 1. 探测结果中 `effectiveProxyUsed` 显示为 `DIRECT`，确认已绕过系统代理<br>2. 本地 Provider 走直连，云端默认 `Inherit` 的 Provider 走系统代理 |
| **S02-AC03** | **多 Provider & Model 协议与 Token 持久化** | 创建 2 个 Provider，分别添加 `chat` 协议模型（上下文 `128000`，输出 `4096`）和 `resp` 协议模型（上下文 `200000`，输出 `16384`），重启应用 | 1. `<DataRootPath>/config/providers.json` 中 `apiKeyEncrypted` 为 DPAPI 密文（非明文）<br>2. 重启后所有 Provider、Model 的协议类型、最大上下文、最大输出 Token 完整还原 |
| **S02-AC04** | **自定义 Header 与 `${session-hash}` 变量解析** | 在 Provider 中开启自定义请求头，点击 `${session-hash}` 胶囊插入 `X-Session-Hash: ${session-hash}` 与 `X-Req-Id: ${request-id}`，向本地回显测试端点发起 2 次同一会话请求和 1 次新会话请求 | 1. 同一会话的 2 次请求收到的 `X-Session-Hash` 完全一致（16 位小写 Hex），`X-Req-Id` 每次不同<br>2. 切换到新会话后，`X-Session-Hash` 变为新的固定哈希值 |
