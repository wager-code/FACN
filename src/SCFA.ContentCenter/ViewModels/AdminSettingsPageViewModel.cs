using SCFA.ContentCenter.Core;

namespace SCFA.ContentCenter.ViewModels;

public sealed class AdminSettingsPageViewModel : ViewModelBase
{
    private string _credentialState = "正在读取服务器状态…";
    private string _status = "";
    private bool _isBusy;

    public string CredentialState { get => _credentialState; private set => Set(ref _credentialState, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(CanSave));
        }
    }
    public bool CanSave => !IsBusy && !App.Services.OfflineMode &&
        AccessPolicy.CanManageCosCredentials(App.Services.CurrentUser) &&
        !string.IsNullOrWhiteSpace(App.Services.Auth.Token);

    public async Task RefreshAsync()
    {
        if (!CanSave) return;
        IsBusy = true;
        try
        {
            var state = await App.Services.PublicationUpload.GetCredentialStatusAsync();
            CredentialState = state.Configured ? "已保存在服务器" : "尚未配置";
            Status = state.Configured && state.UpdatedAt is not null
                ? $"上次更新：{state.UpdatedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "密钥只在服务器加密保存，软件不会读取或显示旧密钥。";
        }
        catch (Exception ex)
        {
            CredentialState = "无法读取";
            Status = "连接发布服务失败：" + ex.Message;
            App.Services.Log.Error("读取服务器 COS 密钥状态失败", ex);
        }
        finally { IsBusy = false; }
    }

    public async Task<bool> SaveAsync(string secretId, string secretKey)
    {
        if (!CanSave) return false;
        IsBusy = true;
        Status = "正在验证 COS 密钥并保存到服务器…";
        try
        {
            var state = await App.Services.PublicationUpload.RotateCredentialAsync(secretId, secretKey);
            CredentialState = state.Configured ? "已保存在服务器" : "尚未配置";
            Status = state.Configured ? "服务器已验证并保存密钥。" : "服务器没有确认保存，请重试。";
            return state.Configured;
        }
        catch (Exception ex)
        {
            Status = "密钥未保存：" + ex.Message;
            App.Services.Log.Error("保存服务器 COS 密钥失败", ex);
            return false;
        }
        finally { IsBusy = false; }
    }
}
