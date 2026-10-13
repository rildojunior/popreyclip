using System.Diagnostics;
using System.Globalization;
using PoPreyClip;
using PoPreyClip.Clips;
using PoPreyClip.Config;
using PoPreyClip.Session;
using PoPreyClip.Hotkeys;
using PoPreyClip.Audio;
using PoPreyClip.Recording;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.Title = "PoPreyClip";

// Opened with a double click (no arguments): start recording right away.
var launchedWithoutArguments = args.Length == 0;
var command = launchedWithoutArguments ? "run" : args[0].ToLowerInvariant();
var exitCode = 1;

try
{
    var options = CommandOptions.Parse(args.Skip(1).ToArray());
    Log.Verbose = options.Flags.Contains("verbose");
    exitCode = command switch
    {
        "run" => await RunAsync(options),
        "remix" => await RemixAsync(options),
        "screenshot" => await ScreenshotAsync(options),
        "devices" => ListDevices(),
        "apps" => ListApps(),
        "config" => ShowConfig(options),
        _ => Help(),
    };
}
catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException or System.Text.Json.JsonException)
{
    Log.Error(ex.Message);
}

if (launchedWithoutArguments && exitCode != 0 && !Console.IsInputRedirected)
{
    // Keep the window open so the error can be read.
    Console.WriteLine();
    Console.WriteLine("Pressione Enter para fechar.");
    Console.ReadLine();
}

return exitCode;

static int Help()
{
    Console.WriteLine("""
        PoPreyClip - clipes de jogos com uma pasta por jogo e áudio separado por aplicativo

        Uso:
          PoPreyClip run [--config <arquivo>] [--clip-after <s>] [--exit-after-clip] [--verbose]
              Inicia o buffer de replay. O atalho (padrão F8) ou Enter salva os últimos segundos.
          PoPreyClip screenshot [--config <arquivo>]
              Salva uma captura de tela PNG na pasta Screenshots do jogo atual.
          PoPreyClip remix <clipe.mp4> [--vol "Discord=40"] [--vol "Jogo=120"] [--out <arquivo>]
              Remixa as faixas separadas de um clipe com novos volumes (0 a 200).
          PoPreyClip devices        Lista os microfones.
          PoPreyClip apps           Lista aplicativos com janela, para usar como fonte de áudio.
          PoPreyClip config         Mostra o caminho e o conteúdo da configuração.
        """);
    return 0;
}

static async Task<int> RunAsync(CommandOptions options)
{
    using var store = ConfigStore.LoadOrCreate(options.Get("config"));
    store.StartWatching();
    var config = store.Current;
    Log.Info($"Configuração: {store.FilePath}");
    Log.Info($"Pasta dos clipes: {Path.Combine(config.ClipFolder, ClipPathBuilder.ClipsSubfolder)}\\<Jogo>");

    using var session = new RecordingSession(store);
    using var quit = new CancellationTokenSource();
    session.Failed += _ => quit.Cancel();
    try
    {
        session.Start();
    }
    catch (InvalidOperationException ex)
    {
        Log.Error(ex.Message);
        Log.Error($"Ou informe o caminho em \"ffmpegPath\" e \"ffprobePath\" no arquivo {store.FilePath}");
        return 1;
    }

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        quit.Cancel();
    };

    async Task SaveClipAsync()
    {
        if (await session.SaveClipAsync() is not null)
        {
            Console.Beep(1200, 120);
        }
    }

    using var hotkeys = new HotkeyManager();
    hotkeys.Register(config.Hotkey, () => _ = SaveClipAsync());
    Log.Info($"Gravando. Pressione {config.Hotkey} (ou Enter aqui) para salvar um clipe, Ctrl+C para sair.");

    if (!Console.IsInputRedirected)
    {
        _ = Task.Run(async () =>
        {
            while (!quit.IsCancellationRequested)
            {
                var line = Console.ReadLine();
                if (line is null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
                {
                    await quit.CancelAsync();
                    return;
                }

                await SaveClipAsync();
            }
        });
    }

    if (options.Get("clip-after") is { } clipAfter)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(double.Parse(clipAfter, CultureInfo.InvariantCulture)), quit.Token);
            await SaveClipAsync();
            if (options.Flags.Contains("exit-after-clip"))
            {
                await quit.CancelAsync();
            }
        });
    }

    try
    {
        await Task.Delay(Timeout.Infinite, quit.Token);
    }
    catch (TaskCanceledException)
    {
    }

    Log.Info("Encerrando...");
    var failed = !session.IsRecording;
    session.Stop();
    return failed ? 1 : 0;
}

static async Task<int> ScreenshotAsync(CommandOptions options)
{
    using var store = ConfigStore.LoadOrCreate(options.Get("config"));
    using var session = new RecordingSession(store);
    await Task.Delay(300);
    return await session.SaveScreenshotAsync() is null ? 1 : 0;
}

static async Task<int> RemixAsync(CommandOptions options)
{
    var input = options.Positional.FirstOrDefault() ?? throw new FormatException("Informe o arquivo do clipe.");
    var config = ConfigStore.LoadOrCreate(options.Get("config")).Current;
    var volumes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in options.GetAll("vol"))
    {
        var separator = entry.LastIndexOf('=');
        if (separator <= 0 || !int.TryParse(entry[(separator + 1)..], out var volume))
        {
            throw new FormatException($"Volume inválido: {entry}. Use Nome=Valor, por exemplo Discord=40.");
        }

        volumes[entry[..separator].Trim()] = volume;
    }

    var tracks = await Remixer.ReadTracksAsync(config, input);
    Console.WriteLine("Faixas encontradas: " + string.Join(", ", tracks.Select(t => $"{t.AudioIndex}={t.Title}")));
    var output = await Remixer.RemixAsync(config, input, options.Get("out"), volumes);
    Log.Info($"Clipe remixado: {output}");
    return 0;
}

static int ListDevices()
{
    foreach (var device in AudioDevices.ListMicrophones())
    {
        Console.WriteLine($"{(device.IsDefault ? "*" : " ")} {device.Name}");
    }

    Console.WriteLine("* = padrão do Windows. Use parte do nome em \"device\" da fonte \"microphone\".");
    return 0;
}

static int ListApps()
{
    var apps = Process.GetProcesses()
        .Where(p =>
        {
            try
            {
                return p.MainWindowHandle != IntPtr.Zero;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        })
        .Select(p => (Exe: p.ProcessName + ".exe", Title: p.MainWindowTitle))
        .DistinctBy(a => a.Exe, StringComparer.OrdinalIgnoreCase)
        .OrderBy(a => a.Exe, StringComparer.OrdinalIgnoreCase);
    foreach (var (exe, title) in apps)
    {
        Console.WriteLine($"{exe,-40} {title}");
    }

    Console.WriteLine("Use o nome do executável como \"id\" de uma fonte de áudio, por exemplo \"Spotify.exe\".");
    return 0;
}

static int ShowConfig(CommandOptions options)
{
    var store = ConfigStore.LoadOrCreate(options.Get("config"));
    Console.WriteLine(store.FilePath);
    Console.WriteLine(File.ReadAllText(store.FilePath));
    return 0;
}

internal sealed class CommandOptions
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FlagNames = new(StringComparer.OrdinalIgnoreCase) { "verbose", "exit-after-clip" };

    public List<string> Positional { get; } = [];

    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static CommandOptions Parse(string[] args)
    {
        var options = new CommandOptions();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                options.Positional.Add(args[i]);
                continue;
            }

            var name = args[i][2..];
            if (FlagNames.Contains(name))
            {
                options.Flags.Add(name);
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new FormatException($"A opção --{name} precisa de um valor.");
            }

            if (!options._values.TryGetValue(name, out var list))
            {
                options._values[name] = list = [];
            }

            list.Add(args[++i]);
        }

        return options;
    }

    public string? Get(string name) => _values.TryGetValue(name, out var list) ? list[^1] : null;

    public IEnumerable<string> GetAll(string name) => _values.TryGetValue(name, out var list) ? list : [];
}
