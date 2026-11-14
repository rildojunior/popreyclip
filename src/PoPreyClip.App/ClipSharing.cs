using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PoPreyClip.Library;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace PoPreyClip.App;

/// <summary>
/// Hands a clip or screenshot to other programs without making a new file: the clipboard (as a file, like Ctrl+C in
/// Explorer), drag and drop, the Windows Share window, or opening Discord / WhatsApp so the player pastes it there.
/// No program lets another one pick the conversation, so the last step is always the player's.
/// </summary>
internal static class ClipSharing
{
    /// <summary>Discord's upload limit without Nitro (20 MB since August 2026). Measured in decimal MB to be safe.</summary>
    public const long DiscordFreeLimitBytes = 20_000_000;

    /// <summary>
    /// The file as Explorer copies it (file list + "copy, not move"). A screenshot also goes as an image, so it pastes
    /// in programs that only take pictures (Paint, sites, image editors).
    /// </summary>
    public static DataObject CreateData(ClipInfo clip, bool includeImage)
    {
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { clip.Path });
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
        if (includeImage && clip.IsScreenshot && LoadImage(clip.Path) is { } image)
        {
            data.SetImage(image);
        }

        return data;
    }

    /// <summary>Puts the clip on the clipboard. False (and a log line) when the clipboard is busy or the file is gone.</summary>
    public static bool CopyToClipboard(ClipInfo clip)
    {
        if (!File.Exists(clip.Path))
        {
            Log.Warn($"Arquivo não encontrado para copiar: {clip.Path}");
            return false;
        }

        var data = CreateData(clip, includeImage: true);
        // Another program may be holding the clipboard for a moment; try a few times before giving up.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(data, copy: true);
                Log.Info($"Copiado para a área de transferência: {Path.GetFileName(clip.Path)}");
                return true;
            }
            catch (ExternalException ex) when (attempt < 4)
            {
                Log.Warn($"Área de transferência ocupada, tentando de novo: {ex.Message}");
                Thread.Sleep(60);
            }
            catch (ExternalException ex)
            {
                Log.Error($"Não foi possível copiar para a área de transferência: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Starts dragging the file out of the window (to Discord, WhatsApp, Explorer, a browser...).</summary>
    public static void StartDrag(DependencyObject source, ClipInfo clip)
    {
        if (!File.Exists(clip.Path))
        {
            return;
        }

        try
        {
            DragDrop.DoDragDrop(source, CreateData(clip, includeImage: false), DragDropEffects.Copy);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            Log.Warn($"Não foi possível arrastar o arquivo: {ex.Message}");
        }
    }

    /// <summary>Whether a link like "discord:" or "whatsapp:" opens an installed app.</summary>
    public static bool IsAppInstalled(string scheme)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(scheme);
            return key?.GetValue("URL Protocol") is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Opens an app or site by its link. False when Windows could not open it.</summary>
    public static bool Open(string link)
    {
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Não foi possível abrir {link}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Opens the Windows Share window for the file (WhatsApp, e-mail, Nearby sharing and other apps).</summary>
    public static bool ShowShareWindow(Window owner, ClipInfo clip)
    {
        var hwnd = new WindowInteropHelper(owner).Handle;
        if (hwnd == IntPtr.Zero || !File.Exists(clip.Path))
        {
            return false;
        }

        try
        {
            var manager = DataTransferManagerInterop.GetForWindow(hwnd);
            TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? handler = null;
            handler = async (sender, args) =>
            {
                sender.DataRequested -= handler;
                var request = args.Request;
                var deferral = request.GetDeferral();
                try
                {
                    request.Data.Properties.Title = Path.GetFileName(clip.Path);
                    request.Data.Properties.Description = clip.IsScreenshot ? "Captura de tela do PoPreyClip" : "Clipe do PoPreyClip";
                    var file = await StorageFile.GetFileFromPathAsync(clip.Path);
                    request.Data.SetStorageItems(new IStorageItem[] { file }, readOnly: true);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Não foi possível preparar o compartilhamento: {ex.Message}");
                    request.FailWithDisplayText("Não foi possível abrir o arquivo para compartilhar.");
                }
                finally
                {
                    deferral.Complete();
                }
            };
            manager.DataRequested += handler;
            DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            Log.Warn($"Não foi possível abrir a janela Compartilhar do Windows: {ex.Message}");
            return false;
        }
    }

    private static BitmapSource? LoadImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null; // The file still goes; only the picture copy is missing.
        }
    }
}
