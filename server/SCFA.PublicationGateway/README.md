# SCFA 管理员发布网关

独立于现有账号服务运行。管理员在 Windows 软件中选地图或 MOD，客户端校验并打包，向本服务取得仅能写入暂存对象的短期 COS 签名 URL，软件直接上传 ZIP，服务端重新验证后把正式包和新清单发布到 COS。普通玩家没有发布接口，也不会收到长期 COS SecretKey。

## 源码文件职责

| 文件 | 作用 |
| --- | --- |
| `Program.cs` | HTTP 路由、依赖注册、统一错误处理 |
| `AdminAuthenticator.cs` | 使用 Bearer Token 向本机账号服务复核管理员身份 |
| `GatewayModels.cs` | 网关配置、加密凭据存储、API DTO、发布 ticket |
| `CosTransport.cs` | COS 凭据验证、对象读写、签名上传 URL 等底层通信 |
| `ManifestValidator.cs` | 验证发布前后的 manifest 变化是否合法 |
| `PackageValidator.cs` | 服务端重新验证暂存 ZIP 的结构、版本与内容哈希 |
| `PublicationCoordinator.cs` | intent/commit/下架/档案/恢复的主业务编排 |
| `UnpublishManifest.cs` | 从正式清单安全移除指定条目 |
| `PublicationArchive.cs` | 组合当前清单与历史快照，生成管理员版本档案 |
| `RestoreManifest.cs` | 从档案构造安全的恢复清单 |

客户端对应代码主要在 `src/SCFA.ContentCenter/Services/PublicationPreparationService.cs` 和 `PublicationUploadService.cs`。完整链路见仓库根目录 `CODEBASE_GUIDE.md`。

## 本地验证

在源码根目录运行：

- dotnet build server/SCFA.PublicationGateway/SCFA.PublicationGateway.csproj -c Release
- dotnet run --project server/SCFA.PublicationGateway.RegressionTests/SCFA.PublicationGateway.RegressionTests.csproj -c Release

回归使用假密钥和临时 ZIP，不访问生产 COS。正式上线前还需用隔离桶完成地图与 MOD 全流程、重试、失败恢复和玩家同步验证。

## 部署约束

1. 先备份现有账号服务的数据、二进制、环境文件和 Nginx 配置；本项目不替换账号服务，也不读取其本地数据库。
2. 账号服务继续监听本机 127.0.0.1:18080，且继续独占公网 HTTPS 18443，必须支持已登录管理员的 GET /v1/auth/me，返回 user.id、role_key 和 status。此服务每次请求都向该接口验证 Token。
3. 把本项目以 Linux x64 自包含方式发布到 /opt/scfa-publication；以独立的 scfa-publication 用户运行，仅监听 127.0.0.1:18081。部署示例见同目录的 .service、.env.example、nginx 示例。
4. 环境变量指定唯一的 COS 桶、地域与根路径。SCFA_PUBLICATION_MASTER_KEY_B64 是服务器专用的随机 32 字节 AES-GCM 主密钥，必须长期保留并单独备份。丢失它就无法解密已保存的 COS 凭据；主密钥和 COS 密钥都不得上传 GitHub。
5. 在独立的 Nginx HTTPS 18444 入口，只把 /v1/admin/publications/ 与 /v1/admin/cos/credentials/ 两个前缀反向代理到 18081，其余路径返回 404。账号及玩家仍使用 18443；客户端发布请求使用 18444，并复用同一证书指纹校验。启用外网端口前先完成内部测试。
6. 首次启动后，用软件登录超级管理员，在“管理员设置”中填写 COS SecretId/SecretKey。服务端先执行随机暂存对象的写、读、删验证，再把凭据加密存入 /var/lib/scfa-publication。COS 身份应只允许该桶的 scfa/manifest、maps、mods、thumbnails、publication-staging 相关操作。
7. 用隔离桶验收后再切换正式桶，确认线上清单与正式包可公开读取或由现有下载机制读取。

本服务对进程内提交加锁；如果同时用 COS 控制台或另一台发布服务修改清单，COS 没有被本项目验证过的条件写入保证，可能出现外部并发竞争。正式环境保持单实例并限制其他写入入口。发布前会保存旧清单到服务端 history 目录。日志不记录密钥、Token 或签名 URL。

当前仓库没有生产账号服务源码及其部署配置。网关曾完成部署，且仓库已生成 `gateway-v61` Release；生产服务器是否已经升级到 dev61 能力必须现场核实。负责人已报告真实地图从软件发布成功，MOD 完整发布闭环以及 dev60/dev61 的下架、档案、恢复仍需生产验收。软件的自动发布按钮只有检测到服务端能力后才启用。
