# 私人复习站：本地运行与数据维护

Node 版服务是**单账号**复习站，使用 SQLite。先按[根 README](../README.md#源码快速验证)初始化账号，再启动服务。启动时选择数据库路径和站点来源；创建账户后不需要每次启动重新初始化。同一台机器浏览器访问 `http://127.0.0.1:8765`。需要剪辑或校验音频时，使用可执行的 FFmpeg。

## 配置

| 环境变量 | 默认或作用 |
| --- | --- |
| `REVIEW_DB` | 默认是 `cloud/data/review.sqlite`；自定义路径包含私人数据，请留在 Git 仓库外 |
| `REVIEW_PORT` | 默认 `8765`；服务仍只监听 `127.0.0.1` |
| `REVIEW_ORIGIN` | 默认 `http://127.0.0.1:<端口>`；使用反代时设置成实际 HTTPS 来源（协议、主机、端口一致） |
| `REVIEW_FFMPEG` | 不设置时从 PATH 取 `ffmpeg`；也可传可执行文件路径 |

## 配对、撤销和备份

在服务所在机器的受信终端操作：

```text
node cloud/cli.mjs pair desktop-1
node cloud/cli.mjs revoke desktop-1
node cloud/cli.mjs backup path/to/new-backup.sqlite
```

`pair` 输出一次性配对码，10 分钟有效；只输入自己的桌面客户端，不粘贴到日志或 issue。`revoke` 按设备标签撤销。备份目标必须是一个新文件，里面可能有词库、音频及账号数据，按私有文件保管。

恢复时先把 `REVIEW_DB` 指向一个不存在的独立数据库文件。下面用 PowerShell 演示；按实际情况替换路径，不要覆盖正在使用的库：

```powershell
$env:REVIEW_DB = 'D:\PrivateReview\restored.sqlite'
node cloud/cli.mjs restore 'D:\PrivateReview\backups\review.sqlite'
node cloud/cli.mjs serve
```

恢复会清除旧认证记录。浏览器需要重新登录，桌面需要重新配对。先在新库确认卡片和音频，再决定是否切换。

## 跨设备边界

- 回环地址不能给手机访问。需要远程访问时，应保持 Node 后端的回环监听，只把完成 TLS 配置的入口列为可访问端点，并核对站点来源、登录、音频及备份恢复。不要为手机直接开放未经保护的 HTTP 端口。
- `cloud/workers/` 是独立的 Cloudflare Workers 适配实现，使用 Durable Object SQLite，不是 Node 数据库的直接迁移，也不会在本仓库测试中自动部署。已有的华为云恢复脚本面向原本机环境，不能当作通用部署脚本。
- 若只需要 Anki 路径，不必部署此站：[手动导出](../cloud/deploy/ankiweb-export.md) · [手机同步试用](../cloud/deploy/ankiweb-three-card-trial.md)。
