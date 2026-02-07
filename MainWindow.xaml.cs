using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskWise.Controls;
using DiskWise.Models;
using DiskWise.ViewModels;

namespace DiskWise;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // Update maximize/restore icon
        MaximizeIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        await ViewModel.SaveSettingsAsync();
    }

    private void Item_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && item.DataContext is FileSystemItem fsItem)
        {
            ViewModel.ItemDoubleClickedCommand.Execute(fsItem);
        }
    }

    private void Drive_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement element && element.DataContext is DriveItem drive)
        {
            ViewModel.DriveDoubleClickedCommand.Execute(drive);
        }
    }

    private void RecentFolder_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement element && element.DataContext is RecentFolder folder)
        {
            ViewModel.RecentFolderClickedCommand.Execute(folder);
        }
    }

    private void SearchResult_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && item.DataContext is FileSystemItem fsItem)
        {
            ViewModel.NavigateToSearchResultCommand.Execute(fsItem);
        }
    }

    private void TopConsumer_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is FileSystemItem fsItem)
        {
            ViewModel.ItemDoubleClickedCommand.Execute(fsItem);
        }
    }

    private void SettingsOverlay_BackdropClick(object sender, MouseButtonEventArgs e)
    {
        ViewModel.CloseSettingsCommand.Execute(null);
    }

    private void PieChart_SliceClicked(object sender, RoutedPropertyChangedEventArgs<PieSliceData?> e)
    {
        if (e.NewValue?.Tag is FileSystemItem fsItem && fsItem.IsDirectory)
            ViewModel.ItemDoubleClickedCommand.Execute(fsItem);
    }

    private void PieLegend_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is PieSliceData slice
            && slice.Tag is FileSystemItem fsItem && fsItem.IsDirectory)
            ViewModel.ItemDoubleClickedCommand.Execute(fsItem);
    }
}
