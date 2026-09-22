# 验收证据

所有命令都在仓库根目录执行，机器上**没有** llama.cpp 二进制、**没有**外网。
因此端到端验证使用随仓库发布的 `LocalAIModelManager.MockEngine`（CLI 与 HTTP 表面模拟
`llama-server`：`--help` / `--version` / `--model` / `--ctx-size` / `-ngl` / `/health` /
`/v1/chat/completions` + SSE）。

> 本机 Windows PowerShell 默认禁止脚本执行，命令前请加
> `powershell -NoProfile -ExecutionPolicy Bypass -File`。

---

## 1. 构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

```
    0 个警告
    0 个错误
Build succeeded.
  App        : ...\src\LocalAIModelManager.App\bin\Debug\net10.0-windows\LocalAIModelManager.exe
  Tests      : ...\tests\LocalAIModelManager.Tests\bin\Debug\net10.0-windows\LocalAIModelManager.Tests.exe
  Mock engine: ...\src\LocalAIModelManager.MockEngine\bin\Debug\net10.0-windows\LocalAIModelManager.MockEngine.exe
```

## 2. 内核单元 + 验收测试

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\acceptance.ps1
```

结果：**25 / 25 通过**（1 项因环境限制 SKIP），耗时约 22 秒。

| # | 用例 | 结果 |
|---|---|---|
| 1 | settings: 默认值安全（不预加载 / 仅回环 / 日志不进磁盘） | PASS |
| 2 | settings: 网关选项携带并发上限与「本机免密钥」开关 | PASS |
| 3 | settings: 未显式开启时无法配置「无密钥的 LAN」 | PASS |
| 4 | settings: 显式逃生门允许无密钥 LAN 但给出警告 | PASS |
| 5 | settings: 关闭 LAN 时 host 一定被拉回回环 | PASS |
| 6 | apikey: 生成 / 掩码 / 固定时间比较 | PASS |
| 7 | logging: 脱敏（密钥、Bearer、api_key=） | PASS |
| 8 | logging: 环形缓冲保留最新记录且不落盘 | PASS |
| 9 | logging: 只有显式保存才写文件 | PASS |
| 10 | llamacpp: `--help` 驱动能力探测（含 alias、检测到的新参数） | PASS |
| 11 | llamacpp: 命令行构造过滤不支持项、保留托管项 | PASS |
| 12 | models: 注册表校验 / 持久化 / **删除条目不删文件** | PASS |
| 13 | models: 损坏配置被隔离而不是崩溃 | PASS |
| 14 | ports: 分配唯一且可释放 | PASS |
| 15 | 引擎发现：在磁盘上找到 `llama-server.exe` 风格可执行文件 | PASS |
| 16 | startup: 开机启动命令永不含加载开关（写入路径） | **SKIP**（沙箱拒绝写 HKCU） |
| 17 | settings: 未知键往返保留、缺失项用默认值补齐 | PASS |
| 18 | **ACCEPTANCE：启动 → 待机 → 按需加载 → 流式 → 空闲卸载 → 重新加载** | PASS |
| 19 | lifecycle: 多模型共存 + 加载上限触发 LRU 驱逐 | PASS |
| 20 | api: 鉴权可开可关、错误密钥被拒 | PASS |
| 21 | engines: 能力探测跟随所安装构建（版本切换） | PASS |
| 22 | lifecycle: 加载失败 → 502 + Failed 状态 | PASS |
| 23 | process: 优雅优先 / 超时强杀 / 无孤儿进程 | PASS |
| 24 | logs: 除显式保存外不写磁盘 | PASS |
| 25 | api: `/v1/models` 列出全部已注册模型（含未加载/已禁用） | PASS |

第 18 号（规格里的验收剧本）断言细节：

- 启动后 `LoadedModelCount == 0`、所有模型 `Standby`、`Processes.LiveCount == 0`
- `/v1/models` 可列出模型且 `loaded=false`，**列模型不会触发加载**
- 无 API Key → `401`，且**不会启动引擎进程**
- 带 Key 请求 → 200，响应含 `[mock:model-a]`、原样提示词、`ctx=2048`（说明模型参数进入了命令行），
  且耗时 ≥ 400ms（证明真的经历了加载过程）
- 内部端口落在配置区间内，且只在回环上可达
- `stream=true` → `Content-Type: text/event-stream`，多块 `chat.completion.chunk`，以 `[DONE]` 结束
- 空闲超时后 → 状态回到 `Standby`、进程消失（`Process.GetProcessById` 失败）、
  端口归还（`Ports.ReservedCount == 0`）、`LastStopMode == Graceful`、退出进程进入历史
- 再次请求 → 重新加载并正常服务（新 PID）
- 全程没有产生任何 `*.log` / `*.txt` 文件

## 3. 桌面应用端到端冒烟（对真实 exe）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\app-smoke.ps1
```

结果：**26 / 26 通过**。

```
1) startup: manager only, nothing loaded
  PASS  no engine process exists after startup
  PASS  /v1/models lists the registered model
  PASS  the model is reported as NOT loaded at startup
  PASS  the model state is standby at startup
  PASS  the runtime snapshot reports 0 loaded models
2) API key enforcement
  PASS  a request without the API key is rejected with 401
3) on-demand load and serve
  PASS  the completion came back from the auto-started engine
  PASS  the prompt was forwarded verbatim
  PASS  the per-model parameter reached the engine command line
  PASS  the request waited for the model to load (644 ms)
  PASS  exactly one engine child process is now running
  PASS  the runtime snapshot reports 1 loaded model
  PASS  the model state is Ready
  PASS  the loaded model exposes its engine PID
  PASS  the internal port comes from the configured range (36100)
4) streaming (SSE)
  PASS  the streaming response uses text/event-stream
  PASS  the stream contains OpenAI chunk objects
  PASS  the stream terminates with [DONE]
5) idle timeout unloads and releases VRAM
  PASS  the idle timeout unloaded the model
  PASS  the engine process was terminated (VRAM released)
  PASS  the model is back in standby
6) the next request reloads the model
  PASS  the model was reloaded on demand
  PASS  a fresh engine process is running again
7) logs stay in memory
  PASS  no log file was written to disk
8) shutdown leaves no orphan processes
  PASS  no engine process survived the application exit

Application smoke test PASSED (26 checks).
```

> 脚本会为一个临时配置目录写好 `settings.json` / `models.json`（1 个引擎 + 1 个模型，
> 空闲超时 4 秒），以 `--minimized` 启动真实的 `LocalAIModelManager.exe`，全程只通过
> HTTP API 交互，最后验证退出后没有任何 `llama-server` 残留。

## 4. 规格条目 ↔ 验证对照

| 规格要求 | 实现位置 | 验证 |
|---|---|---|
| 不使用 Ollama | 全仓库无任何 Ollama 相关代码/端点 | — |
| 首个引擎 = llama.cpp / llama-server | `LlamaCppBackendAdapter` | 用例 9、10、20；冒烟 3 |
| 引擎可独立替换/升级，用户可选择不同可执行文件/版本 | `EngineDefinition.ExecutablePath`、`EngineDiscovery` | 用例 14、20 |
| 架构允许未来后端 | `IBackendAdapter` + `BackendAdapterRegistry` + `GenericOpenAiCompatibleAdapter` | 用例 9、10（通过同一接缝驱动 mock 引擎） |
| 启动只起管理器、不加载模型、全部待机 | `AppRuntime.StartAsync` 不含加载路径；`PreloadOnStartup` 强制 false | 用例 1、17；冒烟 1 |
| 收到请求 → 找模型 → 起后端 → 加载 → 等就绪 → 转发 → 返回 → 支持 SSE | `GatewayHost.HandleInferenceAsync` / `ProxyAsync` | 用例 17；冒烟 3、4 |
| 空闲超时（默认 5 分钟）自动卸载并释放显存 | `LifecycleSettings.IdleTimeoutSeconds = 300` + `UnloadIdleAsync` | 用例 17；冒烟 5 |
| 允许多模型同时加载；显存不足时驱逐 LRU 空闲模型 | `MaxLoadedModels` + `EnsureCapacityAsync` | 用例 18 |
| 模型页：增 / 删 / 改名 / ID / 显示名 / 路径 / 引擎 / 参数 / 启停重启测试 | `ModelsViewModel`、`ModelEditorWindow` | `ModelsView`（人工路径），内核侧用例 11、17、18 |
| 删除模型条目不删文件 | `ModelRegistry.Remove` | 用例 11 |
| 模型参数设置页：上下文 / KV 缓存 / GPU 层 / 线程 / batch / ubatch / 投机解码 / MTP | `ParameterCatalog` + `ModelParametersViewModel` | 用例 9、10、20 |
| 参数从引擎 `--help` 探测，永不假设固定 | `LlamaHelpParser` + 指纹缓存 | 用例 9、20 |
| 拆分为 8 个设置页 | `SettingsSections` + `PageCatalog` | 冒烟（应用可启动并服务） |
| API：自定义 host/port、默认 127.0.0.1、可选 LAN、启用/禁用/生成/自定义/重新生成/显示/复制密钥 | `ApiSettings` + `SettingsSections[api]` + `ApiIntegrationViewModel` | 用例 2–5、20；冒烟 2 |
| `Authorization: Bearer <API_KEY>` | `GatewayHost` 鉴权中间件 | 用例 20；冒烟 2 |
| `GET /v1/models` 列出全部已注册模型 | `GatewayHost` | 用例 25；冒烟 1 |
| `POST /v1/chat/completions` | `GatewayHost` | 用例 18；冒烟 3 |
| API 集成页：Base URL / Key / Model ID / Python / curl / PowerShell，按当前设置生成 | `ApiIntegrationViewModel`（`$$"""` 模板 + 实时取值） | 冒烟（应用正常运行） |
| 运行状态页：网关、已加载模型、状态、活动请求、GPU/VRAM、CPU、最近使用/空闲 | `RuntimeStatusViewModel` + `/v1/internal/status` | 冒烟 1、3、5 |
| 运行日志页：默认仅内存、不自动写盘、清空/搜索/暂停/复制/保存、不记录密钥与完整提示词 | `InMemoryLogStore` + `LogRedactor` + `RuntimeLogsViewModel` | 用例 7、8、9、24；冒烟 7 |
| 进程管理：PID / 模型 / 后端 / 内部端口 / 状态 / 启动时间 | `BackendProcess` / `ProcessInventory` | 用例 23；冒烟 3 |
| 优雅停止优先，超时强杀，防孤儿 | `StopAsync` + `ControlHelper` + Job Object | 用例 23；冒烟 8 |
| 安全：无 Ollama、默认回环、LAN 显式、内部端口不外露、启用时必须鉴权 | 见 `ARCHITECTURE.md` §7 | 用例 1–5、14、20 |
| Windows：开机启动 / 最小化启动 / 托盘 / 托盘启停模型 | `StartupRegistration`、`TrayIconService`、`--startup/--minimized` | 用例 16（SKIP，见下） |
| Windows 重启不自动加载模型 | `ModelDefinition.Normalize` 强制 `AutoLoad=false`；启动参数不含加载开关 | 用例 1、16 |

## 5. 环境限制（非产品缺陷）

| 限制 | 影响 | 处置 |
|---|---|---|
| 沙箱拒绝写 `HKEY_CURRENT_USER\Software` | 无法实测「开机启动」的写入/读取/删除 | 用例 16 标记 **SKIP**，并额外验证 `TrySetEnabled` 会返回可读错误而不是抛异常；命令构造（含「永不含加载开关」）已断言 |
| 无外网、无 `llama-server.exe` | 无法对真实 llama.cpp 跑端到端 | 使用 CLI/HTTP 表面一致的 `MockEngine`；真实引擎下代码路径完全相同 |
| 多节点 MSBuild 不可用（命名管道受限） | `dotnet build <sln>` 默认多节点会失败 | `scripts/env.ps1` 统一 `-m:1 -nodeReuse:false`，已在脚本中固定 |
| 脚本执行策略 | 无法直接 `.\scripts\*.ps1` | 文档统一给出 `-ExecutionPolicy Bypass -File` 形式 |

## 6. 复现全部验证

```powershell
cd LocalAIModelManager
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\acceptance.ps1 -NoBuild
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\app-smoke.ps1 -NoBuild
```
