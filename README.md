# Local AI Model Manager

Windows 11 桌面端的**本地模型生命周期管理器 + OpenAI 兼容 API 网关**。

- **不使用 Ollama。**
- 首个推理引擎：**llama.cpp / llama-server**，可独立替换与升级（用户可选择任意 llama.cpp 版本）。
- 架构允许未来接入其他后端（vLLM、TensorRT-LLM、远程 OpenAI 兼容服务……）。
- 启动时**只启动管理器**：不加载任何模型，全部保持**待机**；收到 API 请求时才按需启动引擎、加载模型、转发并流式返回；空闲超时后自动卸载并释放显存。

技术栈：C# / .NET 10 + WPF（Kestrel 作为内嵌 OpenAI 兼容网关）。
**零第三方 NuGet 依赖**，可完全离线构建与运行。

---

## 1. 直接运行已编译好的软件

打包产物在 `artifacts\` 下，两种形态：

| 产物 | 体积 | 目标机器要求 | 启动方式 |
|---|---|---|---|
| `LocalAIModelManager-0.1.0-win-x64\`（+ `.zip`） | 约 1.2 MB | 需安装 **.NET 10 Desktop Runtime + ASP.NET Core Runtime** | 双击 `LocalAIModelManager.exe` |
| `LocalAIModelManager-0.1.0-win-x64-portable\`（+ `.zip`） | 约 201 MB（zip 83 MB） | **无需安装任何 .NET 组件** | 双击 **`Start.cmd`** |

```
# 非 portable 版：目标机器需要 .NET 10 运行时
LocalAIModelManager-0.1.0-win-x64\LocalAIModelManager.exe

# 免安装版：Start.cmd 会把 DOTNET_ROOT 指向随包的私有 .NET 运行时
LocalAIModelManager-0.1.0-win-x64-portable\Start.cmd
```

两个目录里都带了一个**离线演示引擎**（`engines\mock\llama-server.exe`），
即使机器上完全没有 llama.cpp，也能立刻完整体验
「按需加载 → 流式返回 → 空闲卸载 → 重新加载」的全流程（见 §4）。

### 重新打包

```powershell
# 只打小体积的框架依赖包
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish.ps1

# 同时打免安装版（把本机已安装的 .NET 10 运行时作为私有运行时一起打包）
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Portable

# 指定版本号 / 跳过压缩
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Version 0.2.0 -NoZip
```

## 2. 从源码构建

```powershell
# 构建
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1

# 直接运行开发版
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run.ps1
```

首次启动会自动生成 API Key 并写入
`%APPDATA%\LocalAIModelManager\settings.json`。可用环境变量 `LAMM_CONFIG_DIR` 改配置目录。

## 3. 接上真实的 llama.cpp

1. 下载/解压任意 llama.cpp 发行版（Windows CUDA/Vulkan/CPU 均可）。
2. 打开 **设置 → 推理引擎 → 添加引擎…**，指向该目录下的 `llama-server.exe`
   （或把它放到 `%APPDATA%\LocalAIModelManager\engines\<版本>\llama-server.exe`，
   启动时会自动发现）。
3. 点击 **重新探测**：管理器会运行 `llama-server --help`，把该构建**真正支持**的参数
   渲染到「模型参数」页。
4. 打开 **模型 → 添加模型…**，选择 `.gguf` 文件与引擎，按需覆盖参数。

升级引擎：把新版本解压到别的目录 → 新增一条引擎记录 → 在模型里改指过去即可。
**删除引擎或模型记录都不会删除磁盘上的任何文件。**

### GPU 默认设置（默认就走显卡）

llama.cpp 自己的默认是 `-ngl 0`，也就是**纯 CPU**。管理器内置了 **GPU 优先 + 上下文有界** 的默认参数：

```
--n-gpu-layers = 99      # 全部层卸载到显卡
--ctx-size     = 8192    # 上下文上限（KV cache 大小由它决定）
```

无需任何手动配置；全新安装和已有配置都会带上它们（已有配置在下次启动时自动补齐，
且不会覆盖你已经改过的值）。

| 想做什么 | 怎么做 |
|---|---|
| 用 GPU（默认） | 什么都不用做；确认引擎是 CUDA/Vulkan 版本 |
| 强制只用 CPU | **设置 → 模型参数** → 把 `GPU layers (offload)` 改成 `0` → 保存默认值 |
| 只给某个模型单独设定 | **模型 → 编辑…** → 改该模型的这一项（模型级参数优先级最高，会覆盖全局默认） |
| 让改动生效 | 到 **模型** 页对已加载的模型点 **重启**（改参数不会自动重载） |
| 确认真的在 GPU 上 | **运行状态** 页看显存占用；或看「运行日志」里引擎输出的 `n_gpu_layers` |
| 需要更长上下文 | 把 `Context length (--ctx-size)` 调大（见下） |

层数超过 99 的超大模型请把 `--n-gpu-layers` 调大。机器没有可用 GPU 时，llama.cpp 会忽略该参数并回退到 CPU。

### 小模型为什么能把显存吃满？—— KV cache

**KV cache 才是显存大户，不是模型权重。** llama.cpp 在加载时按**上下文长度**一次性预留
整个 KV cache；而 `--ctx-size` 不指定时它会采用**模型自身的训练上下文**，长上下文模型
（128K / 256K）会直接把显存吃光。

实测：`Hy-MT2-1.8B-Q4_K_M.gguf`，权重只有 1.05 GB，但元数据是
`hunyuan-dense`、32 层、4 个 KV 头、`context_length = 262144`，于是

```
每 token KV = 32 层 × 4 头 × (128+128) 维 × 2 字节(f16) = 64 KB
```

| 上下文 | KV cache | 加上 1.05 GB 权重的总占用 |
|---|---|---|
| **262144（不指定时的默认）** | **16 GB** | ~18 GB → 24GB 卡被吃满 |
| 131072 | 8 GB | ~10 GB |
| 32768 | 2 GB | ~4 GB |
| **8192（本版本默认）** | **0.5 GB** | ~1.7 GB |

要长上下文又不想吃满显存，任选其一：

- `KV cache type (K)` / `(V)` 选 `q8_0` → KV 大约减半（256K 下 16 GB → 约 8.5 GB）；
- 勾选 `Keep KV cache on CPU` → KV 放进内存，不占显存（生成变慢）；
- 把 `--ctx-size` 调到**够用**即可，别按模型的最大上下文设置。

模型加载后若显存占用超过显卡的 85%，管理器会在「模型」页详情和运行日志里直接给出这条提示。

### 自动调参：导入模型不用自己算

管理器会**直接读 GGUF 文件头**（层数、KV 头数、注意力维度、训练上下文、权重体积），
再结合你的显卡显存，自动算出 `--ctx-size` 与 `--n-gpu-layers`。

- 添加模型时选中文件后**自动填入**，编辑窗口里会列出推导过程；不满意可手动改；
- 「模型」页工具栏有 **自动调参**（当前模型）和 **全部自动调参**（列表内所有模型）；
- 「设置 → 资源」可调 **单模型显存使用上限**（默认 70%）——这就是防止「小模型吃满显存」的闸门；
- 结果解释会写进「模型」页下方的输出框，例如：

```
· 模型元数据：hunyuan-dense · 32 层 · 4 个 KV 头 · 训练上下文 262144 · 权重 1.06 GiB。
· KV cache：每 token 约 64.0 KB（f16），按上下文长度一次性预留。
· 显存：共 24.0 GiB（当前空闲 22.3 GiB）；单模型使用上限 70% = 16.8 GiB，
  扣除 1.5 GiB 计算缓冲后可用 15.3 GiB。
· 结果：--ctx-size 131072（KV 约 8.00 GiB）、--n-gpu-layers 99；
  预计显存占用约 10.56 GiB / 24.0 GiB（占显卡 44%，上限 70%）。
```

也可以在命令行直接跑（不启动界面）：

```powershell
LocalAIModelManager.exe --autotune "D:\AI\models\Hy-MT2-1.8B-Q4_K_M.gguf" --out report.txt
LocalAIModelManager.exe --autotune model.gguf --max-vram 50
```

改完参数后，已加载的模型需要点 **重启** 才会用新参数。

## 4. 用离线演示引擎先跑通全流程

1. **设置 → 推理引擎 → 添加引擎…**，可执行文件指向
   `<安装目录>\engines\mock\llama-server.exe`，保存后点「重新探测」。
2. **模型 → 添加模型…**，任选一个 `.gguf` 文件
   （演示引擎不读取内容，只要求文件存在）。
3. 到 **API 集成** 页复制 curl / Python / PowerShell 示例直接调用。

## 5. 客户端接入

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

## 6. 端点

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/v1/models` | 列出所有已注册模型（不触发加载） |
| GET | `/v1/models/{id}` | 单个模型详情 |
| POST | `/v1/chat/completions` | 对话补全，支持 `stream=true`（SSE，真流式透传） |
| POST | `/v1/completions` | 文本补全（透传到引擎） |
| POST | `/v1/embeddings` | 向量化（需引擎支持 `--embedding`） |
| GET | `/v1/internal/status` | 运行状态 JSON（网关/模型/进程/引擎/资源） |
| GET | `/health` | 健康检查（无需鉴权） |

## 7. 安全默认值

| 项 | 默认 |
|---|---|
| 网关监听 | `127.0.0.1:8080`（仅本机） |
| 局域网访问 | 关闭；开启后**强制要求** API Key，否则回退回环 |
| 引擎内部端口 | 只绑定 `127.0.0.1`，永不对外暴露 |
| API Key | 启用，自动生成；日志中只出现掩码 |
| 日志 | 只驻留内存；默认不记录提示词/输出/鉴权头；只有「保存」才写盘 |
| 开机启动 | 关闭；启动命令**永不含**加载模型的开关，重启后所有模型仍待机 |

## 8. 页面

```
模型      模型
运行时    运行状态 · 运行日志
集成      API 集成
设置      常规 · API · 推理引擎 · 模型参数 · 生命周期 · 资源 · 网络 · 高级
```

托盘图标支持显示/隐藏窗口、逐个模型启动/停止/重启、卸载全部模型、按当前设置重启网关、退出。

## 9. 项目结构

见 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)。

```
src/LocalAIModelManager.Core/           UI 无关的核心（网关 / 生命周期 / 适配器 / 进程 / 配置 / 日志 / 资源）
src/LocalAIModelManager.App/            WPF 桌面应用（12 个页面 + 托盘）
src/LocalAIModelManager.MockEngine/     离线用的假 llama-server（CLI + OpenAI 兼容 HTTP），
                                        同时作为发行包内置的演示引擎
src/LocalAIModelManager.ControlHelper/  一次性助手：向引擎控制台投递 CTRL_BREAK（发行包必需）
scripts/                                build / run / publish
docs/packaging/                         发行包内附的说明模板
artifacts/                              打包产物（不纳入版本控制）
```

## 10. 受限环境说明

- 脚本已设置 `DOTNET_CLI_HOME`，避免 .NET CLI 因无法写入用户目录而失败。
- MSBuild 多节点依赖命名管道，部分沙箱禁止；脚本统一使用 `-m:1 -nodeReuse:false`。
  在不受限的机器上可以去掉这两个开关。
- Windows PowerShell 默认禁止脚本执行，请使用
  `powershell -NoProfile -ExecutionPolicy Bypass -File <脚本>`。
- `scripts\publish.ps1` 是纯 ASCII 的：Windows PowerShell 5.1 在脚本没有 UTF-8 BOM 时
  按 ANSI 解析，因此所有中文文案都放在 `docs\packaging\*.txt` 模板里再复制进发行包。
