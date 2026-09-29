# SCFA 内容中心操作记录

Updated: 2026-09-29

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

### 2026-09-29 10:30 — 建立统一操作记录制度

- **执行人：** ChatGPT
- **对象/版本：** SCFA Content Center / V4.0.0-dev61
- **状态：** 🟡 部分完成
- **关联：** `docs/OPERATION_LOG.md`；PR/CI 待本次变更完成后补充

**做了什么**
- 新增统一项目操作记录格式。
- 要求重要开发、部署、排障、发布和回滚记录执行人、时间、结果、未完成项和错误。
- 准备把操作记录加入项目手册入口和 Codex 开发规则。

**已完成**
- 日志格式和记录范围已经确定。
- 已补录最近两次重要仓库整理操作。

**未完成 / 待验证**
- 本次文档变更仍需 GitHub Actions 验证并合并到 `main`。

**错误 / 异常**
- 无。

**结果**
- 后续维护者可以从同一文件了解近期发生过什么，而不必只依赖聊天记录和 Git commit 标题。

**下一步**
- CI 通过并合并后，把本条更新为最终完成状态。

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
