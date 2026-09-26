# SCFA 内容中心

面向《最高指挥官：钢铁联盟》Steam 版的 Windows 地图与 MOD 管理客户端，使用 .NET 8 和 WPF 开发。

## 可以做什么

- 登录账号，浏览云端地图和 MOD，查看名称、版本与预览图。
- 扫描本地内容，识别游戏里的地图名称；在软件内删除不需要的地图或 MOD，删除前自动备份。
- 一键同步缺失或需要更新的内容。安装前校验文件与版本，遇到重复目录或较新的本地版本时保护原文件。
- 为不想同步的内容标记“不喜欢”；一键同步会跳过，仍可手动安装。
- 管理员在软件内准备地图或 MOD 的发布包和清单。

地图和 MOD 存放在腾讯云 COS；账号服务独立运行。**玩家投稿与审核已取消。**管理员发布中心目前只在本机生成待上传材料，实际发布仍由管理员在 COS 控制台完成。

## 运行与开发

支持 Windows；使用 Visual Studio 2022（.NET 桌面开发、.NET 8 SDK）打开 `SCFA.ContentCenter.sln`，或运行下列命令。构建输出位于本机 `artifacts\win-x64`。

```powershell
.\build.ps1
.\run-regression-tests.ps1
```

当前源码版本：`V4.0.0-dev54`。仓库保存源码和文档；本机备份、游戏内容、账号凭据与 COS 密钥不应提交到 GitHub。

## 项目文档

- [产品要求与完成状态](PRODUCT_REQUIREMENTS.md)
- [管理员发布流程与后续计划](ADMIN_PUBLISH_PLAN.md)
- [跨电脑继续开发的项目交接记录](PROJECT_HANDOFF.md)
- [构建与验证记录](BUILD_VALIDATION.md)
