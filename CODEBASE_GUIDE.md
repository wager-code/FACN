# SCFA 内容中心源码导航

Updated: 2026-09-28  
Baseline: `V4.0.0-dev61`

本文件回答两个问题：

1. **某个功能应该改哪些文件？**
2. **仓库里的关键文件分别负责什么？**

第一次接手项目时，先读 `PRODUCT_REQUIREMENTS.md` 和本文件，再开始搜索代码。

## 总体结构

```text
FACN/
├─ src/SCFA.ContentCenter/                  Windows WPF 客户端
├─ server/SCFA.PublicationGateway/          管理员发布网关
├─ server/SCFA.PublicationGateway.RegressionTests/
├─ tests/SCFA.ContentCenter.RegressionTests/ 客户端核心回归/WPF smoke
├─ .github/workflows/                       CI 与网关发布
├─ PRODUCT_REQUIREMENTS.md                  已确认产品要求
├─ ROADMAP.md                               后续开发顺序
├─ PROJECT_HANDOFF.md                       当前生产/交接事实
├─ BUILD_VALIDATION.md                      当前构建与验收门槛
├─ ADMIN_PUBLISH_PLAN.md                    管理员发布架构
└─ ADMIN_PUBLISH_API.md                     客户端与网关 API 契约
```

## 客户端分层

### `App.xaml` / `App.xaml.cs`

应用入口、全局资源、服务初始化、登录/主窗口启动以及客户端更新应用/清理入口。

### `MainWindow.xaml` / `MainWindow.xaml.cs`

主窗口框架和左侧导航。  
页面本身的业务逻辑通常不应继续堆在这里。

### `Services/ServiceRegistry.cs`

客户端服务组合根。  
新增一个真正的长期 Service 时，需要检查是否应该在这里统一创建和复用，而不是在页面里临时 new。

### `Views/`

WPF 界面。主要负责布局、绑定和少量纯 UI 交互。

### `ViewModels/`

页面状态、命令和页面级流程编排。

### `Services/`

真正的业务/网络/文件系统操作。

### `Models/`

客户端配置、内容、备份、账号、同步、任务、更新等数据模型。

### `Core/`

跨模块安全/基础能力，例如内容哈希、身份匹配、安全压缩路径、设置校验、权限策略。

### `DesignSystem/`

颜色、字体、间距以及 WPF 控件样式。  
视觉统一优先改这里，不要在每个页面复制一套 Style。

## 功能 → 主要文件

| 功能 | 客户端主要文件 | 服务端/补充 |
| --- | --- | --- |
| 登录、注册、会话 | `Views/LoginWindow.*`, `Services/AuthApiClient.cs`, `Services/UserSessionService.cs`, `Models/AuthModels.cs` | 账号服务不在本仓库 |
| 用户管理、审计 | `ViewModels/UsersPageViewModel.cs`, `ViewModels/OperationsPageViewModel.cs`, `Services/ManagementService.cs` | 真实权限由账号服务决定 |
| 云端地图/MOD 浏览 | `ViewModels/CloudPageViewModel.cs`, `Views/CloudContentView.*`, `Services/CloudCatalogService.cs`, `Models/ContentModels.cs` | COS 正式清单/对象 |
| 地图/MOD 预览 | `Services/MapPreviewService.cs`, `Services/PreviewImageValidator.cs`, `Views/CloudPreviewWindow.*` | 缩略图可来自清单/COS |
| 本地地图/MOD 扫描 | `ViewModels/LocalPageViewModel.cs`, `Views/LocalContentView.*`, `Services/LocalContentService.cs`, `Services/GamePathService.cs` | 只扫描用户配置目录 |
| 安装/更新/修复 | `Services/InstallService.cs`, `Services/CloudCatalogService.cs`, `Core/SafeArchive.cs`, `Core/ContentHash.cs` | 写入前必须校验与备份 |
| 本地删除/恢复 | `Services/BackupService.cs`, `ViewModels/BackupsPageViewModel.cs`, `Views/BackupsView.*` | 删除前验证备份 |
| 一键同步 | `Services/SyncService.cs`, `ViewModels/SyncPageViewModel.cs`, `Views/SyncView.*` | 调用安装服务，不另写一套安装逻辑 |
| “不喜欢”自动跳过 | `Services/SyncPreferenceService.cs` + 云端/同步页面 | 当前仅本机按账号隔离 |
| 同步历史 | `Services/SyncHistoryService.cs`, `Models/SyncHistoryModels.cs` | 本地数据 |
| **管理员发布地图/MOD** | **`ViewModels/PublicationPageViewModel.cs`, `Views/PublicationView.*`, `Services/PublicationPreparationService.cs`, `Services/PublicationApiModels.cs`, `Services/PublicationUploadService.cs`** | **`server/SCFA.PublicationGateway/`** |
| 管理员 COS 凭据 | `ViewModels/AdminSettingsPageViewModel.cs`, `Views/AdminSettingsView.*`, `Services/PublicationUploadService.cs` | 网关 `Program.cs`, `GatewayModels.cs`, `CosTransport.cs` |
| 下架内容 | `ViewModels/CloudPageViewModel.cs`, `Services/PublicationUploadService.cs` | `PublicationCoordinator.cs`, `UnpublishManifest.cs` |
| 版本档案/历史版本 | `Services/CloudHistoryService.cs`, `ViewModels/CloudHistoryPageViewModel.cs`, `Views/CloudHistoryView.*` | `PublicationArchive.cs`, `PublicationCoordinator.cs` |
| 恢复下架/历史版本 | `Services/PublicationUploadService.cs`, `CloudHistoryPageViewModel.cs` | `RestoreManifest.cs`, `PublicationCoordinator.cs` |
| 客户端更新 | `Services/UpdateService.cs`, `Services/UpdateApplier.cs`, `ViewModels/UpdatesPageViewModel.cs` | 更新清单/账号服务能力 |
| 诊断 | `Services/DiagnosticsService.cs`, `ViewModels/DiagnosticsPageViewModel.cs` | 只读检查优先 |
| 软件设置/首次设置 | `ViewModels/SettingsPageViewModel.cs`, `ViewModels/SetupPageViewModel.cs`, `Services/ConfigService.cs`, `Core/SettingsValidator.cs` | 配置必须安全落盘 |

## “云端地图发布”到底在哪

这是最容易被误解的一条链，分成客户端和服务端两半。

### 客户端

**`PublicationPreparationService.cs`**

- 重新检查选中的本地地图/MOD。
- 生成单顶层目录 ZIP。
- 计算包和内容 SHA-256。
- 生成下一版 manifest。
- 准备可选缩略图。
- **它只准备本地材料，不直接写 COS。**

**`PublicationApiModels.cs`**

- 客户端与发布网关通信使用的 DTO：capability、intent、commit、downlist、archive、restore、COS credential status。
- 只描述协议数据，不执行网络操作。

**`PublicationUploadService.cs`**

- 检查发布网关能力。
- 请求 publication intent。
- 接收对象级临时上传 URL。
- 把 ZIP/缩略图上传到 COS staging。
- 请求服务端 commit。
- 下架、读取档案、恢复版本、COS 凭据状态/轮换也通过这里调用网关。
- 最后重新读取公开清单确认结果。

**`PublicationPageViewModel.cs` + `PublicationView.xaml`**

管理员看到的“发布地图 / 发布 MOD”页面和页面流程。

### 发布网关

**`Program.cs`**

HTTP 路由与依赖注册。这里能快速看到网关对外提供哪些管理员 API。

**`AdminAuthenticator.cs`**

拿管理员 Bearer Token 向本机账号服务复核身份，不自行维护另一套账号库。

**`GatewayModels.cs`**

网关配置、加密 COS 凭据存储、发布 intent/commit/downlist/archive/restore 的 DTO。

**`CosTransport.cs`**

真正与腾讯 COS 通信：凭据验证、对象读写、签名 URL 等底层操作。

**`ManifestValidator.cs`**

验证“旧 manifest → 新 manifest”的变化是否合法，阻止异常条目数、错误路径、版本/哈希冲突等。

**`PackageValidator.cs`**

服务端重新检查 staging ZIP，不相信客户端打包结果。

**`PublicationCoordinator.cs`**

发布网关的主编排器：intent、commit、下架、档案、恢复、历史快照和串行写入。

**`UnpublishManifest.cs`**

只从正式清单移除指定内容，不等于删除 COS 包。

**`PublicationArchive.cs`**

把当前清单与历史快照组合成管理员档案数据。

**`RestoreManifest.cs`**

从档案中安全构造恢复后的正式清单。

## 客户端关键文件索引

### Core

| 文件 | 责任 |
| --- | --- |
| `AccessPolicy.cs` | 根据真实用户角色/权限决定管理员入口和敏感操作是否可用 |
| `AppVersion.cs` | 统一读取/展示客户端版本 |
| `ContentHash.cs` | 文件和目录 SHA-256/内容指纹 |
| `ContentIdentity.cs` | 判断本地内容与云端条目是否是同一个地图/MOD |
| `ProgressStreamContent.cs` | HTTP 上传/传输进度支持 |
| `SafeArchive.cs` | ZIP 相对路径与解压安全规则，防止越界写入 |
| `SettingsValidator.cs` | 配置、目录、网络地址等保存前校验 |

### Services

| 文件 | 责任 |
| --- | --- |
| `AuthApiClient.cs` | 账号服务 HTTP 客户端：登录、注册、身份、健康状态等 |
| `BackupService.cs` | 创建、校验、列出和恢复地图/MOD 备份 |
| `CloudCatalogService.cs` | 读取 COS 正式清单、构造公开 URL、下载内容包 |
| `CloudHistoryService.cs` | 调用管理员档案 API，并转换成客户端可展示的版本列表 |
| `ConfigService.cs` | 本机配置读取、迁移、原子保存和数据目录定位 |
| `DiagnosticsService.cs` | 游戏目录、网络、运行环境等只读诊断 |
| `GamePathService.cs` | 游戏根目录与 Maps/Mods 目录发现/验证 |
| `InstallService.cs` | 下载后校验、备份、解压、替换、冲突清理和失败恢复 |
| `LocalContentService.cs` | 扫描并解析本地地图/MOD 的真实名称、版本、结构 |
| `LogService.cs` | 本地运行日志与启动阶段日志 |
| `LoginCredentialProtector.cs` | 使用 Windows 用户级保护保存“记住密码”等登录信息 |
| `ManagementService.cs` | 用户管理、会话/审计等管理员账号服务调用 |
| `MapPreviewService.cs` | 从地图文件读取/提取游戏预览图 |
| `PreviewImageValidator.cs` | 发布/展示用图片的格式、尺寸与安全检查 |
| `PublicationPreparationService.cs` | 本地发布材料：ZIP、哈希、下一版 manifest、缩略图 |
| `PublicationApiModels.cs` | 客户端 ↔ 发布网关协议 DTO，不执行网络操作 |
| `PublicationUploadService.cs` | 发布网关调用、staging 上传、commit、下架、档案、恢复、COS 凭据管理 |
| `ServiceRegistry.cs` | 创建并共享客户端所有长期 Service |
| `SyncHistoryService.cs` | 保存最近同步执行历史 |
| `SyncPreferenceService.cs` | 保存按账号隔离的“不喜欢/自动跳过”偏好 |
| `SyncService.cs` | 比较云端与本地状态，决定跳过/安装/升级/阻止冲突 |
| `TaskService.cs` | 下载/后台任务状态集合 |
| `UpdateApplier.cs` | 客户端更新后的进程替换与临时文件清理 |
| `UpdateService.cs` | 检查、下载和验证客户端更新包 |
| `UserSessionService.cs` | 本机登录会话的恢复与清除 |

### 主要 ViewModel / View

| ViewModel | 对应界面 | 责任 |
| --- | --- | --- |
| `MainViewModel.cs` | `MainWindow.xaml` | 主导航、页面实例、权限可见性 |
| `DashboardPageViewModel.cs` | `DashboardView.xaml` | 首页状态概览 |
| `CloudPageViewModel.cs` | `CloudContentView.xaml` | 云端地图/MOD 列表、搜索、安装、冲突处理、管理员下架入口 |
| `LocalPageViewModel.cs` | `LocalContentView.xaml` | 本地地图/MOD 列表和安全删除 |
| `SyncPageViewModel.cs` | `SyncView.xaml` | 一键同步状态、取消、结果和历史 |
| `BackupsPageViewModel.cs` | `BackupsView.xaml` | 备份浏览和恢复 |
| `PublicationPageViewModel.cs` | `PublicationView.xaml` | 管理员地图/MOD 发布页面流程 |
| `CloudHistoryPageViewModel.cs` | `CloudHistoryView.xaml` | 管理员版本档案、历史安装和恢复 |
| `AdminSettingsPageViewModel.cs` | `AdminSettingsView.xaml` | 超级管理员 COS 写入凭据设置 |
| `UsersPageViewModel.cs` | `UsersView.xaml` | 用户/账号管理 |
| `OperationsPageViewModel.cs` | `OperationsView.xaml` | 服务状态和审计 |
| `SettingsPageViewModel.cs` | `SettingsView.xaml` | 软件配置、检测和保存 |
| `SetupPageViewModel.cs` | `SetupView.xaml` | 首次设置向导 |
| `UpdatesPageViewModel.cs` | `UpdatesView.xaml` | 客户端更新页面 |
| `DiagnosticsPageViewModel.cs` | `DiagnosticsView.xaml` | 诊断页面 |
| `DownloadsPageViewModel.cs` | `DownloadsView.xaml` | 当前任务/下载列表 |

### Models

模型文件原则上只描述数据，不执行磁盘或网络操作：

- `AppConfig.cs`：本机配置与用户本地资料。
- `AuthModels.cs`：账号/权限/登录 API 数据。
- `BackupModels.cs`：备份元数据。
- `ContentModels.cs`：云端/本地地图和 MOD 数据。
- `DiagnosticModels.cs`：诊断结果。
- `HistoryModels.cs`：版本档案结果。
- `ManagementModels.cs`：用户/审计管理数据。
- `SyncHistoryModels.cs`：同步历史。
- `TaskModels.cs`：后台任务。
- `UpdateModels.cs`：客户端更新。

## 关键安全边界

修改以下区域时必须先读相关回归测试：

- `InstallService.cs`
- `BackupService.cs`
- `SafeArchive.cs`
- `ContentHash.cs`
- `PublicationPreparationService.cs`
- `PublicationUploadService.cs`
- `server/SCFA.PublicationGateway/*`

这些代码直接涉及玩家文件、云端正式清单或管理员凭据，不能只凭“代码更简洁”重写。

## 测试位置

- `tests/SCFA.ContentCenter.RegressionTests/Program.cs`：客户端核心规则和 WPF smoke。当前文件偏大，后续计划按领域拆分。
- `server/SCFA.PublicationGateway.RegressionTests/Program.cs`：发布网关隔离回归。
- `.github/workflows/build-windows.yml`：Windows 客户端 CI。
- `.github/workflows/release-gateway.yml`：验证并生成 Linux 网关 Release。

## 文档分别负责什么

| 文件 | 用途 |
| --- | --- |
| `README.md` | 面向普通 GitHub 用户的项目简介 |
| `PRODUCT_REQUIREMENTS.md` | 产品负责人确认过的要求/状态，最高优先级 |
| `ROADMAP.md` | 下一步开发顺序与阶段目标 |
| `CODEBASE_GUIDE.md` | 功能和源码文件的对应关系 |
| `PROJECT_HANDOFF.md` | 当前生产事实、未知项、换电脑交接 |
| `BUILD_VALIDATION.md` | 当前必须通过的构建/测试/生产验收 |
| `ADMIN_PUBLISH_PLAN.md` | 管理员发布架构与安全设计 |
| `ADMIN_PUBLISH_API.md` | 客户端 ↔ 发布网关 API 契约 |
| `AGENTS.md` | Codex/AI 开发约束 |

## 修改前检查

1. 先确认需求是否已经写入 `PRODUCT_REQUIREMENTS.md`。
2. 从上面的“功能 → 文件”表定位现有实现。
3. 搜索是否已有相同 Model/Service/helper。
4. 不要建立第二条平行安装、同步或发布链。
5. 改核心文件后运行对应回归。
6. 功能状态变化时同步更新 requirements / handoff / validation，而不是新增一次性历史文档。
