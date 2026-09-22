using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LocalAIModelManager.App.Infrastructure;
using LocalAIModelManager.App.Services;
using LocalAIModelManager.App.ViewModels;
using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.App.Services;

/// <summary>
/// Notification area integration: show/hide the window, start and stop models, and
/// exit. The menu is rebuilt every time it opens so it always reflects live state.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private bool _disposed;

    public TrayIconService(AppServices services, ShellViewModel shell)
    {
        _services = services;
        _shell = shell;
        _icon = CreateIcon();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Local AI Model Manager",
            Visible = true,
        };

        _notifyIcon.DoubleClick += (_, _) => _shell.RequestShow();
        _notifyIcon.ContextMenuStrip = new ContextMenuStrip();
        _notifyIcon.ContextMenuStrip.Opening += (_, _) => RebuildMenu(_notifyIcon.ContextMenuStrip!);
    }

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.ShowBalloonTip(3000);
        }
        catch (Exception)
        {
            // Balloon tips are best effort.
        }
    }

    private void RebuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        var showItem = new ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => _shell.RequestShow();
        menu.Items.Add(showItem);

        var hideItem = new ToolStripMenuItem("隐藏到托盘");
        hideItem.Click += (_, _) => _shell.RequestHide();
        menu.Items.Add(hideItem);

        menu.Items.Add(new ToolStripSeparator());

        var gatewayItem = new ToolStripMenuItem(
            _services.Gateway.IsRunning ? $"网关：{_services.Gateway.BaseUrl}" : "网关：已停止")
        {
            Enabled = false,
        };
        menu.Items.Add(gatewayItem);

        var modelsMenu = new ToolStripMenuItem("模型");
        var models = _services.Models.All;
        if (models.Count == 0)
        {
            modelsMenu.DropDownItems.Add(new ToolStripMenuItem("（尚未注册模型）") { Enabled = false });
        }
        else
        {
            foreach (var model in models)
            {
                var status = _services.Lifecycle.GetStatus(model.Id);
                var stateLabel = Labels.ModelState(status?.State ?? ModelState.Standby);
                var modelItem = new ToolStripMenuItem($"{model.DisplayName} — {stateLabel}");

                var start = new ToolStripMenuItem("启动 / 加载");
                start.Click += (_, _) => RunModelCommand(model.Id, start: true);
                modelItem.DropDownItems.Add(start);

                var stop = new ToolStripMenuItem("停止 / 卸载");
                stop.Click += (_, _) => RunModelCommand(model.Id, start: false);
                modelItem.DropDownItems.Add(stop);

                var restart = new ToolStripMenuItem("重启");
                restart.Click += async (_, _) =>
                {
                    try
                    {
                        await _services.Lifecycle.RestartAsync(model.Id, CancellationToken.None).ConfigureAwait(true);
                        _services.Notify($"模型 {model.Id} 已重启。");
                    }
                    catch (Exception ex)
                    {
                        _services.Notify($"重启 {model.Id} 失败：{ex.Message}");
                    }
                };
                modelItem.DropDownItems.Add(restart);

                modelsMenu.DropDownItems.Add(modelItem);
            }
        }

        menu.Items.Add(modelsMenu);

        var unloadAll = new ToolStripMenuItem("卸载全部模型");
        unloadAll.Click += async (_, _) =>
        {
            try
            {
                await _services.Lifecycle.UnloadAllAsync(CancellationToken.None).ConfigureAwait(true);
                _services.Notify("已卸载全部模型并释放显存。");
            }
            catch (Exception ex)
            {
                _services.Notify($"卸载全部模型失败：{ex.Message}");
            }
        };
        menu.Items.Add(unloadAll);

        menu.Items.Add(new ToolStripSeparator());

        var restartGateway = new ToolStripMenuItem("按当前设置重启网关");
        restartGateway.Click += (_, _) => _ = _shell.RestartGatewayAsync();
        menu.Items.Add(restartGateway);

        var openConfig = new ToolStripMenuItem("打开配置目录");
        openConfig.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { _services.ConfigDirectory },
            UseShellExecute = true,
        });
        menu.Items.Add(openConfig);

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => _shell.RequestExit();
        menu.Items.Add(exit);
    }

    private void RunModelCommand(string modelId, bool start)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (start)
                {
                    await _services.Lifecycle.StartAsync(modelId, CancellationToken.None).ConfigureAwait(false);
                    _services.Notify($"模型 {modelId} 已加载。");
                }
                else
                {
                    await _services.Lifecycle.StopAsync(modelId, CancellationToken.None).ConfigureAwait(false);
                    _services.Notify($"模型 {modelId} 已卸载，显存已释放。");
                }
            }
            catch (Exception ex)
            {
                _services.Notify($"{(start ? "启动" : "停止")} {modelId} 失败：{ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(255, 46, 62, 110));
            graphics.FillEllipse(background, 0, 0, 31, 31);

            using var accent = new SolidBrush(Color.FromArgb(255, 76, 141, 255));
            graphics.FillEllipse(accent, 5, 5, 21, 21);

            using var font = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.FromArgb(255, 11, 13, 18));
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            graphics.DrawString("AI", font, textBrush, new RectangleF(0, 0, 32, 32), format);
        }

        // Clone so the icon owns its own handle; the source handle stays alive for
        // the lifetime of the process (a single 32x32 icon).
        using var temporary = Icon.FromHandle(bitmap.GetHicon());
        return (Icon)temporary.Clone();
    }
}
