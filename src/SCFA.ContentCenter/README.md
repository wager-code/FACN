# SCFA.ContentCenter 客户端源码说明

这是 Windows .NET 8 WPF 客户端。

完整“功能 → 文件”导航见仓库根目录 `CODEBASE_GUIDE.md`。

## 目录职责

- `Core/`：跨模块基础与安全规则。
- `Models/`：配置、账号、内容、备份、同步、任务、更新等数据结构。
- `Services/`：网络、文件系统、安装、同步、发布、备份等业务能力。
- `ViewModels/`：页面状态和命令。
- `Views/`：WPF 页面/窗口。
- `DesignSystem/`：统一视觉 token 和控件样式。
- `Assets/`：应用图标、背景、Fluent 图标资源。

## 最重要的入口

- `App.xaml.cs`：应用启动和全局服务初始化。
- `MainWindow.xaml`：主窗口和导航。
- `ViewModels/MainViewModel.cs`：页面实例、导航命令和管理员入口可见性。
- `Services/ServiceRegistry.cs`：客户端服务组合根。

## 常见修改位置

- 云端地图/MOD：`CloudPageViewModel.cs` + `CloudCatalogService.cs`
- 本地地图/MOD：`LocalPageViewModel.cs` + `LocalContentService.cs`
- 安装/修复：`InstallService.cs`
- 备份/恢复：`BackupService.cs`
- 一键同步：`SyncService.cs` + `SyncPageViewModel.cs`
- 管理员发布：`PublicationPreparationService.cs` + `PublicationApiModels.cs` + `PublicationUploadService.cs` + `PublicationPageViewModel.cs`
- 下架/版本档案/恢复：`CloudHistoryService.cs` + `CloudHistoryPageViewModel.cs` + `PublicationUploadService.cs`
- 客户端更新：`UpdateService.cs` + `UpdateApplier.cs`
- 设置：`SettingsPageViewModel.cs` + `ConfigService.cs`

## 维护规则

不要因为某个类变大就立即新建平行实现。优先确认能否拆内部职责，同时保持现有服务入口和安全规则。

涉及玩家目录、备份、ZIP、发布 manifest、COS 上传的改动必须有回归验证。
