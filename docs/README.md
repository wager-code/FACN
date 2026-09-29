# SCFA 内容中心项目手册

这是本仓库的**开发与维护总目录**。

新电脑、新开发者、Codex 新会话开始工作前，先阅读：

**[00_START_HERE.md](00_START_HERE.md)**

## 项目手册目录

1. [00_START_HERE.md](00_START_HERE.md) — 新人入口、阅读顺序、基本规则
2. [PRODUCT_REQUIREMENTS.md](PRODUCT_REQUIREMENTS.md) — 已确认产品要求
3. [ROADMAP.md](ROADMAP.md) — 当前阶段与后续开发顺序
4. [CODEBASE_GUIDE.md](CODEBASE_GUIDE.md) — 功能与源码文件对应关系
5. [PROJECT_HANDOFF.md](PROJECT_HANDOFF.md) — 当前生产状态、已验证事实和未知项
6. [BUILD_VALIDATION.md](BUILD_VALIDATION.md) — 构建、回归与正式验收门槛
7. [OPERATION_LOG.md](OPERATION_LOG.md) — 重要操作、结果、错误、未完成项和下一步
8. [DEPLOYMENT_POLICY.md](DEPLOYMENT_POLICY.md) — Codex 与生产服务器部署边界
9. [ADMIN_PUBLISH_PLAN.md](ADMIN_PUBLISH_PLAN.md) — 管理员地图/MOD 发布架构
10. [ADMIN_PUBLISH_API.md](ADMIN_PUBLISH_API.md) — 客户端与发布网关接口契约
11. [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — 故障分层与服务器交接

## 冲突时按什么为准

1. 产品负责人最新明确决定
2. `PRODUCT_REQUIREMENTS.md`
3. 当前源码和已经通过的测试
4. `PROJECT_HANDOFF.md`
5. `ROADMAP.md`
6. 其他设计说明

`OPERATION_LOG.md` 用于了解“最近发生了什么”，不覆盖产品要求和当前源码事实。

`README.md` 是给普通 GitHub 用户看的项目介绍，不作为实现细节的最高依据。

## 文档维护规则

- 新需求：先更新产品要求，再开发。
- 开发优先级变化：更新路线图。
- 源码职责变化：更新源码导航。
- 生产事实变化：更新项目交接。
- 测试/发布门槛变化：更新构建验收。
- 部署规则变化：更新部署策略和故障排查。
- 重要开发/部署/排障/回滚完成后：更新操作记录。
- 不再为每个 dev 版本新增一次性说明文档；历史交给 Git。
