# Atlumina

> 原名 MyAlbum — 基于 WinUI 3 / Windows App SDK 的 Windows 本地专业相册管理工具。

专为相机摄影场景设计（主力测试：Sony A6700 / RX100 VII 的 ARW 照片），本地优先、无云端依赖，支持海量 RAW 文件的高效浏览、GPS 地图、EXIF 编辑与本地 AI 分析。

## 功能亮点

- **高性能 RAW 浏览** — WIC 提取 RAW 内置预览帧 + 分级加载（256px 缩略图 / 1600px 预览 / 原图深解），三级缓存（LRU 内存 → SQLite 索引 + WebP 磁盘缓存 → 原文件）
- **三栏布局** — 左栏（文件夹树 / 相机 / 评分 / 标签 / 地点）、中栏（网格 / 时间线视图，日-月-年分组，Ctrl+滚轮缩放）、右栏（大图预览 + EXIF 信息 + 星级 + 标签编辑），左右栏可收起
- **时间线刻度尺** — Windows 11 照片应用风格，悬停/拖拽精准定位到日，年份按实际照片分布分段
- **全屏查看器** — 键盘导航、缩放、0-5 星快速评分
- **EXIF 编辑** — 基于 ExifTool CLI 的批量写入（拍摄时间 / 相机型号 / 星级 / GPS），自动探测并支持一键下载安装 ExifTool
- **地图视图** — 内置 Leaflet + Supercluster，GPS 照片动态聚类，支持国内外瓦片源切换与 GCJ-02 校正；时光机模式沿大圆曲线按日期游览
- **GPS 工具** — GPX 轨迹导入自动补全坐标、锚点拖拽定位、库内 GPS 写回源文件、位置自动反解（离线城市 + 在线反编码）
- **本地 AI**（DirectML，可选） — pHash 近重复检测、模糊/低质量照片识别、人脸检测与聚类、语义搜图（MobileCLIP）、场景自动打标（MobileNet）、美学评分（NIMA）、物体检测（YOLO）、主色调分析
- **整理工具** — 去重（SHA-256 + pHash）、格式清理、拍摄时间修复、批量重命名/导出、「在 Camera Raw 中编辑」
- **数据安全** — SQLite WAL + 自动备份/恢复、外部删除检测与冗余清理、原始照片保护开关（可禁用一切写入源文件的操作）
- **打包发布** — Inno Setup 安装包，unpackaged 自包含部署，VC++ / WebView2 运行时联动安装

## 技术栈

| 层 | 技术 |
|---|---|
| 框架 | WinUI 3 / Windows App SDK 2.3.1 / .NET 10（`net10.0-windows10.0.26100.0`） |
| UI | MVVM（CommunityToolkit.Mvvm）、Fluent 2 / Mica 材质 |
| 数据 | SQLite（Microsoft.Data.Sqlite，WAL）、JSON 设置持久化 |
| 元数据 | MetadataExtractor（只读）、ExifTool CLI（写入） |
| 解码 | Windows Imaging Component（WIC） |
| AI | Microsoft.ML.OnnxRuntime.DirectML（NPU → GPU → CPU 自动降级） |
| 地图 | WebView2 + Leaflet + Supercluster（离线捆绑） |

## 工程结构

```
MyAlbum.slnx
├─ src/MyAlbum.App      # WinUI 3 应用（unpackaged + WindowsAppSDKSelfContained）
├─ src/MyAlbum.Core     # 纯类库：模型 / SQLite / EXIF / 缩略图 / 导入 / 监听
├─ src/MyAlbum.Tools    # 控制台测试工具（--scan / --watch / --dump / --tags / --filtertest 等）
├─ installer/           # Inno Setup 脚本（Atlumina.iss）
├─ docs/                # 项目介绍.md（需求规格）+ 项目进度.md（进度与踩坑记录）
├─ test/resource        # 测试样张（ARW / HIF / JPG）
└─ test/nested          # 嵌套子文件夹测试
```

## 构建与运行

要求：.NET 10 SDK、Windows 10.0.26100（或更高）SDK、Rider 2026.2（或 Visual Studio）。

```powershell
# 构建（应 0 警告 0 错误）
dotnet build MyAlbum.slnx -c Debug

# 直接运行（unpackaged + 自包含）
dotnet run --project src/MyAlbum.App

# 控制台测试工具
dotnet run --project src/MyAlbum.Tools -- <目录> <db路径> [--scan|--watch|--dump|--tags|--filtertest]

# 打包安装包
.\build-installer.ps1
```

> 运行前先停掉残留的 `MyAlbum.App` 进程，否则 DLL 被锁会导致构建失败。

## 数据位置

| 内容 | 路径 |
|---|---|
| 应用数据根目录 | `%LOCALAPPDATA%\Atlumina` |
| 数据库 | `%LOCALAPPDATA%\Atlumina\myalbum.db` |
| 缩略图缓存 | `%LOCALAPPDATA%\Atlumina\cache\thumbs` |
| 设置 | `%LOCALAPPDATA%\Atlumina\settings.json` |
| 备份 | `%LOCALAPPDATA%\Atlumina\backups` |
| ExifTool | `%LOCALAPPDATA%\Atlumina\tools` |
| AI 模型 | `%LOCALAPPDATA%\Atlumina\models` |
| WebView2 数据 | `%LOCALAPPDATA%\Atlumina\WebView2` |
| 崩溃日志 | `%TEMP%\Atlumina\crash.log` |

> 卸载时 `%LOCALAPPDATA%\Atlumina` 数据保留，重装零迁移。

## 说明

- 内置地图瓦片源离线可用（Leaflet / Supercluster 已捆绑），在线瓦片与反向地理编码需联网
- AI 模型默认不随包分发，首次使用在 AI 页下载（约 14 MB ~ 64 MB）
- 详细需求规格见 `docs/项目介绍.md`，开发进度与踩坑记录见 `docs/项目进度.md`