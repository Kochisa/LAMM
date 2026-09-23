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
    private static string Prompt => Loc.T("api.example.prompt");

    private bool _revealed;
    private string? _selectedModelId;
    private bool _initialized;

    public ApiIntegrationViewModel(AppServices services)
        : base(services)
    {
        ToggleRevealCommand = new RelayCommand(_ => Revealed = !Revealed);
        RegenerateKeyCommand = new AsyncRelayCommand(RegenerateKeyAsync, () => !IsBusy);
        CopyBaseUrlCommand = new RelayCommand(_ => Copy(BaseUrl, Loc.T("api.copyN.baseUrl")));
        CopyApiKeyCommand = new RelayCommand(_ => Copy(ApiKey, Loc.T("api.copyN.apiKey")));
        CopyModelIdCommand = new RelayCommand(_ => Copy(SelectedModelId ?? string.Empty, Loc.T("api.copyN.modelId")));
        CopyPythonCommand = new RelayCommand(_ => Copy(PythonExample, Loc.T("api.copyN.python")));
        CopyPythonRawCommand = new RelayCommand(_ => Copy(PythonRawExample, Loc.T("api.copyN.pythonRaw")));
        CopyCurlCommand = new RelayCommand(_ => Copy(CurlExample, Loc.T("api.copyN.curl")));
        CopyPowerShellCommand = new RelayCommand(_ => Copy(PowerShellExample, Loc.T("api.copyN.powershell")));
        RefreshCommand = new RelayCommand(_ => Rebuild());
    }

    public override string Title => Loc.T("page.apiIntegration.title");

    public override string Description => Loc.T("page.apiIntegration.desc");

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
                return Loc.T("api.key.disabled");
            }

            return Revealed ? ApiKey : ApiKeyGenerator.Mask(ApiKey);
        }
    }

    public string GatewayStateText => Services.Gateway.IsRunning
        ? Loc.T("api.state.running", Services.Gateway.BaseUrl)
        : Loc.T("status.value.stopped");

    public string GatewayStateTone => Services.Gateway.IsRunning ? "ok" : "error";

    public string BindingText => Services.Current.Api.AllowLanAccess
        ? Loc.T("api.exposure.lan")
        : Loc.T("api.exposure.local");

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
        Endpoints.Add(new EndpointRow("GET", "/v1/models", Loc.T("api.endpoint.models"), Required()));
        Endpoints.Add(new EndpointRow("GET", "/v1/models/{id}", Loc.T("api.endpoint.modelById"), Required()));
        Endpoints.Add(new EndpointRow("POST", "/v1/chat/completions", Loc.T("api.endpoint.chat"), Required()));
        Endpoints.Add(new EndpointRow("POST", "/v1/completions", Loc.T("api.endpoint.completions"), Required()));
        Endpoints.Add(new EndpointRow("POST", "/v1/embeddings", Loc.T("api.endpoint.embeddings"), Required()));
        Endpoints.Add(new EndpointRow("GET", "/v1/internal/status", Loc.T("api.endpoint.status"), Required()));
        Endpoints.Add(new EndpointRow("GET", "/health", Loc.T("api.endpoint.health"), Loc.T("api.auth.none")));

        Warnings.Clear();
        if (!Services.Current.Api.ApiKeyEnabled)
        {
            Warnings.Add(Loc.T("api.warn.noKey"));
        }

        if (Services.Current.Api.AllowLanAccess)
        {
            Warnings.Add(Loc.T("api.warn.lan"));
        }

        if (Services.Models.Count == 0)
        {
            Warnings.Add(Loc.T("api.warn.noModels"));
        }

        if (!Services.Gateway.IsRunning)
        {
            Warnings.Add(Loc.T("api.warn.gatewayDown"));
        }

        RaiseAllPropertiesChanged();
    }

    private string Required() => Services.Current.Api.ApiKeyEnabled ? Loc.T("api.auth.required") : Loc.T("api.auth.none");

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
        builder.AppendLine(Loc.T("api.example.python.listModels"));
        builder.AppendLine("for model in client.models.list().data:");
        builder.AppendLine("    print(model.id)");
        builder.AppendLine();
        builder.AppendLine(Loc.T("api.example.python.firstRequest"));
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
            {{Loc.T("api.example.pythonRaw.header")}}
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
            {{Loc.T("api.example.curl.listModels")}}
            curl {{BaseUrl}}/models -H "Authorization: Bearer {{key}}"

            {{Loc.T("api.example.curl.chat")}}
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
            {{Loc.T("api.example.powershell.listModels")}}
            {{headerLines}}
            Invoke-RestMethod -Uri "{{BaseUrl}}/models" -Headers $headers | ConvertTo-Json -Depth 6

            {{Loc.T("api.example.powershell.chat")}}
            $body = @{
                model    = "{{ModelForExamples}}"
                messages = @(@{ role = "user"; content = "{{Prompt}}" })
                stream   = $false
            } | ConvertTo-Json -Depth 6

            Invoke-RestMethod -Uri "{{BaseUrl}}/chat/completions" -Method Post -Headers $headers -Body $body |
                ConvertTo-Json -Depth 8

            {{Loc.T("api.example.powershell.streaming")}}
            """;
    }

    private void Copy(string text, string what)
    {
        if (ClipboardHelper.SetText(text))
        {
            SetStatus(Loc.T("api.status.copied", what));
        }
    }

    private async Task RegenerateKeyAsync()
    {
        if (!Services.Dialogs.Confirm(Loc.T("api.regenerate.title"), Loc.T("api.regenerate.confirm")))
        {
            return;
        }

        Services.SaveSettings(s => s.Api.ApiKey = ApiKeyGenerator.Create());
        Services.Logs.Redactor.RegisterSecret(Services.Current.Api.ApiKey);
        Revealed = true;
        SetStatus(Loc.T("api.status.keyRegenerated"));
        Services.Notify(Loc.T("api.notify.keyRegenerated"));
        await Task.CompletedTask.ConfigureAwait(true);
    }
}
