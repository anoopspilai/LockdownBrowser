using System.Windows;
using System.Windows.Controls;
using AvaibeExam.Core;

namespace AvaibeExam.Views;

/// <summary>Final screen after release (CONTRACT §7.5). Quit is only possible from here (or before lockdown).</summary>
public partial class ExitView : UserControl
{
    private readonly AppState _state;

    public ExitView(AppState state)
    {
        InitializeComponent();
        _state = state;
        DataContext = state;
        ReasonText.Text = string.IsNullOrEmpty(state.ExitReason) ? string.Empty : "Reason: " + state.ExitReason;
        Loaded += (_, __) =>
        {
            try { QuitButton.Focus(); } catch { /* ignore */ }
        };
    }

    private void QuitButton_Click(object sender, RoutedEventArgs e)
    {
        _state.Quit();
    }
}
