using System.Windows;
using System.Windows.Input;
using KSLauncher.App.Services;

namespace KSLauncher.App;

public enum JoinFriendMode
{
    Cancel,
    WithFriendMods,
    WithOwnMods,
    WithoutMods
}

public partial class JoinFriendDialog : Window
{
    public JoinFriendMode SelectedMode { get; private set; } = JoinFriendMode.Cancel;
    public GameInstanceInfo? SelectedOwnInstance => OwnInstancesBox.SelectedItem as GameInstanceInfo;

    public JoinFriendDialog(FriendProfile friend, IReadOnlyList<GameInstanceInfo> ownInstances)
    {
        InitializeComponent();

        var p = friend.Presence;
        TitleText.Text = $"{friend.Username} играет с модами";
        SubtitleText.Text = $"Система: {friend.AccountSystem} • Версия {p.GameVersion} ({p.Loader})";
        ServerInfoText.Text = $"Сервер: {p.ServerName ?? p.ServerIp} ({p.ServerIp}:{p.ServerPort})";

        var modNames = p.Mods.Select(m => m.Title).ToList();
        ModsListText.Text = modNames.Count > 0
            ? $"Моды друга ({modNames.Count}): {string.Join(", ", modNames)}"
            : "У друга активны моды.";

        OwnInstancesBox.ItemsSource = ownInstances;
        if (ownInstances.Count > 0)
        {
            OwnInstancesBox.SelectedIndex = 0;
        }
        else
        {
            WithOwnModsBtn.IsEnabled = false;
            WithOwnModsBtn.Content = "⚙   Поставить свои моды (нет сборок в Библиотеке)";
        }
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void WithFriendMods_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = JoinFriendMode.WithFriendMods;
        DialogResult = true;
        Close();
    }

    private void WithOwnMods_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = JoinFriendMode.WithOwnMods;
        DialogResult = true;
        Close();
    }

    private void WithoutMods_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = JoinFriendMode.WithoutMods;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = JoinFriendMode.Cancel;
        DialogResult = false;
        Close();
    }
}
