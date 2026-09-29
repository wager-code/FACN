# SCFA 内容中心操作记录

Updated: 2026-09-30

本文件记录项目的重要开发、部署、排障、发布、回滚和结构调整。  
它不是 Git commit 历史的替代品，而是给新开发者和维护者看的“发生了什么、结果如何、还剩什么”。

## 记录规则

需要记录：

- 较大的源码开发/重构
- 重要 Bug 修复
- 服务器部署/升级
- Nginx/systemd/COS/证书/端口等生产变更
- 生产故障排查
- 发布/下架/恢复
- 重大项目结构调整
- 失败的尝试，只要它会影响后续判断

一般不记录：

- 拼写修正
- 单纯格式化
- 无行为变化的小注释
- 每一次中间试验命令

## 执行人写法

尽量使用明确身份：

- `用户/管理员`
- `Codex`
- `ChatGPT`
- `ChatGPT 服务器部署会话`
- `GitHub Actions`
- 具体人工开发者姓名/账号

如果多人共同完成，可以写：

`ChatGPT（方案/代码整理） + GitHub Actions（自动验证）`

## 状态

统一使用：

- ✅ 完成
- 🟡 部分完成
- 🔴 失败
- ⏸️ 暂停/等待外部条件
- ↩️ 已回滚

## 单条记录模板

复制下面模板追加到文件顶部“最新记录”区域：

```markdown
### YYYY-MM-DD HH:mm — 标题

- **执行人：**
- **对象/版本：**
- **状态：** ✅ 完成 / 🟡 部分完成 / 🔴 失败 / ⏸️ 暂停 / ↩️ 已回滚
- **关联：** Commit / PR / Release / 服务器服务（没有则写“无”）

**做了什么**
- ...

**已完成**
- ...

**未完成 / 待验证**
- ...

**错误 / 异常**
- 无
  或
- ...

**结果**
- ...

**下一步**
- ...
```

## 安全要求

- 不记录密码、Token、SecretId、SecretKey、主密钥、私钥。
- 环境变量只写变量名，不写真实值。
- 日志可以写 HTTP 状态码、错误码、RequestId、服务名、端口、Commit SHA。
- 生产故障必须写“已经尝试过什么”，避免下一位维护者重复试错。
- 失败记录不要删除；问题解决后追加新的“已解决”记录并互相引用。

---

# 最新记录

### 2026-09-30 01:50 — 生产 gateway-v61 核实、完整备份与 updater v2 旁路验收

- **执行人：** ChatGPT 服务器部署会话 + 用户/管理员
- **对象/版本：** 生产 Publication Gateway / `gateway-v61`
- **状态：** ✅ 完成（自动更新接管与故障回滚演练仍未执行）
- **关联：** `docs/PROJECT_HANDOFF.md`；生产 `scfa-publication.service`

**做了什么**
- 只读核实生产服务器 OS、服务、版本、磁盘、内存、systemd、Nginx/TLS、账号数据、发布历史、COS 凭据密文与主密钥配置位置。
- 确认实际运行网关为 `gateway-v61`，运行中与磁盘二进制 SHA-256 一致。
- 核实旧 Release 下载曾出现低速超时、Broken pipe 和 HTTP/2 protocol error，但后续完整包 + checksum 的 v61 安装成功。
- 在任何新部署前创建生产完整备份，并完成关键文件、压缩包 SHA-256、服务恢复与异机副本校验。
- 安装旁路 `update-from-release-v2.sh` / `check-gateway-release-v2.sh`，未替换 systemd 正式入口。
- 用真实 `gateway-v61` Release 做 41,115,979 字节完整下载验证，显示分块/总进度并验证 Release SHA-256。
- 第二次 verify-only 验证四个分块全部从缓存复用。
- 用相同 v61 二进制执行一次真实安装链路演练：备份 → `.next` → 原子切换 → restart → health/auth → runtime SHA-256。

**已完成**
- `scfa-publication.service` 最终保持 active。
- `/publication-healthz` 返回 200。
- 未登录管理员 archive API 返回 401。
- 运行中 `/proc/<pid>/exe` 与 `/opt/scfa-publication/SCFA.PublicationGateway` SHA-256 一致。
- 生产 timer 继续保持 disabled。
- 结论：本次问题不需要重装 OS。

**未完成 / 待验证**
- updater v2 还没有纳入仓库源码/CI/正式 systemd 入口。
- 没有在生产上人为制造启动失败，因此 `ROLLBACK_OK/ROLLBACK_FAILED` 路径尚未做故障注入演练。
- 真实 MOD 发布→玩家安装→重复同步→更新仍待完成。
- 下架→档案→恢复仍需真实管理员生产验收。
- 客户端真实更新包验收需先修复 `UpdateService` 文件句柄/SHA-256 问题。

**源码审计发现**
- `CosTransport` 的共享 `HttpClient.Timeout` 当前为无限，需要明确的有界超时/取消策略。
- `PublicationCoordinator` 的 commit/unpublish/restore 共用 manifest 串行锁；必须保留最终写入串行，但应评估缩小耗时临界区。
- `UpdateService.DownloadAsync` 在 `FileShare.None` 输出流仍在作用域时重新打开同一文件做 SHA-256，Windows 下存在失败风险。
- `/publication-healthz` 不返回版本/构建标识，部署验证仍需服务端 SHA/marker 辅助。

**错误 / 异常**
- GitHub Release 旧下载路径曾出现低速超时、Broken pipe、HTTP/2 协议错误。
- v2 完整分块测试中一个分块明显较慢，但四路并行最终约 75 秒完成真实 41MB+ 包；可见进度避免误判为卡死。

**结果**
- 当前生产状态健康，`gateway-v61` 已明确核实。
- 已建立可恢复的备份基线和经过实包验证的 updater v2 行为基线。
- 下一阶段应回到源码修复/回归，而不是继续在生产服务器试错。

**下一步**
- Codex 按 `ROADMAP.md` 的“Codex 当前执行顺序”修复客户端 updater、COS timeout、锁范围、health 版本信息，并把 updater v2 纳入源码管理。
- 源码和 CI 通过后按 `DEPLOYMENT_POLICY.md` 生成新的服务器部署交接单。

---

### 2026-09-29 10:53 — 建立统一操作记录制度

- **执行人：** ChatGPT + GitHub Actions
- **对象/版本：** SCFA Content Center / V4.0.0-dev61
- **状态：** ✅ 完成
- **关联：** PR #3；`docs/OPERATION_LOG.md`

**做了什么**
- 新增统一项目操作记录格式。
- 要求重要开发、部署、排障、发布和回滚记录执行人、时间、结果、未完成项和错误。
- 准备把操作记录加入项目手册入口和 Codex 开发规则。

**已完成**
- 日志格式和记录范围已经确定。
- 已补录最近两次重要仓库整理操作。

**未完成 / 待验证**
- 无。本次为文档/规则变更，不涉及生产服务器状态。

**错误 / 异常**
- 无。

**结果**
- 后续维护者可以从同一文件了解近期发生过什么，而不必只依赖聊天记录和 Git commit 标题。
- PR #3 的 Windows build、核心回归、Profile WPF smoke、Archive WPF smoke 和 win-x64 publish 已通过。

**下一步**
- 后续每次重要开发、部署、故障、回滚或验收结束后，按模板追加记录。

---

### 2026-09-28 — 建立项目手册与服务器部署边界

- **执行人：** ChatGPT + GitHub Actions
- **对象/版本：** SCFA Content Center / V4.0.0-dev61
- **状态：** ✅ 完成
- **关联：** PR #2；squash commit `d77b15a0`

**做了什么**
- 将产品要求、路线图、源码导航、项目交接、构建验收、管理员发布设计/API 统一移动到 `docs/`。
- 新增 `docs/00_START_HERE.md` 作为新电脑、新开发者和 Codex 新会话入口。
- 新增生产部署职责边界和故障排查手册。
- 修改根目录 `AGENTS.md`，要求 Codex 开发前先读项目手册。
- 明确 Codex 不直接执行生产 SSH/systemd/Nginx/COS 密钥/生产环境等变更，而是生成服务器部署交接单。

**已完成**
- 项目文档只保留 `docs/` 一份权威版本。
- 根目录入口简化。
- Windows Release build、核心回归、Profile WPF smoke、Archive WPF smoke、win-x64 publish 全部通过。
- PR #2 已 squash 合并到 `main`。

**未完成 / 待验证**
- 需要在未来真实服务器部署任务中验证 Codex 是否持续遵守交接规则。

**错误 / 异常**
- 文档迁移过程中一次 Git tree“移动+删除”组合操作被工具安全检查阻止；改为先建立新目录、确认文件存在，再逐个删除旧副本，最终未造成文件丢失。

**结果**
- 新维护者已有统一入口；服务器部署与源码开发职责边界已写入仓库。

**下一步**
- 后续重大规则变化同步更新项目手册和本操作记录。

---

### 2026-09-28 — 清理仓库噪音并整理 dev61 源码结构

- **执行人：** ChatGPT + GitHub Actions
- **对象/版本：** SCFA Content Center / V4.0.0-dev61
- **状态：** ✅ 完成
- **关联：** PR #1；squash commit `5c1f342b`

**做了什么**
- 删除过期 `MIGRATION_STATUS.md`、一次性 `design-qa.md` 和旧 V3/Win32 `reference-dev51/` 参考资料。
- 压缩 `PROJECT_HANDOFF.md` 和 `BUILD_VALIDATION.md`，改成当前状态而非版本流水账。
- 新增路线图和源码导航。
- 将 `PublicationUploadService.cs` 内 API DTO 拆到 `PublicationApiModels.cs`。
- 将 dev61 archive WPF smoke 接入 CI。
- CI 增加 Pull Request 触发。

**已完成**
- 未删除实际仍有引用的业务 Service/Model。
- 发布 DTO 与网络逻辑完成低风险职责拆分。
- Windows restore/build、核心回归、两个 WPF smoke、win-x64 publish 全部通过。
- PR #1 已 squash 合并到 `main`。

**未完成 / 待验证**
- `tests/SCFA.ContentCenter.RegressionTests/Program.cs` 仍偏大。
- `CloudPageViewModel.cs` 与 `SettingsPageViewModel.cs` 仍需后续按职责小步拆分。
- MOD真实发布、下架/档案/恢复、客户端更新链仍需生产验收。

**错误 / 异常**
- 无影响最终结果的源码错误。

**结果**
- 仓库对 Codex 和人工开发者更容易理解，旧文档不会继续干扰当前需求判断。

**下一步**
- 技术债按 `ROADMAP.md` 逐步处理，不做一次性大重构。
