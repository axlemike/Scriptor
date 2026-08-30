using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GUI.ViewModel;
using Scripts.Scriptor;
using Scripts.Scriptor.Conductor;

namespace GUI
{
    public sealed partial class MainWindow : Window
    {
        private ScriptRuntimeService _runtime = null!;
        private readonly List<ParameterViewModel> _parameterViewModels = new();
        private readonly ObservableCollection<ScriptNode> _treeNodes = new();
        private readonly Dictionary<string, ScriptRoutineDescriptor> _routinesById = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PlaylistDefinition> _playlists = new();
        private readonly List<CommandDefinition> _commands = new();
        private readonly Dictionary<string, RoutineRunRowUi> _runRowsByScope = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] SpinnerFrames = ["|", "/", "-", "\\"];
        private const string PlaylistItemDragFormat = "application/x-scriptor-playlist-item";
        private const uint MessageBeepInformation = 0x00000040;
        private const string DefaultCommandIconUri = "avares://GUI/Assets/Scriptor_Icon_Pack/Scriptor_32x32.png";
        private readonly DispatcherTimer _statusSpinnerTimer = new() { Interval = TimeSpan.FromMilliseconds(110) };
        private readonly DispatcherTimer _commandsReloadTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private int _spinnerFrameIndex;
        private static readonly IBrush PanelBorderBrush = new SolidColorBrush(Color.Parse("#3F3F46"));
        private static readonly IBrush PanelFillBrush = new SolidColorBrush(Color.Parse("#1E1E1E"));
        private static readonly IBrush RunningStatusBrush = new SolidColorBrush(Color.Parse("#00BCF2"));
        private static readonly IBrush SuccessStatusBrush = new SolidColorBrush(Color.Parse("#16C60C"));
        private static readonly IBrush FailureStatusBrush = new SolidColorBrush(Color.Parse("#F44747"));
        private static readonly IBrush IdleStatusBrush = new SolidColorBrush(Color.Parse("#808080"));
        private static readonly IBrush StatusTextOnBadgeBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));

        private TreeView _collectionsTree = null!;
        private TextBlock _collectionDescriptionBox = null!;
        private StackPanel _parameterPanel = null!;
        private TextBlock _routineDescriptionBox = null!;
        private Button _reloadButton = null!;
        private Button _runButton = null!;
        private Button _newPlaylistButton = null!;
        private Button _editPlaylistsButton = null!;
        private Button _generateProjectButton = null!;
        private Button _settingsButton = null!;
        private Button _runSelectedButton = null!;
        private Button _copyRunLogButton = null!;
        private TextBlock _scriptPathStatus = null!;
        private Border _compilationWarningBanner = null!;
        private TextBlock _compilationWarningBannerText = null!;
        private StackPanel _runLogRowsPanel = null!;
        private ScrollViewer _runLogScrollViewer = null!;

        private ScriptRoutineDescriptor? _currentRoutine;
        private ScriptNode? _selectedNode;
        private Action<List<ParameterViewModel>>? _saveDefaultsAction;
        private string _scriptsRoot = string.Empty;
        private readonly SettingsService _settingsService;
        private readonly GlobalHotKey _quickCommandHotKey = new();
        private FileSystemWatcher? _commandsWatcher;
        private ScriptNode? _draggedPlaylistItemNode;
        private Point _dragStartPoint;
        private QuickCommandWindow? _quickCommandWindow;

        public MainWindow()
        {
            _settingsService = new SettingsService(SettingsService.GetDefaultSettingsPath());
            _scriptsRoot = Path.GetFullPath(ResolveScriptsRoot(_settingsService.Current));
            InitializeComponent();

            RestoreWindowState();

            _reloadButton.Click += ReloadButton_Click;
            _runButton.Click += RunButton_Click;
            _newPlaylistButton.Click += NewPlaylistButton_Click;
            _editPlaylistsButton.Click += EditPlaylistsButton_Click;
            _generateProjectButton.Click += GenerateProjectButton_Click;
            _settingsButton.Click += SettingsButton_Click;
            _runSelectedButton.Click += RunButton_Click;
            _copyRunLogButton.Click += CopyRunLogButton_Click;
            _collectionsTree.SelectionChanged += CollectionsTree_SelectionChanged;
            _settingsService.SettingsChanged += SettingsService_SettingsChanged;

            var scriptsRoot = ResolveScriptsRoot(_settingsService.Current);
            InitializeRuntime(scriptsRoot);

            Logger.EntryWritten += Logger_EntryWritten;

            _statusSpinnerTimer.Tick += StatusSpinnerTimer_Tick;
            _statusSpinnerTimer.Start();
            _commandsReloadTimer.Tick += CommandsReloadTimer_Tick;
            _quickCommandHotKey.Pressed += QuickCommandHotKey_Pressed;
            Opened += MainWindow_Opened;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            _collectionsTree = this.FindControl<TreeView>("CollectionsTree") ?? throw new InvalidOperationException("CollectionsTree not found.");
            _collectionDescriptionBox = this.FindControl<TextBlock>("CollectionDescription") ?? throw new InvalidOperationException("CollectionDescription not found.");
            _parameterPanel = this.FindControl<StackPanel>("ParameterPanel") ?? throw new InvalidOperationException("ParameterPanel not found.");
            _routineDescriptionBox = this.FindControl<TextBlock>("RoutineDescription") ?? throw new InvalidOperationException("RoutineDescription not found.");
            _reloadButton = this.FindControl<Button>("ReloadButton") ?? throw new InvalidOperationException("ReloadButton not found.");
            _runButton = this.FindControl<Button>("RunButton") ?? throw new InvalidOperationException("RunButton not found.");
            _newPlaylistButton = this.FindControl<Button>("NewPlaylistButton") ?? throw new InvalidOperationException("NewPlaylistButton not found.");
            _editPlaylistsButton = this.FindControl<Button>("EditPlaylistsButton") ?? throw new InvalidOperationException("EditPlaylistsButton not found.");
            _generateProjectButton = this.FindControl<Button>("GenerateProjectButton") ?? throw new InvalidOperationException("GenerateProjectButton not found.");
            _settingsButton = this.FindControl<Button>("SettingsButton") ?? throw new InvalidOperationException("SettingsButton not found.");
            _runSelectedButton = this.FindControl<Button>("RunSelectedButton") ?? throw new InvalidOperationException("RunSelectedButton not found.");
            _copyRunLogButton = this.FindControl<Button>("CopyRunLogButton") ?? throw new InvalidOperationException("CopyRunLogButton not found.");
            _scriptPathStatus = this.FindControl<TextBlock>("ScriptPathStatus") ?? throw new InvalidOperationException("ScriptPathStatus not found.");
            _compilationWarningBanner = this.FindControl<Border>("CompilationWarningBanner") ?? throw new InvalidOperationException("CompilationWarningBanner not found.");
            _compilationWarningBannerText = this.FindControl<TextBlock>("CompilationWarningBannerText") ?? throw new InvalidOperationException("CompilationWarningBannerText not found.");
            _runLogRowsPanel = this.FindControl<StackPanel>("RunLogRowsPanel") ?? throw new InvalidOperationException("RunLogRowsPanel not found.");
            _runLogScrollViewer = this.FindControl<ScrollViewer>("RunLogScrollViewer") ?? throw new InvalidOperationException("RunLogScrollViewer not found.");
        }

        private void InitializeRuntime(string scriptsRoot)
        {
            var normalizedScriptsRoot = Path.GetFullPath(scriptsRoot);
            Directory.CreateDirectory(normalizedScriptsRoot);

            if (_runtime != null)
            {
                _runtime.ScriptsReloaded -= Runtime_ScriptsReloaded;
                _runtime.CompilationFailed -= Runtime_CompilationFailed;
                _runtime.Dispose();
            }

            _scriptsRoot = normalizedScriptsRoot;
            Logger.ConfigureFileLogging(_scriptsRoot);
            Title = $"Scriptor GUI - {_scriptsRoot}";
            _scriptPathStatus.Text = $"Scripts path: {_scriptsRoot}";

            _runtime = new ScriptRuntimeService(_scriptsRoot);
            _runtime.ScriptsReloaded += Runtime_ScriptsReloaded;
            _runtime.CompilationFailed += Runtime_CompilationFailed;
            _runtime.StartWatching();
            ConfigureCommandsWatcher();
            _runtime.ReloadScripts();
        }

        private void ConfigureCommandsWatcher()
        {
            _commandsWatcher?.Dispose();

            var commandsDirectory = Path.GetDirectoryName(GetCommandsPath())!;
            Directory.CreateDirectory(commandsDirectory);
            _commandsWatcher = new FileSystemWatcher(commandsDirectory, Path.GetFileName(GetCommandsPath()))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _commandsWatcher.Changed += CommandsWatcher_Changed;
            _commandsWatcher.Created += CommandsWatcher_Changed;
            _commandsWatcher.Deleted += CommandsWatcher_Changed;
            _commandsWatcher.Renamed += CommandsWatcher_Renamed;
        }

        private void CommandsWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            Dispatcher.UIThread.Post(RestartCommandsReloadTimer);
        }

        private void CommandsWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            Dispatcher.UIThread.Post(RestartCommandsReloadTimer);
        }

        private void RestartCommandsReloadTimer()
        {
            _commandsReloadTimer.Stop();
            _commandsReloadTimer.Start();
        }

        private void CommandsReloadTimer_Tick(object? sender, EventArgs e)
        {
            _commandsReloadTimer.Stop();
            _commands.Clear();
            _commands.AddRange(LoadCommands());
            RefreshCommandsTree();
            AppendLog("Reloaded commands.");
        }

        private void MainWindow_Opened(object? sender, EventArgs e)
        {
            if (!_quickCommandHotKey.TryRegister(out var error))
            {
                AppendLog($"Windows+Alt+S quick command is unavailable: {error}");
            }
        }

        private void QuickCommandHotKey_Pressed(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(ShowQuickCommandWindow);
        }

        private void ShowQuickCommandWindow()
        {
            if (_quickCommandWindow != null)
            {
                _quickCommandWindow.Activate();
                return;
            }

            _quickCommandWindow = new QuickCommandWindow(
                _routinesById.Values.OrderBy(routine => routine.Name).ToList(),
                _commands.OrderBy(command => command.Name).ToList(),
                RunQuickRoutineAsync,
                ExecuteCommandAsync,
                ExecuteQuickCommand);
            _quickCommandWindow.Closed += (_, _) => _quickCommandWindow = null;

            if (GlobalHotKey.TryGetCursorPosition(out var cursorX, out var cursorY)
                && Screens.ScreenFromPoint(new PixelPoint(cursorX, cursorY)) is { } screen)
            {
                var width = (int)Math.Round(_quickCommandWindow.Width * screen.Scaling);
                var height = (int)Math.Round(_quickCommandWindow.Height * screen.Scaling);
                _quickCommandWindow.Position = new PixelPoint(
                    screen.Bounds.X + Math.Max(0, (screen.Bounds.Width - width) / 2),
                    screen.Bounds.Y + Math.Max(0, (screen.Bounds.Height - height) / 2));
            }

            _quickCommandWindow.Show();
            _quickCommandWindow.Activate();
        }

        private async Task<string?> RunQuickRoutineAsync(
            ScriptRoutineDescriptor routine,
            IReadOnlyDictionary<string, string> parameterValues)
        {
            var arguments = new List<object?>();
            foreach (var parameter in routine.Parameters)
            {
                var name = parameter.DisplayName ?? parameter.Name;
                var value = parameterValues.TryGetValue(name, out var overridden)
                    ? overridden
                    : parameter.DefaultValue?.ToString() ?? string.Empty;
                if (!TryConvert(parameter.ParameterType, value, out var converted))
                {
                    return $"Invalid value for {name} ({parameter.ParameterType.Name}).";
                }

                arguments.Add(converted);
            }

            var scopeId = Guid.NewGuid().ToString("N");
            StartRunRow(scopeId, routine.Name, DateTimeOffset.Now, isRunning: true, collapseOnComplete: false);
            AddRunMessage(scopeId, $"Running {routine.Name} from quick command...");
            var result = await _runtime.ExecuteRoutineAsync(routine, arguments, scopeId);
            CompleteRunRow(result.ExecutionScopeId, result.IsSuccess, result.Duration, result.StartedAt);
            PlayCompletionChime();
            if (result.Exception == null)
            {
                return result.IsSuccess ? null : $"Routine '{routine.Name}' failed.";
            }

            AddRunMessage(result.ExecutionScopeId, result.Exception.ToString(), Logger.LogLevel.Error);
            return result.Exception.Message;
        }

        private void ExecuteQuickCommand(QuickCommandAction command)
        {
            switch (command)
            {
                case QuickCommandAction.Reload:
                    _runtime.ReloadScripts();
                    break;

                case QuickCommandAction.Show:
                    WindowState = WindowState.Normal;
                    Show();
                    Activate();
                    break;

                case QuickCommandAction.Minimize:
                    WindowState = WindowState.Minimized;
                    break;
            }
        }

        private async void SettingsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var dialog = new SettingsWindow(_scriptsRoot, ApplyScriptsPathSetting);
            await dialog.ShowDialog<bool>(this);
        }

        private void ApplyScriptsPathSetting(string selectedScriptsPath)
        {
            if (string.IsNullOrWhiteSpace(selectedScriptsPath))
            {
                return;
            }

            var normalized = Path.GetFullPath(selectedScriptsPath);
            if (string.Equals(_scriptsRoot, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settingsService.ScriptsRoot = normalized;
        }

        private void SettingsService_SettingsChanged(object? sender, AppSettings settings)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var resolvedRoot = ResolveScriptsRoot(settings);
                var normalized = Path.GetFullPath(resolvedRoot);
                if (string.Equals(_scriptsRoot, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                InitializeRuntime(normalized);
                AppendLog($"Updated scripts path to: {normalized}");
            });
        }

        private void Runtime_ScriptsReloaded(object? sender, ScriptRuntimeSnapshot snapshot)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _routinesById.Clear();
                foreach (var collection in snapshot.Collections)
                {
                    foreach (var routine in collection.Routines)
                    {
                        _routinesById[GetRoutineKey(routine)] = routine;
                    }
                }

                _playlists.Clear();
                _playlists.AddRange(LoadPlaylists());
                _commands.Clear();
                _commands.AddRange(LoadCommands());

                RebuildOperationsTree(snapshot);
                _ = CacheCommandIconsAsync();

                AppendLog($"Loaded {snapshot.Collections.Count} collections.");
                HideCompilationWarningBanner();
            });
        }

        private void HideCompilationWarningBanner()
        {
            _compilationWarningBannerText.Text = string.Empty;
            _compilationWarningBanner.IsVisible = false;
        }

        private void ShowCompilationWarningBanner(string message)
        {
            _compilationWarningBannerText.Text = message;
            _compilationWarningBanner.IsVisible = true;
        }

        private void RebuildOperationsTree(
            ScriptRuntimeSnapshot snapshot,
            string? selectPlaylistName = null,
            string? selectPlaylistItemId = null)
        {
            _treeNodes.Clear();
            _collectionDescriptionBox.Text = string.Empty;

            BuildOperationsTree(snapshot);
            _collectionsTree.ItemsSource = _treeNodes;

            if (!string.IsNullOrWhiteSpace(selectPlaylistItemId))
            {
                var itemNode = FindNode(_treeNodes, node => node.PlaylistItem?.Id == selectPlaylistItemId);
                if (itemNode != null)
                {
                    _collectionsTree.SelectedItem = itemNode;
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(selectPlaylistName))
            {
                var playlistNode = FindNode(_treeNodes, node => node.Kind == ScriptNodeKind.Playlist && string.Equals(node.Name, selectPlaylistName, StringComparison.OrdinalIgnoreCase));
                if (playlistNode != null)
                {
                    _collectionsTree.SelectedItem = playlistNode;
                }
            }
        }

        private static ScriptNode? FindNode(IEnumerable<ScriptNode> nodes, Func<ScriptNode, bool> predicate)
        {
            foreach (var node in nodes)
            {
                if (predicate(node))
                {
                    return node;
                }

                var child = FindNode(node.Children, predicate);
                if (child != null)
                {
                    return child;
                }
            }

            return null;
        }

        private void BuildOperationsTree(ScriptRuntimeSnapshot snapshot)
        {
            var collectionsRoot = new ScriptNode { Name = "Collections", Kind = ScriptNodeKind.CollectionsRoot };
            var playlistsRoot = new ScriptNode { Name = "PlayLists", Kind = ScriptNodeKind.PlaylistsRoot };
            var commandsRoot = new ScriptNode { Name = "Commands", Kind = ScriptNodeKind.CommandsRoot };

            foreach (var collection in snapshot.Collections)
            {
                var collectionNode = new ScriptNode
                {
                    Name = $"{collection.Name} ({collection.Routines.Count} scripts)",
                    Description = collection.Description ?? string.Empty,
                    Collection = collection,
                    Kind = ScriptNodeKind.Collection,
                };

                foreach (var routine in collection.Routines)
                {
                    collectionNode.Children.Add(new ScriptNode
                    {
                        Name = routine.Name,
                        Description = routine.Description ?? string.Empty,
                        Collection = collection,
                        Routine = routine,
                        Kind = ScriptNodeKind.Routine,
                    });
                }

                collectionsRoot.Children.Add(collectionNode);
            }

            foreach (var playlist in _playlists)
            {
                playlistsRoot.Children.Add(BuildPlaylistNode(playlist));
            }

            foreach (var command in _commands)
            {
                commandsRoot.Children.Add(new ScriptNode
                {
                    Name = command.Name,
                    Description = command.Description,
                    Kind = ScriptNodeKind.Command,
                    Command = command,
                    Icon = LoadCommandIcon(ResolveCommandIconPath(command) ?? DefaultCommandIconUri),
                });
            }

            _treeNodes.Add(collectionsRoot);
            _treeNodes.Add(playlistsRoot);
            _treeNodes.Add(commandsRoot);
        }

        private void RefreshCommandsTree()
        {
            var commandsRoot = _treeNodes.FirstOrDefault(node => node.Kind == ScriptNodeKind.CommandsRoot);
            if (commandsRoot == null)
            {
                return;
            }

            commandsRoot.Children.Clear();
            foreach (var command in _commands)
            {
                commandsRoot.Children.Add(new ScriptNode
                {
                    Name = command.Name,
                    Description = command.Description,
                    Kind = ScriptNodeKind.Command,
                    Command = command,
                    Icon = LoadCommandIcon(ResolveCommandIconPath(command) ?? DefaultCommandIconUri),
                });
            }

            _ = CacheCommandIconsAsync();
        }

        private ScriptNode BuildPlaylistNode(PlaylistDefinition playlist)
        {
            var playlistNode = new ScriptNode
            {
                Name = playlist.Name,
                Kind = ScriptNodeKind.Playlist,
                Playlist = playlist,
                Description = "Playlist execution (sequential; parallel groups run concurrently).",
            };

            for (var index = 0; index < playlist.Items.Count; index++)
            {
                var itemNode = BuildPlaylistItemNode(playlist, playlist.Items[index]);
                itemNode.Name = $"{index + 1}. {itemNode.Name}";
                playlistNode.Children.Add(itemNode);
            }

            return playlistNode;
        }

        private ScriptNode BuildPlaylistItemNode(PlaylistDefinition playlist, PlaylistItemDefinition item)
        {
            if (item.Type == PlaylistItemType.ParallelGroup)
            {
                var parallelNode = new ScriptNode
                {
                    Name = string.IsNullOrWhiteSpace(item.DisplayName) ? "Parallel Routines" : item.DisplayName,
                    Kind = ScriptNodeKind.PlaylistParallelGroup,
                    Playlist = playlist,
                    PlaylistItem = item,
                };

                foreach (var child in item.Children)
                {
                    parallelNode.Children.Add(BuildPlaylistItemNode(playlist, child));
                }

                return parallelNode;
            }

            ScriptRoutineDescriptor? routine = null;
            if (!string.IsNullOrWhiteSpace(item.RoutineId))
            {
                _routinesById.TryGetValue(item.RoutineId, out routine);
            }

            return new ScriptNode
            {
                Name = routine?.Name ?? item.DisplayName,
                Description = routine?.Description ?? string.Empty,
                Kind = ScriptNodeKind.PlaylistRoutine,
                Playlist = playlist,
                PlaylistItem = item,
                Routine = routine,
            };
        }

        private void CollectionsTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_collectionsTree.SelectedItem is not ScriptNode node)
            {
                return;
            }

            _selectedNode = node;

            if (!string.IsNullOrWhiteSpace(node.Description))
            {
                _collectionDescriptionBox.Text = node.Description;
            }

            if (node.Kind == ScriptNodeKind.Routine && node.Routine != null)
            {
                DisplayRoutine(node.Routine, null, SaveRoutineDefaults);
            }

            if (node.Kind == ScriptNodeKind.PlaylistRoutine && node.Routine != null && node.PlaylistItem != null)
            {
                DisplayRoutine(node.Routine, node.PlaylistItem.ParameterValues, values => SavePlaylistItemDefaults(node.PlaylistItem!, values));
            }

            if (node.Kind == ScriptNodeKind.Command && node.Command != null)
            {
                DisplayCommand(node.Command);
            }
        }

        private void DisplayCommand(CommandDefinition command)
        {
            SaveCurrentRoutineDefaults();
            _currentRoutine = null;
            _saveDefaultsAction = null;
            _parameterViewModels.Clear();
            _parameterPanel.Children.Clear();
            _routineDescriptionBox.Text = $"{command.Description}\n\n{command.Type}: {command.Target}";
        }

        private async Task CacheCommandIconsAsync()
        {
            foreach (var command in _commands.Where(command => command.Type == CommandType.Url && string.IsNullOrWhiteSpace(command.IconPath)))
            {
                var iconPath = await CommandIconCache.CacheWebsiteIconAsync(_scriptsRoot, command);
                if (iconPath == null)
                {
                    continue;
                }

                var node = FindNode(_treeNodes, candidate => ReferenceEquals(candidate.Command, command));
                node?.SetIcon(LoadCommandIcon(new Uri(iconPath).AbsoluteUri));
            }
        }

        private string? ResolveCommandIconPath(CommandDefinition command)
        {
            if (!string.IsNullOrWhiteSpace(command.IconPath))
            {
                if (Uri.TryCreate(command.IconPath, UriKind.Absolute, out var uri))
                {
                    return uri.AbsoluteUri;
                }

                var path = Path.GetFullPath(Path.Combine(_scriptsRoot, command.IconPath));
                return File.Exists(path) ? new Uri(path).AbsoluteUri : null;
            }

            var cachePath = Path.Combine(_scriptsRoot, ".scriptor", "command-icons", $"{command.Id}.ico");
            return File.Exists(cachePath) ? new Uri(cachePath).AbsoluteUri : null;
        }

        private static Bitmap? LoadCommandIcon(string iconPath)
        {
            try
            {
                if (Uri.TryCreate(iconPath, UriKind.Absolute, out var uri))
                {
                    if (uri.Scheme.Equals("avares", StringComparison.OrdinalIgnoreCase))
                    {
                        using var stream = AssetLoader.Open(uri);
                        return new Bitmap(stream);
                    }

                    if (uri.IsFile)
                    {
                        return new Bitmap(uri.LocalPath);
                    }
                }

                return new Bitmap(iconPath);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException)
            {
                return null;
            }
        }

        private void DisplayRoutine(
            ScriptRoutineDescriptor routine,
            Dictionary<string, string>? overrideDefaults,
            Action<List<ParameterViewModel>> saveAction)
        {
            SaveCurrentRoutineDefaults();

            _currentRoutine = routine;
            _saveDefaultsAction = saveAction;
            _routineDescriptionBox.Text = routine.Description ?? string.Empty;
            _parameterViewModels.Clear();
            _parameterPanel.Children.Clear();

            var defaults = LoadDefaultsFile();
            defaults.TryGetValue(GetRoutineKey(routine), out var saved);

            foreach (var parameter in routine.Parameters)
            {
                var name = parameter.DisplayName ?? parameter.Name;
                var value = parameter.DefaultValue?.ToString() ?? string.Empty;
                if (overrideDefaults != null && overrideDefaults.TryGetValue(name, out var overriddenValue))
                {
                    value = overriddenValue;
                }
                if (saved != null && saved.TryGetValue(name, out var savedValue))
                {
                    value = savedValue;
                }

                var vm = new ParameterViewModel
                {
                    Name = name,
                    Value = value,
                    Description = parameter.Description ?? string.Empty,
                    Usage = parameter.Usage ?? string.Empty
                };
                _parameterViewModels.Add(vm);

                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*"), Margin = new Avalonia.Thickness(0, 0, 0, 6) };

                var nameBox = new TextBlock
                {
                    Text = vm.Name,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                var tip = string.IsNullOrWhiteSpace(vm.Description) && string.IsNullOrWhiteSpace(vm.Usage)
                    ? null
                    : $"{vm.Description}\n{vm.Usage}";
                if (!string.IsNullOrWhiteSpace(tip))
                {
                    Avalonia.Controls.ToolTip.SetTip(nameBox, tip);
                }
                nameBox.DoubleTapped += (_, _) =>
                {
                    _routineDescriptionBox.Text = string.IsNullOrWhiteSpace(vm.Usage)
                        ? vm.Description
                        : $"{vm.Description}\nUsage: {vm.Usage}";
                };

                var input = CreateParameterEditor(parameter, vm);

                Grid.SetColumn(nameBox, 0);
                Grid.SetColumn(input, 1);
                row.Children.Add(nameBox);
                row.Children.Add(input);
                _parameterPanel.Children.Add(row);
            }
        }

        private Control CreateParameterEditor(ScriptParameterDescriptor parameter, ParameterViewModel vm)
        {
            var parameterType = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
            var usage = vm.Usage ?? string.Empty;

            if (parameterType == typeof(FileInfo) || HasUiHint(usage, "file"))
            {
                return CreatePathEditor(vm, isDirectory: false);
            }

            if (parameterType == typeof(DirectoryInfo) || HasUiHint(usage, "folder") || HasUiHint(usage, "directory"))
            {
                return CreatePathEditor(vm, isDirectory: true);
            }

            if (HasUiHint(usage, "password"))
            {
                var password = new TextBox
                {
                    Text = vm.Value ?? string.Empty,
                    PasswordChar = '●',
                };

                password.LostFocus += (_, _) => SaveParameter(vm, password.Text);
                password.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                    {
                        SaveParameter(vm, password.Text);
                    }
                };
                return password;
            }

            if (parameterType == typeof(bool))
            {
                var checkBox = new CheckBox
                {
                    IsChecked = bool.TryParse(vm.Value, out var parsed) && parsed,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };

                checkBox.IsCheckedChanged += (_, _) => SaveParameter(vm, checkBox.IsChecked == true ? bool.TrueString : bool.FalseString);
                return checkBox;
            }

            if (parameterType.IsEnum)
            {
                var options = Enum.GetNames(parameterType);
                var combo = new ComboBox
                {
                    ItemsSource = options,
                    SelectedItem = options.FirstOrDefault(o => string.Equals(o, vm.Value, StringComparison.OrdinalIgnoreCase)) ?? options.FirstOrDefault(),
                };

                combo.SelectionChanged += (_, _) => SaveParameter(vm, combo.SelectedItem?.ToString());
                return combo;
            }

            if (IsNumericType(parameterType))
            {
                var isInteger = IsIntegerType(parameterType);

                if (TryParseSliderHint(usage, out var sliderMin, out var sliderMax, out var sliderStep))
                {
                    var slider = new Slider
                    {
                        Minimum = sliderMin,
                        Maximum = sliderMax,
                        TickFrequency = sliderStep,
                        IsSnapToTickEnabled = isInteger,
                    };

                    if (double.TryParse(vm.Value, global::System.Globalization.NumberStyles.Any, global::System.Globalization.CultureInfo.InvariantCulture, out var sliderParsed) ||
                        double.TryParse(vm.Value, global::System.Globalization.NumberStyles.Any, global::System.Globalization.CultureInfo.CurrentCulture, out sliderParsed))
                    {
                        slider.Value = Math.Max(sliderMin, Math.Min(sliderMax, sliderParsed));
                    }

                    var valueText = new TextBlock
                    {
                        Text = isInteger
                            ? Math.Round(slider.Value).ToString(global::System.Globalization.CultureInfo.InvariantCulture)
                            : slider.Value.ToString("0.###", global::System.Globalization.CultureInfo.InvariantCulture),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    };

                    void SaveSlider()
                    {
                        var value = isInteger ? Math.Round(slider.Value) : slider.Value;
                        valueText.Text = isInteger
                            ? value.ToString(global::System.Globalization.CultureInfo.InvariantCulture)
                            : value.ToString("0.###", global::System.Globalization.CultureInfo.InvariantCulture);
                        SaveParameter(vm, valueText.Text);
                    }

                    slider.PropertyChanged += (_, e) =>
                    {
                        if (e.Property == RangeBase.ValueProperty)
                        {
                            SaveSlider();
                        }
                    };

                    var sliderGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,70") };
                    Grid.SetColumn(slider, 0);
                    Grid.SetColumn(valueText, 1);
                    sliderGrid.Children.Add(slider);
                    sliderGrid.Children.Add(valueText);
                    return sliderGrid;
                }

                var numeric = new NumericUpDown
                {
                    Increment = isInteger ? 1m : 0.1m,
                    Minimum = decimal.MinValue,
                    Maximum = decimal.MaxValue,
                    FormatString = isInteger ? "N0" : "N3",
                };

                if (decimal.TryParse(vm.Value, global::System.Globalization.NumberStyles.Any, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
                    decimal.TryParse(vm.Value, global::System.Globalization.NumberStyles.Any, global::System.Globalization.CultureInfo.CurrentCulture, out parsed))
                {
                    numeric.Value = parsed;
                }

                void SaveNumeric()
                {
                    if (numeric.Value is null)
                    {
                        SaveParameter(vm, string.Empty);
                        return;
                    }

                    var value = numeric.Value.Value;
                    SaveParameter(vm, isInteger
                        ? decimal.Truncate(value).ToString(global::System.Globalization.CultureInfo.InvariantCulture)
                        : value.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
                }

                numeric.LostFocus += (_, _) => SaveNumeric();
                numeric.ValueChanged += (_, _) => SaveNumeric();
                return numeric;
            }

            var multiline = HasUiHint(usage, "multiline");
            var textBox = new TextBox
            {
                Text = vm.Value,
                AcceptsReturn = multiline,
                Height = multiline ? 72 : double.NaN,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            };

            textBox.LostFocus += (_, _) => SaveParameter(vm, textBox.Text);
            textBox.KeyDown += (_, e) =>
            {
                if (!multiline && e.Key == Key.Enter)
                {
                    SaveParameter(vm, textBox.Text);
                }
            };

            return textBox;
        }

        private Control CreatePathEditor(ParameterViewModel vm, bool isDirectory)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var textBox = new TextBox { Text = vm.Value };
            var browseButton = new Button
            {
                Content = isDirectory ? "Browse Folder" : "Browse File",
                Margin = new Avalonia.Thickness(8, 0, 0, 0),
            };

            textBox.LostFocus += (_, _) => SaveParameter(vm, textBox.Text);
            textBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    SaveParameter(vm, textBox.Text);
                }
            };

            browseButton.Click += async (_, _) =>
            {
                if (isDirectory)
                {
                    var dialog = new OpenFolderDialog { Title = vm.Name };
                    var selected = await dialog.ShowAsync(this);
                    if (!string.IsNullOrWhiteSpace(selected))
                    {
                        textBox.Text = selected;
                        SaveParameter(vm, selected);
                    }
                }
                else
                {
                    var dialog = new OpenFileDialog { Title = vm.Name, AllowMultiple = false };
                    var selected = await dialog.ShowAsync(this);
                    if (selected != null && selected.Length > 0 && !string.IsNullOrWhiteSpace(selected[0]))
                    {
                        textBox.Text = selected[0];
                        SaveParameter(vm, selected[0]);
                    }
                }
            };

            Grid.SetColumn(textBox, 0);
            Grid.SetColumn(browseButton, 1);
            grid.Children.Add(textBox);
            grid.Children.Add(browseButton);
            return grid;
        }

        private static bool HasUiHint(string usage, string hint)
        {
            return usage?.IndexOf($"ui:{hint}", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryParseSliderHint(string usage, out double min, out double max, out double step)
        {
            min = 0;
            max = 100;
            step = 1;

            if (string.IsNullOrWhiteSpace(usage))
            {
                return false;
            }

            var match = Regex.Match(
                usage,
                "ui:slider\\((?<min>-?\\d+(?:\\.\\d+)?),(?<max>-?\\d+(?:\\.\\d+)?)(?:,(?<step>\\d+(?:\\.\\d+)?))?\\)",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                return false;
            }

            if (!double.TryParse(match.Groups["min"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out min) ||
                !double.TryParse(match.Groups["max"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out max))
            {
                return false;
            }

            if (match.Groups["step"].Success)
            {
                if (!double.TryParse(match.Groups["step"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out step))
                {
                    step = 1;
                }
            }

            if (max <= min)
            {
                max = min + 1;
            }

            if (step <= 0)
            {
                step = 1;
            }

            return true;
        }

        private static bool IsNumericType(Type type)
        {
            return type == typeof(byte)
                || type == typeof(sbyte)
                || type == typeof(short)
                || type == typeof(ushort)
                || type == typeof(int)
                || type == typeof(uint)
                || type == typeof(long)
                || type == typeof(ulong)
                || type == typeof(float)
                || type == typeof(double)
                || type == typeof(decimal);
        }

        private static bool IsIntegerType(Type type)
        {
            return type == typeof(byte)
                || type == typeof(sbyte)
                || type == typeof(short)
                || type == typeof(ushort)
                || type == typeof(int)
                || type == typeof(uint)
                || type == typeof(long)
                || type == typeof(ulong);
        }

        private void SaveParameter(ParameterViewModel vm, string? newValue)
        {
            vm.Value = newValue ?? string.Empty;
            SaveCurrentRoutineDefaults();
        }

        private void SaveCurrentRoutineDefaults()
        {
            if (_currentRoutine == null || _saveDefaultsAction == null)
            {
                return;
            }

            _saveDefaultsAction(_parameterViewModels);
        }

        private void SaveRoutineDefaults(List<ParameterViewModel> values)
        {
            if (_currentRoutine == null)
            {
                return;
            }

            var defaults = LoadDefaultsFile();
            defaults[GetRoutineKey(_currentRoutine)] = values.ToDictionary(p => p.Name, p => p.Value);
            SaveDefaultsFile(defaults);
        }

        private void SavePlaylistItemDefaults(PlaylistItemDefinition item, List<ParameterViewModel> values)
        {
            item.ParameterValues = values.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
            SavePlaylists(_playlists, _selectedNode?.Playlist);
        }

        private async void RunButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_selectedNode?.Kind == ScriptNodeKind.Command && _selectedNode.Command != null)
            {
                await ExecuteCommandFromUiAsync(_selectedNode.Command);
                return;
            }

            if (_selectedNode?.Kind == ScriptNodeKind.Playlist && _selectedNode.Playlist != null)
            {
                ClearRunLog();
                await ExecutePlaylistAsync(_selectedNode.Playlist).ConfigureAwait(false);
                return;
            }

            if (_currentRoutine == null)
            {
                AppendLog("Select a routine to run.");
                return;
            }

            var converted = new List<object?>();
            for (var i = 0; i < _currentRoutine.Parameters.Count; i++)
            {
                var parameter = _currentRoutine.Parameters[i];
                var vm = _parameterViewModels[i];
                if (!TryConvert(parameter.ParameterType, vm.Value, out var value))
                {
                    AppendLog($"Invalid value for {vm.Name} ({parameter.ParameterType.Name})");
                    return;
                }
                converted.Add(value);
            }

            SaveCurrentRoutineDefaults();
            ClearRunLog();
            var scopeId = Guid.NewGuid().ToString("N");
            var row = StartRunRow(scopeId, _currentRoutine.Name, DateTimeOffset.Now, isRunning: true, collapseOnComplete: false);
            AddRunMessage(scopeId, $"Running {_currentRoutine.Name}...");

            var result = await _runtime.ExecuteRoutineAsync(_currentRoutine, converted, scopeId).ConfigureAwait(false);

            Dispatcher.UIThread.Post(() =>
            {
                CompleteRunRow(result.ExecutionScopeId, result.IsSuccess, result.Duration, result.StartedAt);
                PlayCompletionChime();
                if (result.Exception != null)
                {
                    AddRunMessage(result.ExecutionScopeId, result.Exception.ToString(), Logger.LogLevel.Error);
                }
            });
        }

        private async Task ExecutePlaylistAsync(PlaylistDefinition playlist)
        {
            ClearRunLog();
            AppendLog($"Running playlist {playlist.Name}...");
            foreach (var item in playlist.Items)
            {
                if (item.Type == PlaylistItemType.ParallelGroup)
                {
                    var tasks = item.Children
                        .Where(child => child.Type == PlaylistItemType.Routine)
                        .Select(child => ExecutePlaylistRoutineItemAsync(child));
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                else
                {
                    await ExecutePlaylistRoutineItemAsync(item).ConfigureAwait(false);
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                AppendLog($"Playlist {playlist.Name} completed.");
                PlayCompletionChime();
            });
        }

        private async Task ExecutePlaylistRoutineItemAsync(PlaylistItemDefinition item)
        {
            if (string.IsNullOrWhiteSpace(item.RoutineId) || !_routinesById.TryGetValue(item.RoutineId, out var routine))
            {
                Dispatcher.UIThread.Post(() => AppendLog($"Playlist item '{item.DisplayName}' routine not found."));
                return;
            }

            var scopeId = Guid.NewGuid().ToString("N");
            Dispatcher.UIThread.Post(() =>
            {
                StartRunRow(scopeId, item.DisplayName, DateTimeOffset.Now, isRunning: true, collapseOnComplete: true);
                AddRunMessage(scopeId, $"Running playlist item {item.DisplayName}...");
            });

            var args = new List<object?>();
            foreach (var parameter in routine.Parameters)
            {
                var name = parameter.DisplayName ?? parameter.Name;
                var raw = item.ParameterValues.TryGetValue(name, out var value)
                    ? value
                    : parameter.DefaultValue?.ToString() ?? string.Empty;

                if (!TryConvert(parameter.ParameterType, raw, out var converted))
                {
                    Dispatcher.UIThread.Post(() => AppendLog($"Invalid playlist parameter value for {name} in item {item.DisplayName}."));
                    return;
                }

                args.Add(converted);
            }

            var result = await _runtime.ExecuteRoutineAsync(routine, args, scopeId).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                CompleteRunRow(result.ExecutionScopeId, result.IsSuccess, result.Duration, result.StartedAt);

                if (result.Exception != null)
                {
                    AddRunMessage(result.ExecutionScopeId, result.Exception.ToString(), Logger.LogLevel.Error);
                }
            });
        }

        private void NewPlaylistButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var baseName = "New Playlist";
            var name = baseName;
            var index = 1;
            while (_playlists.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                index++;
                name = $"{baseName} {index}";
            }

            var playlist = new PlaylistDefinition { Name = name };
            _playlists.Add(playlist);
            SavePlaylists(_playlists, playlist);
            var playlistsRoot = GetOrCreatePlaylistsRootNode();
            var playlistNode = BuildPlaylistNode(playlist);
            playlistsRoot.Children.Add(playlistNode);
            _collectionsTree.SelectedItem = playlistNode;
        }

        private async void EditPlaylistsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            await ShowPlaylistEditorAsync(_selectedNode?.Playlist);
        }

        private async Task ShowPlaylistEditorAsync(PlaylistDefinition? selectedPlaylist, ScriptRoutineDescriptor? routineToAdd = null)
        {
            SaveCurrentRoutineDefaults();

            var editor = new PlaylistEditorWindow(
                _playlists,
                _routinesById.Values.OrderBy(routine => routine.Name).ToList(),
                GetRoutineKey,
                playlist => SavePlaylists(_playlists, playlist),
                selectedPlaylist,
                routineToAdd);

            await editor.ShowDialog(this);
            RefreshPlaylistTree(editor.SelectedPlaylistName, editor.SelectedPlaylistItemId);
        }

        private void PlaylistTreeItem_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control control || control.DataContext is not ScriptNode node)
            {
                return;
            }

            var point = e.GetCurrentPoint(control);
            if (point.Properties.IsRightButtonPressed)
            {
                _collectionsTree.SelectedItem = node;
                var menu = CreateTreeContextMenu(node);
                if (menu == null)
                {
                    return;
                }

                menu.Open(control);
                e.Handled = true;
                return;
            }

            if (point.Properties.IsLeftButtonPressed && node.Kind == ScriptNodeKind.PlaylistRoutine)
            {
                _draggedPlaylistItemNode = node;
                _dragStartPoint = e.GetPosition(_collectionsTree);
            }
        }

        private async void PlaylistTreeItem_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (_draggedPlaylistItemNode == null
                || sender is not Control control
                || control.DataContext is not ScriptNode node
                || !ReferenceEquals(node, _draggedPlaylistItemNode)
                || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            {
                return;
            }

            var position = e.GetPosition(_collectionsTree);
            if (Math.Abs(position.X - _dragStartPoint.X) < 4 && Math.Abs(position.Y - _dragStartPoint.Y) < 4)
            {
                return;
            }

            _draggedPlaylistItemNode = null;
            var data = new DataObject();
            data.Set(PlaylistItemDragFormat, node.PlaylistItem!.Id);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }

        private void PlaylistTreeItem_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            _draggedPlaylistItemNode = null;
        }

        private async void CommandTreeItem_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (e.Source is Control { DataContext: ScriptNode { Kind: ScriptNodeKind.Command, Command: { } command } })
            {
                await ExecuteCommandFromUiAsync(command);
                e.Handled = true;
            }
        }

        private void PlaylistTreeItem_DragOver(object? sender, DragEventArgs e)
        {
            if (sender is not Control { DataContext: ScriptNode target }
                || target.Kind != ScriptNodeKind.PlaylistRoutine
                || !e.Data.Contains(PlaylistItemDragFormat))
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }

            var sourceItemId = e.Data.Get(PlaylistItemDragFormat) as string;
            var sourceNode = FindNode(_treeNodes, node => node.PlaylistItem?.Id == sourceItemId);
            if (sourceNode?.Kind != ScriptNodeKind.PlaylistRoutine
                || sourceNode.Playlist == null
                || !ReferenceEquals(sourceNode.Playlist, target.Playlist)
                || !AreInSamePlaylistContainer(sourceNode.Playlist, sourceNode.PlaylistItem!, target.PlaylistItem!))
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }

            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void PlaylistTreeItem_Drop(object? sender, DragEventArgs e)
        {
            if (sender is not Control control
                || control.DataContext is not ScriptNode target
                || target.Kind != ScriptNodeKind.PlaylistRoutine
                || target.Playlist == null
                || target.PlaylistItem == null
                || !e.Data.Contains(PlaylistItemDragFormat)
                || e.Data.Get(PlaylistItemDragFormat) is not string sourceItemId)
            {
                return;
            }

            var sourceNode = FindNode(_treeNodes, node => node.PlaylistItem?.Id == sourceItemId);
            if (sourceNode?.Kind != ScriptNodeKind.PlaylistRoutine
                || sourceNode.PlaylistItem == null
                || !ReferenceEquals(sourceNode.Playlist, target.Playlist)
                || ReferenceEquals(sourceNode.PlaylistItem, target.PlaylistItem)
                || !TryMovePlaylistItem(target.Playlist, sourceNode.PlaylistItem, target.PlaylistItem, e.GetPosition(control).Y > control.Bounds.Height / 2))
            {
                return;
            }

            SavePlaylists(_playlists, target.Playlist);
            RefreshPlaylistTree(target.Playlist.Name, sourceNode.PlaylistItem.Id);
            e.Handled = true;
        }

        private ContextMenu? CreateTreeContextMenu(ScriptNode node)
        {
            var menu = new ContextMenu();
            switch (node.Kind)
            {
                case ScriptNodeKind.Routine when node.Routine != null:
                    var addToPlaylistMenu = new MenuItem { Header = "Add to Playlist" };
                    foreach (var playlist in _playlists
                        .OrderByDescending(playlist => playlist.LastEditedAt ?? DateTimeOffset.MinValue)
                        .ThenBy(playlist => playlist.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        addToPlaylistMenu.Items.Add(CreateMenuItem(playlist.Name, () => AddRoutineToPlaylist(node.Routine, playlist)));
                    }

                    if (_playlists.Count == 0)
                    {
                        addToPlaylistMenu.IsEnabled = false;
                    }

                    menu.Items.Add(addToPlaylistMenu);
                    menu.Items.Add(CreateMenuItem("Add to New Playlist", async () => await AddRoutineToNewPlaylistAsync(node.Routine)));
                    menu.Items.Add(CreateMenuItem("Refresh", RefreshScripts));
                    return menu;

                case ScriptNodeKind.Playlist when node.Playlist != null:
                    menu.Items.Add(CreateMenuItem("Rename", async () => await RenamePlaylistAsync(node.Playlist)));
                    menu.Items.Add(CreateMenuItem("Edit", async () => await ShowPlaylistEditorAsync(node.Playlist)));
                    menu.Items.Add(CreateMenuItem("Remove", async () => await RemovePlaylistAsync(node.Playlist)));
                    menu.Items.Add(new Separator());
                    menu.Items.Add(CreateMenuItem("Refresh", RefreshScripts));
                    return menu;

                case ScriptNodeKind.PlaylistRoutine when node.Playlist != null && node.PlaylistItem != null:
                    if (node.Routine != null)
                    {
                        menu.Items.Add(CreateMenuItem("Edit Parameters", () =>
                        {
                            _collectionsTree.SelectedItem = node;
                            DisplayRoutine(node.Routine, node.PlaylistItem.ParameterValues, values => SavePlaylistItemDefaults(node.PlaylistItem, values));
                        }));
                    }

                    menu.Items.Add(CreateMenuItem("Remove from Playlist", () => RemovePlaylistItem(node.Playlist, node.PlaylistItem)));
                    menu.Items.Add(new Separator());
                    menu.Items.Add(CreateMenuItem("Refresh", RefreshScripts));
                    return menu;

                case ScriptNodeKind.PlaylistParallelGroup when node.Playlist != null && node.PlaylistItem != null:
                    menu.Items.Add(CreateMenuItem("Remove Parallel Group", () => RemovePlaylistItem(node.Playlist, node.PlaylistItem)));
                    menu.Items.Add(new Separator());
                    menu.Items.Add(CreateMenuItem("Refresh", RefreshScripts));
                    return menu;

                case ScriptNodeKind.Command when node.Command != null:
                    menu.Items.Add(CreateMenuItem("Run", async () => await ExecuteCommandFromUiAsync(node.Command)));
                    menu.Items.Add(CreateMenuItem("Refresh", RefreshScripts));
                    return menu;

                default:
                    return null;
            }
        }

        private static MenuItem CreateMenuItem(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        private void RefreshScripts()
        {
            _runtime.ReloadScripts();
        }

        private Task<string?> ExecuteCommandAsync(CommandDefinition command)
        {
            try
            {
                switch (command.Type)
                {
                    case CommandType.Url:
                        if (!Uri.TryCreate(command.Target, UriKind.Absolute, out var uri)
                            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        {
                            return Task.FromResult<string?>($"Command '{command.Name}' has an invalid HTTP(S) URL.");
                        }

                        if (Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }) == null)
                        {
                            return Task.FromResult<string?>($"Command '{command.Name}' could not open the URL.");
                        }

                        AppendLog($"Opened {command.Name}.");
                        return Task.FromResult<string?>(null);

                    case CommandType.Program:
                        if (string.IsNullOrWhiteSpace(command.Target))
                        {
                            return Task.FromResult<string?>($"Command '{command.Name}' has no program target.");
                        }

                        if (Process.Start(new ProcessStartInfo(command.Target)
                        {
                            Arguments = command.Arguments ?? string.Empty,
                            UseShellExecute = true,
                        }) == null)
                        {
                            return Task.FromResult<string?>($"Command '{command.Name}' could not start the program.");
                        }

                        AppendLog($"Started {command.Name}.");
                        return Task.FromResult<string?>(null);

                    default:
                        return Task.FromResult<string?>($"Command '{command.Name}' has an unsupported type.");
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                or InvalidOperationException
                or NotSupportedException)
            {
                var error = $"Command '{command.Name}' failed: {exception.Message}";
                AppendLog(error);
                return Task.FromResult<string?>(error);
            }
        }

        private async Task ExecuteCommandFromUiAsync(CommandDefinition command)
        {
            var error = await ExecuteCommandAsync(command);
            if (!string.IsNullOrWhiteSpace(error))
            {
                AppendLog(error);
            }
        }

        private async Task RenamePlaylistAsync(PlaylistDefinition playlist)
        {
            var name = await PlaylistNameDialog.ShowAsync(this, "Rename Playlist", playlist.Name);
            if (name == null || string.Equals(name, playlist.Name, StringComparison.Ordinal))
            {
                return;
            }

            if (_playlists.Any(existing => !ReferenceEquals(existing, playlist)
                && string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                await PlaylistNameDialog.ShowMessageAsync(this, "Playlist names must be unique.");
                return;
            }

            playlist.Name = name;
            SavePlaylists(_playlists, playlist);
            RefreshPlaylistTree(playlist.Name);
        }

        private async Task RemovePlaylistAsync(PlaylistDefinition playlist)
        {
            if (!await PlaylistNameDialog.ConfirmAsync(this, $"Delete playlist '{playlist.Name}'?"))
            {
                return;
            }

            _playlists.Remove(playlist);
            SavePlaylists(_playlists);
            RefreshPlaylistTree();
        }

        private async Task AddRoutineToNewPlaylistAsync(ScriptRoutineDescriptor routine)
        {
            var baseName = $"{routine.Name} Playlist";
            var name = await PlaylistNameDialog.ShowAsync(this, "New Playlist", GetUniquePlaylistName(baseName));
            if (name == null)
            {
                return;
            }

            if (_playlists.Any(playlist => string.Equals(playlist.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                await PlaylistNameDialog.ShowMessageAsync(this, "Playlist names must be unique.");
                return;
            }

            var item = CreatePlaylistItem(routine);
            var playlist = new PlaylistDefinition { Name = name, Items = [item] };
            _playlists.Add(playlist);
            SavePlaylists(_playlists, playlist);
            RefreshPlaylistTree(playlist.Name, item.Id);
        }

        private void AddRoutineToPlaylist(ScriptRoutineDescriptor routine, PlaylistDefinition playlist)
        {
            var item = CreatePlaylistItem(routine);
            playlist.Items.Add(item);
            SavePlaylists(_playlists, playlist);
            RefreshPlaylistTree(playlist.Name, item.Id);
        }

        private void RemovePlaylistItem(PlaylistDefinition playlist, PlaylistItemDefinition item)
        {
            var container = FindPlaylistItemContainer(playlist, item);
            if (container == null)
            {
                return;
            }

            container.Remove(item);
            SavePlaylists(_playlists, playlist);
            RefreshPlaylistTree(playlist.Name);
        }

        private PlaylistItemDefinition CreatePlaylistItem(ScriptRoutineDescriptor routine)
        {
            return new PlaylistItemDefinition
            {
                Type = PlaylistItemType.Routine,
                DisplayName = routine.Name,
                RoutineId = GetRoutineKey(routine),
                ParameterValues = routine.Parameters.ToDictionary(
                    parameter => parameter.DisplayName ?? parameter.Name,
                    parameter => parameter.DefaultValue?.ToString() ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase),
            };
        }

        private string GetUniquePlaylistName(string baseName)
        {
            var name = baseName;
            var suffix = 2;
            while (_playlists.Any(playlist => string.Equals(playlist.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                name = $"{baseName} {suffix++}";
            }

            return name;
        }

        private static bool TryMovePlaylistItem(PlaylistDefinition playlist, PlaylistItemDefinition source, PlaylistItemDefinition target, bool insertAfter)
        {
            var sourceContainer = FindPlaylistItemContainer(playlist, source);
            var targetContainer = FindPlaylistItemContainer(playlist, target);
            if (sourceContainer == null || targetContainer == null || !ReferenceEquals(sourceContainer, targetContainer))
            {
                return false;
            }

            var sourceIndex = sourceContainer.IndexOf(source);
            var targetIndex = sourceContainer.IndexOf(target);
            sourceContainer.RemoveAt(sourceIndex);
            if (sourceIndex < targetIndex)
            {
                targetIndex--;
            }

            sourceContainer.Insert(targetIndex + (insertAfter ? 1 : 0), source);
            return true;
        }

        private static bool AreInSamePlaylistContainer(PlaylistDefinition playlist, PlaylistItemDefinition first, PlaylistItemDefinition second)
        {
            var firstContainer = FindPlaylistItemContainer(playlist, first);
            var secondContainer = FindPlaylistItemContainer(playlist, second);
            return firstContainer != null && ReferenceEquals(firstContainer, secondContainer);
        }

        private static List<PlaylistItemDefinition>? FindPlaylistItemContainer(PlaylistDefinition playlist, PlaylistItemDefinition item)
        {
            if (playlist.Items.Contains(item))
            {
                return playlist.Items;
            }

            foreach (var group in playlist.Items.Where(candidate => candidate.Type == PlaylistItemType.ParallelGroup))
            {
                var container = FindPlaylistItemContainer(group, item);
                if (container != null)
                {
                    return container;
                }
            }

            return null;
        }

        private static List<PlaylistItemDefinition>? FindPlaylistItemContainer(PlaylistItemDefinition group, PlaylistItemDefinition item)
        {
            if (group.Children.Contains(item))
            {
                return group.Children;
            }

            foreach (var child in group.Children.Where(candidate => candidate.Type == PlaylistItemType.ParallelGroup))
            {
                var container = FindPlaylistItemContainer(child, item);
                if (container != null)
                {
                    return container;
                }
            }

            return null;
        }

        private void RefreshPlaylistTree(string? selectPlaylistName = null, string? selectPlaylistItemId = null)
        {
            var playlistsRoot = GetOrCreatePlaylistsRootNode();
            playlistsRoot.Children.Clear();
            foreach (var playlist in _playlists)
            {
                playlistsRoot.Children.Add(BuildPlaylistNode(playlist));
            }

            if (!string.IsNullOrWhiteSpace(selectPlaylistItemId))
            {
                var itemNode = FindNode(playlistsRoot.Children, node => node.PlaylistItem?.Id == selectPlaylistItemId);
                if (itemNode != null)
                {
                    _collectionsTree.SelectedItem = itemNode;
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(selectPlaylistName))
            {
                var playlistNode = playlistsRoot.Children.FirstOrDefault(node =>
                    string.Equals(node.Name, selectPlaylistName, StringComparison.OrdinalIgnoreCase));
                if (playlistNode != null)
                {
                    _collectionsTree.SelectedItem = playlistNode;
                }
            }
        }

        private ScriptNode GetOrCreatePlaylistsRootNode()
        {
            var playlistsRoot = _treeNodes.FirstOrDefault(n => n.Kind == ScriptNodeKind.PlaylistsRoot);
            if (playlistsRoot != null)
            {
                return playlistsRoot;
            }

            playlistsRoot = new ScriptNode { Name = "PlayLists", Kind = ScriptNodeKind.PlaylistsRoot };
            _treeNodes.Add(playlistsRoot);
            return playlistsRoot;
        }

        private static void PlayCompletionChime()
        {
            if (OperatingSystem.IsWindows())
            {
                MessageBeep(MessageBeepInformation);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint type);

        private static bool TryConvert(Type type, string value, out object? output)
        {
            if (type == typeof(string))
            {
                output = value;
                return true;
            }

            if (type == typeof(int) && int.TryParse(value, out var i))
            {
                output = i;
                return true;
            }

            if (type == typeof(bool) && bool.TryParse(value, out var b))
            {
                output = b;
                return true;
            }

            if (type == typeof(double) && double.TryParse(value, out var d))
            {
                output = d;
                return true;
            }

            if (type == typeof(float) && float.TryParse(value, out var f))
            {
                output = f;
                return true;
            }

            if (type == typeof(decimal) && decimal.TryParse(value, out var dec))
            {
                output = dec;
                return true;
            }

            if (type == typeof(long) && long.TryParse(value, out var l))
            {
                output = l;
                return true;
            }

            if (type == typeof(FileInfo))
            {
                output = string.IsNullOrWhiteSpace(value) ? null : new FileInfo(value);
                return true;
            }

            if (type == typeof(DirectoryInfo))
            {
                output = string.IsNullOrWhiteSpace(value) ? null : new DirectoryInfo(value);
                return true;
            }

            if (type.IsEnum && Enum.TryParse(type, value, true, out var e))
            {
                output = e;
                return true;
            }

            output = null;
            return false;
        }

        private void ReloadButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _runtime.ReloadScripts();
        }

        private void GenerateProjectButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var commonProjectPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "ScriptorCommon", "ScriptorCommon.csproj"));
            if (!File.Exists(commonProjectPath))
            {
                commonProjectPath = null;
            }

            var commonAssemblyPath = typeof(Scripts.Scriptor.Logger).Assembly.Location;
            var snapshot = _runtime.CurrentSnapshot ?? new ScriptRuntimeSnapshot(Array.Empty<ScriptCollectionDescriptor>(), Array.Empty<ScriptPackageDependency>());
            var result = ScriptProjectGenerator.EnsureScriptProject(_runtime.ScriptsRoot, snapshot.PackageDependencies, commonProjectPath, commonAssemblyPath);
            foreach (var message in result.Messages)
            {
                AppendLog(message);
            }

            if (string.IsNullOrWhiteSpace(result.SolutionPath) || !File.Exists(result.SolutionPath))
            {
                AppendLog("No generated solution file was found to open.");
                return;
            }

            if (TryOpenSolutionInNewVisualStudioInstance(result.SolutionPath, out var launchMessage))
            {
                AppendLog(launchMessage);
            }
            else
            {
                AppendLog(launchMessage);
            }
        }

        private static bool TryOpenSolutionInNewVisualStudioInstance(string solutionPath, out string message)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "devenv.exe",
                    Arguments = $"\"{solutionPath}\"",
                    UseShellExecute = true,
                };

                var process = Process.Start(startInfo);
                if (process != null)
                {
                    message = $"Opened generated script solution in Visual Studio: {solutionPath}";
                    return true;
                }
            }
            catch (Exception ex)
            {
                message = $"Visual Studio direct launch failed ({ex.Message}). Falling back to shell open...";
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = solutionPath,
                    UseShellExecute = true,
                });

                message = $"Opened generated script solution via shell association: {solutionPath}";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Failed to open generated script solution: {ex.Message}";
                return false;
            }
        }

        private void Runtime_CompilationFailed(object? sender, IReadOnlyList<ScriptCompilationDiagnostic> diagnostics)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var excludedFiles = diagnostics
                    .Where(d => string.Equals(d.Id, "SCRIPT_EXCLUDED", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(d.FilePath))
                    .Select(d => Path.GetFileName(d.FilePath))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var errorCount = diagnostics.Count(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase));
                if (excludedFiles.Count > 0)
                {
                    var filesSummary = string.Join(", ", excludedFiles.Take(3));
                    if (excludedFiles.Count > 3)
                    {
                        filesSummary += ", ...";
                    }

                    ShowCompilationWarningBanner($"Script reload completed with compile errors. Excluded file(s): {filesSummary}. See Run Log for details.");
                }
                else if (errorCount > 0)
                {
                    ShowCompilationWarningBanner($"Script reload failed with {errorCount} error(s). See Run Log for details.");
                }
                else
                {
                    HideCompilationWarningBanner();
                }

                foreach (var diagnostic in diagnostics)
                {
                    AppendLog($"{diagnostic.Severity} {diagnostic.Id}: {diagnostic.Message} [{diagnostic.FilePath}] ({diagnostic.Line}:{diagnostic.Column})");
                }
            });
        }

        private static string GetRoutineKey(ScriptRoutineDescriptor routine)
        {
            return (routine.Method.DeclaringType?.FullName ?? "<unknown>") + "." + routine.Method.Name;
        }

        private Dictionary<string, Dictionary<string, string>> LoadDefaultsFile()
        {
            try
            {
                var path = GetDefaultsPath();
                if (!File.Exists(path))
                {
                    return new Dictionary<string, Dictionary<string, string>>();
                }

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json) ?? new Dictionary<string, Dictionary<string, string>>();
            }
            catch
            {
                return new Dictionary<string, Dictionary<string, string>>();
            }
        }

        private void SaveDefaultsFile(Dictionary<string, Dictionary<string, string>> defaults)
        {
            var path = GetDefaultsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        private string GetDefaultsPath()
        {
            return Path.Combine(_scriptsRoot, ".scriptor", "defaults.json");
        }

        private static string ResolveScriptsRoot(AppSettings settings)
        {
            var configuredPath = settings.ScriptsRoot;
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return configuredPath;
            }

            var outputScripts = Path.Combine(AppContext.BaseDirectory, "Scripts");

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir != null; i++)
            {
                var candidate = Path.Combine(dir.FullName, "User_Defined_Scripts");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            return outputScripts;
        }

        private string GetPlaylistsPath()
        {
            return Path.Combine(_scriptsRoot, ".scriptor", "playlists.json");
        }

        private string GetCommandsPath()
        {
            return Path.Combine(_scriptsRoot, ".scriptor", "commands.json");
        }

        private string GetWindowStatePath()
        {
            return Path.Combine(_scriptsRoot, ".scriptor", "window-state.json");
        }

        private void RestoreWindowState()
        {
            try
            {
                var path = GetWindowStatePath();
                if (!File.Exists(path))
                {
                    return;
                }

                var json = File.ReadAllText(path);
                var state = JsonSerializer.Deserialize<WindowSessionState>(json);
                if (state == null)
                {
                    return;
                }

                if (state.Width > 300)
                {
                    Width = state.Width;
                }

                if (state.Height > 200)
                {
                    Height = state.Height;
                }

                if (state.X.HasValue && state.Y.HasValue)
                {
                    Position = new PixelPoint(state.X.Value, state.Y.Value);
                }

                if (!string.IsNullOrWhiteSpace(state.WindowState) && Enum.TryParse<WindowState>(state.WindowState, out var parsedState))
                {
                    WindowState = parsedState;
                }
            }
            catch
            {
            }
        }

        private void SaveWindowState()
        {
            try
            {
                var state = new WindowSessionState
                {
                    WindowState = WindowState.ToString(),
                    Width = Bounds.Width,
                    Height = Bounds.Height,
                };

                if (WindowState == WindowState.Normal)
                {
                    state.X = Position.X;
                    state.Y = Position.Y;
                }

                var path = GetWindowStatePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch
            {
            }
        }

        private List<PlaylistDefinition> LoadPlaylists()
        {
            try
            {
                var path = GetPlaylistsPath();
                if (!File.Exists(path))
                {
                    return new List<PlaylistDefinition>();
                }

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<PlaylistDefinition>>(json) ?? new List<PlaylistDefinition>();
            }
            catch
            {
                return new List<PlaylistDefinition>();
            }
        }

        private List<CommandDefinition> LoadCommands()
        {
            try
            {
                var path = GetCommandsPath();
                if (!File.Exists(path))
                {
                    var defaultCommands = CreateDefaultCommands();
                    SaveCommands(defaultCommands);
                    return defaultCommands;
                }

                var json = File.ReadAllText(path);
                var commands = JsonSerializer.Deserialize<List<CommandDefinition>>(json) ?? new List<CommandDefinition>();
                if (AddMissingDefaultCommands(commands))
                {
                    SaveCommands(commands);
                }

                return commands;
            }
            catch
            {
                return new List<CommandDefinition>();
            }
        }

        private static List<CommandDefinition> CreateDefaultCommands()
        {
            return
            [
                new()
                {
                    Name = "Open Pond.net",
                    Description = "Open the Pond.net website in your default browser.",
                    Type = CommandType.Url,
                    Target = "https://pond.net",
                },
                new()
                {
                    Name = "Open MakeMKV",
                    Description = "Start MakeMKV from its standard Windows installation path.",
                    Type = CommandType.Program,
                    Target = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                        "MakeMKV",
                        "makemkv.exe"),
                },
            ];
        }

        private static bool AddMissingDefaultCommands(List<CommandDefinition> commands)
        {
            var changed = false;
            foreach (var defaultCommand in CreateDefaultCommands())
            {
                var matchingCommands = commands
                    .Where(command => IsSameCommandAction(command, defaultCommand))
                    .ToList();
                if (matchingCommands.Count == 0)
                {
                    commands.Add(defaultCommand);
                    changed = true;
                    continue;
                }

                var commandToKeep = matchingCommands.FirstOrDefault(command =>
                    !string.Equals(command.Name, defaultCommand.Name, StringComparison.OrdinalIgnoreCase))
                    ?? matchingCommands[0];
                foreach (var duplicate in matchingCommands.Where(command =>
                    !ReferenceEquals(command, commandToKeep)
                    && string.Equals(command.Name, defaultCommand.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    commands.Remove(duplicate);
                    changed = true;
                }
            }

            return changed;
        }

        private static bool IsSameCommandAction(CommandDefinition first, CommandDefinition second)
        {
            if (first.Type != second.Type)
            {
                return false;
            }

            if (first.Type == CommandType.Url
                && Uri.TryCreate(first.Target, UriKind.Absolute, out var firstUri)
                && Uri.TryCreate(second.Target, UriKind.Absolute, out var secondUri))
            {
                return Uri.Compare(
                    firstUri,
                    secondUri,
                    UriComponents.SchemeAndServer | UriComponents.Path,
                    UriFormat.SafeUnescaped,
                    StringComparison.OrdinalIgnoreCase) == 0;
            }

            return string.Equals(first.Target, second.Target, StringComparison.OrdinalIgnoreCase)
                && string.Equals(first.Arguments, second.Arguments, StringComparison.Ordinal);
        }

        private void SaveCommands(List<CommandDefinition> commands)
        {
            var path = GetCommandsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(commands, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        private void SavePlaylists(List<PlaylistDefinition> playlists, PlaylistDefinition? editedPlaylist = null)
        {
            if (editedPlaylist != null)
            {
                editedPlaylist.LastEditedAt = DateTimeOffset.UtcNow;
            }

            var path = GetPlaylistsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(playlists, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        private void Logger_EntryWritten(Logger.LogEntry entry)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (string.IsNullOrWhiteSpace(entry.ScopeKey))
                {
                    AppendLog($"{entry.Level}: {entry.Message}");
                    return;
                }

                if (entry.IsProgress)
                {
                    UpdateRunProgress(entry.ScopeKey!, entry.ProgressKey ?? "progress", entry.ProgressValue ?? 0, entry.Message);
                }
                else if (!string.IsNullOrWhiteSpace(entry.ProgressKey))
                {
                    AddProgressDetailMessage(entry.ScopeKey!, entry.ProgressKey!, entry.Message, entry.Level);
                }
                else
                {
                    AddRunMessage(entry.ScopeKey!, entry.Message, entry.Level);
                }
            });
        }

        private void ClearRunLog()
        {
            _runRowsByScope.Clear();
            _runLogRowsPanel.Children.Clear();
        }

        private void ClearRunLogMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ClearRunLog();
        }

        private async void CopyRunLogButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                AppendLog("Clipboard is unavailable.");
                return;
            }

            await clipboard.SetTextAsync(BuildRunLogText());
        }

        private string BuildRunLogText()
        {
            var output = new StringBuilder();
            foreach (var row in _runRowsByScope.Values.OrderBy(row => row.StartedAt))
            {
                output.Append('[')
                    .Append(row.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                    .Append("] ")
                    .Append(row.Status)
                    .Append(": ")
                    .AppendLine(row.ScriptName);

                foreach (var entry in row.Entries)
                {
                    output.Append("  ").AppendLine(entry);
                }
            }

            return output.ToString();
        }

        private void ScrollRunLogToBottom()
        {
            _runLogScrollViewer.Offset = new Avalonia.Vector(_runLogScrollViewer.Offset.X, _runLogScrollViewer.Extent.Height);
        }

        private void StatusSpinnerTimer_Tick(object? sender, EventArgs e)
        {
            if (_runRowsByScope.Count == 0)
            {
                return;
            }

            _spinnerFrameIndex = (_spinnerFrameIndex + 1) % SpinnerFrames.Length;
            var frame = SpinnerFrames[_spinnerFrameIndex];

            foreach (var row in _runRowsByScope.Values)
            {
                if (!row.IsRunning)
                {
                    continue;
                }

                row.StatusText.Text = frame;
                row.StatusText.Foreground = RunningStatusBrush;
                row.StatusBadge.Background = Brushes.Transparent;
            }
        }

        private RoutineRunRowUi StartRunRow(
            string scopeId,
            string scriptName,
            DateTimeOffset startedAt,
            bool isRunning = true,
            bool collapseOnComplete = false)
        {
            if (_runRowsByScope.TryGetValue(scopeId, out var existing))
            {
                return existing;
            }

            var detailsPanel = new StackPanel { Spacing = 4 };
            detailsPanel.IsVisible = true;

            var toggleButton = new Button
            {
                Content = "▸",
                Width = 20,
                Height = 20,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Padding = new Avalonia.Thickness(0)
            };

            var nameText = new TextBlock
            {
                Text = scriptName,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

            var statusText = new TextBlock
            {
                Text = isRunning ? SpinnerFrames[0] : "•",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = isRunning ? RunningStatusBrush : IdleStatusBrush,
                FontWeight = FontWeight.SemiBold,
                FontFamily = new FontFamily("Consolas")
            };
            var statusBadge = new Border
            {
                CornerRadius = new Avalonia.CornerRadius(10),
                Padding = new Avalonia.Thickness(6, 2),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Background = Brushes.Transparent,
                Child = statusText,
                MinWidth = 26
            };
            var timeText = new TextBlock { Text = startedAt.ToLocalTime().ToString("HH:mm:ss"), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };

            toggleButton.Click += (_, _) =>
            {
                if (_runRowsByScope.TryGetValue(scopeId, out var runRow) && runRow.IsRunning)
                {
                    return;
                }

                detailsPanel.IsVisible = !detailsPanel.IsVisible;
                toggleButton.Content = detailsPanel.IsVisible ? "▾" : "▸";
            };

            var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*,120,120") };
            Grid.SetColumn(toggleButton, 0);
            Grid.SetColumn(nameText, 1);
            Grid.SetColumn(statusBadge, 2);
            Grid.SetColumn(timeText, 3);
            headerGrid.Children.Add(toggleButton);
            headerGrid.Children.Add(nameText);
            headerGrid.Children.Add(statusBadge);
            headerGrid.Children.Add(timeText);

            var containerGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
            Grid.SetRow(headerGrid, 0);
            Grid.SetRow(detailsPanel, 1);
            containerGrid.Children.Add(headerGrid);
            containerGrid.Children.Add(detailsPanel);

            var border = new Border { BorderThickness = new Avalonia.Thickness(1), BorderBrush = Avalonia.Media.Brushes.Gray, Padding = new Avalonia.Thickness(4), Child = containerGrid };
            border.BorderBrush = PanelBorderBrush;
            border.Background = PanelFillBrush;
            border.CornerRadius = new Avalonia.CornerRadius(6);
            _runLogRowsPanel.Children.Add(border);
            ScrollRunLogToBottom();

            var row = new RoutineRunRowUi(scopeId, scriptName, toggleButton, detailsPanel, statusText, statusBadge, timeText, startedAt)
            {
                IsRunning = isRunning,
                Status = isRunning ? "Running" : "Idle",
                CollapseOnComplete = collapseOnComplete,
            };
            row.ToggleButton.Content = row.DetailsPanel.IsVisible ? "▾" : "▸";
            _runRowsByScope[scopeId] = row;
            return row;
        }

        private void AddRunMessage(string scopeId, string message, Logger.LogLevel level = Logger.LogLevel.Event)
        {
            if (!_runRowsByScope.TryGetValue(scopeId, out var row))
            {
                row = StartRunRow(scopeId, scopeId, DateTimeOffset.Now);
            }

            var levelText = level == Logger.LogLevel.Error ? "ERROR" : level == Logger.LogLevel.Warning ? "WARN" : "INFO";
            row.Entries.Add($"[{DateTime.Now:HH:mm:ss}] {levelText}: {message}");
            var levelBrush = level == Logger.LogLevel.Error
                ? Brushes.Red
                : level == Logger.LogLevel.Warning
                    ? Brushes.Goldenrod
                    : Brushes.DodgerBlue;

            var entryGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,120,120"), Margin = new Avalonia.Thickness(0, 1, 0, 1) };
            var childMarker = new TextBlock
            {
                Text = "↳",
                Foreground = Brushes.Gray,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };

            var statusPill = new Border
            {
                CornerRadius = new Avalonia.CornerRadius(4),
                BorderThickness = new Avalonia.Thickness(1),
                BorderBrush = PanelBorderBrush,
                Background = Brushes.Transparent,
                Padding = new Avalonia.Thickness(6, 2),
                Child = new TextBlock
                {
                    Text = levelText,
                    Foreground = levelBrush,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                }
            };

            var line = new TextBlock
            {
                Text = $"[{DateTime.Now:HH:mm:ss}] {message}",
                TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = level == Logger.LogLevel.Error
                    ? Avalonia.Media.Brushes.Red
                    : level == Logger.LogLevel.Warning
                        ? Avalonia.Media.Brushes.Goldenrod
                        : Avalonia.Media.Brushes.Gainsboro
            };

            var timeText = new TextBlock
            {
                Text = string.Empty,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = Brushes.Gray,
                FontFamily = new FontFamily("Consolas")
            };

            var detailsGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*") };
            Grid.SetColumn(statusPill, 0);
            Grid.SetColumn(line, 1);
            detailsGrid.Children.Add(statusPill);
            detailsGrid.Children.Add(line);

            Grid.SetColumn(childMarker, 0);
            Grid.SetColumn(detailsGrid, 1);
            Grid.SetColumn(timeText, 3);
            entryGrid.Children.Add(childMarker);
            entryGrid.Children.Add(detailsGrid);
            entryGrid.Children.Add(timeText);

            var rowBorder = new Border
            {
                BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
                BorderBrush = PanelBorderBrush,
                Padding = new Avalonia.Thickness(0, 1, 0, 2),
                Child = entryGrid,
            };

            row.DetailsPanel.Children.Add(rowBorder);
            ScrollRunLogToBottom();
        }

        private ProgressRowUi EnsureProgressRow(string scopeId, string progressKey, string message)
        {
            if (!_runRowsByScope.TryGetValue(scopeId, out var row))
            {
                row = StartRunRow(scopeId, scopeId, DateTimeOffset.Now);
            }

            if (!row.ProgressBars.TryGetValue(progressKey, out var progressBar))
            {
                var startedAt = DateTimeOffset.Now;
                progressBar = new ProgressRowUi
                {
                    StartedAt = startedAt,
                    Progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 14 },
                    PercentageText = new TextBlock
                    {
                        Text = "0%",
                        FontFamily = new FontFamily("Consolas"),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Foreground = Brushes.Gainsboro,
                    },
                    MessageText = new TextBlock
                    {
                        Text = $"[{startedAt:HH:mm:ss}] {(string.IsNullOrWhiteSpace(message) ? progressKey : message)}",
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = Brushes.Gainsboro,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    }
                };
                row.ProgressBars[progressKey] = progressBar;

                var rowContainer = new StackPanel { Spacing = 2 };

                var entryGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,120,120"), Margin = new Avalonia.Thickness(0, 1, 0, 1) };
                var childMarker = new TextBlock
                {
                    Text = "↳",
                    Foreground = Brushes.Gray,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                };

                var taskPillText = new TextBlock
                {
                    Text = "TASK",
                    Foreground = Brushes.DeepSkyBlue,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                };

                var taskPill = new Button
                {
                    Background = Brushes.Transparent,
                    BorderBrush = PanelBorderBrush,
                    BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(4),
                    Padding = new Avalonia.Thickness(6, 1),
                    Content = taskPillText,
                    MinWidth = 58,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                };

                var progressStatus = new Grid { ColumnDefinitions = new ColumnDefinitions("*,40") };
                Grid.SetColumn(progressBar.Progress, 0);
                Grid.SetColumn(progressBar.PercentageText, 1);
                progressStatus.Children.Add(progressBar.Progress);
                progressStatus.Children.Add(progressBar.PercentageText);

                progressBar.ElapsedText = new TextBlock
                {
                    Text = "00:00.000",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = Brushes.Gray,
                    FontFamily = new FontFamily("Consolas")
                };

                progressBar.DetailsPanel = new StackPanel { Spacing = 1, IsVisible = false, Margin = new Avalonia.Thickness(20, 0, 0, 0) };

                taskPill.Click += (_, _) =>
                {
                    progressBar.DetailsPanel.IsVisible = !progressBar.DetailsPanel.IsVisible;
                    taskPillText.Text = progressBar.DetailsPanel.IsVisible ? "TASK ▾" : "TASK ▸";
                };

                var detailsGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*") };
                Grid.SetColumn(taskPill, 0);
                Grid.SetColumn(progressBar.MessageText, 1);
                detailsGrid.Children.Add(taskPill);
                detailsGrid.Children.Add(progressBar.MessageText);

                Grid.SetColumn(childMarker, 0);
                Grid.SetColumn(detailsGrid, 1);
                Grid.SetColumn(progressStatus, 2);
                Grid.SetColumn(progressBar.ElapsedText, 3);
                entryGrid.Children.Add(childMarker);
                entryGrid.Children.Add(detailsGrid);
                entryGrid.Children.Add(progressStatus);
                entryGrid.Children.Add(progressBar.ElapsedText);

                var rowBorder = new Border
                {
                    BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
                    BorderBrush = PanelBorderBrush,
                    Padding = new Avalonia.Thickness(0, 1, 0, 2),
                    Child = entryGrid,
                };

                rowContainer.Children.Add(rowBorder);
                rowContainer.Children.Add(progressBar.DetailsPanel);
                row.DetailsPanel.Children.Add(rowContainer);
            }

            return progressBar;
        }

        private void UpdateRunProgress(string scopeId, string progressKey, double value, string message)
        {
            var progressBar = EnsureProgressRow(scopeId, progressKey, message);

            var clamped = Math.Max(0, Math.Min(100, value));
            progressBar.Progress.Value = clamped;
            progressBar.PercentageText.Text = $"{clamped:0.#}%";
            var elapsed = DateTimeOffset.Now - progressBar.StartedAt;
            progressBar.ElapsedText.Text = elapsed.ToString(@"mm\:ss\.fff");
            if (!string.IsNullOrWhiteSpace(message))
            {
                progressBar.MessageText.Text = $"[{progressBar.StartedAt:HH:mm:ss}] {message}";
            }

            if (progressBar.LastDetailPercent is null || Math.Abs(clamped - progressBar.LastDetailPercent.Value) >= 10)
            {
                if (_runRowsByScope.TryGetValue(scopeId, out var row))
                {
                    row.Entries.Add($"[{DateTime.Now:HH:mm:ss}] {progressKey}: {clamped:0.#}% - {message}");
                }

                progressBar.DetailsPanel.Children.Add(new TextBlock
                {
                    Text = $"[{DateTime.Now:HH:mm:ss}] {clamped:0.#}% - {message}",
                    Foreground = Brushes.Gray,
                    FontFamily = new FontFamily("Consolas")
                });
                progressBar.LastDetailPercent = clamped;
            }

            ScrollRunLogToBottom();
        }

        private void AddProgressDetailMessage(string scopeId, string progressKey, string message, Logger.LogLevel level)
        {
            var progressRow = EnsureProgressRow(scopeId, progressKey, progressKey);
            if (_runRowsByScope.TryGetValue(scopeId, out var row))
            {
                row.Entries.Add($"[{DateTime.Now:HH:mm:ss}] {progressKey}: {message}");
            }

            var detail = new TextBlock
            {
                Text = $"[{DateTime.Now:HH:mm:ss}] {message}",
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = level == Logger.LogLevel.Error
                    ? Brushes.Red
                    : level == Logger.LogLevel.Warning
                        ? Brushes.Goldenrod
                        : Brushes.Gainsboro
            };

            progressRow.DetailsPanel.Children.Add(detail);
            ScrollRunLogToBottom();
        }

        private void CompleteRunRow(string scopeId, bool success, TimeSpan elapsed, DateTimeOffset startedAt)
        {
            if (!_runRowsByScope.TryGetValue(scopeId, out var row))
            {
                return;
            }

            row.IsRunning = false;
            row.Status = success ? "Succeeded" : "Failed";
            row.StatusText.Text = success ? "✓" : "✗";
            row.StatusText.Foreground = success ? SuccessStatusBrush : FailureStatusBrush;
            row.StatusBadge.Background = Brushes.Transparent;
            row.TimeText.Text = elapsed.ToString(@"mm\:ss\.fff");

            if (row.CollapseOnComplete)
            {
                row.DetailsPanel.IsVisible = false;
                row.ToggleButton.Content = "▸";
            }
        }

        private void AppendLog(string message)
        {
            var row = StartRunRow("system", "System", DateTimeOffset.Now, false);
            row.Entries.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            var line = new TextBlock { Text = $"[{DateTime.Now:HH:mm:ss}] {message}", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            row.DetailsPanel.Children.Add(line);
            row.DetailsPanel.IsVisible = true;
            row.ToggleButton.Content = "▾";
        }

        private sealed class RoutineRunRowUi
        {
            public RoutineRunRowUi(
                string scopeId,
                string scriptName,
                Button toggleButton,
                StackPanel detailsPanel,
                TextBlock statusText,
                Border statusBadge,
                TextBlock timeText,
                DateTimeOffset startedAt)
            {
                ScopeId = scopeId;
                ScriptName = scriptName;
                ToggleButton = toggleButton;
                DetailsPanel = detailsPanel;
                StatusText = statusText;
                StatusBadge = statusBadge;
                TimeText = timeText;
                StartedAt = startedAt;
            }

            public string ScopeId { get; }
            public string ScriptName { get; }
            public Button ToggleButton { get; }
            public StackPanel DetailsPanel { get; }
            public TextBlock StatusText { get; }
            public Border StatusBadge { get; }
            public TextBlock TimeText { get; }
            public DateTimeOffset StartedAt { get; }
            public Dictionary<string, ProgressRowUi> ProgressBars { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> Entries { get; } = new();
            public string Status { get; set; } = "Running";
            public bool IsRunning { get; set; }
            public bool CollapseOnComplete { get; set; }
        }

        private sealed class ProgressRowUi
        {
            public DateTimeOffset StartedAt { get; set; }
            public ProgressBar Progress { get; set; } = null!;
            public TextBlock PercentageText { get; set; } = null!;
            public TextBlock MessageText { get; set; } = null!;
            public TextBlock ElapsedText { get; set; } = null!;
            public StackPanel DetailsPanel { get; set; } = null!;
            public double? LastDetailPercent { get; set; }
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveWindowState();
            _settingsService.SettingsChanged -= SettingsService_SettingsChanged;
            Logger.EntryWritten -= Logger_EntryWritten;
            _statusSpinnerTimer.Stop();
            _statusSpinnerTimer.Tick -= StatusSpinnerTimer_Tick;
            _commandsReloadTimer.Stop();
            _commandsReloadTimer.Tick -= CommandsReloadTimer_Tick;
            _commandsWatcher?.Dispose();
            _quickCommandHotKey.Pressed -= QuickCommandHotKey_Pressed;
            _quickCommandHotKey.Dispose();
            _runtime.Dispose();
            base.OnClosed(e);
        }

        private sealed class WindowSessionState
        {
            public double Width { get; set; }
            public double Height { get; set; }
            public int? X { get; set; }
            public int? Y { get; set; }
            public string? WindowState { get; set; }
        }

    }
}
