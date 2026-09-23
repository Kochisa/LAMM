Local AI Model Manager - 可执行目录（需要 .NET 10 运行时）
============================================================

运行前请在目标机器安装以下两个运行时（x64）：

  1) .NET Desktop Runtime 10   提供 Microsoft.WindowsDesktop.App（WPF 界面）
  2) ASP.NET Core Runtime 10   提供 Microsoft.AspNetCore.App（API 网关依赖它）

下载地址：https://dotnet.microsoft.com/download/dotnet/10.0

安装完成后，双击 LocalAIModelManager.exe 即可运行。
如果不想在目标机器安装运行时，请改用 *-portable 版本（自带运行时，体积更大）。


首次启动会发生什么
------------------
  * 在 %APPDATA%\LocalAIModelManager 生成 settings.json 与 models.json；
  * 自动生成一个 API 密钥（可在「设置 → API」查看、复制或重新生成）；
  * 只启动管理器与 API 网关，**不会加载任何模型**，所有模型保持待机；
  * 收到 POST /v1/chat/completions 时才会启动引擎、加载模型、转发并流式返回；
  * 空闲 5 分钟（可配置）后自动卸载模型并释放显存。


目录说明
--------
  LocalAIModelManager.exe                主程序（UI + OpenAI 兼容 API 网关）
  LocalAIModelManager.ControlHelper.exe  必需：一次性助手进程，向引擎控制台投递
                                         CTRL_BREAK 以实现优雅停止
  engines\mock\llama-server.exe          可选：离线演示引擎。即使没有 llama.cpp，
                                         也能完整体验「按需加载 → 流式返回 → 空闲卸载」
  README-FIRST.txt                       本文件


接上真实的 llama.cpp
--------------------
  1. 下载任意 llama.cpp Windows 发行版并解压到任意目录；
  2. 打开「设置 → 推理引擎 → 添加引擎…」，指向该目录下的 llama-server.exe；
  3. 点击「重新探测」，管理器会运行 llama-server --help，把该构建真正支持的参数
     渲染到「模型参数」页；
  4. 打开「模型 → 添加模型…」，选择 .gguf 文件与引擎，按需覆盖参数。

  升级引擎：把新版本解压到别的目录，新增一条引擎记录，在模型里改指过去即可。
  删除引擎或模型记录都不会删除磁盘上的任何文件。


使用离线演示引擎试用
--------------------
  1. 在「设置 → 推理引擎 → 添加引擎…」中把可执行文件指向：
         <本目录>\engines\mock\llama-server.exe
  2. 在「模型 → 添加模型…」中任选一个 .gguf 文件（演示引擎不读取内容，只要求文件存在）；
  3. 到「API 集成」页复制 curl / Python / PowerShell 示例直接调用。

  安全提示：默认只监听 127.0.0.1；开启局域网访问必须同时启用 API 密钥。


文档
----
  docs\ARCHITECTURE.md  架构与设计说明


GPU 加速（默认已开启）
----------------------
  管理器默认下发 --n-gpu-layers = 99，即把全部层卸载到显卡。
  llama.cpp 自己的默认是 0（纯 CPU），所以这一项由管理器直接给默认值。

    * 想强制用 CPU：设置 → 模型参数 → 把 GPU layers (offload) 改成 0 → 保存默认值；
    * 想让改动生效：到「模型」页对已加载的模型点「重启」；
    * 想让某个模型单独设定：模型 → 编辑… → 改该模型的这一项（模型级优先）；
    * 确认是否真的在用 GPU：运行状态页看显存占用，或运行日志里的 n_gpu_layers。


显存被吃满？（小模型却占满显卡）
--------------------------------
  这是 KV cache，不是模型权重。llama.cpp 在加载时按【上下文长度】一次性预留整个
  KV cache；若不指定 --ctx-size，它会采用模型自身的训练上下文 —— 对长上下文模型
  可能是 128K / 256K，KV cache 会达到十几 GB。

  实测例子：Hy-MT2-1.8B-Q4_K_M.gguf（权重仅 1.05 GB，32 层，4 个 KV 头）
    每 token KV = 32 层 x 4 头 x 256 维 x 2 字节 = 64 KB
      上下文   8192  ->  KV 约 0.5 GB
      上下文  32768  ->  KV 约 2 GB
      上下文 262144  ->  KV 约 16 GB   <-- 默认按模型训练上下文，显存直接爆掉

  本版本已默认下发 --ctx-size = 8192（可在「设置 → 模型参数」修改）。
  需要长上下文又要省显存，任选其一：
    * 用 --cache-type-k q8_0 与 --cache-type-v q8_0 压缩 KV cache（约减半）；
    * 勾选 --no-kv-offload，把 KV cache 放到内存而不是显存（生成会变慢）；
    * 把 --ctx-size 调到够用即可，不必盲追模型的最大上下文。
