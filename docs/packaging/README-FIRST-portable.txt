Local AI Model Manager - 免安装版本（自带私有 .NET 运行时）
============================================================

启动方式：双击 **Start.cmd**（不要直接双击 LocalAIModelManager.exe）。

  Start.cmd 会把 DOTNET_ROOT 指向本目录下的 dotnet\ 私有运行时，然后启动主程序。
  因此目标机器**无需安装任何 .NET 组件**。

  直接双击 LocalAIModelManager.exe 也可以运行，但那样会使用机器上已安装的 .NET 10
  运行时（Desktop Runtime + ASP.NET Core Runtime）；若未安装则会提示缺少运行时。
  这种情况下请改用 Start.cmd。


首次启动会发生什么
------------------
  * 在 %APPDATA%\LocalAIModelManager 生成 settings.json 与 models.json；
  * 自动生成一个 API 密钥（可在「设置 → API」查看、复制或重新生成）；
  * 只启动管理器与 API 网关，**不会加载任何模型**，所有模型保持待机；
  * 收到 POST /v1/chat/completions 时才会启动引擎、加载模型、转发并流式返回；
  * 空闲 5 分钟（可配置）后自动卸载模型并释放显存。


目录说明
--------
  Start.cmd                              启动入口（推荐）
  LocalAIModelManager.exe                主程序（UI + OpenAI 兼容 API 网关）
  LocalAIModelManager.ControlHelper.exe  必需：一次性助手进程，向引擎控制台投递
                                         CTRL_BREAK 以实现优雅停止
  dotnet\                                私有 .NET 10 运行时（10.0.4），约 200 MB
    dotnet.exe
    host\fxr\10.0.4\
    shared\Microsoft.NETCore.App\10.0.4\
    shared\Microsoft.WindowsDesktop.App\10.0.4\
    shared\Microsoft.AspNetCore.App\10.0.4\
  engines\mock\llama-server.exe          可选：离线演示引擎。即使没有 llama.cpp，
                                         也能完整体验「按需加载 → 流式返回 → 空闲卸载」
  README-FIRST.txt                       本文件

  体积说明：约 200 MB 中绝大部分是私有运行时，这是「免安装」的代价。
  如果目标机器已经装了 .NET 10 运行时，请改用体积仅约 1 MB 的非 portable 版本。


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
