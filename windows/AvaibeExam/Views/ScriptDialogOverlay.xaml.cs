using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AvaibeExam.Core;

namespace AvaibeExam.Views;

/// <summary>
/// W-24: in-window replacement for alert() / confirm() / prompt() / beforeunload. A native
/// MessageBox would be a separate top-level window outside the kiosk window's capture
/// protection and focus watchdog; this overlay stays inside the exam window.
/// </summary>
public partial class ScriptDialogOverlay : UserControl
{
    private AppState? _state;
    private ScriptDialogState? _model;
    private bool _updating;

    public ScriptDialogOverlay()
    {
        InitializeComponent();
    }

    public void Attach(AppState state)
    {
        _state = state;
        Render();
    }

    /// <summary>Re-binds to AppState.ScriptDialog (may be a new object) and refreshes the UI.</summary>
    public void Render()
    {
        var model = _state?.ScriptDialog;
        if (!ReferenceEquals(model, _model))
        {
            _model = model;
            _updating = true;
            try
            {
                InputBox.Text = model?.InputText ?? string.Empty;
            }
            finally
            {
                _updating = false;
            }
        }
        if (model == null) return;
        var request = model.Request;
        switch (request.Kind)
        {
            case "confirm": TitleText.Text = "Please confirm"; break;
            case "prompt": TitleText.Text = "The exam page asks for input"; break;
            case "beforeunload": TitleText.Text = "Leave this page?"; break;
            default: TitleText.Text = "Message from the exam page"; break;
        }
        MessageText.Text = request.Message;
        InputBox.Visibility = request.HasInput ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = request.HasCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    public void FocusDefault()
    {
        try
        {
            if (InputBox.Visibility == Visibility.Visible) InputBox.Focus(); else OkButton.Focus();
        }
        catch
        {
            // ignore
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || _model == null) return;
        var text = InputBox.Text ?? string.Empty;
        if (text.Length > 2000) text = text.Substring(0, 2000);
        _model.InputText = text;
    }

    private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            _model?.Complete(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _model?.Complete(false);
            e.Handled = true;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => _model?.Complete(true);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _model?.Complete(false);
}
