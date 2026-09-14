using System.Windows.Controls;

namespace AvaibeExam.Views;

/// <summary>Modal "Exam paused" layer (policy.externalDisplayAction == PAUSE). Swallows all input.</summary>
public partial class PauseOverlay : UserControl
{
    public PauseOverlay()
    {
        InitializeComponent();
    }

    public string Message
    {
        get => MessageText.Text;
        set => MessageText.Text = value;
    }
}
