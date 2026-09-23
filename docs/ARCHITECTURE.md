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
│  └─ packaging/                    # 发行包内附说明的模板（UTF-8，避免脚本编码问题）
├─ scripts/
│  ├─ env.ps1                       # 共享环境（DOTNET_CLI_HOME、单节点 MSBuild）
│  ├─ build.ps1                     # 构建整个解决方案
│  ├─ run.ps1                        # 启动桌面应用
│  └─ publish.ps1                   # 打包成可分发软件（框架依赖 / 免安装）
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
│  ├─ LocalAIModelManager.MockEngine/      # 离线 OpenAI 兼容「假 llama-server」（发行包内置演示引擎）
│  └─ LocalAIModelManager.ControlHelper/   # 一次性助手：给引擎送 CTRL_BREAK（发行包必需组件）
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

参数解析优先级：`ModelParameters.Defaults`（全局，用户设置） → `ModelParameters.ExtraArguments` →
**模型自己的参数**（最高） → 最后追加 `AdditionalArguments`（自由文本行，全局 + 模型）。
`--model / --host / --port / --alias / --no-webui` 属于「管理器托管」，模型无法覆盖。

**参数策略：未设置就不下发。** 这里没有任何「内置默认值」——`ModelParameterSettings.Defaults`
和 `ModelDefinition.Parameters` 对新配置和导入的模型**都是空的**。`ResolveParameters()` 只做合并，
不发明任何键。因此：

- 命令行里出现的推理参数，只可能来自用户的显式配置；
- 显存占用由**模型本身 + llama.cpp 自身默认值**决定，而不是由本应用决定；
- 早期版本（defaultsVersion 1/2）曾自动写入 `--n-gpu-layers=99` 与 `--ctx-size=8192`；
  `Normalize()` 在升到版本 3 时会**把这两个自动值删掉**（只删值仍等于自动值的那两条），
  之后不再写入任何东西；
- 引擎若不支持某个用户参数，`BuildLaunchPlan` 会按能力探测结果丢弃它并记录警告。

**「其他参数」**：界面上不再为几十个冷门开关各做一个控件，只保留 6 项常用参数
（`EssentialParameters`：context / GPU layers / threads / batch / ubatch / KV cache），
其余通过自由文本行输入，例如 `--some-option value` 或单独的 `--another-option`。
`AdditionalArguments.Tokenize()` 按空白切分、保留顺序、去掉成对引号，并挡掉管理器托管的 flag。

**注意 llama.cpp 自身的默认值**：不设 `--n-gpu-layers` 时 `-ngl = 0`（纯 CPU）；不设
`--ctx-size` 时它采用模型自身的训练上下文，而 KV cache 是按上下文长度**一次性预留**的，
往往远大于权重。以 `Hy-MT2-1.8B-Q4_K_M`（`hunyuan-dense`，32 层，4 个 KV 头，
`context_length = 262144`）为例，每 token 的 KV 是 `32 × 4 × (128+128) × 2 字节 = 64 KB`：

| 上下文 | KV cache | 含 1.05 GB 权重的总占用 |
|---|---|---|
| 262144（不设 --ctx-size 时的默认） | 16 GB | ~18 GB |
| 32768 | 2 GB | ~4 GB |
| 8192 | 0.5 GB | ~1.7 GB |

这两件事都由用户决定；`ModelAutoTuner` 只在**显式触发**时（模型编辑窗口的按钮、模型页的
「自动调参」、命令行 `--autotune`）给出建议值。

自动调参的策略是「**按目标上下文配**」，不是「按剩余显存配」：
`ResourceSettings.AutoTuneContextSize`（默认 8192）是瞄准值，KV cache 由它决定；
`ResourceSettings.MaxVramUsagePercent`（默认 70%）只是**安全阀**，仅在目标值都放不下时
才用于收缩上下文、再收缩层数。把上限当目标会让 1.8B 模型在 24 GiB 卡上占掉 18 GiB——
那正是这条策略要避免的。

模型加载完成后，`ModelLifecycleManager.WarnIfVramIsTightAsync` 会检查该进程占用的显存；
超过显卡 85%（或空闲不足 512 MiB）时，在模型状态与运行日志中直接给出这条解释和可选处置
（调小 `--ctx-size`、用 `--cache-type-k/-v q8_0` 压缩 KV、或 `--no-kv-offload` 放到内存）。


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
| 常规 | `general` | 开机启动、启动最小化、关闭到托盘、**界面语言**、主题、启动时探测引擎 |
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

## 8.1 多语言（简体中文 / English / 日本語 / Français）

界面与**日志输出**共用同一套本地化资源，切换语言后两者同时生效。

| 组件 | 位置 | 作用 |
|---|---|---|
| `Localizer` | `Core/Localization/Localizer.cs` | 语言状态、查找、`LanguageChanged` 事件、切换 UI 区域性 |
| `Loc` | 同上 | 全局静态别名：`Loc.T("key")` / `Loc.T("key", arg0, arg1)` |
| `TExtension` | `App/Infrastructure/TExtension.cs` | XAML 标记扩展：`Text="{loc:T models.toolbar.add}"` |
| `strings.*.json` | `Core/Localization/` | 四个语言字典，作为 `EmbeddedResource` 打进程序集 |

规则：

1. **查找顺序**：当前语言 → `en-US` → 键名本身。某个键漏译只会退化成英文，不会让界面报错。
2. **占位符**：JSON 里写 `{0}`、`{1}`，调用方按位置传参，`Loc.T` 内部做 `string.Format`；占位符写坏时返回原文而不是抛异常。
3. **日志语言在写日志的那一刻决定**。已经写进内存缓冲的条目保留当时的语言——这是有意行为，避免刷新历史日志时文字被改写。
4. **不切换数据区域性**（`DefaultThreadCurrentCulture` 保持不变），只切 `DefaultThreadCurrentUICulture`。参数是当作不变文本解析和拼接的，小数点变成逗号的区域会让 `0.7` 这类值悄悄出错。
5. **运行期重建**：语言变化时 `ShellViewModel` 重建导航项、清空页面/视图缓存并重新导航；`PageCatalog`、`SettingsSections`、各页 `Title`/`Description` 都是每次访问时构建，不缓存在 `static readonly` 里。
6. 引擎自身的名称、模型 ID、命令行开关（`--ctx-size`）、HTTP 头与文件通配符（`*.gguf`）**不翻译**。

---

## 9. 扩展一个新后端

1. 新建 `class XVllmAdapter : OpenAiCompatibleBackendAdapter`，实现
   `Kind` / `DisplayName` / `DefaultExecutableFileName` / `Inspect` / `BuildLaunchPlan`。
2. 在 `BackendAdapterRegistry.CreateDefault` 中注册。
3. 在 `BackendAdapterKinds` 加一个常量，在引擎编辑器的下拉里加一项。

生命周期、网关、资源、日志、UI **全部无需改动** —— 这是「架构允许未来后端」的具体含义。

---

## 10. 部署形态

| 形态 | 体积 | 目标机器要求 | 入口 |
|---|---|---|---|
| 框架依赖包 | 约 1.2 MB | .NET 10 Desktop Runtime + ASP.NET Core Runtime | `LocalAIModelManager.exe` |
| 免安装包 | 约 201 MB | 无 | `Start.cmd`（设置 `DOTNET_ROOT` 指向随包私有运行时） |

**为什么不用 `--self-contained`**：apphost 是否为自包含在 apphost 编译期由 SDK 决定，
需要 runtime pack；该 pack 属于 NuGet 包，在没有外网的环境下拿不到。
手工把 `runtimeconfig.json` 改成 `includedFrameworks` 并不可行（实测 hostfxr 仍按
框架依赖解析并报 `hostpolicy.dll not found`）。因此免安装包采用**私有 .NET 运行时 +
`DOTNET_ROOT`** 这一受支持方案；想确认它确实生效，可以在程序运行时查看进程加载的模块，
`coreclr.dll` / `PresentationFramework.dll` / `Microsoft.AspNetCore.Server.Kestrel.Core.dll`
应当全部来自包内的 `dotnet\` 目录。

发行包内还带一个 `engines\mock\llama-server.exe`（即 `MockEngine`，重命名后的 apphost），
让没有 llama.cpp 的机器也能立刻跑通全流程。

---

## 11. 已知限制

- **本机没有 llama.cpp 二进制**：仓库所在环境无外网、无 `llama-server.exe`，因此自带的
  `MockEngine`（CLI 与 HTTP 表面与 llama.cpp 对齐）既是开发期的替身，也是发行包里的演示引擎。
  切到真实引擎只需把引擎路径指过去，代码路径完全相同。
- **`SetConsoleCtrlHandler` 不可用于 WPF 主进程**：见 §6 的 `ControlHelper` 说明。
- **多节点 MSBuild 在部分受限环境不可用**（命名管道受限），脚本统一使用
  `-m:1 -nodeReuse:false`；在不受限的机器上可以去掉这两个开关。
- **开机启动需要能写 HKCU**：受限账户下写 `HKEY_CURRENT_USER\...\Run` 会被拒绝，此时
  「常规」页会给出明确错误并把开关回滚为关闭，而不是静默失败。
- 界面目前为简体中文单语言（`General.Language` 字段保留但未用于切换文案）。
