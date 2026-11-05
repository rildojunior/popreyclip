using System.Windows;
using PoPreyClip.App.ViewModels;
using PoPreyClip.Config;
using PoPreyClip.Games;
using Forms = System.Windows.Forms;

namespace PoPreyClip.App;

/// <summary>Notification-area icon: keeps PoPreyClip reachable while the window is hidden.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly MainWindow _window;
    private readonly MainViewModel _viewModel;
    private readonly Forms.ToolStripMenuItem _toggleItem;
    private static readonly TimeSpan GameReadyTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ClipFailedTime = TimeSpan.FromSeconds(6);
    private bool _hintShown;

    /// <summary>New version found during a game; announced when the game closes.</summary>
    private PoPreyClip.Updates.UpdateInfo? _pendingUpdate;

    public TrayIcon(MainWindow window, MainViewModel viewModel, Action exit)
    {
        _window = window;
        _viewModel = viewModel;
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;

        var menu = new Forms.ContextMenuStrip();
        // Status line at the top, not clickable, like the text shown when the mouse is over the icon.
        var statusItem = new Forms.ToolStripMenuItem(viewModel.TrayStatus) { Enabled = false };
        menu.Items.Add(statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Abrir PoPreyClip", null, (_, _) => window.BringToFront());
        menu.Items.Add("Salvar clipe agora", null, (_, _) => viewModel.SaveClipCommand.Execute(null));
        menu.Items.Add("Capturar tela", null, (_, _) => viewModel.SaveScreenshotCommand.Execute(null));
        _toggleItem = new Forms.ToolStripMenuItem(viewModel.ToggleText, null, (_, _) => viewModel.ToggleRecordingCommand.Execute(null));
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => exit());
        menu.Opening += (_, _) =>
        {
            statusItem.Text = viewModel.TrayStatus;
            _toggleItem.Text = viewModel.ToggleText;
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = viewModel.TrayText,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => window.BringToFront();
        _icon.BalloonTipClicked += (_, _) => window.BringToFront();

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TrayText))
            {
                _icon.Text = viewModel.TrayText;
            }
            else if (e.PropertyName == nameof(MainViewModel.IsGameOpen) && !viewModel.IsGameOpen && _pendingUpdate is { } pending)
            {
                _pendingUpdate = null;
                AnnounceUpdate(pending);
            }
        };
        viewModel.Updates.UpdateFound += update =>
        {
            // A notice in the middle of a match is missed or gets in the way: wait for the game to close.
            if (viewModel.IsGameOpen)
            {
                _pendingUpdate = update;
            }
            else
            {
                AnnounceUpdate(update);
            }
        };
        viewModel.ScreenshotSaved += info => AnnounceSaved("Captura de tela salva", info.Game);
        // Like Medal, "Clipe salvo" shows when the key is accepted; the file is ready a second or two later.
        viewModel.ClipStarted += game => AnnounceSaved("Clipe salvo", $"{game} · {viewModel.Settings.ClipSecondsText}");
        viewModel.ClipFailed += AnnounceClipFailed;
        viewModel.GameReady += AnnounceGameReady;
        viewModel.Settings.NoticeTestRequested += () =>
            Announce(viewModel.Settings.SavedNotice.Value, "Clipe salvo", "Assim o aviso aparece durante o jogo");
        window.HiddenToTray += () =>
        {
            if (!_hintShown)
            {
                _hintShown = true;
                _icon.ShowBalloonTip(3000, "PoPreyClip continua aberto", "A captura de clipes segue na bandeja. Clique duas vezes no ícone para abrir.", Forms.ToolTipIcon.None);
            }
        };
    }

    private void AnnounceUpdate(PoPreyClip.Updates.UpdateInfo update)
    {
        if (!_window.IsVisible || _window.WindowState == WindowState.Minimized)
        {
            _icon.ShowBalloonTip(4000, "Nova versão do PoPreyClip", $"A versão {update.Version.ToString(3)} está disponível. Abra o app para atualizar.", Forms.ToolTipIcon.None);
        }
    }

    /// <summary>
    /// Announces a saved file in the style chosen in the settings. Skipped while the PoPreyClip window is in front,
    /// because the window already shows its own message.
    /// </summary>
    private void AnnounceSaved(string title, string detail)
    {
        if (!_window.IsActive)
        {
            Announce(_viewModel.Settings.SavedNotice.Value, title, detail);
        }
    }

    /// <summary>
    /// Like Medal's "Ready to clip": when a game is detected, says the clip capture is on and which keys to use.
    /// Follows the same notice style as the saved files, and is skipped while the PoPreyClip window is in front.
    /// </summary>
    private void AnnounceGameReady(DetectedGame game)
    {
        if (_window.IsActive)
        {
            return;
        }

        var settings = _viewModel.Settings;
        var lines = new List<string> { game.DisplayName, $"{settings.SaveClipHotkey} salva os últimos {settings.ClipSecondsText}" };
        if (!string.IsNullOrWhiteSpace(settings.ScreenshotHotkey))
        {
            lines.Add($"{settings.ScreenshotHotkey} captura a tela");
        }

        Announce(settings.SavedNotice.Value, "Pronto para clipar", string.Join("\n", lines), OverlayNotice.GameGlyph, GameReadyTime);
    }

    /// <summary>
    /// The "Clipe salvo" notice has already shown, so a failure must be said too: in the notice style chosen while
    /// the window is hidden, or in the window's error strip when it is in front.
    /// </summary>
    private void AnnounceClipFailed(PoPreyClip.Session.ClipFailedInfo info)
    {
        if (_window.IsActive)
        {
            _viewModel.ErrorMessage = $"O clipe de {info.Game} não foi salvo. {info.Reason.Replace('\n', ' ')}";
            return;
        }

        Announce(_viewModel.Settings.SavedNotice.Value, "Clipe não salvo", $"{info.Game}\n{info.Reason}",
            OverlayNotice.ErrorGlyph, ClipFailedTime, error: true);
    }

    private void Announce(string style, string title, string detail, string glyph = OverlayNotice.CheckGlyph, TimeSpan? visibleTime = null, bool error = false)
    {
        switch (style)
        {
            case AppConfig.NoticeOverlay:
                OverlayNotice.Notify(title, detail, glyph, visibleTime, error);
                break;
            case AppConfig.NoticeWindows:
                _icon.ShowBalloonTip(3000, title, detail, error ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.None);
                break;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
