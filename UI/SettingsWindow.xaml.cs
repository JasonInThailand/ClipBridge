using System.Windows;
using ClipBridge.Core;

namespace ClipBridge.UI;

public partial class SettingsWindow : Window
{
    private readonly Settings _s;
    public bool Saved { get; private set; }

    public SettingsWindow(Settings s)
    {
        _s = s;
        InitializeComponent();
        KeyBox.Text = s.SharedKey;
        PeerBox.Text = string.Join(", ", s.Peers);
        NameBox.Text = s.MachineName;
        SyncChk.IsChecked = s.EnableSync;
        MaxItemsBox.Text = s.MaxItems.ToString();
        MaxImageBox.Text = s.MaxImageMB.ToString();
        MaxFileBox.Text = s.MaxFileTransferMB.ToString();
        OcrChk.IsChecked = s.EnableOcr;
        StartChk.IsChecked = s.StartWithWindows;
        RememberPosChk.IsChecked = s.RememberListPosition;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var restart = KeyBox.Text != _s.SharedKey || NameBox.Text != _s.MachineName || (SyncChk.IsChecked ?? true) != _s.EnableSync
                      || string.Join(", ", _s.Peers) != PeerBox.Text;
        _s.SharedKey = KeyBox.Text.Trim();
        _s.Peers = PeerBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _s.MachineName = string.IsNullOrWhiteSpace(NameBox.Text) ? Environment.MachineName : NameBox.Text.Trim();
        _s.EnableSync = SyncChk.IsChecked ?? true;
        if (int.TryParse(MaxItemsBox.Text, out var mi)) _s.MaxItems = Math.Clamp(mi, 10, 5000);
        if (int.TryParse(MaxImageBox.Text, out var mm)) _s.MaxImageMB = Math.Clamp(mm, 1, 200);
        if (int.TryParse(MaxFileBox.Text, out var mf)) _s.MaxFileTransferMB = Math.Clamp(mf, 0, 2000);
        _s.EnableOcr = OcrChk.IsChecked ?? true;
        _s.StartWithWindows = StartChk.IsChecked ?? true;
        _s.RememberListPosition = RememberPosChk.IsChecked ?? true;
        _s.Save();
        App.ApplyStartWithWindows(_s.StartWithWindows);
        Saved = true;
        if (restart)
        {
            Note.Text = "Sync settings changed. ClipBridge will restart now to apply them.";
            App.RestartApp();
            return;
        }
        Close();
    }
}
