using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AvaibeExam.Core;
using AvaibeExam.Models;

namespace AvaibeExam.Views;

/// <summary>
/// CONTRACT §7.5 / §9.6 / §10.4 — the native exit / hold overlay ("Exam locked", "Session
/// terminated", "Exam submitted", "Time is up"). Lockdown ends only after the server's signed
/// authorization is verified (POST /unlock or a RELEASE command). "Back to exam" (W-17) is shown
/// only while nothing has been submitted and no release is pending.
/// </summary>
public partial class ExitOverlay : UserControl
{
    private AppState? _state;
    private ExitOverlayState? _model;
    private bool _updatingText;

    public ExitOverlay()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;   // W-32
    }

    public void Attach(AppState state)
    {
        _state = state;
        Render();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_model != null) _model.PropertyChanged -= OnModelChanged;
        _model = null;
    }

    /// <summary>Re-binds to AppState.ExitOverlay (may be a new object) and refreshes the UI.</summary>
    public void Render()
    {
        var model = _state?.ExitOverlay;
        if (!ReferenceEquals(model, _model))
        {
            if (_model != null) _model.PropertyChanged -= OnModelChanged;
            _model = model;
            if (_model != null) _model.PropertyChanged += OnModelChanged;
            CodeBox.Text = string.Empty;
        }
        RenderModel();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => RenderModel();

    private void RenderModel()
    {
        var model = _model;
        if (model == null) return;
        TitleText.Text = model.Title;
        MessageText.Text = model.Message;
        CodePanel.Visibility = model.AllowReleaseCode ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = model.ErrorText ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrEmpty(model.ErrorText) ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = model.Verifying ? "Verifying…" : model.StatusText;
        CodeBox.IsEnabled = !model.Verifying;
        VerifyButton.IsEnabled = !model.Verifying && CodeBox.Text.Length == 6;
        BackButton.Visibility = model.CanGoBack && !model.Verifying ? Visibility.Visible : Visibility.Collapsed;
        ModeText.Text = "Mode: " + (_state?.LockdownMode ?? LockdownMode.None).DisplayName();
    }

    public void FocusCode()
    {
        if (CodePanel.Visibility == Visibility.Visible)
        {
            try { CodeBox.Focus(); } catch { /* ignore */ }
        }
    }

    private void CodeBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void CodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingText) return;
        var digits = new string(CodeBox.Text.Where(char.IsDigit).ToArray());
        if (digits.Length > 6) digits = digits.Substring(0, 6);
        if (digits != CodeBox.Text)
        {
            _updatingText = true;
            try
            {
                CodeBox.Text = digits;
                CodeBox.CaretIndex = digits.Length;
            }
            finally
            {
                _updatingText = false;
            }
        }
        VerifyButton.IsEnabled = _model != null && !_model.Verifying && CodeBox.Text.Length == 6;
    }

    private void CodeBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            Verify();
            e.Handled = true;
        }
    }

    private void VerifyButton_Click(object sender, RoutedEventArgs e) => Verify();

    private void BackButton_Click(object sender, RoutedEventArgs e) => _state?.DismissExitOverlay();

    private void Verify()
    {
        if (_state == null || CodeBox.Text.Length != 6) return;
        var code = CodeBox.Text;
        CodeBox.Text = string.Empty;   // never leave the digits on screen or in the control
        _state.VerifyReleaseCode(code);
    }
}
