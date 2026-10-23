using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using PoPreyClip.Config;
using PoPreyClip.Voice;

namespace PoPreyClip.App.ViewModels;

/// <summary>An assistant name or a command shown as a chip with a remove button.</summary>
public sealed class VoiceWordItem(string text, Action<string> remove)
{
    public string Text { get; } = text;

    public string Display { get; } = text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.GetCultureInfo("pt-BR")) + text[1..];

    public ICommand RemoveCommand { get; } = new RelayCommand(() => remove(text));
}

/// <summary>"Comando de voz" card: say the assistant's name and a command to save a clip, like Medal's voice clipping.</summary>
public sealed class VoiceCommandsViewModel : ObservableObject
{
    private static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(10);
    private readonly ConfigStore _store;
    private readonly VoiceClipTrigger _trigger;
    private readonly Dispatcher _dispatcher;
    private string _newName = "";
    private string _newCommand = "";
    private string? _namesError;
    private string? _commandsError;
    private bool _isTesting;
    private string? _testText;
    private bool _testAccepted;
    private string? _problem;

    public VoiceCommandsViewModel(ConfigStore store, VoiceClipTrigger trigger, Dispatcher dispatcher)
    {
        _store = store;
        _trigger = trigger;
        _dispatcher = dispatcher;
        AddNameCommand = new RelayCommand(AddName);
        AddVoiceCommandCommand = new RelayCommand(AddCommand);
        TestCommand = new AsyncRelayCommand(TestAsync, () => !IsTesting);
        _problem = trigger.Problem;
        trigger.ProblemChanged += problem => _dispatcher.InvokeAsync(() => Problem = problem);
        store.Changed += _ => _dispatcher.InvokeAsync(Load);
        Load();
    }

    private VoiceConfig V => _store.Current.Voice;

    public bool Enabled
    {
        get => V.Enabled;
        set
        {
            _store.Update(c => c.Voice.Enabled = value);
            OnPropertyChanged();
        }
    }

    public ObservableCollection<VoiceWordItem> Names { get; } = [];

    public ObservableCollection<VoiceWordItem> Commands { get; } = [];

    public string NewName
    {
        get => _newName;
        set => SetProperty(ref _newName, value ?? "");
    }

    public string NewCommand
    {
        get => _newCommand;
        set => SetProperty(ref _newCommand, value ?? "");
    }

    public ICommand AddNameCommand { get; }

    public ICommand AddVoiceCommandCommand { get; }

    public string? NamesError
    {
        get => _namesError;
        private set => SetProperty(ref _namesError, value);
    }

    public string? CommandsError
    {
        get => _commandsError;
        private set => SetProperty(ref _commandsError, value);
    }

    /// <summary>Commands of a single word are easy to say by accident.</summary>
    public string? OneWordWarning
    {
        get
        {
            var oneWord = V.Commands.Where(c => VoicePhrases.WordCount(c) < 2).ToList();
            return oneWord.Count == 0 ? null
                : $"Comandos de uma palavra só ({string.Join(", ", oneWord.Select(c => $"\"{c}\""))}) são ditos sem querer com mais facilidade. Prefira duas ou mais palavras.";
        }
    }

    /// <summary>Every phrase that saves a clip, e.g. "oni clipa isso · oni faz um clipe".</summary>
    public string PhrasesText => string.Join("  ·  ", VoicePhrases.Build(V).Select(p => $"\"{p}\""));

    public IReadOnlyList<Option<string>> SensitivityOptions { get; } =
    [
        new(VoiceConfig.SensitivityStrict, "Rigorosa (só confiança alta)"),
        new(VoiceConfig.SensitivityNormal, "Normal (média e alta)"),
        new(VoiceConfig.SensitivityFlexible, "Flexível (baixa, média e alta)"),
    ];

    public Option<string> Sensitivity
    {
        get => SensitivityOptions.FirstOrDefault(o => o.Value == V.Sensitivity) ?? SensitivityOptions[1];
        set
        {
            if (value is not null)
            {
                _store.Update(c => c.Voice.Sensitivity = value.Value);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Why the voice command can't listen (no Portuguese speech pack, microphone blocked...), or null.</summary>
    public string? Problem
    {
        get => _problem;
        private set => SetProperty(ref _problem, value);
    }

    public ICommand TestCommand { get; }

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string? TestText
    {
        get => _testText;
        private set => SetProperty(ref _testText, value);
    }

    /// <summary>The last test heard a phrase that would save a clip (shown in green).</summary>
    public bool TestAccepted
    {
        get => _testAccepted;
        private set => SetProperty(ref _testAccepted, value);
    }

    private void Load()
    {
        Reload(Names, V.AssistantNames, RemoveName);
        Reload(Commands, V.Commands, RemoveCommand);
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Sensitivity));
        OnPropertyChanged(nameof(PhrasesText));
        OnPropertyChanged(nameof(OneWordWarning));
    }

    private static void Reload(ObservableCollection<VoiceWordItem> target, List<string> items, Action<string> remove)
    {
        if (target.Select(i => i.Text).SequenceEqual(items))
        {
            return;
        }

        target.Clear();
        foreach (var item in items)
        {
            target.Add(new VoiceWordItem(item, remove));
        }
    }

    private void AddName()
    {
        var name = VoicePhrases.Normalize(NewName);
        NamesError = name.Length == 0 ? "Digite um nome."
            : V.AssistantNames.Contains(name) ? "Esse nome já está na lista."
            : null;
        if (NamesError is null)
        {
            _store.Update(c => c.Voice.AssistantNames.Add(name));
            NewName = "";
            Load();
        }
    }

    private void RemoveName(string name)
    {
        NamesError = V.AssistantNames.Count <= 1 ? "Deixe pelo menos um nome. Sem ele, qualquer conversa com o comando salvaria um clipe." : null;
        if (NamesError is null)
        {
            _store.Update(c => c.Voice.AssistantNames.Remove(name));
            Load();
        }
    }

    private void AddCommand()
    {
        var command = VoicePhrases.Normalize(NewCommand);
        CommandsError = command.Length == 0 ? "Digite um comando."
            : V.Commands.Contains(command) ? "Esse comando já está na lista."
            : null;
        if (CommandsError is null)
        {
            _store.Update(c => c.Voice.Commands.Add(command));
            NewCommand = "";
            Load();
        }
    }

    private void RemoveCommand(string command)
    {
        CommandsError = V.Commands.Count <= 1 ? "Deixe pelo menos um comando." : null;
        if (CommandsError is null)
        {
            _store.Update(c => c.Voice.Commands.Remove(command));
            Load();
        }
    }

    /// <summary>Listens for 10 seconds and shows what was understood. Never saves a clip.</summary>
    private async Task TestAsync()
    {
        TestAccepted = false;
        if (VoiceCommandListener.AvailabilityProblem() is { } unavailable)
        {
            TestText = unavailable;
            return;
        }

        var phrases = VoicePhrases.Build(V);
        IsTesting = true;
        TestText = "Preparando o microfone...";
        await _trigger.SetPausedAsync(true);
        var listener = new VoiceCommandListener();
        try
        {
            var heard = new TaskCompletionSource<VoiceHeard>(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.Heard += h =>
            {
                if (h.Phrase.Length > 0)
                {
                    heard.TrySetResult(h);
                }
                else
                {
                    _dispatcher.InvokeAsync(() => TestText = "Ouvi uma fala, mas não era nenhuma das frases. Tente de novo...");
                }
            };

            try
            {
                await listener.StartAsync(phrases);
            }
            catch (InvalidOperationException ex)
            {
                TestText = ex.Message;
                return;
            }

            TestText = $"Escutando por {TestDuration.TotalSeconds:0} segundos... Diga \"{phrases[0]}\".";
            if (await Task.WhenAny(heard.Task, Task.Delay(TestDuration)) != heard.Task)
            {
                TestText = "Nenhuma frase reconhecida em 10 segundos. Fale mais perto do microfone e confira se ele é o microfone padrão do Windows.";
                return;
            }

            var result = heard.Task.Result;
            var confidence = VoicePhrases.Describe(result.Confidence);
            TestAccepted = VoicePhrases.IsAccepted(result.Confidence, V.Sensitivity);
            TestText = TestAccepted
                ? $"Entendi \"{result.Phrase}\" com confiança {confidence}. Isso salvaria um clipe."
                : $"Entendi \"{result.Phrase}\", mas com confiança {confidence}. Com a sensibilidade {Sensitivity.Label.Split(' ')[0]}, isso não salvaria um clipe.";
        }
        finally
        {
            await listener.StopAsync();
            await _trigger.SetPausedAsync(false);
            IsTesting = false;
        }
    }
}
