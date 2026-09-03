using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using KaringLatencyMonitor.App.Services;
using KaringLatencyMonitor.App.ViewModels;
using KaringLatencyMonitor.Core.Data;
using KaringLatencyMonitor.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinUIEx;

namespace KaringLatencyMonitor.App;

public sealed partial class MainWindow : Window
{
    private bool _initialized;
    private readonly KaringApiClient _api;
    private TrayIcon? _trayIcon;
    private bool _allowClose;
    private bool _isHiddenToTray;
    private const uint TrayIconId = 0x4B4C;

    public MainWindow()
    {
        var repository = new SqliteRepository(AppPaths.DatabasePath);
        _api = new KaringApiClient();
        var collector = new CollectionService(_api, repository);
        ViewModel = new MainViewModel(
            repository,
            collector,
            new SettingsStore(),
            new CollectorScheduler());

        InitializeComponent();
        AppWindow.Title = "Karing 延迟监控";
        AppWindow.Resize(new SizeInt32(1180, 720));
        ConfigureTray();
        Activated += OnFirstActivated;
        Closed += (_, _) =>
        {
            _trayIcon?.Dispose();
            ViewModel.Dispose();
            _api.Dispose();
        };
    }

    public MainViewModel ViewModel { get; }

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        Activated -= OnFirstActivated;
        try
        {
            await ViewModel.InitializeAsync();
            StartupDiagnostics.Write("Main view model initialized.");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Main view model initialization failed.", exception);
        }
    }

    private void GroupSelectorFlyout_Opening(
        object sender,
        object args)
    {
        GroupSelectorList.Width = GroupSelectorButton.ActualWidth;
        if (ViewModel.SelectedGroup is { } selectedGroup)
        {
            GroupSelectorList.ScrollIntoView(selectedGroup);
        }
    }

    private async void GroupSelectorList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (GroupSelectorList.SelectedItem is GroupOptionViewModel group)
        {
            if (string.Equals(
                    ViewModel.SelectedGroup?.Name,
                    group.Name,
                    StringComparison.Ordinal))
            {
                return;
            }

            GroupSelectorFlyout.Hide();
            await ViewModel.SelectGroupAsync(group);
        }
    }

    private void NodeSearchBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        ViewModel.SearchText = NodeSearchBox.Text;
    }

    private async void NodeCheckBox_Click(object sender, RoutedEventArgs args)
    {
        await ViewModel.SaveSelectionAsync();
    }

    private async void NodeSelectionList_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
        {
            return;
        }

        var orderedTags = ViewModel.VisibleNodes
            .Select(node => node.Tag)
            .ToArray();
        await ViewModel.SaveDefaultNodeOrderAsync(orderedTags);
    }

    private async void RestoreDefaultSortButton_Click(object sender, RoutedEventArgs args) =>
        await ViewModel.RestoreDefaultSortAsync();

    private async void Hours24DelaySortButton_Click(object sender, RoutedEventArgs args) =>
        await ViewModel.ToggleHours24DelaySortAsync();

    private async void Hours24AvailabilitySortButton_Click(object sender, RoutedEventArgs args) =>
        await ViewModel.ToggleHours24AvailabilitySortAsync();

    private async void Days7DelaySortButton_Click(object sender, RoutedEventArgs args) =>
        await ViewModel.ToggleDays7DelaySortAsync();

    private async void Days7AvailabilitySortButton_Click(object sender, RoutedEventArgs args) =>
        await ViewModel.ToggleDays7AvailabilitySortAsync();

    private async void Days30DelaySortButton_Click(object sender, RoutedEventArgs args) =>
        await ViewModel.ToggleDays30DelaySortAsync();

    private async void Days30AvailabilitySortButton_Click(object sender, RoutedEventArgs args) =>
        await ViewModel.ToggleDays30AvailabilitySortAsync();

    private void RefreshGroupsButton_Click(object sender, RoutedEventArgs args) =>
        ViewModel.RefreshGroupsCommand.Execute(null);

    private void CollectNowButton_Click(object sender, RoutedEventArgs args) =>
        ViewModel.CollectNowCommand.Execute(null);

    private void SelectAllButton_Click(object sender, RoutedEventArgs args) =>
        ViewModel.SelectAllCommand.Execute(null);

    private void ClearAllButton_Click(object sender, RoutedEventArgs args) =>
        ViewModel.ClearAllCommand.Execute(null);

    private void InvertSelectionButton_Click(object sender, RoutedEventArgs args) =>
        ViewModel.InvertSelectionCommand.Execute(null);

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs args) =>
        ViewModel.SaveSettingsCommand.Execute(null);

    private void ConfigureTray()
    {
        try
        {
            var iconPath = TrayIconAsset.EnsureCreated();
            AppWindow.SetIcon(iconPath);
            _trayIcon = new TrayIcon(TrayIconId, iconPath, "Karing 延迟监控");
            _trayIcon.Selected += (_, _) => RestoreFromTray();
            _trayIcon.ContextMenu += (_, args) => args.Flyout = BuildTrayMenu();
            _trayIcon.IsVisible = true;
        }
        catch
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
        }

        AppWindow.Closing += (_, args) =>
        {
            if (_allowClose || _trayIcon is null)
            {
                return;
            }

            HideToTray();
            args.Cancel = true;
        };
    }

    private MenuFlyout BuildTrayMenu()
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "打开" };
        open.Click += (_, _) => RestoreFromTray();
        menu.Items.Add(open);

        var collect = new MenuFlyoutItem
        {
            Text = "立即采集",
            IsEnabled = ViewModel.CollectNowCommand.CanExecute(null)
        };
        collect.Click += (_, _) => ViewModel.CollectNowCommand.Execute(null);
        menu.Items.Add(collect);
        menu.Items.Add(new MenuFlyoutSeparator());

        var exit = new MenuFlyoutItem { Text = "退出" };
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(exit);
        return menu;
    }

    private void HideToTray()
    {
        if (_isHiddenToTray)
        {
            return;
        }

        _isHiddenToTray = true;
        RootGrid.Visibility = Visibility.Collapsed;
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Hide();
        ReleaseUiResources();
    }

    internal void RestoreFromTray()
    {
        _isHiddenToTray = false;
        RootGrid.Visibility = Visibility.Visible;
        AppWindow.IsShownInSwitchers = true;
        AppWindow.Show();
        Activate();
    }

    private void ExitApplication()
    {
        _allowClose = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Close();
    }

    private static void ReleaseUiResources()
    {
        try
        {
            // Collection and JSON parsing can leave temporary buffers on the LOH.
            // The user-driven tray transition is a suitable low-frequency point for
            // a compacting collection; never do this on the periodic probe path.
            GCSettings.LargeObjectHeapCompactionMode =
                GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Optimized,
                blocking: true,
                compacting: true);
            GC.WaitForPendingFinalizers();

            using var process = Process.GetCurrentProcess();
            if (!SetProcessWorkingSetSize(
                    process.Handle,
                    new IntPtr(-1),
                    new IntPtr(-1)))
            {
                StartupDiagnostics.Write(
                    $"Unable to trim process working set: Win32 {Marshal.GetLastWin32Error()}.");
            }
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Unable to release tray UI resources.", exception);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr process,
        IntPtr minimumWorkingSetSize,
        IntPtr maximumWorkingSetSize);
}
