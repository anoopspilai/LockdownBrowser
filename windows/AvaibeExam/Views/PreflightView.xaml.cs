using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AvaibeExam.Core;
using AvaibeExam.Models;

namespace AvaibeExam.Views;

/// <summary>
/// CONTRACT §7.2 — checklist with ✓ / ✗ / ! / – rows, required/optional tags and details.
/// [Start Exam] is enabled only when every required check passes and the server accepted the session.
/// </summary>
public partial class PreflightView : UserControl
{
    private readonly AppState _state;

    public PreflightView(AppState state)
    {
        InitializeComponent();
        _state = state;
        DataContext = state;
        state.PropertyChanged += OnStateChanged;
        Unloaded += (_, __) => state.PropertyChanged -= OnStateChanged;
        Render();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Cheap enough to re-render on any change.
        Render();
    }

    private void Render()
    {
        var session = _state.Session;
        SessionText.Text = session != null
            ? session.StudentOrEmpty.Name + " · " + session.ExamOrEmpty.Title + " (" + session.ExamOrEmpty.Code + ")"
            : "Session not started yet — the server rejected the readiness check.";

        var policy = _state.Policy;
        PolicyText.Text = "Policy: " + (_state.HasServerPolicy ? "server" : "conservative defaults") +
                          " · Mode: " + policy.Mode +
                          " · Requires Assigned Access: " + (policy.RequireAAC ? "yes" : "no") +
                          " · External display: " + policy.ExternalDisplayAction.Wire() +
                          " · Release on submit: " + policy.ReleaseOnSubmit +
                          " · Offline grace: " + policy.OfflineGraceSeconds + "s" +
                          " · Resources: " + policy.AllowedLinks.Count;

        var failures = _state.PreflightServerFailures;
        if (failures.Count > 0)
        {
            ServerFailuresText.Text = "Server rejected the readiness check: " + string.Join(", ", failures);
            ServerFailuresText.Visibility = Visibility.Visible;
        }
        else
        {
            ServerFailuresText.Visibility = Visibility.Collapsed;
        }

        var error = _state.PreflightError;
        ErrorText.Text = error ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;

        if (_state.RequireAACButUnavailable)
        {
            ModeNoteText.Text = "This exam's policy requires a Windows Assigned Access (kiosk) configuration. No machine-wide kiosk " +
                                "configuration was found on this device (client-reported), so the client will refuse to start in " +
                                "kiosk-fallback mode. Ask your administrator to configure the kiosk account.";
            ModeNoteText.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
            ModeNoteText.Visibility = Visibility.Visible;
        }
        else if (!_state.AssignedAccessHint)
        {
            ModeNoteText.Text = "No Assigned Access kiosk configuration found (client-reported). The exam will run in kiosk-fallback mode: best-effort " +
                                "controls (fullscreen topmost window, keyboard hook, capture protection, focus watchdog) that are reported " +
                                "honestly to your teacher. Ctrl+Alt+Del cannot be blocked.";
            ModeNoteText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            ModeNoteText.Visibility = Visibility.Visible;
        }
        else
        {
            ModeNoteText.Visibility = Visibility.Collapsed;
        }

        BackButton.IsEnabled = !_state.IsBusy;
        RerunButton.IsEnabled = !_state.IsBusy;
        StartButton.IsEnabled = _state.CanStartExam;
        var blockers = _state.CanStartExam ? new System.Collections.Generic.List<string>() : _state.StartBlockers;
        BlockersText.Text = string.Join("\n", blockers);
        BlockersText.Visibility = blockers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StartButton.ToolTip = _state.CanStartExam ? "Start the exam and lock this PC" : "See the reasons above";
        BusyText.Text = _state.IsBusy ? _state.BusyText : string.Empty;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _state.BackToLogin();

    private void RerunButton_Click(object sender, RoutedEventArgs e) => _state.RerunPreflight();

    private void StartButton_Click(object sender, RoutedEventArgs e) => _state.StartExam();
}
