# LLPlayer 原声卡 · Windows x64 本机候选

批次 `20261127-125305-144e1786`。主程序版本 `0.3.0+da101d87681bb4d1d87a59884bca95043dd1158b`，作者 `umlx5h`。

## 发布状态

[GitHub Releases](https://github.com/Deeer233/llplayer-subtitle-study/releases) 暂无可下载资产。下面记录的是本机验收过的安装器和免安装 ZIP，不是公开发布。播放器改动和构建脚本见[源码重建说明](../docs/DEVELOPMENT.md#从干净克隆构建-windows-播放器)。

## 交付与使用

交付目录：`C:\Users\Deeer\Desktop\美剧字幕翻译\交付\LLPlayer-Anki-0.3.0-polish.1-20261127-125305-144e1786`。

| 文件 | 用途 | SHA-256 |
|---|---|---|
| `Setup.exe` | NSIS 每用户安装。不要求提升为管理员，不自动启动播放器。 | `543019cd717479688eb0cc04fe7f6655261986425dd6556cc5e136afe1dd63ff` |
| `LLPlayer-Anki-0.3.0-polish.1-win-x64.zip` | 免安装版。完整解压到新目录，再打开其中的 `LLPlayer.exe`。 | `2beedf23c48e9403f716a822822432193c6db9f5fec728f3e1d2634c2e0c3e1d` |
| `payload-manifest.json` | 同一干净文件树 972 个应用文件的相对路径、长度和摘要。NSIS 卸载器为安装版附加项，不在 ZIP 内。 | `557137312a4af704083b6c5850b89ab047ae436e7686a13aa77681c8055e79ee` |
| `SHA256SUMS.txt` | 上列三个完整文件的校验清单。 | — |

默认安装目录为 `%LOCALAPPDATA%\LLPlayer-Anki-Polish-144e1786`。如果目标已存在，请选同盘上级目录已存在的全新路径，不要覆盖旧包。Windows「已安装的应用」显示 `LLPlayer Original Review`。回退时先正常关闭新版，从该卸载项卸载，再从保留的免安装 ZIP 解压到新目录并打开（仍是本发行候选，不是旧版）。卸载只移除路径和 SHA-256 均匹配的程序文件；保留程序目录内的设置、清单外文件及同名但内容不同的文件。词本仍在 `%LOCALAPPDATA%\LLPlayer\PrivateReview`，不做迁移。

## 实测证据

| 检查 | 结果和记录 |
|---|---|
| 相同探针：旧包 7/8 | `C:\Users\Deeer\Desktop\美剧字幕翻译\.pilot\polish-regression-e956a620c7fb\report.md`。旧包部分字段改动不锁定试听。 |
| 相同探针：新版 8/8 | `C:\Users\Deeer\Desktop\美剧字幕翻译\.pilot\polish-regression-0df32e5daca3\report.md`。实际包 `LLPlayer.dll` SHA-256 `d8f448ec739ae9202d05ee989cf83c6a16a13735c7c2d98760b46de606e384b4`。试听实测从 0.117 秒推进到 0.226 秒（总长 1.123 秒）；取消清理临时音频，词库字节不变。 |
| 离屏专项：8/8 | `C:\Users\Deeer\Desktop\美剧字幕翻译\packaging\ui-checks\.pilot\ui-qa-20261127-131941-48c19c4ea6dd\report.json`。450 × 430 DIP、200%／235%、四态、写前取消、损坏偏好回退及显式选卡优先；同目录有四张 PNG。 |
| 每用户静默安装和卸载：通过 | `C:\Users\Deeer\Desktop\美剧字幕翻译\.pilot\polish-install-check-144e1786-v2-bc7edb11\report.json`。未提权账户，正常装卸退出码均为 0。972 个文件与 ZIP 的路径、长度和 SHA-256 一致。中文快捷方式的目标、工作目录正确，未观察到自动启动。注册和卸载范围均限当前用户。已有目标返回 2，哨兵及目录不变；文件被锁返回 3，保留快捷方式与卸载项，解锁后重试成功。 |
| 注册的卸载入口：通过 | `C:\Users\Deeer\Desktop\美剧字幕翻译\.pilot\polish-install-check-144e1786-reg-bc7edb11\report.json`。用已注册的 `Uninstall.exe` 路径加 `/S`，独立等到卸载子进程完成，而非只看启动器退出码。两次运行均移除程序、快捷方式和 HKCU 卸载项，未发现对应 HKLM 项。4 个合成文件逐个验 SHA-256：词本、设置、程序子目录中的清单外文件、清单内同名且等长的改写文件。 |
| 冻结和旧入口：通过 | `C:\Users\Deeer\Desktop\美剧字幕翻译\.pilot\polish-freeze-20261127-124604-ecae8750\source.zip`，SHA-256 `8aa81a38dd468d975bc4cf3ad0c89b736fe28a42680bbd1da63db66ca98b1786`。复核 316 项冻结文件与当前工作字节一致，其中上游 307 项。仅隔离副本改两个 WPF 调用点并注入五个 UI 文件；无注入旧入口编译通过。 |

回归用合成媒体、临时词库、静音 WPF 播放和模拟 Anki。未验真实桌面播放器交互、文件选择框、实际扬声器、物理显示器 DPI 切换、安装图形向导、真实 Anki、手机或干净机器。快捷方式只读取，不点击。安装验收用显式隔离路径。用实际卸载器的字节一致副本（NSIS `_?=`）取得真实卸载退出码，另测注册入口的静默子进程及最终文件状态。未覆盖全部卷、ACL、磁盘写满或开始菜单重定向组合。

## 本机离线复核

以下用 PowerShell 7.4+。清理后保留冻结源码、正式安装包、ZIP 和报告；重复的解包件与历史构建目录已移除。从最终 ZIP 完整解压到新目录，再按提示填入实际解压路径。快照与应用产物的目录不能互换；`-NoInject` 产物仅用于旧入口编译验证。

```powershell
$root = 'C:\Users\Deeer\Desktop\美剧字幕翻译'
$frozen = "$root\.pilot\polish-freeze-20261127-124604-ecae8750"
$package = Read-Host '用于回归的最终 ZIP 完整解压目录'
$delivery = "$root\交付\LLPlayer-Anki-0.3.0-polish.1-20261127-125305-144e1786"
& "$root\packaging\build\Freeze-PolishSource.ps1" -VerifySnapshot $frozen
& "$root\packaging\checks\Invoke-PolishRegression.ps1" -Package $package
& "$root\packaging\ui-checks\Invoke-OffscreenUiQa.ps1" -Package $package
& "$root\packaging\installer\Test-Installer.ps1" -VerifyArtifactsOnly `
  -InstallerPath "$delivery\Setup.exe" `
  -InstallerSha256 '543019cd717479688eb0cc04fe7f6655261986425dd6556cc5e136afe1dd63ff' `
  -ZipPath "$delivery\LLPlayer-Anki-0.3.0-polish.1-win-x64.zip" `
  -ManifestPath "$delivery\payload-manifest.json"
```

需要重建时，原发布脚本用 `-Snapshot $frozen` 生成新的隔离构建目录。截音工具取自保留的中文字幕错时补配版，并仍校验固定 SHA-256；也可用 `-ClipFfmpeg` 指定从最终 ZIP 解压出的同一文件。安装器源码固定本次文件清单与本机 NSIS 3.12 编译器。`Build-Installer.ps1` 接收 `-PayloadRoot`、`-ManifestPath`、`-RunDirectory`；运行目录是工作区 `.pilot` 的直接子目录。本机安装测试还须显式提供新隔离目录、预期账户及其 Programs 路径；不使用已有安装位置。

## 发布边界

当前文件仅供本机验收。上传安装器和免安装 ZIP 前，应完成：

- 为实际分发的版本准备对应源码、构建与安装脚本、修改说明及 LLPlayer GPL 文本。基线链接不能替代本项目的修改源码。
- 本机实测：`FFmpeg/*.dll` 的版本标记均为 `n8.0-16-gd8605a6b55-20250925`；`ClipTools/ffmpeg.exe -version` 报告 `9.0.2-essentials_build-www.gyan.dev`，构建参数含 `--enable-gpl --enable-version3 --enable-static`。仍须分别补齐两套构建的对应源码和适用许可，并核对 ECDICT 数据、运行时依赖及包内媒体的再分发材料。
- 在干净的 Windows x64 环境验收启动、安装及卸载。候选安装器未签名；正式发布前处理签名和下载说明。
- 从已评审的 `main` 提交创建版本标签。先以草稿上传经复验的安装器、免安装 ZIP、`SHA256SUMS.txt`、对应源码和许可资料，再核对下载后的 SHA-256 和发布说明。GitHub 自动生成的源码压缩包不包含固定提交的上游源码，不能单独作为对应源码交付。
