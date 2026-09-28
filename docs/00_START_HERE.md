# START HERE — SCFA 内容中心开发入口

Updated: 2026-09-28  
Baseline: `V4.0.0-dev61`

如果你是新电脑、新开发者或 Codex 新会话，**修改源码前先读本文件**。

## 项目目标

SCFA 内容中心是面向《最高指挥官：钢铁联盟》Steam 版玩家的 Windows 内容管理工具，负责地图/MOD 的浏览、安装、更新、同步、冲突保护、备份恢复和管理员发布。

仓库包含：

- Windows .NET 8 WPF 客户端
- 独立管理员 Publication Gateway
- 客户端与网关回归测试
- GitHub Actions CI / Release
- 本 `docs/` 项目手册

## 开工前阅读顺序

必须先读：

1. [PRODUCT_REQUIREMENTS.md](PRODUCT_REQUIREMENTS.md)
2. [ROADMAP.md](ROADMAP.md)
3. [CODEBASE_GUIDE.md](CODEBASE_GUIDE.md)
4. [PROJECT_HANDOFF.md](PROJECT_HANDOFF.md)
5. [BUILD_VALIDATION.md](BUILD_VALIDATION.md)

任务涉及管理员发布时，再读：

- [ADMIN_PUBLISH_PLAN.md](ADMIN_PUBLISH_PLAN.md)
- [ADMIN_PUBLISH_API.md](ADMIN_PUBLISH_API.md)

任务涉及服务器、COS、Nginx、systemd、端口、证书或生产环境时，必须读：

- [DEPLOYMENT_POLICY.md](DEPLOYMENT_POLICY.md)
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md)

## 当前开发方向

当前重点不是继续无计划堆功能，而是完成 V4.0 正式版生产闭环：

- MOD 真实发布 → 玩家安装 → 重复同步 → 更新
- 下架 → 档案 → 恢复
- 客户端正式更新链
- Steam/FAF 兼容性检查和管理员验收

详细优先级以 [ROADMAP.md](ROADMAP.md) 为准。

## 源码修改规则

- 先看 [CODEBASE_GUIDE.md](CODEBASE_GUIDE.md)，找到已有模块。
- 新建 Service/Model/helper 前先搜索仓库，避免重复实现。
- 不恢复 dev52 已取消的普通玩家投稿/审核流程。
- 不把长期 COS SecretId/SecretKey 放进玩家客户端。
- 不自动覆盖无法确认身份的重复本地地图/MOD。
- 不因为文件大就一次性重写核心模块；小步拆分并保持 CI 通过。
- 安装、备份、ZIP、manifest、COS、账号权限相关修改必须保留安全校验。

## Codex 与服务器的分工

**Codex 负责源码。生产服务器部署和运维不由 Codex直接执行。**

Codex可以负责：

- 源码开发/重构
- 测试和本地构建
- Git/PR/CI
- Release 包准备
- 部署脚本草稿
- 整理 API、端口、环境变量变化
- 生成服务器部署交接单

遇到以下内容时，停止生产变更并按 [DEPLOYMENT_POLICY.md](DEPLOYMENT_POLICY.md) 交接：

- SSH/远程生产命令
- systemd
- Nginx
- 防火墙/安全组/公网端口
- HTTPS证书
- COS生产权限或真实密钥
- 生产环境变量/密钥
- 生产数据迁移/删除
- Publication Gateway 正式服务器升级
- 需要结合真实服务器状态判断的 403/404/502/连接失败

## 新电脑开始工作

1. Clone 仓库。
2. 先阅读 `docs/` 项目手册。
3. 打开 `SCFA.ContentCenter.sln`。
4. 私有配置、服务器备份、游戏内容单独恢复，不提交 Git。
5. 按 [BUILD_VALIDATION.md](BUILD_VALIDATION.md) 建立构建/回归基线。
6. 确认任务是源码开发还是生产服务器运维。
7. 再开始修改。
