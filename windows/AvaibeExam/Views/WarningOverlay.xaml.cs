using System;
using System.Windows;
using System.Windows.Controls;

namespace AvaibeExam.Views;

/// <summary>CONTRACT §7.4 — shown on security events (external display, deactivation, WARN command).</summary>
public partial class WarningOverlay : UserControl
{
    public event Action? Dismissed;

    public WarningOverlay()
    {
        InitializeComponent();
    }

    public string Message
    {
        get => MessageText.Text;
        set => MessageText.Text = value;
    }

    public void FocusButton()
    {
        try { OkButton.Focus(); } catch { /* ignore */ }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
    }
}
