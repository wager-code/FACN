using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

namespace SCFA.ContentCenter.Views;

public partial class ProfileWindow : Window
{
    private readonly string _userKey;
    private readonly string _originalAvatarPath;
    private string? _pendingAvatarPath;
    private bool _removeAvatar;
    private bool _saving;

    public ProfileWindow(string userKey)
    {
        if (string.IsNullOrWhiteSpace(userKey)) throw new ArgumentException("账号标识不能为空", nameof(userKey));
        _userKey = userKey.Trim();
        InitializeComponent();

        var profile = App.Services.Config.Current.LocalUserProfiles
            .FirstOrDefault(x => x.UserKey.Equals(_userKey, StringComparison.OrdinalIgnoreCase));
        DisplayNameBox.Text = profile?.DisplayName ?? "";
        QQBox.Text = profile?.QQ ?? "";
        PhoneBox.Text = profile?.Phone ?? "";
        _originalAvatarPath = profile?.AvatarPath ?? "";
        if (File.Exists(_originalAvatarPath))
        {
            try { AvatarPreview.Source = LoadAvatar(_originalAvatarPath); }
            catch { AvatarPreview.Source = null; }
        }
    }

    private void ChooseAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择本机头像",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var preview = LoadAvatar(dialog.FileName);
            _pendingAvatarPath = dialog.FileName;
            _removeAvatar = false;
            AvatarPreview.Source = preview;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法使用这张图片", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingAvatarPath = null;
        _removeAvatar = true;
        AvatarPreview.Source = null;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        _saving = true;
        SaveButton.IsEnabled = false;
        ChooseAvatarButton.IsEnabled = false;
        RemoveAvatarButton.IsEnabled = false;
        string? copiedAvatar = null;
        try
        {
            var avatarPath = _removeAvatar ? "" : _originalAvatarPath;
            if (_pendingAvatarPath is not null)
            {
                _ = LoadAvatar(_pendingAvatarPath);
                var avatarDirectory = Path.Combine(ConfigService.ResolveDataDirectory(), "avatars");
                Directory.CreateDirectory(avatarDirectory);
                var extension = Path.GetExtension(_pendingAvatarPath).ToLowerInvariant() is ".png" ? ".png" : ".jpg";
                copiedAvatar = Path.Combine(avatarDirectory, Guid.NewGuid().ToString("N") + extension);
                File.Copy(_pendingAvatarPath, copiedAvatar);
                avatarPath = copiedAvatar;
            }

            var config = App.Services.Config;
            var draft = ConfigService.Clone(config.Current);
            var profile = new LocalUserProfile
            {
                UserKey = _userKey,
                DisplayName = DisplayNameBox.Text.Trim(),
                QQ = QQBox.Text.Trim(),
                Phone = PhoneBox.Text.Trim(),
                AvatarPath = avatarPath
            };
            var index = draft.LocalUserProfiles.FindIndex(x => x.UserKey.Equals(_userKey, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) draft.LocalUserProfiles[index] = profile;
            else draft.LocalUserProfiles.Add(profile);
            await config.SaveAsync(draft);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            if (copiedAvatar is not null)
            {
                try { File.Delete(copiedAvatar); } catch { }
            }
            MessageBox.Show(this, ex.Message, "保存资料失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
            ChooseAvatarButton.IsEnabled = true;
            RemoveAvatarButton.IsEnabled = true;
        }
    }

    private static BitmapSource LoadAvatar(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg")) throw new InvalidDataException("头像只能使用 PNG 或 JPEG 图片。");
        using var stream = File.OpenRead(path);
        if (stream.Length is < 8 or > 5 * 1024 * 1024) throw new InvalidDataException("头像文件须小于 5 MB。");
        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header);
        var isPng = header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var isJpeg = header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
        if (extension == ".png" ? !isPng : !isJpeg) throw new InvalidDataException("头像扩展名与图片格式不一致。");
        stream.Position = 0;
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("无法读取头像图片。");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth is < 1 or > 4096 || frame.PixelHeight is < 1 or > 4096)
            throw new InvalidDataException("头像尺寸须在 4096 × 4096 像素以内。");
        frame.Freeze();
        return frame;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_saving) DialogResult = false;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_saving) DialogResult = false;
    }
}
