# 开发与构建

## 运行环境与检查范围

从仓库根目录执行命令。Node 脚本使用 Node.js 24；Windows 播放器及独立复习工具用 .NET 10 桌面 SDK。PowerShell 脚本以各自的 `#Requires` 为准：冻结与离线构建要求 7.4+，独立复习检查等脚本要求 7.6+。FFmpeg 和 FFprobe 用于字幕与音频，HTTPS 用例需要 OpenSSL。

| 范围 | 如何运行 | 额外依赖或边界 |
| --- | --- | --- |
| 可移植的服务端、HTTPS、字幕和验收表 | [README 的快速验证命令](../README.md#源码快速验证) | FFmpeg/FFprobe/OpenSSL；GitHub Actions 只跑此层 |
| 本地模型接口和 Windows 启动流程 | `node --test pilot/study-model.test.mjs` | 本机 `pwsh`；测试用回环模拟响应，不验证真实模型的翻译质量 |
| 独立 WPF 工具 | `dotnet build .\review\OriginalReview\OriginalReview.csproj -c Release` | Windows、.NET 10 SDK；只是编译，不等于发布 |
| 独立复习逻辑 | `pwsh -NoProfile -File .\review\check-review.ps1` | 需要本地 `.pilot/StudyDictionary.zip`，也可通过 `-Dictionary` 指定；见[模块说明](../review/README.md) |
| 桌面进程联调 | `node --test cloud/checks/desktop.test.mjs` | 构建 `cloud/checks/DesktopChecks/DesktopChecks.csproj` 并设置 `REVIEW_DESKTOP_DLL`；工程还引用未提交的上游 `StudyDialogue.cs`；未设置 DLL 时跳过 |
| 浏览器检查 | `node --test cloud/checks/browser.test.mjs` | 设置 `REVIEW_PLAYWRIGHT` 为本机可加载的 Playwright 模块路径；需要 Chromium，可用 `REVIEW_CHROME` 指定；未配置 Playwright 时跳过 |
| 候选包、WPF 离屏和 NSIS 验收 | [候选包记录](../packaging/RELEASE.md) · [离屏检查](../packaging/ui-checks/README.md) | Windows、完整候选产物，及脚本要求的本机依赖；不在 GitHub CI 内 |

## 字幕试验

探测和核对用用户自行准备的媒体。`scan` 读取本地 MP4/MKV 的英文字轨或同名的本地外挂字幕；不会联网搜索字幕。`timeline` 用人工锚点核对已有字幕；格式见 [`assessTimeline`](../pilot/media.mjs) 及[相关测试](../pilot/feasibility.test.mjs)。

```text
node pilot/feasibility.mjs --help
node pilot/feasibility.mjs scan path/to/episode.mkv
node pilot/feasibility.mjs timeline path/to/episode.en.srt --anchors path/to/anchors.json
```

`translate` 是本地模型试验，不是字幕搜索或转录入口。它要求在 Ollama 与运行命令的环境中均设置 `OLLAMA_NO_CLOUD=1`，只连接 `127.0.0.1:11434`，仅接受已安装的 `qwen3:4b` 或 `qwen3:8b`，不自动下载。输入是本地采样 JSON，不是视频；以[调用测试](../pilot/feasibility.test.mjs)和[实现](../pilot/providers.mjs)为准。结果必须人工核对，不能当作已验真的双语字幕。

## 为什么干净克隆不能重建播放器

- [`upstream/LLPlayer`](https://github.com/umlx5h/LLPlayer) 是独立的上游工作副本，不是本仓库的子模块。冻结脚本要求基线 `da101d87681bb4d1d87a59884bca95043dd1158b` 或其后代；本项目本机候选还依赖上游目录中未发布的工作改动，仅克隆该提交不够。
- 本机维护者需具备对应工作副本、词典材料、受校验的 FFmpeg、离线运行时/NuGet 依赖与打包工具。先冻结，再对快照构建；不要直接把未审计的本机目录或打包件改成 Git 跟踪文件。
- 可用 `Freeze-PolishSource.ps1 -VerifySnapshot` 核对冻结目录；`Publish-PolishOffline.ps1 -Snapshot` 接收快照，`-VerifyPayload` 接收发布运行目录。它们不是同一路径。安装器按载荷清单构建和验收，完整流程见[发布记录](../packaging/RELEASE.md)。
- 评审真实播放、DPI、图形安装、真机 Anki 或手机同步时，单独记录环境与结果。合成媒体和离屏检查不能替代人工验收。
