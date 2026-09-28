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
