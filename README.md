# LLPlayer 字幕学习与原声复习

用已有的英文字幕学习：在 Windows 播放器里查词、回听当前句，把自己核对过的原声和词义存成复习卡。可以留在本机，也可以手动导入 Anki，或使用独立的私人复习站。

> **这是源码与验证仓库，不是安装包下载页。** 本机候选包、影视素材、词典构建缓存和带补丁的 LLPlayer 上游工作副本没有提交。干净克隆可运行下面的 Node 检查和本地复习站，但不能据此重建当前完整播放器候选。

## 选一个入口

| 我想做什么 | 从哪里开始 |
| --- | --- |
| 已有 Windows 候选包，想做第一张原声卡 | [完整使用说明](LLPlayer原声卡-完整使用说明.md) |
| 把原声卡导到桌面 Anki，再同步到手机 | [Anki 导出说明](cloud/deploy/ankiweb-export.md) · [三卡验收](cloud/deploy/ankiweb-three-card-trial.md) |
| 导入现有词表，独立做文字或原声复习 | [独立 Windows 复习工具](review/README.md) |
| 在本机运行私人复习站 | [本地复习站、配对与备份](docs/LOCAL-REVIEW.md) |
| 开发、跑测试、复核 Windows 候选包 | [开发与构建](docs/DEVELOPMENT.md) · [候选包记录](packaging/RELEASE.md) |

## 典型流程

1. 用有权使用的本地媒体和匹配的英文字轨。先确认英文原句和声轨对得上；中文字幕错时或义项不确定时，不要直接生成原声卡。
2. 在支持的 LLPlayer 候选包里查词、回听，填写语境义并试听。确认片段与例句一致后再保存。
3. 选择复习方式：留在本地词库；通过桌面 Anki 和 AnkiConnect 写入；或导出 TSV、MP3 后手动导入。LLPlayer 不会替你登录或同步 AnkiWeb。
4. 如需跨设备，用 Anki 手动同步并等媒体传完，或者部署自己的私人复习站。独立 Windows 复习工具不会自动读取播放器词库。

## 源码快速验证

在仓库根目录，用 **Node.js 24**，并确保 `ffmpeg`、`ffprobe` 和 `openssl` 在 PATH 中。FFmpeg 需要带 `libmp3lame`；Windows 找不到 OpenSSL 时，可用 `REVIEW_OPENSSL` 指向本机的可执行文件。以下测试只用合成素材、临时数据库和回环服务，不需要 `.pilot/`、真实 Anki 或云账号。

```text
node --test cloud/checks/server.test.mjs cloud/checks/https.test.mjs pilot/feasibility.test.mjs pilot/review-study-translation.test.mjs pilot/study-audit-form.test.mjs
```

运行私人复习站还需要 FFmpeg 验证上传的原声音频。账号初始化必须在交互式终端执行；按提示输入两次密码（12–256 字符）。

```text
node cloud/cli.mjs init Asia/Shanghai
node cloud/cli.mjs serve
```

在同一台机器打开 [http://127.0.0.1:8765](http://127.0.0.1:8765)。默认数据是 `cloud/data/review.sqlite`，服务只监听回环，手机无法直接访问。配置、备份恢复和跨设备限制见[私人复习站说明](docs/LOCAL-REVIEW.md)。

## 目录与状态

| 目录 | 内容 |
| --- | --- |
| [`pilot/`](pilot/) | 字幕探测、时间轴核对、本地模型试验及人工验收工具 |
| [`cloud/`](cloud/) | Node/SQLite 私人复习站、Web 界面、桌面集成及检查；`workers/` 是单独的 Cloudflare 适配实现 |
| [`review/`](review/) | 独立的 WPF 复习工具、词表样例和检查 |
| [`packaging/`](packaging/) | Windows 候选冻结、离线构建、NSIS 安装器与验收 |

当前以 Windows x64 本机候选和回归测试为主，不承诺安装包在干净机器可用，不代表真机同步、真实 Anki 或生产部署已验收。构建产物与本机验收路径见[候选包记录](packaging/RELEASE.md)，它们不是仓库中的下载链接。

## 维护与权利

- [贡献指南](CONTRIBUTING.md) · [安全与隐私](SECURITY.md) · [仓库变更记录](CHANGELOG.md) · [上游与许可说明](NOTICE.md)
- 请使用有权播放、截取和同步的素材。不要在 issues、PR 或日志中附上完整节目、原声词库、私有数据库、账号凭据或签名媒体地址。
