# LLPlayer 原声卡 · Windows x64 本机候选

批次 `20261127-125305-144e1786`。主程序版本 `0.3.0+da101d87681bb4d1d87a59884bca95043dd1158b`，作者 `umlx5h`。

## 交付与使用

交付目录：`C:\Users\Deeer\Desktop\美剧字幕翻译\交付\LLPlayer-Anki-0.3.0-polish.1-20261127-125305-144e1786`。

| 文件 | 用途 | SHA-256 |
|---|---|---|
| `Setup.exe` | NSIS 每用户安装。不要求提升为管理员，不自动启动播放器。 | `ce9e7c36771c64b7681e3105d6a8d4ce7e651f9153b5c1a6e722d98787cfcd04` |
| `LLPlayer-Anki-0.3.0-polish.1-win-x64.zip` | 免安装版。完整解压到新目录，再打开其中的 `LLPlayer.exe`。 | `2beedf23c48e9403f716a822822432193c6db9f5fec728f3e1d2634c2e0c3e1d` |
| `payload-manifest.json` | 同一干净文件树 972 个应用文件的相对路径、长度和摘要。NSIS 卸载器为安装版附加项，不在 ZIP 内。 | `557137312a4af704083b6c5850b89ab047ae436e7686a13aa77681c8055e79ee` |
| `SHA256SUMS.txt` | 上列三个完整文件的校验清单。 | — |

默认安装目录为 `%LOCALAPPDATA%\Programs\LLPlayer-Anki-Polish-144e1786`；Windows「已安装的应用」显示 `LLPlayer Original Review`。如果需要回退，正常关闭新版，通过该卸载项移除安装版，再打开原交付包。不要覆盖旧包；不要清理 `%LOCALAPPDATA%\LLPlayer\PrivateReview`。程序设置仍在各自程序目录，卸载不移除未列入清单的文件。

## 实测证据

| 检查 | 结果和记录 |
|---|---|
| 相同探针：旧包 7/8 | `C:\Users\Deeer\Desktop\美剧字幕翻译\.pilot\polish-regression-e956a620c7fb\report.md`。旧包部分字段改动不锁定试听。 |
| 相同探针：新版 8/8 | `C:\Users\Deeer\Desktop\美剧字幕翻译\.pilot\polish-regression-0df32e5daca3\report.md`。实际包 `LLPlayer.dll` SHA-256 `d8f448ec739ae9202d05ee989cf83c6a16a13735c7c2d98760b46de606e384b4`。试听实测从 0.117 秒推进到 0.226 秒（总长 1.123 秒）；取消清理临时音频，词库字节不变。 |
| 离屏专项：8/8 | `C:\Users\Deeer\Desktop\美剧字幕翻译\packaging\ui-checks\.pilot\ui-qa-20261127-131941-48c19c4ea6dd\report.json`。450 × 430 DIP、200%／235%、四态、写前取消、损坏偏好回退及显式选卡优先；同目录有四张 PNG。 |
| 每用户静默安装和卸载：通过 | `C:\Users\Deeer\Desktop\美剧字幕翻译\.pilot\polish-install-check-144e1786-2e13db4c\report.json`。未提权账户，实际安装和卸载均返回 0；972 个文件与 ZIP 路径、长度和 SHA-256 一致。读取中文路径快捷方式，目标及工作目录正确，未观察到自动启动。卸载移除本批次程序、快捷方式及 HKCU 卸载项，未发现对应 HKLM 项。合成词本、设置及一份清单外文件逐字节保留。两个退出码及用时另见同目录的 `install.log` 与 `uninstall.log`。 |
| 冻结和旧入口：通过 | `C:\Users\Deeer\Desktop\美剧字幕翻译\.pilot\polish-freeze-20261127-124604-ecae8750\source.zip`，SHA-256 `8aa81a38dd468d975bc4cf3ad0c89b736fe28a42680bbd1da63db66ca98b1786`。复核 316 项冻结文件与当前工作字节一致，其中上游 307 项。仅隔离副本改两个 WPF 调用点并注入五个 UI 文件；无注入旧入口编译通过。 |

回归用合成媒体、临时词库、静音 WPF 播放和模拟 Anki。未验真实桌面播放器交互、文件选择框、实际扬声器、物理显示器 DPI 切换、安装图形向导、真实 Anki、手机或干净机器。快捷方式只读取，不点击。安装验收用显式隔离路径，卸载用实际生成卸载器的字节一致副本（NSIS `_?=` 隔离调用）。

## 本机离线复核

以下用 PowerShell 7.4+。快照与应用产物的目录不能互换；`-NoInject` 产物只用于旧入口编译验证，不交付。

```powershell
$root = 'C:\Users\Deeer\Desktop\美剧字幕翻译'
$frozen = "$root\.pilot\polish-freeze-20261127-124604-ecae8750"
$build = "$root\.pilot\polish-release-20261127-125305-144e1786"
$delivery = "$root\交付\LLPlayer-Anki-0.3.0-polish.1-20261127-125305-144e1786"
& "$root\packaging\build\Freeze-PolishSource.ps1" -VerifySnapshot $frozen
& "$root\packaging\build\Publish-PolishOffline.ps1" -VerifyPayload $build
& "$root\packaging\checks\Invoke-PolishRegression.ps1" -Package "$build\payload"
& "$root\packaging\ui-checks\Invoke-OffscreenUiQa.ps1" -Package "$build\payload"
& "$root\packaging\installer\Test-Installer.ps1" -VerifyArtifactsOnly `
  -InstallerPath "$delivery\Setup.exe" `
  -InstallerSha256 'ce9e7c36771c64b7681e3105d6a8d4ce7e651f9153b5c1a6e722d98787cfcd04' `
  -ZipPath "$delivery\LLPlayer-Anki-0.3.0-polish.1-win-x64.zip" `
  -ManifestPath "$delivery\payload-manifest.json"
```

需要重建时，原发布脚本用 `-Snapshot $frozen` 生成新的隔离构建目录。安装器源码固定本次文件清单与本机 NSIS 3.12 编译器。`Build-Installer.ps1` 接收 `-PayloadRoot`、`-ManifestPath`、`-RunDirectory`；运行目录是工作区 `.pilot` 的直接子目录。本机安装测试还须显式提供新隔离目录、预期账户及其 Programs 路径；不使用已有安装位置。

## 发布边界

仅用于本机验收，不对外分发。可信签名、完整对应源码与许可材料、干净机原生依赖尚未核齐。根仓库仅有本地提交与 `pre-polish` 回退标签；没有 `origin`，待推远端。交付核验和记录落盘后才请求正常关机，不强制关闭应用。
