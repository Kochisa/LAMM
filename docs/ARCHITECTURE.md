# Local AI Model Manager — 架构与项目结构

> 目标：Windows 11 桌面端的**本地模型生命周期管理器 + OpenAI 兼容 API 网关**。
> 不使用 Ollama。首个推理引擎为 **llama.cpp / llama-server**，引擎可独立替换与升级。

---

## 1. 设计原则

| 原则 | 落地方式 |
|---|---|
| 启动只起管理器 | `AppRuntime.StartAsync()` 只启动网关与监控；`LifecycleSettings.PreloadOnStartup` 被 `Normalize()` 强制为 `false` |
| 模型按需加载 | 网关收到 `POST /v1/chat/completions` 才走 `IModelLifecycleManager.AcquireAsync` |
| 引擎可替换 | `IBackendAdapter` 是唯一接缝；模型记录只引用 `EngineId`，不引用任何可执行文件 |
| 参数不写死 | 参数目录（`ParameterCatalog`）只是候选集，**实际可用项由所安装引擎的 `--help` 输出决定** |
| UI 不碰引擎 | UI → `AppServices` → `AppRuntime` → `ModelLifecycleManager` → `IBackendAdapter` → 子进程 |
| 默认安全 | 网关默认 `127.0.0.1`；引擎端口**永远**只绑定 `127.0.0.1`；LAN 必须显式开启且强制要求 API Key |
| 日志不落盘 | `InMemoryLogStore` 是环形缓冲；只有「运行日志 → 保存」会写文件 |
| 不留孤儿进程 | Windows Job Object（`KILL_ON_JOB_CLOSE`）+ 优雅优先的停止流程 |

---

## 2. 分层结构

```
┌──────────────────────────────────────────────────────────────┐
│  UI  (WPF, net10.0-windows)                                  │
│  MainWindow + 12 个独立页面 + 托盘                            │
│  ViewModels 只依赖 AppServices                                │
└───────────────┬──────────────────────────────────────────────┘
                │ AppServices（UI 门面，缓存能力探测结果）
┌───────────────▼──────────────────────────────────────────────┐
│  Model Manager Core  (LocalAIModelManager.Core)              │
│                                                              │
│  Gateway        GatewayHost / 鉴权中间件 / SSE 代理            │
│  Lifecycle      ModelLifecycleManager / 空闲卸载 / LRU 驱逐    │
│  Configuration  SettingsService / ModelRegistry / JSON 存储   │
│  Resources      SystemResourceMonitor (nvidia-smi + CPU)      │
│  Logging        InMemoryLogStore / LogRedactor                │
│  Processes      BackendProcess / ProcessInventory / JobObject │
│  Backends       IBackendAdapter  ◀── 唯一引擎接缝              │
└───────────────┬──────────────────────────────────────────────┘
                │ IBackendAdapter.BuildLaunchPlan / StartProcess / WaitUntilReady
┌───────────────▼──────────────────────────────────────────────┐
│  Backend Adapter                                             │
│  LlamaCppBackendAdapter        产物：llama-server.exe         │
│  GenericOpenAiCompatibleAdapter 未来：vLLM / TRT-LLM / 远程    │
└───────────────┬──────────────────────────────────────────────┘
                │ CreateProcess（stdout/stderr 重定向）
┌───────────────▼──────────────────────────────────────────────┐
│  llama-server  每个模型一个受管子进程，绑定 127.0.0.1:<内部端口> │
└──────────────────────────────────────────────────────────────┘
```

**关键约束**：UI 层不引用 `System.Diagnostics.Process`，不拼接命令行，不知道内部端口是哪一个 —— 它只调用 `AppServices` / `IModelLifecycleManager`。

---

## 3. 项目结构

```
LocalAIModelManager/
├─ LocalAIModelManager.sln
├─ Directory.Build.props            # net10.0-windows / nullable / 版本号
├─ NuGet.config                     # 清空包源：全离线构建（零第三方依赖）
├─ docs/
│  ├─ ARCHITECTURE.md               # 本文
│  └─ VERIFICATION.md               # 验收证据
├─ scripts/
│  ├─ env.ps1                       # 共享环境（DOTNET_CLI_HOME、单节点 MSBuild）
│  ├─ build.ps1                     # 构建整个解决方案
│  ├─ run.ps1                        # 启动桌面应用
│  ├─ acceptance.ps1                # 24 项内核 + 验收测试
│  └─ app-smoke.ps1                 # 26 项「真实可执行文件」端到端冒烟
├─ src/
│  ├─ LocalAIModelManager.Core/     # 无 UI 依赖的核心
│  │  ├─ Models/                    # ModelDefinition / EngineDefinition / ModelState
│  │  ├─ Configuration/             # AppSettings（8 个分区）/ ModelRegistry / JSON 存储
│  │  ├─ Backends/                  # IBackendAdapter / 能力探测 / 参数目录 / 引擎发现
│  │  ├─ Processes/                 # BackendProcess / ProcessInventory / JobObject / 端口分配
│  │  ├─ Lifecycle/                 # ModelLifecycleManager（状态机 + 驱逐）
│  │  ├─ Gateway/                   # Kestrel 网关 / 鉴权 / SSE 代理
│  │  ├─ Resources/                 # nvidia-smi + GetSystemTimes 采样
│  │  ├─ Logging/                   # 内存环形日志 + 脱敏
│  │  └─ Runtime/                   # AppRuntime（组合根）/ RuntimeStatusSnapshot / 开机启动
│  ├─ LocalAIModelManager.App/      # WPF 桌面应用
│  │  ├─ App.xaml(.cs)              # 启动顺序、单实例、托盘、退出
│  │  ├─ MainWindow.xaml(.cs)       # 导航外壳 + 状态栏
│  │  ├─ Infrastructure/            # ObservableObject / Command / 转换器 / 页面目录
│  │  ├─ Services/                  # AppServices / DialogService / TrayIconService / ThemeManager
│  │  ├─ ViewModels/                # Models / RuntimeStatus / RuntimeLogs / ApiIntegration / Settings
│  │  ├─ Views/                     # 对应 XAML
│  │  └─ Themes/                    # Dark / Light / Controls
│  ├─ LocalAIModelManager.MockEngine/      # 离线 OpenAI 兼容「假 llama-server」
│  └─ LocalAIModelManager.ControlHelper/   # 一次性助手：给引擎送 CTRL_BREAK
└─ tests/
   └─ LocalAIModelManager.Tests/    # 自带断言框架的离线测试 + 验收工具
```

**为什么零第三方依赖**：目标机器可能没有 NuGet 源。整个解决方案只使用框架引用
（`Microsoft.NETCore.App` / `Microsoft.WindowsDesktop.App` / `Microsoft.AspNetCore.App`），
因此 `NuGet.config` 直接 `<clear/>` 掉所有包源，restore 完全离线且确定。

---

## 4. 核心概念

### 4.1 引擎（`EngineDefinition`）

引擎 = 适配器种类 + 可执行文件路径 + 少量 CLI 约定。**只有这些**，所以「升级 llama.cpp」
= 换一个 `ExecutablePath` 或新增一条引擎记录，模型改指过去即可，无需改代码。

```jsonc
{
  "id": "llamacpp-b7200",
  "name": "llama.cpp (b7200)",
  "adapterKind": "llamacpp",              // 决定用哪个 IBackendAdapter
  "executablePath": "D:\\llama\\b7200\\llama-server.exe",
  "extraArguments": { "--flash-attn": "" },
  "startupTimeoutSeconds": 180,
  "shutdownGraceSeconds": 10
}
```

`EngineDiscovery` 会扫描配置目录、程序目录、`%LOCALAPPDATA%`、`PATH` 等位置自动发现
`llama-server.exe`，因此「把新版解压到任意目录」即可被识别。

### 4.2 能力探测（决定参数集合）

```
IBackendAdapter.Inspect(engine)
   → 运行 llama-server --version （8s 超时）
   → 运行 llama-server --help    （20s 超时）
   → LlamaHelpParser.Parse()
        · 抽取版本号
        · 抽取 help 中出现的所有 flag
        · 与 ParameterCatalog 求交集 → 「本构建支持」的参数
        · help 中出现但目录里没有的 flag → DetectedOnly 参数（一样可用）
   → 按 (engineId, executablePath, 文件指纹) 缓存；二进制被替换后自动失效
```

结论：**UI 只会展示该二进制真正支持的参数**；升级 llama.cpp 后新参数自动出现，
旧构建不支持的参数在界面里置灰并标注「该引擎不支持」。

### 4.3 模型（`ModelDefinition`）

```jsonc
{
  "id": "qwen3-8b",                  // API 里的 model 字段，创建后不可改
  "displayName": "Qwen3 8B",
  "filePath": "D:\\models\\qwen3-8b-q4.gguf",
  "engineId": "llamacpp-b7200",
  "enabled": true,
  "autoLoad": false,                 // 永久为 false
  "parameters": { "--ctx-size": "8192", "--n-gpu-layers": "99" }
}
```

参数解析优先级：`ModelParameters.Defaults` → `ModelParameters.ExtraArguments` → **模型自己的参数**（最高）。
`--model / --host / --port / --alias / --no-webui` 属于「管理器托管」，模型无法覆盖。

### 4.4 生命周期状态机

```
Standby ──AcquireAsync──► Starting ──► Loading ──► Ready
   ▲                                                 │
   │                             空闲超时 / LRU 驱逐 / 手动停止
   └────────────── Stopping ◄────────────────────────┘
任意阶段失败 ──► Failed（下次请求会重试）
```

- 并发请求：同一模型只有一个「加载者」，其余请求等待同一个 `TaskCompletionSource`。
- `ModelLease`：请求持有期间 `ActiveRequests++`，因此不会被空闲卸载；释放时刷新
  `LastUsedUtc`（一次长请求结束后才开始计时）。
- 空闲扫描：`IdleUnloadAsync()` 每 5 秒执行，卸载「空闲超过 `IdleTimeoutSeconds`
  且 `ActiveRequests == 0`」的模型 → 结束进程 → 释放端口 → 记录 `Graceful/ForcedKill`。

### 4.5 容量与显存驱逐

`EnsureCapacityAsync(incomingModel)` 在加载前执行：

1. 若 `Ready` 数量 ≥ `MaxLoadedModels` → 按 `LastUsedUtc` 升序驱逐空闲模型；
   若无空闲可驱逐 → 抛 `ModelCapacityException` → 网关返回 **503 `no_capacity`**。
2. 若开启显存驱逐且 `nvidia-smi` 可用且空闲显存 < `MinFreeVramMiB` → 继续驱逐
   LRU 空闲模型并重新采样；若无空闲可驱逐 → 记录警告后仍然尝试加载（让引擎给出真实错误）。

---

## 5. 请求流程（`POST /v1/chat/completions`）

```
客户端 ──Bearer API_KEY──► Kestrel
   │
   ├─ 异常边界中间件
   ├─ 鉴权中间件（/health 除外；固定时间比较；记录时脱敏）
   ├─ 读取请求体 → 解析 model / stream
   ├─ 未注册模型 → 404 model_not_found
   ├─ AcquireAsync(modelId)
   │     ├─ 已在 Ready → 直接返回租约
   │     └─ 否则：驱逐 → 分配内部端口 → BuildLaunchPlan → 启动子进程
   │              → 轮询 /health（404 时退化为 TCP 连通性）→ Ready
   ├─ 转发到 http://127.0.0.1:<内部端口>/v1/chat/completions（body 原样透传）
   ├─ text/event-stream → DisableBuffering + 逐块 Flush（真流式）
   └─ 释放租约（刷新 LastUsedUtc）
```

超时策略：`HttpClient.Timeout = 无限`，改由「请求 Aborted + 配置超时」的
`CancellationTokenSource` 控制，避免长流式响应被客户端超时切断。

---

## 6. 进程管理

| 关注点 | 实现 |
|---|---|
| 追踪 | `BackendProcess`：PID / 模型 / 引擎 / 内部端口 / 状态 / 启动时间 / 工作集 / 停止方式 |
| 聚合 | `ProcessInventory`：活动进程 + 已退出历史（供运行状态页展示） |
| 优雅停止 | ① 关闭 stdin；② 由 `ControlHelper` 一次性进程 `AttachConsole(pid)` + `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT)` |
| 强制停止 | 超过 `ShutdownGraceSeconds` 后 `Kill(entireProcessTree: true)` |
| 防孤儿 | 所有子进程加入 Job Object（`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`）；管理器崩溃/被杀时内核一并回收 |
| 内部端口 | `PortAllocator` 在配置区间内找空闲端口，绑定前先 `TcpListener` 试绑；只绑 `127.0.0.1` |

**为什么需要 `ControlHelper`**：`GenerateConsoleCtrlEvent` 只能作用到「与调用者共享控制台」的进程，
所以调用者必须先 `AttachConsole`。而无窗口的 WPF 进程一旦附加到引擎控制台，就会**收到自己发出的
CTRL_BREAK 并被终止**（实测退出码 `0xC000013A`）。把这一步放进一次性助手进程后，即使助手被信号
杀死也没有损失，管理器完全不受影响。

---

## 7. 安全模型

| 项 | 默认 | 强制约束 |
|---|---|---|
| 网关监听 | `127.0.0.1:8080` | 关闭 LAN 时 `GatewayOptions.FromSettings` 会把任意 host 拉回回环 |
| 局域网访问 | 关闭 | 开启 LAN 时若无 API Key 且未显式设置 `allowLanWithoutApiKey` → **回退回环并强制开鉴权**（fail closed） |
| API Key | 启用，自动生成 `sk-lamm-…` | `CryptographicOperations.FixedTimeEquals` 比较；日志中只出现掩码 |
| 引擎内部端口 | `127.0.0.1` 随机端口 | `ApiSettings.Loopback` 在 `NetworkSettings.Normalize()` 中被写死，模型/引擎无法覆盖 |
| 日志 | 仅内存，不含密钥/鉴权头/提示词 | `LogRedactor`：注册密钥字面量 + `Bearer` 模式 + `sk-…` 模式 + `api_key=` 模式 |
| 开机启动 | 关闭 | `StartupRegistration.BuildStartupArguments` 只产出 `--startup [--minimized]`，永不含加载开关 |

---

## 8. 设置与页面

**8 个独立设置页**（不存在把所有东西塞进一页）：

| 页面 | 分区 | 主要内容 |
|---|---|---|
| 常规 | `general` | 开机启动、启动最小化、关闭到托盘、主题、启动时探测引擎 |
| API | `api` | 监听地址/端口、LAN 开关、API Key 启用/生成/复制/显示、并发、超时、状态端点 |
| 推理引擎 | —（自定义页） | 引擎增删改、`--help` 探测、设为默认、查看该构建支持的参数 |
| 模型参数 | —（自定义页） | **按引擎探测结果**渲染参数默认值（含「该引擎不支持」的置灰项） |
| 生命周期 | `lifecycle` | 空闲超时、并发上限、显存驱逐阈值、优雅停止宽限 |
| 资源 | `resources` | 采样开关/间隔、主 GPU、按进程统计显存、实时读数 |
| 网络 | `network` | 内部端口区间（只读的回环绑定约束）、出站代理 |
| 高级 | `advanced` | 日志级别/缓冲、内容日志开关、请求上限、无密钥 LAN 逃生门 |

其中 6 个页面由 `SettingsSections` 描述符 + 一个通用 `SettingsPageView` 驱动；
推理引擎与模型参数两页因为需要引擎能力交互而单独实现。导航分组由 `PageCatalog` 决定：

```
模型      → 模型
运行时    → 运行状态、运行日志
集成      → API 集成
设置      → 常规、API、推理引擎、模型参数、生命周期、资源、网络、高级
```

---

## 9. 扩展一个新后端

1. 新建 `class XVllmAdapter : OpenAiCompatibleBackendAdapter`，实现
   `Kind` / `DisplayName` / `DefaultExecutableFileName` / `Inspect` / `BuildLaunchPlan`。
2. 在 `BackendAdapterRegistry.CreateDefault` 中注册。
3. 在 `BackendAdapterKinds` 加一个常量，在引擎编辑器的下拉里加一项。

生命周期、网关、资源、日志、UI **全部无需改动** —— 这是「架构允许未来后端」的具体含义。

---

## 10. 验证策略

三层，全部离线可跑：

1. **单元测试（17 项）**：配置默认值与安全约束、JSON 往返与损坏隔离、脱敏、环形日志、
   `--help` 解析、命令行构造与参数过滤、模型注册表、端口分配、引擎发现、开机启动命令、
   网关选项传递。
2. **内核验收测试（8 项）**：真实 Kestrel + 真实子进程，覆盖
   「待机 → 按需加载 → 流式 → 空闲卸载 → 重新加载」、LRU 驱逐、鉴权开关、
   **引擎版本切换导致参数集合变化**、加载失败 502、优雅/强制停止与孤儿检查、日志不落盘。
3. **应用冒烟测试（26 项）**：对**真正构建出来的 `LocalAIModelManager.exe`** 做端到端验证
   （见 `scripts/app-smoke.ps1`）。

证据与命令见 `docs/VERIFICATION.md`。

---

## 11. 已知限制

- **无 llama.cpp 二进制时无法验证真实引擎**：本仓库所在环境无外网、无 `llama-server.exe`，
  因此所有端到端验证使用自带的 `MockEngine`（CLI 与 HTTP 表面与 llama.cpp 对齐）。
  切换到真实引擎只需把引擎路径指过去，代码路径完全相同。
- **`SetConsoleCtrlHandler` 不可用于 WPF 主进程**：见 §6 的 `ControlHelper` 说明。
- **多节点 MSBuild 在此沙箱不可用**（命名管道受限），脚本统一使用 `-m:1 -nodeReuse:false`。
- **HKCU 注册表写入在此沙箱被拒绝**：开机启动写入路径无法实测，只验证了命令构造与
  非抛异常的错误上报路径（测试报告中标记为 SKIP）。
- 界面目前为简体中文单语言（`General.Language` 字段保留但未用于切换文案）。
