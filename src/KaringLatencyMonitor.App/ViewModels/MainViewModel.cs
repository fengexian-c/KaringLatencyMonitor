using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KaringLatencyMonitor.App.Models;
using KaringLatencyMonitor.App.Services;
using KaringLatencyMonitor.Core.Data;
using KaringLatencyMonitor.Core.Models;
using KaringLatencyMonitor.Core.Services;
using Microsoft.UI.Dispatching;

namespace KaringLatencyMonitor.App.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const long RetentionMs = 31L * 24 * 60 * 60 * 1000;
    private readonly SqliteRepository _repository;
    private readonly CollectionService _collector;
    private readonly SettingsStore _settingsStore;
    private readonly CollectorScheduler _scheduler;
    private readonly DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private readonly SemaphoreSlim _sortGate = new(1, 1);
    private bool _initialized;
    private bool _isBusy;
    private bool _isCollecting;
    private string _statusText = "正在初始化…";
    private string _searchText = string.Empty;
    private GroupOptionViewModel? _selectedGroup;
    private string _baseUrl = ControllerOptions.Default.BaseUrl;
    private string _secret = string.Empty;
    private string _targetUrl = ControllerOptions.Default.TargetUrl;
    private double _timeoutSeconds = ControllerOptions.Default.TimeoutSeconds;
    private double _maxConcurrency = ControllerOptions.Default.MaxConcurrency;
    private double _intervalMinutes = ControllerOptions.Default.IntervalMinutes;
    private bool _autoCollectionEnabled = true;
    private IReadOnlyList<NodeStatisticsRow> _dashboardRows = Array.Empty<NodeStatisticsRow>();
    private DashboardSortPreference _sortPreference = DashboardSortPreference.Default;

    public MainViewModel(
        SqliteRepository repository,
        CollectionService collector,
        SettingsStore settingsStore,
        CollectorScheduler scheduler)
    {
        _repository = repository;
        _collector = collector;
        _settingsStore = settingsStore;
        _scheduler = scheduler;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        RefreshGroupsCommand = new AsyncRelayCommand(RefreshGroupsAsync, () => !IsBusy);
        CollectNowCommand = new AsyncRelayCommand(CollectNowAsync, () => CanCollect);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => !IsBusy);
        SelectAllCommand = new AsyncRelayCommand(() => SetAllSelectionsAsync(true));
        ClearAllCommand = new AsyncRelayCommand(() => SetAllSelectionsAsync(false));
        InvertSelectionCommand = new AsyncRelayCommand(InvertSelectionAsync);
    }

    public ObservableCollection<GroupOptionViewModel> Groups { get; } = new();

    public ObservableCollection<NodeSelectionViewModel> Nodes { get; } = new();

    public ObservableCollection<NodeSelectionViewModel> VisibleNodes { get; } = new();

    public ObservableCollection<NodeStatisticsRowDisplay> Rows { get; } = new();

    public IAsyncRelayCommand RefreshGroupsCommand { get; }

    public IAsyncRelayCommand CollectNowCommand { get; }

    public IAsyncRelayCommand SaveSettingsCommand { get; }

    public IAsyncRelayCommand SelectAllCommand { get; }

    public IAsyncRelayCommand ClearAllCommand { get; }

    public IAsyncRelayCommand InvertSelectionCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanReorderNodes));
                NotifyCommandStates();
            }
        }
    }

    public bool IsCollecting
    {
        get => _isCollecting;
        private set
        {
            if (SetProperty(ref _isCollecting, value))
            {
                OnPropertyChanged(nameof(CanCollect));
                OnPropertyChanged(nameof(CanReorderNodes));
                NotifyCommandStates();
            }
        }
    }

    public bool CanCollect => !IsBusy && !IsCollecting && SelectedGroup is not null;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyNodeFilter();
                OnPropertyChanged(nameof(CanReorderNodes));
            }
        }
    }

    public GroupOptionViewModel? SelectedGroup
    {
        get => _selectedGroup;
        private set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                OnPropertyChanged(nameof(CanCollect));
                OnPropertyChanged(nameof(SelectedGroupDisplayName));
                NotifyCommandStates();
            }
        }
    }

    public string SelectedGroupDisplayName =>
        SelectedGroup?.DisplayName ?? "请选择节点组";

    public string SelectionSummary => $"已选择 {Nodes.Count(item => item.IsSelected)}/{Nodes.Count}";

    public bool CanReorderNodes =>
        !IsBusy
        && !IsCollecting
        && string.IsNullOrWhiteSpace(SearchText)
        && Nodes.Count > 1;

    public bool CanRestoreDefaultSort => _sortPreference.Key != DashboardSortKey.Default;

    public string Hours24SortGlyph => SortGlyph(DashboardSortKey.Hours24Delay);

    public string Hours24AvailabilitySortGlyph =>
        SortGlyph(DashboardSortKey.Hours24Availability);

    public string Days7SortGlyph => SortGlyph(DashboardSortKey.Days7Delay);

    public string Days7AvailabilitySortGlyph =>
        SortGlyph(DashboardSortKey.Days7Availability);

    public string Days30SortGlyph => SortGlyph(DashboardSortKey.Days30Delay);

    public string Days30AvailabilitySortGlyph =>
        SortGlyph(DashboardSortKey.Days30Availability);

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value);
    }

    public string Secret
    {
        get => _secret;
        set => SetProperty(ref _secret, value);
    }

    public string TargetUrl
    {
        get => _targetUrl;
        set => SetProperty(ref _targetUrl, value);
    }

    public double TimeoutSeconds
    {
        get => _timeoutSeconds;
        set => SetProperty(ref _timeoutSeconds, value);
    }

    public double MaxConcurrency
    {
        get => _maxConcurrency;
        set => SetProperty(ref _maxConcurrency, value);
    }

    public double IntervalMinutes
    {
        get => _intervalMinutes;
        set => SetProperty(ref _intervalMinutes, value);
    }

    public bool AutoCollectionEnabled
    {
        get => _autoCollectionEnabled;
        set => SetProperty(ref _autoCollectionEnabled, value);
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        IsBusy = true;
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            await Task.Run(_repository.Initialize);
            var retentionCutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - RetentionMs;
            await Task.Run(() => _repository.DeleteSamplesOlderThan(retentionCutoff));
            var loaded = await _settingsStore.LoadAsync();
            ApplySettings(loaded.Settings, loaded.Secret);

            var cached = await Task.Run(_repository.GetCachedGroups);
            ReplaceGroups(cached, loaded.Settings.SelectedGroupName);

            try
            {
                var liveGroups = await _collector.RefreshGroupsAsync(CurrentOptions());
                ReplaceGroups(liveGroups, loaded.Settings.SelectedGroupName);
                StatusText = $"已连接 Karing，发现 {liveGroups.Count} 个节点组";
            }
            catch (KaringApiException exception)
            {
                StatusText = "Karing 暂不可用，正在显示本地缓存：" + exception.Message;
            }

            if (SelectedGroup is not null)
            {
                await LoadSortPreferenceAsync(SelectedGroup.Name);
                await LoadNodesAndDashboardAsync(SelectedGroup.Name);
            }
        }
        finally
        {
            IsBusy = false;
        }

        ConfigureScheduler(runImmediately: AutoCollectionEnabled && SelectedGroup is not null);
    }

    public async Task SelectGroupAsync(GroupOptionViewModel? group)
    {
        if (group is null || string.Equals(SelectedGroup?.Name, group.Name, StringComparison.Ordinal))
        {
            return;
        }

        SelectedGroup = group;
        IsBusy = true;
        try
        {
            try
            {
                await _collector.RefreshGroupAsync(CurrentOptions(), group.Name);
            }
            catch (KaringApiException exception)
            {
                StatusText = "无法刷新节点组，使用本地成员缓存：" + exception.Message;
            }

            await LoadSortPreferenceAsync(group.Name);
            await LoadNodesAndDashboardAsync(group.Name);
            await PersistSettingsAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveSelectionAsync()
    {
        var groupName = SelectedGroup?.Name;
        if (groupName is null)
        {
            return;
        }

        await _selectionGate.WaitAsync();
        try
        {
            var selectedTags = Nodes
                .Where(item => item.IsSelected)
                .Select(item => item.Tag)
                .ToArray();
            await Task.Run(() => _repository.SaveSelection(groupName, selectedTags));
            OnPropertyChanged(nameof(SelectionSummary));
            await LoadDashboardAsync(groupName);
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    public async Task SaveDefaultNodeOrderAsync(IReadOnlyList<string> orderedTags)
    {
        var groupName = SelectedGroup?.Name;
        if (groupName is null || !CanReorderNodes)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await Task.Run(() => _repository.SaveDefaultNodeOrder(groupName, orderedTags));
            await LoadNodesAndDashboardAsync(groupName);
            StatusText = "默认节点顺序已保存";
        }
        catch (Exception exception)
        {
            await LoadNodesAndDashboardAsync(groupName);
            StatusText = "保存节点顺序失败：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task RestoreDefaultSortAsync() =>
        SaveSortPreferenceAsync(DashboardSortPreference.Default);

    public Task ToggleHours24DelaySortAsync() =>
        ToggleSortAsync(DashboardSortKey.Hours24Delay);

    public Task ToggleHours24AvailabilitySortAsync() =>
        ToggleSortAsync(DashboardSortKey.Hours24Availability);

    public Task ToggleDays7DelaySortAsync() =>
        ToggleSortAsync(DashboardSortKey.Days7Delay);

    public Task ToggleDays7AvailabilitySortAsync() =>
        ToggleSortAsync(DashboardSortKey.Days7Availability);

    public Task ToggleDays30DelaySortAsync() =>
        ToggleSortAsync(DashboardSortKey.Days30Delay);

    public Task ToggleDays30AvailabilitySortAsync() =>
        ToggleSortAsync(DashboardSortKey.Days30Availability);

    private async Task RefreshGroupsAsync()
    {
        IsBusy = true;
        try
        {
            var selectedName = SelectedGroup?.Name;
            var groups = await _collector.RefreshGroupsAsync(CurrentOptions());
            ReplaceGroups(groups, selectedName);
            if (SelectedGroup is not null)
            {
                await LoadSortPreferenceAsync(SelectedGroup.Name);
                await LoadNodesAndDashboardAsync(SelectedGroup.Name);
            }

            StatusText = $"已刷新 {groups.Count} 个节点组";
        }
        catch (KaringApiException exception)
        {
            StatusText = "刷新失败：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CollectNowAsync()
    {
        await CollectInternalAsync(CancellationToken.None);
    }

    private async Task CollectInternalAsync(CancellationToken cancellationToken)
    {
        var groupName = SelectedGroup?.Name;
        if (groupName is null || IsCollecting)
        {
            return;
        }

        IsCollecting = true;
        try
        {
            StatusText = "正在采集…";
            var progress = new Progress<ProbeOutcome>(outcome =>
            {
                StatusText = outcome.Kind switch
                {
                    ProbeOutcomeKind.Success => $"{outcome.Tag}: {outcome.DelayMs} ms",
                    ProbeOutcomeKind.NodeFailure => $"{outcome.Tag}: 探测失败",
                    _ => $"{outcome.Tag}: 控制器不可用"
                };
            });
            var result = await _collector.RunOnceAsync(
                CurrentOptions(),
                groupName,
                progress,
                cancellationToken);
            await LoadNodesAndDashboardAsync(groupName);
            StatusText = FormatCollectionResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "采集已停止";
        }
        catch (Exception exception)
        {
            StatusText = "采集失败：" + exception.Message;
        }
        finally
        {
            IsCollecting = false;
        }
    }

    private async Task SaveSettingsAsync()
    {
        IsBusy = true;
        try
        {
            await PersistSettingsAsync();
            ConfigureScheduler(runImmediately: false);
            StatusText = "设置已保存";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SetAllSelectionsAsync(bool selected)
    {
        foreach (var node in Nodes)
        {
            node.IsSelected = selected;
        }

        await SaveSelectionAsync();
    }

    private async Task InvertSelectionAsync()
    {
        foreach (var node in Nodes)
        {
            node.IsSelected = !node.IsSelected;
        }

        await SaveSelectionAsync();
    }

    private async Task LoadNodesAndDashboardAsync(string groupName)
    {
        var nodes = await Task.Run(() => _repository.GetSelectableNodes(groupName));
        Nodes.Clear();
        foreach (var node in nodes)
        {
            Nodes.Add(new NodeSelectionViewModel(node));
        }

        ApplyNodeFilter();
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanReorderNodes));
        await LoadDashboardAsync(groupName);
    }

    private async Task LoadDashboardAsync(string groupName)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var anchor = DashboardTimeAlignment.CeilToFiveMinutes(now);
        var snapshot = await Task.Run(() => _repository.LoadDashboard(groupName, anchor));
        _dashboardRows = snapshot.Rows;
        ApplyDashboardSort();
    }

    private async Task LoadSortPreferenceAsync(string groupName)
    {
        var preference = await Task.Run(() =>
            _repository.GetDashboardSortPreference(groupName));
        SetSortPreference(preference);
    }

    private Task ToggleSortAsync(DashboardSortKey key)
    {
        var next = _sortPreference.Key == key
            ? new DashboardSortPreference(key, !_sortPreference.Descending)
            : new DashboardSortPreference(key, false);
        return SaveSortPreferenceAsync(next);
    }

    private async Task SaveSortPreferenceAsync(DashboardSortPreference preference)
    {
        var groupName = SelectedGroup?.Name;
        if (groupName is null)
        {
            return;
        }

        await _sortGate.WaitAsync();
        try
        {
            var previous = _sortPreference;
            SetSortPreference(preference);
            ApplyDashboardSort();
            try
            {
                await Task.Run(() =>
                    _repository.SaveDashboardSortPreference(groupName, preference));
                StatusText = preference.Key == DashboardSortKey.Default
                    ? "已恢复默认节点顺序"
                    : $"已按{SortPeriodName(preference.Key)}{SortMetricName(preference.Key)}"
                      + (preference.Descending ? "降序" : "升序")
                      + "排列";
            }
            catch (Exception exception)
            {
                SetSortPreference(previous);
                ApplyDashboardSort();
                StatusText = "保存排序设置失败：" + exception.Message;
            }
        }
        finally
        {
            _sortGate.Release();
        }
    }

    private void SetSortPreference(DashboardSortPreference preference)
    {
        _sortPreference = preference;
        OnPropertyChanged(nameof(CanRestoreDefaultSort));
        OnPropertyChanged(nameof(Hours24SortGlyph));
        OnPropertyChanged(nameof(Hours24AvailabilitySortGlyph));
        OnPropertyChanged(nameof(Days7SortGlyph));
        OnPropertyChanged(nameof(Days7AvailabilitySortGlyph));
        OnPropertyChanged(nameof(Days30SortGlyph));
        OnPropertyChanged(nameof(Days30AvailabilitySortGlyph));
    }

    private void ApplyDashboardSort()
    {
        var sorted = DashboardRowSorter.Sort(_dashboardRows, _sortPreference);
        Rows.Clear();
        foreach (var row in sorted)
        {
            Rows.Add(new NodeStatisticsRowDisplay(row));
        }
    }

    private string SortGlyph(DashboardSortKey key) => _sortPreference.Key == key
        ? _sortPreference.Descending ? "↓" : "↑"
        : "↕";

    private static string SortPeriodName(DashboardSortKey key) => key switch
    {
        DashboardSortKey.Hours24Delay or DashboardSortKey.Hours24Availability => "24小时",
        DashboardSortKey.Days7Delay or DashboardSortKey.Days7Availability => "7天",
        DashboardSortKey.Days30Delay or DashboardSortKey.Days30Availability => "30天",
        _ => string.Empty
    };

    private static string SortMetricName(DashboardSortKey key) => key switch
    {
        DashboardSortKey.Hours24Availability or
        DashboardSortKey.Days7Availability or
        DashboardSortKey.Days30Availability => "可用率",
        DashboardSortKey.Hours24Delay or
        DashboardSortKey.Days7Delay or
        DashboardSortKey.Days30Delay => "延迟",
        _ => string.Empty
    };

    private void ReplaceGroups(
        IReadOnlyList<NodeGroupDescriptor> groups,
        string? preferredName)
    {
        var currentName = preferredName ?? SelectedGroup?.Name;
        Groups.Clear();
        foreach (var group in groups)
        {
            Groups.Add(new GroupOptionViewModel(group.Name, group.Type, group.Nodes.Count));
        }

        SelectedGroup = Groups.FirstOrDefault(item =>
                            string.Equals(item.Name, currentName, StringComparison.Ordinal))
                        ?? Groups.FirstOrDefault();
    }

    private void ApplyNodeFilter()
    {
        VisibleNodes.Clear();
        var query = SearchText.Trim();
        foreach (var node in Nodes)
        {
            if (query.Length == 0 || node.Tag.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                VisibleNodes.Add(node);
            }
        }
    }

    private void ApplySettings(AppSettings settings, string secret)
    {
        BaseUrl = settings.BaseUrl;
        TargetUrl = settings.TargetUrl;
        TimeoutSeconds = settings.TimeoutSeconds;
        MaxConcurrency = settings.MaxConcurrency;
        IntervalMinutes = settings.IntervalMinutes;
        AutoCollectionEnabled = settings.AutoCollectionEnabled;
        Secret = secret;
    }

    private ControllerOptions CurrentOptions() => new ControllerOptions(
        BaseUrl,
        Secret,
        TargetUrl,
        (int)Math.Round(TimeoutSeconds),
        (int)Math.Round(MaxConcurrency),
        (int)Math.Round(IntervalMinutes)).Normalize();

    private AppSettings CurrentSettings()
    {
        var options = CurrentOptions();
        return new AppSettings(
            options.BaseUrl,
            options.TargetUrl,
            options.TimeoutSeconds,
            options.MaxConcurrency,
            options.IntervalMinutes,
            AutoCollectionEnabled,
            SelectedGroup?.Name);
    }

    private Task PersistSettingsAsync() =>
        _settingsStore.SaveAsync(CurrentSettings(), Secret);

    private void ConfigureScheduler(bool runImmediately)
    {
        _scheduler.Stop();
        if (!AutoCollectionEnabled)
        {
            return;
        }

        _scheduler.Start(
            cancellationToken => RunOnUiThreadAsync(
                () => CollectInternalAsync(cancellationToken)),
            () => TimeSpan.FromMinutes(CurrentOptions().IntervalMinutes),
            runImmediately);
    }

    private Task RunOnUiThreadAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    completion.SetResult(true);
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }))
        {
            completion.SetCanceled();
        }

        return completion.Task;
    }

    private static string FormatCollectionResult(CollectionResult result) => result.Status switch
    {
        ProbeRunStatus.Complete =>
            $"采集完成：成功 {result.SuccessCount}，失败 {result.FailureCount}",
        ProbeRunStatus.Partial =>
            $"部分完成：成功 {result.SuccessCount}，失败 {result.FailureCount}；{result.Error}",
        ProbeRunStatus.ControllerOffline =>
            "Karing 控制器离线，本轮未计入节点可用率",
        ProbeRunStatus.Cancelled => "采集已取消",
        _ => "采集失败：" + result.Error
    };

    private void NotifyCommandStates()
    {
        RefreshGroupsCommand.NotifyCanExecuteChanged();
        CollectNowCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _selectionGate.Dispose();
    }
}
