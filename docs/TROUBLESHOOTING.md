# 故障排查与服务器交接

Updated: 2026-09-30

先判断问题属于哪一层，不要先改代码。

```text
SCFA Windows 客户端
        ↓ HTTPS
Nginx 公网入口
        ↓
账号服务 / Publication Gateway
        ↓
腾讯云 COS
```

## 常见状态码只代表线索

- `401`：优先检查登录/Token/认证链。
- `403`：角色权限、COS权限、签名、密钥、对象路径都可能。
- `404`：Nginx路由、服务端版本或API路径可能不一致。
- `405`：通常说明接口路径存在，但HTTP方法不匹配。
- `502`：优先检查 Nginx 到上游服务、监听端口、服务状态。
- 超时：网络、安全组、端口、TLS、服务阻塞、COS请求都可能。

## 生产排障先收集只读信息

```bash
systemctl status scfa-publication --no-pager
journalctl -u scfa-publication -n 200 --no-pager
ss -lntp
nginx -t
```

需要时再提供相关 Nginx server/location 配置、curl 请求、HTTP状态和响应头。

**不要粘贴 SecretKey、Token、服务器主密钥或私有证书。**

## 当前关键层

### 账号服务

- 监听端口是否正确
- `/v1/auth/me` 是否能验证管理员 Token
- role/status 是否符合预期

### Publication Gateway

- 生产实际运行版本
- systemd 服务状态和 MainPID
- 本机监听端口
- 数据目录权限
- 主密钥环境变量是否存在（不输出值）
- COS 凭据是否配置
- capabilities/downlist/archive/restore 是否与当前源码版本一致
- healthz=200 只表示服务存活；当前 healthz 尚不包含版本号
- 需要确认真实运行二进制时，同时核对：
  - `/var/lib/scfa-publication/installed-gateway-release`
  - `systemctl show scfa-publication.service -p MainPID`
  - `/opt/scfa-publication/SCFA.PublicationGateway` SHA-256
  - `/proc/<MainPID>/exe` SHA-256
- 如果磁盘文件和运行中 exe SHA-256 不一致，先停止继续部署并查明进程是否真正重启

### Nginx

- 18443/18444 职责是否混淆
- location 是否转发到正确本机端口
- 新 API 前缀是否实际放行
- TLS/证书/客户端校验是否一致

### COS

- Bucket/Region/Root 是否一致
- CAM 身份是否正确
- staging/manifest/maps/mods/thumbnails 所需权限是否齐全
- 不用永久“全部权限”掩盖具体权限问题

## 什么时候交回 Codex

只有已经确认是源码缺陷时，再交回 Codex，例如：

- 客户端请求路径写错
- DTO 和服务器响应不一致
- 代码缺少明确状态处理
- Release 包缺文件
- 回归测试缺覆盖

如果是服务器实际状态问题，继续在部署会话处理。

## 交给 ChatGPT 部署会话

提供：

- [DEPLOYMENT_POLICY.md](DEPLOYMENT_POLICY.md) 的交接单
- 当前错误文本/截图
- 实际服务器只读日志
- 具体目标，例如“部署 gateway-v61 并验收 archive/restore”
