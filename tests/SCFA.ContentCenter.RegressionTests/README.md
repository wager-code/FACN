# 客户端回归测试说明

`SCFA.ContentCenter.RegressionTests` 是项目的核心回归与 WPF smoke 测试入口。

## 当前覆盖方向

- 配置与路径安全
- 云端清单/内容模型
- 地图/MOD 扫描
- 安装、更新、冲突保护
- 备份与恢复
- 一键同步与偏好
- 管理员权限
- 发布材料与发布客户端安全约束
- 下架/档案/恢复相关绑定
- WPF 页面构造与绑定 smoke

## 常用命令

从仓库根目录：

```powershell
.\run-regression-tests.ps1
```

单独运行 UI smoke：

```powershell
dotnet run --project tests/SCFA.ContentCenter.RegressionTests/SCFA.ContentCenter.RegressionTests.csproj -c Release --no-build -- --profile-ui-smoke
dotnet run --project tests/SCFA.ContentCenter.RegressionTests/SCFA.ContentCenter.RegressionTests.csproj -c Release --no-build -- --archive-ui-smoke
```

## 结构债务

当前 `Program.cs` 已经很大。后续整理应按领域拆分测试，例如：

- `ContentSafetyTests`
- `InstallSyncTests`
- `PublicationClientTests`
- `AdminUiSmokeTests`
- `SettingsAndSessionTests`

拆分时只移动测试组织方式，不改变测试断言和生产逻辑；每一步都必须保持 CI 绿色。
