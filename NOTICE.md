# 上游与许可说明

- 播放器上游：[umlx5h/LLPlayer](https://github.com/umlx5h/LLPlayer)，本项目构建脚本要求的基线为 [`da101d8`](https://github.com/umlx5h/LLPlayer/tree/da101d87681bb4d1d87a59884bca95043dd1158b)。该上游版本附有 [GNU GPL v3 许可证文本](https://github.com/umlx5h/LLPlayer/blob/da101d87681bb4d1d87a59884bca95043dd1158b/LICENSE)。
- 本项目的[上游补丁](packaging/build/LLPlayer-da101d8.patch)修改了字幕操作、单句回听、播放进度与复习入口，重建时保留上游的作者和许可证文本。构建入口见[开发与构建](docs/DEVELOPMENT.md#从干净克隆构建-windows-播放器)。
- FFmpeg、ECDICT、Anki/AnkiConnect、Cloudflare 适配依赖等各自适用上游许可。它们在文档里的名称或链接不改变其许可。本仓库的跟踪文件不包含本机候选二进制及其完整分发材料。
- 此说明不授予本仓库原创部分新的许可。本机候选对外分发前，仍需核对依赖与构建配置、完整对应源码、修改说明和应交付的许可证文件。不要把本仓库当成完整的源码合规交付。
