# AGENTS.md

MyAlbum — 基于 WinUI 3 / Windows App SDK 的 Windows 本地专业相册管理工具（主力场景：Sony A6700 / RX100 VII 的 ARW 相机照片）。

## 必读文档（先读这两个再动手）

- **`docs/项目介绍.md`** — 完整需求规格（PROJECT_SPEC），含三栏布局定稿与「在 Camera Raw 中编辑」需求
- **`docs/项目进度.md`** — 当前进度、已决策、踩坑记录、下一步计划（改代码前务必看「踩坑记录」，避免重蹈覆辙）

## 工程要点

- 解决方案 `MyAlbum.slnx`，三个项目：
  - `src/MyAlbum.App` — WinUI 3 应用（unpackaged + `WindowsAppSDKSelfContained`，`dotnet run` 直接运行）
  - `src/MyAlbum.Core` — 模型 / SQLite / EXIF / 缩略图 / 导入 / 监听（无 UI）
  - `src/MyAlbum.Tools` — 控制台测试工具（`--scan / --watch / --dump / --tags / --filtertest`）
- TFM：`net10.0-windows10.0.26100.0`；Rider 2026.2 开发
- **构建/验证**：`dotnet build MyAlbum.slnx -c Debug`（应 0 警告 0 错误）；运行 `dotnet run --project src/MyAlbum.App`
- 测试样张：`test/resource`（HIF/ARW/JPG）、`test/nested`（子文件夹）

## 网络

- 下载 GitHub / HuggingFace 以及其他工具时请挂上梯子：Clash 代理端口 `7897`（如 `http://127.0.0.1:7897`）

## 约定

- 遵循现有代码风格：MVVM（CommunityToolkit.Mvvm 源生成 `[ObservableProperty]`/`[RelayCommand]`）、Service 注入（`App.Services` 的 DI 容器）
- 主 UI 线程严禁 I/O / 解码 / AI 推理（全部异步到后台）
- 改完必须构建验证；涉及运行时问题看 `%TEMP%\Atlumina\crash.log`
- 运行前先停掉残留的 `MyAlbum.App` 进程（否则 DLL 被锁，构建失败）
