using System.Windows.Controls;

namespace AmwalECR.Example.Views;

public partial class LogView : UserControl
{
    public LogView()
    {
        InitializeComponent();
    }

    /// <summary>Follows the newest line, like a terminal.</summary>
    private void OnLogChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();
}
