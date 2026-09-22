using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using LocalAIModelManager.App.Infrastructure;
using LocalAIModelManager.App.Services;
using LocalAIModelManager.Core.Configuration;

namespace LocalAIModelManager.App.ViewModels;

public sealed record EndpointRow(string Method, string Path, string Description, string Auth);

/// <summary>
/// API Integration page. Every snippet is generated from the live settings and the
/// currently selected model, so what the user copies is exactly what is running.
/// </summary>
public sealed class ApiIntegrationViewModel : PageViewModelBase
{
    private const string Prompt = "你好，请用一句话介绍你自己。";

    private bool _revealed;
    private string? _selectedModelId;
    private bool _initialized;

    public ApiIntegrationViewModel(AppServices services)
        : base(services)
    {
        ToggleRevealCommand = new RelayCommand(_ => Revealed = !Revealed);
        RegenerateKeyCommand = new AsyncRelayCommand(RegenerateKeyAsync, () => !IsBusy);
        CopyBaseUrlCommand = new RelayCommand(_ => Copy(BaseUrl, "Base URL"));
        CopyApiKeyCommand = new RelayCommand(_ => Copy(ApiKey, "API 密钥"));
        CopyModelIdCommand = new RelayCommand(_ => Copy(SelectedModelId ?? string.Empty, "模型 ID"));
        CopyPythonCommand = new RelayCommand(_ => Copy(PythonExample, "Python 示例"));
        CopyPythonRawCommand = new RelayCommand(_ => Copy(PythonRawExample, "Python streaming 示例"));
        CopyCurlCommand = new RelayCommand(_ => Copy(CurlExample, "curl 示例"));
        CopyPowerShellCommand = new RelayCommand(_ => Copy(PowerShellExample, "PowerShell 示例"));
        RefreshCommand = new RelayCommand(_ => Rebuild());
    }

    public override string Title => "API 集成";

    public override string Description =>
        "当前生效的接入信息与可复制示例。所有示例都根据当前设置与所选模型实时生成。";

    public ObservableCollection<string> ModelIds { get; } = new();

    public ObservableCollection<EndpointRow> Endpoints { get; } = new();

    public ObservableCollection<string> Warnings { get; } = new();

    public string? SelectedModelId
    {
        get => _selectedModelId;
        set
        {
            if (SetProperty(ref _selectedModelId, value))
            {
                RaiseAllPropertiesChanged();
            }
        }
    }

    public bool Revealed
    {
        get => _revealed;
        set
        {
            if (SetProperty(ref _revealed, value))
            {
                OnPropertyChanged(nameof(ApiKeyDisplay));
                OnPropertyChanged(nameof(PythonExample));
                OnPropertyChanged(nameof(PythonRawExample));
                OnPropertyChanged(nameof(CurlExample));
                OnPropertyChanged(nameof(PowerShellExample));
            }
        }
    }

    public string BaseUrl => $"{Services.Gateway.BaseUrl}/v1";

    public string RootUrl => Services.Gateway.BaseUrl;

    public string ApiKey => Services.Current.Api.ApiKeyEnabled ? Services.Current.Api.ApiKey : string.Empty;

    public string ApiKeyDisplay
    {
        get
        {
            if (!Services.Current.Api.ApiKeyEnabled)
            {
                return "（鉴权已关闭）";
            }

            return Revealed ? ApiKey : ApiKeyGenerator.Mask(ApiKey);
        }
    }

    public string GatewayStateText => Services.Gateway.IsRunning
        ? $"运行中 · {Services.Gateway.BaseUrl}"
        : "已停止";

    public string GatewayStateTone => Services.Gateway.IsRunning ? "ok" : "error";

    public string BindingText => Services.Current.Api.AllowLanAccess
        ? "已允许局域网访问（引擎内部端口仍只绑定 127.0.0.1）"
        : "仅本机可访问（127.0.0.1）";

    public string ModelForExamples =>
        string.IsNullOrWhiteSpace(SelectedModelId) ? "<MODEL_ID>" : SelectedModelId!;

    public string PythonExample => BuildPython();
    public string PythonRawExample => BuildPythonRaw();
    public string CurlExample => BuildCurl();
    public string PowerShellExample => BuildPowerShell();

    public ICommand ToggleRevealCommand { get; }
    public ICommand RegenerateKeyCommand { get; }
    public ICommand CopyBaseUrlCommand { get; }
    public ICommand CopyApiKeyCommand { get; }
    public ICommand CopyModelIdCommand { get; }
    public ICommand CopyPythonCommand { get; }
    public ICommand CopyPythonRawCommand { get; }
    public ICommand CopyCurlCommand { get; }
    public ICommand CopyPowerShellCommand { get; }
    public ICommand RefreshCommand { get; }

    public override Task InitializeAsync()
    {
        if (!_initialized)
        {
            _initialized = true;
            Rebuild();
        }

        return Task.CompletedTask;
    }

    public override Task RefreshAsync()
    {
        Rebuild();
        return Task.CompletedTask;
    }

    public void Rebuild()
    {
        var previous = SelectedModelId;
        ModelIds.Clear();
        foreach (var model in Services.Models.All.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            ModelIds.Add(model.Id);
        }

        _selectedModelId = ModelIds.FirstOrDefault(id => string.Equals(id, previous, StringComparison.OrdinalIgnoreCase))
                           ?? ModelIds.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedModelId));
        OnPropertyChanged(nameof(ModelForExamples));

        Endpoints.Clear();
        Endpoints.Add(new EndpointRow("GET", "/v1/models", "列出所有已注册模型（含加载状态）", Required()));
        Endpoints.Add(new EndpointRow("GET", "/v1/models/{id}", "查询单个模型的详细信息", Required()));
        Endpoints.Add(new EndpointRow("POST", "/v1/chat/completions", "对话补全，支持 stream=true 的 SSE 流式输出", Required()));
        Endpoints.Add(new EndpointRow("POST", "/v1/completions", "文本补全（透传到引擎）", Required()));
        Endpoints.Add(new EndpointRow("POST", "/v1/embeddings", "向量化（需引擎支持 --embedding）", Required()));
        Endpoints.Add(new EndpointRow("GET", "/v1/internal/status", "管理器运行状态 JSON", Required()));
        Endpoints.Add(new EndpointRow("GET", "/health", "健康检查（无需鉴权）", "无"));

        Warnings.Clear();
        if (!Services.Current.Api.ApiKeyEnabled)
        {
            Warnings.Add("API 密钥已关闭：任何能访问该端口的主机都可以直接调用模型。");
        }

        if (Services.Current.Api.AllowLanAccess)
        {
            Warnings.Add("已开启局域网访问：请确认防火墙规则与密钥强度。");
        }

        if (Services.Models.Count == 0)
        {
            Warnings.Add("尚未注册任何模型：请先在“模型”页面添加模型，否则请求会返回 404。");
        }

        if (!Services.Gateway.IsRunning)
        {
            Warnings.Add("API 网关当前未运行：请在“API”设置页保存后重启网关。");
        }

        RaiseAllPropertiesChanged();
    }

    private string Required() => Services.Current.Api.ApiKeyEnabled ? "Authorization: Bearer <API_KEY>" : "无（鉴权已关闭）";

    private string BuildPython()
    {
        var key = Services.Current.Api.ApiKeyEnabled ? ApiKey : "not-needed";
        var builder = new StringBuilder();
        builder.AppendLine("# pip install openai");
        builder.AppendLine("from openai import OpenAI");
        builder.AppendLine();
        builder.AppendLine("client = OpenAI(");
        builder.AppendLine($"    base_url=\"{BaseUrl}\",");
        builder.AppendLine($"    api_key=\"{key}\",");
        builder.AppendLine(")");
        builder.AppendLine();
        builder.AppendLine("# 列出所有已注册模型（不会触发加载）");
        builder.AppendLine("for model in client.models.list().data:");
        builder.AppendLine("    print(model.id)");
        builder.AppendLine();
        builder.AppendLine("# 首次请求会自动启动引擎并加载模型，随后按 SSE 流式返回");
        builder.AppendLine("stream = client.chat.completions.create(");
        builder.AppendLine($"    model=\"{ModelForExamples}\",");
        builder.AppendLine($"    messages=[{{\"role\": \"user\", \"content\": \"{Prompt}\"}}],");
        builder.AppendLine("    stream=True,");
        builder.AppendLine(")");
        builder.AppendLine();
        builder.AppendLine("for chunk in stream:");
        builder.AppendLine("    delta = chunk.choices[0].delta.content");
        builder.AppendLine("    if delta:");
        builder.AppendLine("        print(delta, end=\"\", flush=True)");
        return builder.ToString();
    }

    private string BuildPythonRaw()
    {
        var headers = new StringBuilder();
        headers.Append("{\"Content-Type\": \"application/json\"");
        if (Services.Current.Api.ApiKeyEnabled)
        {
            headers.Append($", \"Authorization\": \"Bearer {ApiKey}\"");
        }

        headers.Append('}');

        return $$"""
            # 不依赖任何 SDK：直接发 HTTP 并逐块读取 SSE
            import json
            import requests

            response = requests.post(
                "{{BaseUrl}}/chat/completions",
                headers={{headers}},
                json={
                    "model": "{{ModelForExamples}}",
                    "messages": [{"role": "user", "content": "{{Prompt}}"}],
                    "stream": True,
                },
                stream=True,
            )

            response.raise_for_status()
            for line in response.iter_lines(decode_unicode=True):
                if not line or not line.startswith("data: "):
                    continue
                payload = line[6:]
                if payload == "[DONE]":
                    break
                delta = json.loads(payload)["choices"][0]["delta"].get("content")
                if delta:
                    print(delta, end="", flush=True)
            """;
    }

    private string BuildCurl()
    {
        var key = Services.Current.Api.ApiKeyEnabled ? ApiKey : "not-needed";
        return $$"""
            # 1) 列出模型
            curl {{BaseUrl}}/models -H "Authorization: Bearer {{key}}"

            # 2) 对话补全（curl 7.68+ 会逐块刷新，可直接看到流式输出）
            curl -N {{BaseUrl}}/chat/completions \
              -H "Content-Type: application/json" \
              -H "Authorization: Bearer {{key}}" \
              -d "{\"model\":\"{{ModelForExamples}}\",\"messages\":[{\"role\":\"user\",\"content\":\"{{Prompt}}\"}],\"stream\":true}"
            """;
    }

    private string BuildPowerShell()
    {
        var headerLines = new StringBuilder();
        headerLines.Append("$headers = @{ \"Content-Type\" = \"application/json\"");
        if (Services.Current.Api.ApiKeyEnabled)
        {
            headerLines.Append($"; \"Authorization\" = \"Bearer {ApiKey}\"");
        }

        headerLines.Append(" }");

        return $$"""
            # 1) 列出模型
            {{headerLines}}
            Invoke-RestMethod -Uri "{{BaseUrl}}/models" -Headers $headers | ConvertTo-Json -Depth 6

            # 2) 对话补全（非流式）
            $body = @{
                model    = "{{ModelForExamples}}"
                messages = @(@{ role = "user"; content = "{{Prompt}}" })
                stream   = $false
            } | ConvertTo-Json -Depth 6

            Invoke-RestMethod -Uri "{{BaseUrl}}/chat/completions" -Method Post -Headers $headers -Body $body |
                ConvertTo-Json -Depth 8

            # 3) 流式：PowerShell 7+ 建议使用 System.Net.Http 逐行读取 SSE
            """;
    }

    private void Copy(string text, string what)
    {
        if (ClipboardHelper.SetText(text))
        {
            SetStatus($"已复制{what}。");
        }
    }

    private async Task RegenerateKeyAsync()
    {
        if (!Services.Dialogs.Confirm("重新生成 API 密钥", "重新生成后，所有已配置旧密钥的客户端都会立即失效。继续吗？"))
        {
            return;
        }

        Services.SaveSettings(s => s.Api.ApiKey = ApiKeyGenerator.Create());
        Services.Logs.Redactor.RegisterSecret(Services.Current.Api.ApiKey);
        Revealed = true;
        SetStatus("已生成新的 API 密钥。");
        Services.Notify("API 密钥已重新生成。");
        await Task.CompletedTask.ConfigureAwait(true);
    }
}
