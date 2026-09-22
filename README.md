# Local AI Model Manager

Windows 11 桌面端的**本地模型生命周期管理器 + OpenAI 兼容 API 网关**。

- **不使用 Ollama。**
- 首个推理引擎：**llama.cpp / llama-server**，可独立替换与升级（用户可选择任意 llama.cpp 版本）。
- 架构允许未来接入其他后端（vLLM、TensorRT-LLM、远程 OpenAI 兼容服务……）。
- 启动时**只启动管理器**：不加载任何模型，全部保持**待机**；收到 API 请求时才按需启动引擎、加载模型、转发并流式返回；空闲超时后自动卸载并释放显存。

技术栈：C# / .NET 10 + WPF（Kestrel 作为内嵌 OpenAI 兼容网关）。
**零第三方 NuGet 依赖**，可完全离线构建与运行。

---

## 1. 快速开始

```powershell
# 构建（沙箱/受限环境请使用 -ExecutionPolicy Bypass）
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1

# 运行桌面应用
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run.ps1

# 内核单元 + 验收测试（24 项，离线）
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\acceptance.ps1

# 对真实 exe 的端到端冒烟（26 项，离线）
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\app-smoke.ps1
```

首次启动会自动生成 API Key 并写入
`%APPDATA%\LocalAIModelManager\settings.json`。可用环境变量 `LAMM_CONFIG_DIR` 改配置目录。

## 2. 接上真实的 llama.cpp

1. 下载/解压任意 llama.cpp 发行版（Windows CUDA/Vulkan/CPU 均可）。
2. 打开 **设置 → 推理引擎 → 添加引擎…**，指向该目录下的 `llama-server.exe`
   （或把它放到 `%APPDATA%\LocalAIModelManager\engines\<版本>\llama-server.exe`，
   启动时会自动发现）。
3. 点击 **重新探测**：管理器会运行 `llama-server --help`，把该构建**真正支持**的参数
   渲染到「模型参数」页。
4. 打开 **模型 → 添加模型…**，选择 `.gguf` 文件与引擎，按需覆盖参数。

升级引擎：把新版本解压到别的目录 → 新增一条引擎记录 → 在模型里改指过去即可。
**删除引擎或模型记录都不会删除磁盘上的任何文件。**

## 3. 客户端接入

```text
Base URL:  http://127.0.0.1:8080/v1
API Key:   sk-lamm-…          （Authorization: Bearer <API_KEY>）
模型 ID:   你在「模型」页里设置的 Model ID
```

```bash
curl -N http://127.0.0.1:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $LAMM_KEY" \
  -d '{"model":"model-a","messages":[{"role":"user","content":"你好"}],"stream":true}'
```

```python
from openai import OpenAI

client = OpenAI(base_url="http://127.0.0.1:8080/v1", api_key="sk-lamm-…")
stream = client.chat.completions.create(
    model="model-a",
    messages=[{"role": "user", "content": "你好"}],
    stream=True,
)
for chunk in stream:
    if chunk.choices[0].delta.content:
        print(chunk.choices[0].delta.content, end="", flush=True)
```

`GET /v1/models` 会列出**全部已注册模型**（含未加载的），并附带管理器扩展字段：

```json
{ "id": "model-a", "object": "model", "owned_by": "local-ai-model-manager",
  "lamm": { "displayName": "Model A", "state": "standby", "loaded": false, "engineId": "llamacpp-b7200" } }
```

「API 集成」页会根据当前设置实时生成 Python / curl / PowerShell 示例，可一键复制。

## 4. 端点

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/v1/models` | 列出所有已注册模型（不触发加载） |
| GET | `/v1/models/{id}` | 单个模型详情 |
| POST | `/v1/chat/completions` | 对话补全，支持 `stream=true`（SSE，真流式透传） |
| POST | `/v1/completions` | 文本补全（透传到引擎） |
| POST | `/v1/embeddings` | 向量化（需引擎支持 `--embedding`） |
| GET | `/v1/internal/status` | 运行状态 JSON（网关/模型/进程/引擎/资源） |
| GET | `/health` | 健康检查（无需鉴权） |

## 5. 安全默认值

| 项 | 默认 |
|---|---|
| 网关监听 | `127.0.0.1:8080`（仅本机） |
| 局域网访问 | 关闭；开启后**强制要求** API Key，否则回退回环 |
| 引擎内部端口 | 只绑定 `127.0.0.1`，永不对外暴露 |
| API Key | 启用，自动生成；日志中只出现掩码 |
| 日志 | 只驻留内存；默认不记录提示词/输出/鉴权头；只有「保存」才写盘 |
| 开机启动 | 关闭；启动命令**永不含**加载模型的开关，重启后所有模型仍待机 |

## 6. 页面

```
模型      模型
运行时    运行状态 · 运行日志
集成      API 集成
设置      常规 · API · 推理引擎 · 模型参数 · 生命周期 · 资源 · 网络 · 高级
```

托盘图标支持显示/隐藏窗口、逐个模型启动/停止/重启、卸载全部模型、按当前设置重启网关、退出。

## 7. 项目结构

见 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)；验收证据见 [`docs/VERIFICATION.md`](docs/VERIFICATION.md)。

```
src/LocalAIModelManager.Core/        UI 无关的核心（网关 / 生命周期 / 适配器 / 进程 / 配置 / 日志 / 资源）
src/LocalAIModelManager.App/         WPF 桌面应用（12 个页面 + 托盘）
src/LocalAIModelManager.MockEngine/  离线用的假 llama-server（CLI + OpenAI 兼容 HTTP）
src/LocalAIModelManager.ControlHelper/ 一次性助手：向引擎控制台投递 CTRL_BREAK
tests/LocalAIModelManager.Tests/     自带断言框架的单元 + 验收测试
scripts/                             构建 / 运行 / 测试脚本
```

## 8. 受限环境说明

- 脚本已设置 `DOTNET_CLI_HOME`，避免 .NET CLI 因无法写入用户目录而失败。
- MSBuild 多节点依赖命名管道，部分沙箱禁止；脚本统一使用 `-m:1 -nodeReuse:false`。
  在不受限的机器上可以去掉这两个开关。
- Windows PowerShell 默认禁止脚本执行，请使用
  `powershell -NoProfile -ExecutionPolicy Bypass -File <脚本>`。
