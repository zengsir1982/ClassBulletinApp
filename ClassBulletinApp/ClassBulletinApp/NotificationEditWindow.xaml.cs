using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class NotificationEditWindow : Window
{
    private readonly ObservableCollection<Notification> _items = new();

    public NotificationEditWindow()
    {
        InitializeComponent();
        Lst.ItemsSource = _items;
        Reload();
    }

    private void Reload()
    {
        _items.Clear();
        foreach (var n in Store.LoadNotifications()
                     .OrderByDescending(x => x.Pinned)
                     .ThenByDescending(x => x.CreatedAt))
            _items.Add(n);
    }

    private void Persist()
        => Store.SaveNotifications(_items.ToList());

    private void Lst_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Lst.SelectedItem is Notification n)
        {
            TxtTitle.Text = n.Title;
            TxtContent.Text = n.Content;
        }
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var title = TxtTitle.Text.Trim();
        var content = TxtContent.Text.Trim();
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(content)) return;
        var n = new Notification { Title = title, Content = content, CreatedAt = DateTime.Now };
        _items.Insert(0, n);
        Persist();
        TxtTitle.Text = "";
        TxtContent.Text = "";
        AppEvents.RaiseNotificationChanged();
    }

    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (Lst.SelectedItem is not Notification n)
        {
            System.Windows.MessageBox.Show("请先在列表中选中要更新的通知。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        n.Title = TxtTitle.Text.Trim();
        n.Content = TxtContent.Text.Trim();
        Persist();
        Lst.Items.Refresh();
        AppEvents.RaiseNotificationChanged();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Lst.SelectedItem is not Notification n)
        {
            System.Windows.MessageBox.Show("请先在列表中选中要删除的通知。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var r = System.Windows.MessageBox.Show($"确定删除通知「{(string.IsNullOrEmpty(n.Title) ? n.Content : n.Title)}」吗？",
            "删除通知", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;
        _items.Remove(n);
        Persist();
        TxtTitle.Text = "";
        TxtContent.Text = "";
        AppEvents.RaiseNotificationChanged();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
