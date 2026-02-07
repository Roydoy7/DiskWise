using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
}
