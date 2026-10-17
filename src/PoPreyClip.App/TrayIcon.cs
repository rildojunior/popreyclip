using System.Windows;
using PoPreyClip.App.ViewModels;
using PoPreyClip.Config;
using Forms = System.Windows.Forms;

namespace PoPreyClip.App;

/// <summary>Notification-area icon: keeps PoPreyClip reachable while the window is hidden.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly MainWindow _window;
    private readonly MainViewModel _viewModel;
    private readonly Forms.ToolStripMenuItem _toggleItem;
    private bool _hintShown;

    public TrayIcon(MainWindow window, MainViewModel viewModel, Action exit)
    {
        _window = window;
        _viewModel = viewModel;
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir PoPreyClip", null, (_, _) => window.BringToFront());
        menu.Items.Add("Salvar clipe agora", null, (_, _) => viewModel.SaveClipCommand.Execute(null));
        menu.Items.Add("Capturar tela", null, (_, _) => viewModel.SaveScreenshotCommand.Execute(null));
        _toggleItem = new Forms.ToolStripMenuItem("Parar gravação", null, (_, _) => viewModel.ToggleRecordingCommand.Execute(null));
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => exit());
        menu.Opening += (_, _) => _toggleItem.Text = viewModel.IsRecording ? "Parar gravação" : "Iniciar gravação";

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "PoPreyClip",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => window.BringToFront();
        _icon.BalloonTipClicked += (_, _) => window.BringToFront();

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsRecording))
            {
                _icon.Text = viewModel.IsRecording ? "PoPreyClip · gravando" : viewModel.IsWaitingForGame ? "PoPreyClip · em espera" : "PoPreyClip · parado";
            }
        };
        viewModel.Updates.UpdateFound += update =>
        {
            if (!_window.IsVisible || _window.WindowState == WindowState.Minimized)
            {
                _icon.ShowBalloonTip(4000, "Nova versão do PoPreyClip", $"A versão {update.Version.ToString(3)} está disponível. Abra o app para atualizar.", Forms.ToolTipIcon.None);
            }
        };
        viewModel.ScreenshotSaved += info => AnnounceSaved("Captura de tela salva", info.Game);
        viewModel.ClipSaved += info => AnnounceSaved("Clipe salvo", $"{info.Game} · {viewModel.Settings.ClipSecondsText}");
        viewModel.Settings.NoticeTestRequested += () =>
            Announce(viewModel.Settings.SavedNotice.Value, "Clipe salvo", "Assim o aviso aparece durante o jogo");
        window.HiddenToTray += () =>
        {
            if (!_hintShown)
            {
                _hintShown = true;
                _icon.ShowBalloonTip(3000, "PoPreyClip continua aberto", "A gravação segue na bandeja. Clique duas vezes no ícone para abrir.", Forms.ToolTipIcon.None);
            }
        };
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

    private void Announce(string style, string title, string detail)
    {
        switch (style)
        {
            case AppConfig.NoticeOverlay:
                OverlayNotice.Notify(title, detail);
                break;
            case AppConfig.NoticeWindows:
                _icon.ShowBalloonTip(3000, title, detail, Forms.ToolTipIcon.None);
                break;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
