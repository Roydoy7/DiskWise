using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiskWise.Services;

/// <summary>
/// Manages navigation history for Explorer-style browsing
/// </summary>
public partial class NavigationService : ObservableObject
{
    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();

    [ObservableProperty]
    private string _currentPath = string.Empty;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    [ObservableProperty]
    private bool _canGoUp;

    [ObservableProperty]
    private bool _isAtHome = true;

    public const string HomePath = "::HOME::";

    public event EventHandler<string>? NavigationRequested;

    public void NavigateTo(string path)
    {
        if (path == CurrentPath) return;

        if (!string.IsNullOrEmpty(CurrentPath))
        {
            _backStack.Push(CurrentPath);
        }

        _forwardStack.Clear();
        CurrentPath = path;
        UpdateState();

        NavigationRequested?.Invoke(this, path);
    }

    public void GoBack()
    {
        if (!CanGoBack) return;

        _forwardStack.Push(CurrentPath);
        CurrentPath = _backStack.Pop();
        UpdateState();

        NavigationRequested?.Invoke(this, CurrentPath);
    }

    public void GoForward()
    {
        if (!CanGoForward) return;

        _backStack.Push(CurrentPath);
        CurrentPath = _forwardStack.Pop();
        UpdateState();

        NavigationRequested?.Invoke(this, CurrentPath);
    }

    public void GoUp()
    {
        if (!CanGoUp || IsAtHome) return;

        var parent = Path.GetDirectoryName(CurrentPath);
        if (!string.IsNullOrEmpty(parent))
        {
            NavigateTo(parent);
        }
        else
        {
            // At drive root, go to home
            GoHome();
        }
    }

    public void GoHome()
    {
        NavigateTo(HomePath);
    }

    private void UpdateState()
    {
        CanGoBack = _backStack.Count > 0;
        CanGoForward = _forwardStack.Count > 0;
        IsAtHome = CurrentPath == HomePath;
        CanGoUp = !IsAtHome && !string.IsNullOrEmpty(CurrentPath);
    }

    public void Clear()
    {
        _backStack.Clear();
        _forwardStack.Clear();
        CurrentPath = HomePath;
        UpdateState();
    }
}
