using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopTimeTracker.Services;
using DesktopTimeTracker.Tracking;
using DesktopTimeTracker.ViewModels;

namespace DesktopTimeTracker;

public partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => LoadBackground(viewModel);

        // 背景相关属性变化时立即重载背景（无需点保存）
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.SelectedBackgroundId)
                or nameof(MainViewModel.SelectedBackgroundMode)
                or nameof(MainViewModel.BackgroundOpacity))
            {
                LoadBackground(viewModel);
            }
        };

        StateChanged += (_, _) =>
        {
            MaxButton.Content = WindowState == WindowState.Maximized ? "❐" : "▢";
            MaxButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        };
    }

    /// <summary>Lets the tray "exit" command actually close the window.</summary>
    public void PrepareForExit() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            ((App)Application.Current).HideToTray();
            return;
        }
        base.OnClosing(e);
    }

    // ---------------- 自绘标题栏（无系统非客户区） ----------------v

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (IsInteractiveControl(e.OriginalSource as DependencyObject)) return;

        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private static bool IsInteractiveControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button or TextBox or ComboBox or CheckBox or RadioButton
                or ScrollViewer or DataGrid or ProgressBar or MenuItem)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ---------------- 背景动态加载 ----------------
    private void LoadBackground(MainViewModel vm)
    {
        try
        {
            var sel = vm.BackgroundChoices.FirstOrDefault(x => x.Id == vm.SelectedBackgroundId);
            if (sel is null || sel.Id == Models.BackgroundImage.NoneId || string.IsNullOrEmpty(sel.FileName))
            {
                BgImage.Visibility = Visibility.Hidden;
                return;
            }
            var path = System.IO.Path.Combine(BackgroundImageStore.BackgroundsDirectory(TrackerSettings.DefaultFolder), sel.FileName);
            if (!System.IO.File.Exists(path)) { BgImage.Visibility = Visibility.Hidden; return; }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            BgImage.Source = bmp;
            BgImage.Opacity = vm.BackgroundOpacity;
            BgImage.Visibility = Visibility.Visible;

            switch (vm.SelectedBackgroundMode)
            {
                case Models.BackgroundMode.CornerTopRight:
                    BgImage.Stretch = Stretch.Uniform;
                    BgImage.HorizontalAlignment = HorizontalAlignment.Right;
                    BgImage.VerticalAlignment = VerticalAlignment.Top;
                    break;
                case Models.BackgroundMode.CornerBottomLeft:
                    BgImage.Stretch = Stretch.Uniform;
                    BgImage.HorizontalAlignment = HorizontalAlignment.Left;
                    BgImage.VerticalAlignment = VerticalAlignment.Bottom;
                    break;
                case Models.BackgroundMode.FillCover:
                    BgImage.Stretch = Stretch.UniformToFill;
                    BgImage.HorizontalAlignment = HorizontalAlignment.Stretch;
                    BgImage.VerticalAlignment = VerticalAlignment.Stretch;
                    break;
                default:
                    BgImage.Stretch = Stretch.Uniform;
                    BgImage.HorizontalAlignment = HorizontalAlignment.Stretch;
                    BgImage.VerticalAlignment = VerticalAlignment.Stretch;
                    break;
            }
        }
        catch
        {
            BgImage.Visibility = Visibility.Hidden;
        }
    }

    // ---------------- 标题栏快捷按钮 ----------------
    private void OnThemeQuickClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var menu = new ContextMenu();
        foreach (var pair in ThemeManager.Available)
        {
            var item = new MenuItem { Header = pair.Value };
            var copy = pair.Key;
            item.Click += (_, _) => { ThemeManager.Apply(copy); };
            if (vm.Settings.ThemeName == pair.Key)
                item.Icon = new TextBlock { Text = "✓", Margin = new Thickness(0) };
            menu.Items.Add(item);
        }
        ((Button)sender).ContextMenu = menu;
        menu.PlacementTarget = (Button)sender;
        menu.IsOpen = true;
    }

    private void OnPetQuickClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.SyncPetModels();
        var menu = new ContextMenu();

        var toggle = new MenuItem { Header = "显示桌宠", IsCheckable = true, IsChecked = vm.Settings.ShowPet };
        toggle.Click += (_, _) =>
        {
            vm.Settings.ShowPet = toggle.IsChecked;
            vm.Settings.Save();
        };
        menu.Items.Add(toggle);
        menu.Items.Add(new Separator());

        foreach (var m in vm.PetModelChoices)
        {
            var item = new MenuItem { Header = m.Name, Tag = m.Id };
            item.Click += (_, _) => vm.SelectedPetModelId = m.Id;
            if (vm.SelectedPetModelId == m.Id)
                item.Icon = new TextBlock { Text = "✓" };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());

        var openFolder = new MenuItem { Header = "打开模型文件夹" };
        openFolder.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PetModelStore.ModelsDirectory(TrackerSettings.DefaultFolder)) { UseShellExecute = true }); }
            catch { }
        };
        menu.Items.Add(openFolder);

        var gotoSettings = new MenuItem { Header = "去设置页导入 / 重命名" };
        gotoSettings.Click += (_, _) => vm.NavigateToSettingsCommand.Execute(null);
        menu.Items.Add(gotoSettings);

        ((Button)sender).ContextMenu = menu;
        menu.PlacementTarget = (Button)sender;
        menu.IsOpen = true;
    }

    private void OnBackgroundQuickClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.SyncBackgrounds();
        var menu = new ContextMenu();

        foreach (var bg in vm.BackgroundChoices)
        {
            var item = new MenuItem { Header = bg.Name, Tag = bg.Id };
            item.Click += (_, _) => vm.SelectedBackgroundId = bg.Id;
            if (vm.SelectedBackgroundId == bg.Id)
                item.Icon = new TextBlock { Text = "✓" };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());

        var openFolder = new MenuItem { Header = "打开背景文件夹" };
        openFolder.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(BackgroundImageStore.BackgroundsDirectory(TrackerSettings.DefaultFolder)) { UseShellExecute = true }); }
            catch { }
        };
        menu.Items.Add(openFolder);

        var gotoSettings = new MenuItem { Header = "去设置页导入 / 删除" };
        gotoSettings.Click += (_, _) => vm.NavigateToSettingsCommand.Execute(null);
        menu.Items.Add(gotoSettings);

        ((Button)sender).ContextMenu = menu;
        menu.PlacementTarget = (Button)sender;
        menu.IsOpen = true;
    }

    private void OnSaveQuickClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.SaveSettingsCommand.Execute(null);
    }
}
