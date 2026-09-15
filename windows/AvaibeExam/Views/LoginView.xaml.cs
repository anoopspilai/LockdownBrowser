using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AvaibeExam.Core;

namespace AvaibeExam.Views;

/// <summary>CONTRACT §7.1 — backend URL, enrollment token (only when not enrolled), student code, exam code, Continue.</summary>
public partial class LoginView : UserControl
{
    private readonly AppState _state;

    public LoginView(AppState state)
    {
        InitializeComponent();
        _state = state;
        DataContext = state;
        SubtitleText.Text = "Secure exam client for Windows · v" + Constants.ClientVersion;
        // §10.9: IT-preset base URL => read-only field.
        BaseUrlBox.IsReadOnly = state.BaseUrlLocked;
        BaseUrlBox.IsEnabled = !state.BaseUrlLocked;
        BaseUrlLockedText.Visibility = state.BaseUrlLocked ? Visibility.Visible : Visibility.Collapsed;
        state.PropertyChanged += OnStateChanged;
        Unloaded += (_, __) => state.PropertyChanged -= OnStateChanged;
        Render();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Enrollment):
            case nameof(AppState.IsEnrolled):
            case nameof(AppState.IsBusy):
            case nameof(AppState.BusyText):
            case nameof(AppState.LoginError):
                Render();
                break;
        }
    }

    private void Render()
    {
        var enrollment = _state.Enrollment;
        EnrollPanel.Visibility = enrollment == null ? Visibility.Visible : Visibility.Collapsed;
        EnrolledText.Text = enrollment == null
            ? "Device not enrolled"
            : "Enrolled as " + enrollment.DeviceId + " (" + enrollment.Mode + ")";
        ResetButton.Visibility = enrollment == null ? Visibility.Collapsed : Visibility.Visible;

        var error = _state.LoginError;
        ErrorText.Text = error ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;

        ContinueButton.IsEnabled = !_state.IsBusy;
        ResetButton.IsEnabled = !_state.IsBusy;
        BusyText.Text = _state.IsBusy ? _state.BusyText : string.Empty;
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        _state.ContinueFromLogin();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _state.ResetEnrollment();
    }
}
