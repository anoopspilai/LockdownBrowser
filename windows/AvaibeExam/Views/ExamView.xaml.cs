using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AvaibeExam.Core;
using AvaibeExam.Models;

namespace AvaibeExam.Views;

/// <summary>CONTRACT §7.3 — status strip (with §10.3 Resources and §10.6 offline time) + WebView2 + overlays.</summary>
public partial class ExamView : UserControl
{
    private readonly AppState _state;
    private bool _resettingResources;

    public ExamView(AppState state)
    {
        InitializeComponent();
        _state = state;
        DataContext = state;
        Exit.Attach(state);
        Dialog.Attach(state);
        Warning.Dismissed += () => _state.DismissWarning();

        state.PropertyChanged += OnStateChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Render();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HostWebView();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _state.PropertyChanged -= OnStateChanged;
        WebHost.Content = null;
    }

    private void HostWebView()
    {
        var control = _state.Web.Control;
        if (control == null)
        {
            WebHost.Content = null;
            LoadingText.Visibility = Visibility.Visible;
            return;
        }
        if (!ReferenceEquals(WebHost.Content, control))
        {
            WebHost.Content = control;
        }
        LoadingText.Visibility = Visibility.Collapsed;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Session):
            case nameof(AppState.Policy):
            case nameof(AppState.AllowedLinks):
            case nameof(AppState.RemainingSeconds):
            case nameof(AppState.RemainingTimeText):
            case nameof(AppState.LockdownMode):
            case nameof(AppState.IsOnline):
            case nameof(AppState.BackendReachable):
            case nameof(AppState.OfflineSeconds):
            case nameof(AppState.OfflineText):
            case nameof(AppState.DisplayCount):
            case nameof(AppState.WarningMessage):
            case nameof(AppState.PauseMessage):
            case nameof(AppState.ExitOverlay):
            case nameof(AppState.ScriptDialog):
            case nameof(AppState.IsOnResourcePage):
            case nameof(AppState.BlockedNotice):
            case nameof(AppState.IsExternalExam):
            case nameof(AppState.CanFinishExam):
                Render();
                break;
        }
    }

    private void Render()
    {
        HostWebView();

        var session = _state.Session;
        StudentText.Text = session?.StudentOrEmpty.Name ?? string.Empty;

        var notice = _state.BlockedNotice;
        BlockedNoticeText.Text = notice ?? string.Empty;
        BlockedNoticeBar.Visibility = string.IsNullOrEmpty(notice) ? Visibility.Collapsed : Visibility.Visible;
        ExamText.Text = session?.ExamOrEmpty.Title ?? string.Empty;

        CountdownText.Text = AppState.FormatCountdown(_state.RemainingSeconds);
        CountdownText.Foreground = _state.RemainingSeconds < 300
            ? (Brush)FindResource("DangerBrush")
            : Brushes.White;

        RenderBadge(_state.LockdownMode);

        var online = _state.IsOnline;
        var backend = _state.BackendReachable;
        OnlineDot.Fill = (Brush)FindResource(online && backend ? "SuccessBrush" : "DangerBrush");
        OnlineText.Text = online ? (backend ? "Online" : "Backend unreachable") : "Offline";

        var offline = _state.OfflineText;
        OfflineText.Text = offline;
        OfflineText.Visibility = string.IsNullOrEmpty(offline) ? Visibility.Collapsed : Visibility.Visible;

        if (_state.DisplayCount > 1)
        {
            DisplaysText.Text = _state.DisplayCount + " displays";
            DisplaysText.Visibility = Visibility.Visible;
        }
        else
        {
            DisplaysText.Visibility = Visibility.Collapsed;
        }

        RenderResources();

        // Overlays
        var pause = _state.PauseMessage;
        Pause.Message = pause ?? string.Empty;
        Pause.Visibility = pause != null ? Visibility.Visible : Visibility.Collapsed;

        var warning = _state.WarningMessage;
        var warningWasHidden = Warning.Visibility != Visibility.Visible;
        Warning.Message = warning ?? string.Empty;
        Warning.Visibility = warning != null ? Visibility.Visible : Visibility.Collapsed;

        var exitWasHidden = Exit.Visibility != Visibility.Visible;
        var exit = _state.ExitOverlay;
        Exit.Render();
        Exit.Visibility = exit != null ? Visibility.Visible : Visibility.Collapsed;

        var dialog = _state.ScriptDialog;
        var dialogWasHidden = Dialog.Visibility != Visibility.Visible;
        Dialog.Render();
        Dialog.Visibility = dialog != null ? Visibility.Visible : Visibility.Collapsed;

        var anyOverlay = pause != null || warning != null || exit != null || dialog != null;
        // Airspace: the WebView2 HWND would paint over WPF overlays, so hide it while one is up.
        WebHost.Visibility = anyOverlay ? Visibility.Hidden : Visibility.Visible;

        if (exit != null && exitWasHidden) Exit.FocusCode();
        else if (warning != null && warningWasHidden && exit == null) Warning.FocusButton();
        else if (dialog != null && dialogWasHidden && exit == null && warning == null) Dialog.FocusDefault();
    }

    private void RenderResources()
    {
        var links = _state.AllowedLinks;
        var hasLinks = links != null && links.Count > 0;
        var external = _state.IsExternalExam;
        FinishedButton.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
        FinishedButton.IsEnabled = external && _state.CanFinishExam;
        if (!hasLinks && !external)
        {
            ResourcesPanel.Visibility = Visibility.Collapsed;
            return;
        }
        ResourcesPanel.Visibility = Visibility.Visible;
        ResourcesPicker.Visibility = hasLinks ? Visibility.Visible : Visibility.Collapsed;
        if (hasLinks && !ReferenceEquals(ResourcesBox.ItemsSource, links))
        {
            _resettingResources = true;
            try
            {
                ResourcesBox.ItemsSource = links;
                ResourcesBox.SelectedIndex = -1;
            }
            finally
            {
                _resettingResources = false;
            }
        }
        BackToExamButton.Visibility = _state.IsOnResourcePage ? Visibility.Visible : Visibility.Collapsed;
        var locked = _state.ExitOverlay != null || _state.PauseMessage != null;
        ResourcesBox.IsEnabled = !locked;
        BackToExamButton.IsEnabled = !locked;
    }

    private void ResourcesBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_resettingResources) return;
        var link = ResourcesBox.SelectedItem as AllowedLink;
        if (link == null) return;
        _resettingResources = true;
        try
        {
            ResourcesBox.SelectedIndex = -1;   // behaves like a menu, not a persistent selection
        }
        finally
        {
            _resettingResources = false;
        }
        _state.OpenAllowedLink(link);
    }

    private void BackToExamButton_Click(object sender, RoutedEventArgs e)
    {
        _state.BackToExam();
    }

    private void FinishedButton_Click(object sender, RoutedEventArgs e)
    {
        FinishedButton.IsEnabled = false;   // re-enabled by Render if the student cancels
        _state.RequestStudentFinished();
        Render();
    }

    private void RenderBadge(LockdownMode mode)
    {
        string text;
        Brush fg;
        Color bg;
        switch (mode)
        {
            case LockdownMode.AssignedAccess:
                text = "Assigned Access (client-reported)";
                fg = (Brush)FindResource("SuccessBrush");
                bg = Color.FromArgb(0x33, 0x16, 0xA3, 0x4A);
                BadgeBorder.ToolTip = "A machine-wide (HKLM) Shell Launcher / Assigned Access configuration was found. This is a client-reported hint; the server decides whether it counts. Client controls are applied as well.";
                break;
            case LockdownMode.KioskFallback:
                text = "Kiosk fallback";
                fg = (Brush)FindResource("WarningBrush");
                bg = Color.FromArgb(0x33, 0xD9, 0x77, 0x06);
                BadgeBorder.ToolTip = "Best-effort kiosk mode: no Assigned Access configuration found. Ctrl+Alt+Del cannot be blocked; all attempts are reported.";
                break;
            default:
                text = "Not locked";
                fg = (Brush)FindResource("DangerBrush");
                bg = Color.FromArgb(0x33, 0xDC, 0x26, 0x26);
                BadgeBorder.ToolTip = "Not locked.";
                break;
        }
        BadgeText.Text = text;
        BadgeText.Foreground = fg;
        BadgeBorder.Background = new SolidColorBrush(bg);
    }
}
