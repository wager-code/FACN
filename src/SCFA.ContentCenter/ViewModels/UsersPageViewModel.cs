using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.ViewModels;

public sealed class UsersPageViewModel : ViewModelBase
{
    private AdminUserRecord? _selected;
    private string _searchText = "";
    private AdminChoice _selectedRole;
    private AdminChoice _selectedStatus;
    private string _status = "等待刷新";

    public UsersPageViewModel()
    {
        _selectedRole = Roles[0];
        _selectedStatus = UserStatuses[0];
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => SelectedItem is not null && CanWrite);
        RevokeSessionsCommand = new AsyncRelayCommand(RevokeSessionsAsync, () => SelectedItem is not null && CanRevokeSessions);
        _ = RefreshAsync();
    }

    public ObservableCollection<AdminUserRecord> Items { get; } = [];
    public ICollectionView ItemsView { get; }
    public AdminChoice[] Roles { get; } =
    [
        new("user", "普通用户"),
        new("reviewer", "内容审核员"),
        new("publisher", "内容发布员"),
        new("admin", "管理员"),
        new("super_admin", "超级管理员")
    ];
    public AdminChoice[] UserStatuses { get; } =
    [
        new("active", "正常"),
        new("disabled", "已停用"),
        new("suspended", "已暂停")
    ];
    public AdminUserRecord? SelectedItem
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            if (value is not null)
            {
                SelectedRole = Roles.FirstOrDefault(x => x.Key.Equals(value.RoleKey, StringComparison.OrdinalIgnoreCase)) ?? Roles[0];
                SelectedStatus = UserStatuses.FirstOrDefault(x => x.Key.Equals(value.Status, StringComparison.OrdinalIgnoreCase)) ?? UserStatuses[0];
            }
            OnPropertyChanged(nameof(SelectedUserLabel));
            OnPropertyChanged(nameof(SelectedUserMetadata));
            OnPropertyChanged(nameof(SelectedPermissionSummary));
            SaveCommand.RaiseCanExecuteChanged();
            RevokeSessionsCommand.RaiseCanExecuteChanged();
        }
    }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) { ItemsView.Refresh(); OnPropertyChanged(nameof(VisibleCount)); } } }
    public AdminChoice SelectedRole { get => _selectedRole; set => Set(ref _selectedRole, value); }
    public AdminChoice SelectedStatus { get => _selectedStatus; set => Set(ref _selectedStatus, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool CanRead => AccessPolicy.CanReadUsers(App.Services.CurrentUser);
    public bool CanWrite => AccessPolicy.CanManageUsers(App.Services.CurrentUser);
    public bool CanRevokeSessions => AccessPolicy.CanRevokeSessions(App.Services.CurrentUser);
    public int TotalCount => Items.Count;
    public int VisibleCount => ItemsView.Cast<AdminUserRecord>().Count();
    public int ActiveCount => Items.Count(x => x.Status.Equals("active", StringComparison.OrdinalIgnoreCase));
    public int RestrictedCount => Items.Count - ActiveCount;
    public int SessionCount => Items.Sum(x => Math.Max(0, x.ActiveSessions));
    public string AccessLabel => CanWrite ? "可修改角色与状态" : CanRead ? "只读用户目录" : "无用户目录权限";
    public string SelectedUserLabel => SelectedItem is null ? "请选择一个用户" : string.IsNullOrWhiteSpace(SelectedItem.DisplayName) ? SelectedItem.Username : SelectedItem.DisplayName;
    public string SelectedUserMetadata => SelectedItem is null ? "选择后查看账号标识、角色和登录信息" : $"@{SelectedItem.Username} · {SelectedItem.Email}";
    public string SelectedPermissionSummary => SelectedItem is null ? "—" : SelectedItem.Permissions.Count == 0 ? "未返回权限明细" : $"{SelectedItem.Permissions.Count} 项权限";
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand RevokeSessionsCommand { get; }

    private bool FilterItem(object value)
    {
        if (value is not AdminUserRecord item || string.IsNullOrWhiteSpace(SearchText)) return true;
        var query = SearchText.Trim();
        return item.Username.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Email.Contains(query, StringComparison.OrdinalIgnoreCase) || item.RoleDisplay.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.StatusDisplay.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private async Task RefreshAsync()
    {
        if (!CanRead) { Status = "当前账号没有用户读取权限。"; return; }
        try
        {
            Status = "正在读取服务器用户…";
            var users = await App.Services.Management.FetchUsersAsync();
            Items.Clear();
            foreach (var user in users.OrderBy(x => x.Username, StringComparer.CurrentCultureIgnoreCase)) Items.Add(user);
            ItemsView.Refresh();
            NotifySummaries();
            Status = $"真实用户列表已读取 · {Items.Count} 人";
        }
        catch (Exception ex)
        {
            Status = "读取失败：" + ex.Message;
            App.Services.Log.Error("读取用户列表失败", ex);
        }
    }

    private async Task SaveAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        if (MessageBox.Show($"确定将用户 {item.Username} 更新为角色“{SelectedRole.Label}”、状态“{SelectedStatus.Label}”吗？", "确认修改用户", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            Status = "正在更新用户…";
            await App.Services.Management.UpdateUserAsync(item.Id, SelectedRole.Key, SelectedStatus.Key);
            await RefreshAsync();
        }
        catch (Exception ex) { Status = "更新失败：" + ex.Message; MessageBox.Show(ex.Message, "用户更新失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task RevokeSessionsAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        if (MessageBox.Show($"确定强制注销用户 {item.Username} 的全部活动会话？", "撤销用户会话", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            Status = "正在撤销用户会话…";
            await App.Services.Management.RevokeSessionsAsync(item.Id);
            await RefreshAsync();
        }
        catch (Exception ex) { Status = "撤销失败：" + ex.Message; MessageBox.Show(ex.Message, "会话撤销失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void NotifySummaries()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(RestrictedCount));
        OnPropertyChanged(nameof(SessionCount));
    }
}

public sealed record AdminChoice(string Key, string Label)
{
    public override string ToString() => Label;
}
