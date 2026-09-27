# 管理员软件内自动发布：服务端接口约定

状态：2026-09-27 客户端已接入下列接口，但现有生产账号服务没有这些路由。本文件是待实施接口，不是已部署服务。产品目标是管理员在软件里选地图或 MOD 后，一键校验、打包、上传并上架；不再要求打开 COS 网页上传。普通玩家没有发布权限，也不接收长期 COS KEY。

## 发布流程

1. 客户端以管理员登录 Token 请求 GET /v1/admin/publications/capabilities。服务端必须独立核对 Token、账号状态和发布权限，返回 available 和 message。未配置 COS 写入身份时返回 available=false。
2. 客户端对本地内容重新扫描，核对线上清单、游戏版本、目标目录、ZIP 文件和 SHA-256。然后向 POST /v1/admin/publications/intents 提交发布意图，包括 kind (map/mod)、package_key、package_sha256、package_size、content_sha256、manifest_key、original_manifest_sha256、next_manifest、next_manifest_sha256，以及可选缩略图的对象键、哈希和大小。
3. 服务端再次验证管理员权限、清单结构、版本与目录冲突、目标对象不存在或确认为安全升级、文件大小、路径和意图有效期。服务端随机生成 GUID publication_id 和只能写入 scfa/publication-staging/<publication_id>/ 下单个对象的短期 COS PUT 签名地址，返回 package_upload_key、package_upload_url，必要时返回 thumbnail_upload_key、thumbnail_upload_url。正式包对象键不得直接给客户端作为上传目标。
4. 客户端验证签名 URL 的 HTTPS 主机、桶和暂存对象路径后，直接向 COS 上传 ZIP 和可选缩略图。此阶段正式清单不变。
5. 客户端请求 POST /v1/admin/publications/<publication_id>/commit，带 package_sha256 和 next_manifest_sha256。服务端从 COS 重新读取暂存对象，独立计算哈希、检查 ZIP 文件名/路径/文件数/压缩比和地图或 MOD 必要结构，并核对包内游戏版本。Steam/FAF 语义无法自动判定的内容必须有管理员审核记录，不能自动放行。
6. 服务端串行处理清单提交，提交前再次确认线上旧清单与 original_manifest_sha256 一致；如不一致就拒绝发布并要求重新准备。先复制已验证对象到不可变的正式对象键并核对，再备份旧清单，最后写入新清单。若 COS 条件写入不可用，不能宣称跨外部写入者具备原子 CAS 语义；需限制其他写入入口并记录冲突风险。返回 manifest_sha256，重复 commit 要幂等。
7. 客户端重新读取公开清单，核对哈希后显示成功。任何阶段失败都不得把未校验内容加入正式清单；暂存对象过期后清理。服务端记录操作者、内容 ID、包哈希、时间和结果，日志不包含 Token、签名 URL 或 COS 密钥。

## COS 密钥轮换

管理员设置页未来调用服务端状态与轮换接口。仅具备专门权限的管理员能提交 SecretId/SecretKey；服务器验证新身份对指定桶和路径的最小权限后安全保存并切换，响应只返回状态和轮换时间。长期密钥不进入玩家客户端、公开仓库、日志或审计。现阶段发布接口与轮换接口均未部署，不能把客户端按钮可见等同于正式发布可用。

## 上线前验收

- 使用隔离桶和测试账号验证管理员成功发布地图与 MOD；普通账号调用每个接口均被服务端拒绝。
- 测试重复 ID、重复目录、同版本异内容、降级、源目录在打包中变化、已变更的线上清单、无效 ZIP、错误哈希、过期签名 URL、取消与重试。
- 测试发布前玩家看到旧清单，成功提交后首次安装正确，再次同步跳过，回滚后恢复旧清单。
- 保留当前账号服务的数据与登录行为；新服务部署前先备份服务器数据、当前二进制和有效配置，不用旧部署包覆盖当前服务。