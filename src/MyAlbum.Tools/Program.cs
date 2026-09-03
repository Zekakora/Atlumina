using MyAlbum.Core.Data;
using MyAlbum.Core.Models;
using MyAlbum.Core.Services;

if (args.Length < 2)
{
    Console.WriteLine("用法: MyAlbum.Tools <图片目录> <数据库文件路径> [--tags] [--dump] [--scan] [--watch] [--exiftool] [--dedup] [--gpsgroup] [--rename] [--export] [--ai] [--deep]");
    return 1;
}

string folder = Path.GetFullPath(args[0]);
string dbPath = Path.GetFullPath(args[1]);
bool dumpTags = args.Contains("--tags");
bool dumpDb = args.Contains("--dump");
bool doScan = args.Contains("--scan");
bool doWatch = args.Contains("--watch");

if (!Directory.Exists(folder))
{
    Console.WriteLine($"目录不存在: {folder}");
    return 2;
}

var db = new PhotoDatabase(dbPath);
await db.InitializeAsync();
var reader = new MetadataReaderService();
var thumbs = new ThumbnailService(Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "cache", "thumbs"));
var library = new LibraryService(db, reader, thumbs);

if (args.Contains("--backup"))
{
    int idx = Array.IndexOf(args, "--backup");
    var dir = idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    if (string.IsNullOrWhiteSpace(dir))
    {
        Console.WriteLine("用法: MyAlbum.Tools <目录> <db路径> --backup <备份目录>");
        return 1;
    }
    var backup = new DatabaseBackupService(db);
    var path = await backup.BackupAsync(dir, "pre_refactor");
    Console.WriteLine("备份完成: " + path);
    return 0;
}

if (args.Contains("--backuptest"))
{
    return await RunBackupTestAsync(db);
}

if (args.Contains("--exiftool"))
{
    return await RunExifToolTestAsync(folder, db);
}

if (args.Contains("--dedup"))
{
    return await RunDedupTestAsync(db, thumbs);
}

if (args.Contains("--namededup"))
{
    return await RunNameDedupTestAsync(db, thumbs);
}

if (args.Contains("--formatcleanup"))
{
    return await RunFormatCleanupTestAsync(db);
}

if (args.Contains("--ai"))
{
    return await RunAiTestAsync(db);
}

if (args.Contains("--deep"))
{
    return await RunDeepTestAsync(db);
}

if (args.Contains("--geotest"))
{
    return await RunGeoTestAsync(db);
}

if (args.Contains("--gpsstats"))
{
    return await RunGpsStatsAsync(db);
}

if (args.Contains("--place"))
{
    return await RunPlaceTestAsync(db);
}

if (args.Contains("--migratetest"))
{
    return await RunMigrateTestAsync(db);
}

if (args.Contains("--placeinc"))
{
    return await RunPlaceIncrementalTestAsync(db);
}

if (args.Contains("--placereuse"))
{
    return await RunPlaceReuseTestAsync(db);
}

if (args.Contains("--rescan"))
{
    return await RunRescanPreserveTestAsync(db);
}

if (args.Contains("--maintenance"))
{
    return await RunMaintenanceTestAsync(db);
}

if (args.Contains("--llmretry"))
{
    return await RunLlmRetryTestAsync(db);
}

if (args.Contains("--quality"))
{
    return await RunQualityTestAsync(db);
}

if (args.Contains("--thumbprobe"))
{
    return await RunThumbProbeAsync(db, reader);
}

if (args.Contains("--tag"))
{
    return await RunTagTestAsync(db);
}

if (args.Contains("--face"))
{
    return await RunFaceTestAsync(db);
}

if (args.Contains("--testgps"))
{
    return await RunTestGpsAsync(db);
}

if (args.Contains("--gpsgroup"))
{
    return await RunGpsGroupTestAsync(db);
}

if (args.Contains("--rename"))
{
    return await RunRenameTestAsync(db);
}

if (args.Contains("--export"))
{
    return await RunExportTestAsync(db);
}

if (dumpDb)
{
    Console.WriteLine($"库内共 {await db.GetPhotoCountAsync()} 条：");
    foreach (var p in await db.GetPhotosAsync(100))
    {
        Console.WriteLine($"  #{p.Id} {p.FileName} [{p.Kind}] {p.TakenAtUtc:yyyy-MM-dd HH:mm:ss} {p.CameraMake} {p.CameraModel} ISO={p.Iso} {p.ShutterSpeed} f/{p.Aperture:0.0} {p.FocalLengthMm:0}mm {p.Width}x{p.Height} GPS={p.GpsLatitude:0.00000},{p.GpsLongitude:0.00000} rating={p.Rating} missing={p.IsMissing} thumb={p.ThumbnailCachePath is not null}");
    }
    return 0;
}

if (dumpTags)
{
    foreach (var file in LibraryService.EnumerateImages(folder))
    {
        Console.WriteLine($"=== {Path.GetFileName(file)} ===");
        foreach (var (dirTag, value) in MetadataReaderService.ReadAllTags(file))
        {
            Console.WriteLine($"  {dirTag} = {value}");
        }
        Console.WriteLine();
    }
    return 0;
}

if (doWatch)
{
    return await RunWatchTestAsync(folder, db, library);
}

if (args.Contains("--hidetest"))
{
    var nested = @"D:\Project Data\MyAlbum\test\nested";
    await library.ScanFolderAsync(nested);
    Console.WriteLine($"库内总数(含 nested): {await db.GetPhotoCountAsync()}");
    await db.SetFolderHiddenAsync(nested, true);
    Console.WriteLine($"隐藏 nested 后总数: {await db.GetPhotoCountAsync()}");
    Console.WriteLine("文件夹 IsHidden:");
    foreach (var f in await db.GetFoldersAsync())
    {
        Console.WriteLine($"  {f.Path}  hidden={f.IsHidden}");
    }
    await db.SetFolderHiddenAsync(nested, false);
    Console.WriteLine($"恢复后总数: {await db.GetPhotoCountAsync()}");
    return 0;
}

if (args.Contains("--filtertest"))
{
    Console.WriteLine("=== 相机列表 ===");
    foreach (var (model, count) in await db.GetCameraModelsAsync())
    {
        Console.WriteLine($"  {model}: {count}");
    }
    Console.WriteLine("=== 目录计数 ===");
    foreach (var (dir, count) in await db.GetDirectoryCountsAsync())
    {
        Console.WriteLine($"  {dir}: {count}");
    }
    Console.WriteLine("=== 按相机筛选 ===");
    var cam = (await db.GetCameraModelsAsync()).FirstOrDefault().Model;
    if (cam is not null)
    {
        var photos = await db.QueryPhotosAsync(cameraModel: cam);
        Console.WriteLine($"  {cam} -> {photos.Count} 张");
    }
    Console.WriteLine("=== 标签测试 ===");
    var first = (await db.GetPhotosAsync(1)).FirstOrDefault();
    if (first is not null)
    {
        await db.AddTagAsync(first.Id, "测试标签", isAuto: false);
        await db.AddTagAsync(first.Id, "自动标签A", isAuto: true);
        Console.WriteLine("  手动标签: " + string.Join(",", (await db.GetTagsWithCountsAsync(false)).Select(t => $"{t.Name}({t.Count})")));
        Console.WriteLine("  AI 标签: " + string.Join(",", (await db.GetTagsWithCountsAsync(true)).Select(t => $"{t.Name}({t.Count})")));
        Console.WriteLine("  该照片标签: " + string.Join(",", (await db.GetPhotoTagsAsync(first.Id)).Select(t => t.Name)));
        var tagged = await db.QueryPhotosAsync(tag: "测试标签");
        Console.WriteLine($"  按标签筛选 '测试标签' -> {tagged.Count} 张");
        await db.RemoveTagAsync(first.Id, "测试标签");
        Console.WriteLine("  删除后: " + (await db.GetTagsWithCountsAsync(false)).Count);
        await db.RemoveTagAsync(first.Id, "自动标签A");
    }
    Console.WriteLine("=== 智能相册往返 ===");
    await db.UpsertSmartAlbumAsync(new MyAlbum.Core.Models.SmartAlbum
    {
        Name = "测试相册",
        FilterJson = new MyAlbum.Core.Models.LibraryFilter { CameraModel = cam }.ToJson(),
        CreatedUtc = DateTime.UtcNow,
    });
    foreach (var a in await db.GetSmartAlbumsAsync())
    {
        Console.WriteLine($"  #{a.Id} {a.Name} filter={a.FilterJson}");
        await db.DeleteSmartAlbumAsync(a.Id);
    }
    Console.WriteLine("  删除后: " + (await db.GetSmartAlbumsAsync()).Count);
    return 0;
}

if (doScan)
{
    var progress = new Progress<ScanProgress>(p =>
    {
        if (p.Processed % 10 == 0 || p.Processed == p.TotalFiles)
        {
            Console.WriteLine($"  [{p.Processed}/{p.TotalFiles}] 新增 {p.Indexed} 跳过 {p.Skipped} 失败 {p.Failed}  {Path.GetFileName(p.CurrentFile)}");
        }
    });
    var result = await library.ScanFolderAsync(folder, progress);
    Console.WriteLine($"\n扫描完成: 共 {result.TotalFiles}，新增 {result.Indexed}，跳过 {result.Skipped}，失败 {result.Failed}，缺失标记 {result.MarkedMissing}，库内 {await db.GetPhotoCountAsync()}");
    return result.Failed > 0 ? 3 : 0;
}

// Default: brute-force index of every file.
string[] supported = [".arw", ".jpg", ".jpeg", ".hif", ".heic", ".heif", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".dng", ".cr2", ".cr3", ".nef", ".raf"];
var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
    .Where(f => supported.Contains(Path.GetExtension(f).ToLowerInvariant()))
    .OrderBy(f => f)
    .ToList();

Console.WriteLine($"找到 {files.Count} 个候选图片文件，开始索引...\n");
int ok = 0, fail = 0;
foreach (var file in files)
{
    try
    {
        var p = reader.Read(file);
        p.ThumbnailCachePath = await thumbs.GetOrCreateThumbnailAsync(p);
        await db.UpsertPhotoAsync(p);
        ok++;
        Console.WriteLine($"[OK]   {Path.GetFileName(file)} ({p.Kind})");
        Console.WriteLine($"       拍摄时间: {p.TakenAtUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}  相机: {p.CameraMake} {p.CameraModel}  镜头: {p.LensModel}");
        Console.WriteLine($"       ISO: {p.Iso?.ToString() ?? "N/A"}  快门: {p.ShutterSpeed ?? "N/A"}  光圈: f/{p.Aperture?.ToString("0.0") ?? "N/A"}  焦距: {p.FocalLengthMm?.ToString("0.0") ?? "N/A"}mm");
        Console.WriteLine($"       尺寸: {p.Width?.ToString() ?? "N/A"}x{p.Height?.ToString() ?? "N/A"}  GPS: {FormatGps(p)}");
    }
    catch (Exception ex)
    {
        fail++;
        Console.WriteLine($"[FAIL] {Path.GetFileName(file)}: {ex.Message}");
    }
}
Console.WriteLine($"\n索引完成: 成功 {ok}，失败 {fail}，库内总数 {await db.GetPhotoCountAsync()}");
return fail > 0 ? 3 : 0;

static string FormatGps(PhotoRecord p)
{
    if (p.GpsLatitude is null || p.GpsLongitude is null) return "无";
    return $"{p.GpsLatitude:0.00000}, {p.GpsLongitude:0.00000}";
}

static async Task<int> RunWatchTestAsync(string folder, PhotoDatabase db, LibraryService library)
{
    var testDir = Path.Combine(Path.GetDirectoryName(folder) ?? ".", "_watchtest");
    Directory.CreateDirectory(testDir);

    using var watcher = new FolderWatcherService(library, db);
    watcher.WatchFolder(testDir);
    Console.WriteLine($"监听 {testDir}，测试 增/删/改名...");

    // Copies happen AFTER the watcher starts so they arrive as Created events.
    foreach (var file in LibraryService.EnumerateImages(folder))
    {
        File.Copy(file, Path.Combine(testDir, Path.GetFileName(file)), true);
    }
    await Task.Delay(4000);
    var all = Directory.GetFiles(testDir, "*.*");
    File.Delete(all[0]);
    Console.WriteLine($"  删除: {Path.GetFileName(all[0])}");

    await Task.Delay(4000);
    var renamed = all[^1];
    var newName = Path.Combine(testDir, "RENAMED" + Path.GetExtension(renamed));
    File.Move(renamed, newName);
    Console.WriteLine($"  改名: {Path.GetFileName(renamed)} -> RENAMED{Path.GetExtension(renamed)}");

    await Task.Delay(4000);
    Console.WriteLine($"\n监听测试结束。该目录库内记录：");
    foreach (var p in await db.GetPhotosByDirectoryAsync(testDir))
    {
        Console.WriteLine($"  {p.FileName} missing={p.IsMissing}");
    }
    return 0;
}

static async Task<int> RunTestGpsAsync(PhotoDatabase db)
{
    // Writes sample GPS coords (Shanghai area) for every photo to verify the map rendering.
    var photos = await db.GetPhotosAsync(500);
    var items = new List<(long, double, double, double?)>();
    double baseLat = 31.2304, baseLon = 121.4737;
    for (int i = 0; i < photos.Count; i++)
    {
        items.Add((photos[i].Id, baseLat + i * 0.001, baseLon + i * 0.001, null));
    }
    await db.BulkSetGpsAsync(items);
    Console.WriteLine($"已为 {items.Count} 张照片写入测试 GPS（上海城区）。");
    return 0;
}

static async Task<int> RunExifToolTestAsync(string folder, PhotoDatabase db)
{
    var writer = new ExifWriterService();
    if (!writer.IsAvailable)
    {
        Console.WriteLine($"未找到 ExifTool。请安装到 {ExifWriterService.SuggestedInstallDir}（文件名 exiftool.exe），或加入 PATH。");
        return 3;
    }
    Console.WriteLine($"ExifTool: {writer.FindExifTool()}");

    var photos = await db.GetPhotosByDirectoryPrefixAsync(folder);
    if (photos.Count == 0)
    {
        Console.WriteLine("库内无照片，先运行 --scan。");
        return 2;
    }
    var sample = photos[0];
    Console.WriteLine($"对 {sample.FileName} 试写评分=3 并回读验证：");
    var edit = new ExifEditOptions
    {
        FilePath = sample.FilePath,
        Rating = 3,
    };
    Console.WriteLine("  参数: " + string.Join(" ", ExifWriterService.BuildArgs(edit)));
    var result = await writer.WriteBatchAsync([edit]);
    var r = result[0];
    Console.WriteLine($"  {(r.Success ? "OK" : "失败")}: {r.Message}");
    if (r.Success)
    {
        var refreshed = new MetadataReaderService().Read(sample.FilePath);
        Console.WriteLine($"  回读评分 = {refreshed.Rating}");
    }
    return r.Success ? 0 : 3;
}

static async Task<int> RunDedupTestAsync(PhotoDatabase db, ThumbnailService thumbs)
{
    var service = new DuplicateService(thumbs);
    var photos = await db.GetPhotosAsync(500);
    Console.WriteLine($"对 {photos.Count} 张照片执行去重分析…");
    var groups = service.FindDuplicates(photos);
    if (groups.Count == 0)
    {
        Console.WriteLine("未发现重复组（样张仅 3 张，预期为空）。");
        return 0;
    }
    foreach (var g in groups)
    {
        Console.WriteLine($"  {(g.IsExact ? "精确" : $"近重复 pHash={g.PhashDistance}")} 组: {string.Join(" | ", g.Photos.Select(p => p.FileName))}");
        Console.WriteLine($"    建议保留: {string.Join(" + ", g.KeepPaths.Select(Path.GetFileName))}");
    }
    Console.WriteLine($"共 {groups.Count} 组。pHash 已回写 PhotoRecord 对象（内存）；批量落库由调用方决定。");
    return 0;
}

static async Task<int> RunNameDedupTestAsync(PhotoDatabase db, ThumbnailService thumbs)
{
    var service = new DuplicateService(thumbs);
    var photos = await db.GetPhotosAsync(500);
    Console.WriteLine($"对 {photos.Count} 张照片按文件名（跨文件夹）分组…");
    var groups = service.FindNameDuplicates(photos);
    if (groups.Count == 0)
    {
        Console.WriteLine("未发现同名文件跨文件夹重复。");
        return 0;
    }
    foreach (var g in groups)
    {
        Console.WriteLine($"组 {g.Stem}（{g.Occurrences.Count} 个位置）:");
        foreach (var o in g.Occurrences)
        {
            string marker = o.IsSuggestedKeep ? "✔ 建议保留" : "  可删除  ";
            Console.WriteLine($"  {marker} {o.Directory}  [{o.FormatsText}]  {string.Join(" | ", o.Photos.Select(p => p.FileName))}");
        }
    }
    Console.WriteLine($"共 {groups.Count} 组（未删除任何文件）。");
    return 0;
}

static async Task<int> RunFormatCleanupTestAsync(PhotoDatabase db)
{
    var service = new FormatCleanupService(db);
    var photos = await db.GetPhotosAsync(500);
    var groups = service.GroupByPhoto(photos);
    Console.WriteLine($"对 {photos.Count} 张照片按（文件夹 + 主文件名）分组，共 {groups.Count} 组（≥2 个文件）：");
    foreach (var g in groups)
    {
        string formats = string.Join(" + ", g.Select(p => p.Extension.TrimStart('.').ToUpperInvariant()));
        Console.WriteLine($"  {g[0].DirectoryPath}\\{Path.GetFileNameWithoutExtension(g[0].FileName)} : {formats}");
    }
    Console.WriteLine("（仅演示分组，未删除任何文件）");
    return 0;
}

static async Task<int> RunAiTestAsync(PhotoDatabase db)
{
    Console.WriteLine("=== AI 设备探测 ===");
    var probe = AiEngine.Probe();
    Console.WriteLine($"  NPU: {probe.NpuAvailable}  GPU: {probe.GpuAvailable}  最佳: {probe.BestName}");
    Console.WriteLine("  DirectML 可用: " + AiEngine.IsDirectMlAvailable);
    Console.WriteLine("  计算适配器 (DXCore):");
    foreach (var a in AiEngine.EnumerateAdapters())
    {
        Console.WriteLine($"    {(a.IsNpu ? "[NPU] " : "")}{a.Name}");
    }
    Console.WriteLine($"  已发现 ONNX 模型: {(AiEngine.DiscoverModels().Length == 0 ? "无" : string.Join(", ", AiEngine.DiscoverModels()))}");
    Console.WriteLine($"  模型目录: {AiEngine.ModelsDirectory}");
    Console.WriteLine();

    var vision = new VisionAnalysisService(db);
    var progress = new Progress<(int Done, int Total, string File)>(p =>
    {
        if (p.Done % 10 == 0 || p.Done == p.Total)
        {
            Console.WriteLine($"  [{p.Done}/{p.Total}] 已分析 {p.File}");
        }
    });
    Console.WriteLine("=== 视觉分析（pHash + 模糊检测）===");
    var result = await vision.AnalyzeLibraryAsync(progress);
    Console.WriteLine($"分析完成: 待分析 {result.Total}，成功 {result.Analyzed}，失败 {result.Failed}");
    if (vision.LastError is not null)
    {
        Console.WriteLine($"  最近错误: {vision.LastError}");
    }

    long analyzed = await db.CountAnalyzedPhotosAsync();
    var blurry = await db.GetBlurryPhotosAsync(VisionAnalysisService.BlurThreshold, 20);
    Console.WriteLine($"\n库内已分析: {analyzed} 张；模糊（BlurScore <= {VisionAnalysisService.BlurThreshold}）: {blurry.Count} 张");
    foreach (var p in blurry.Take(10))
    {
        Console.WriteLine($"  模糊 {p.BlurScore:0.0}  {p.FileName}");
    }

    var pending = await db.GetPhotosPendingVisionAsync(1);
    Console.WriteLine(pending.Count == 0 ? "\n全部照片已完成分析。" : "\n仍有照片待分析。");
    return 0;
}

static async Task<int> RunTagTestAsync(PhotoDatabase db)
{
    Console.WriteLine("=== MobileNet 场景自动打标 ===");
    var model = AiModelDownloader.MobileNet;
    if (!AiModelDownloader.IsInstalled(model))
    {
        Console.WriteLine($"模型未安装，开始下载 ({model.DisplayName})…");
        var dlProgress = new Progress<(long Received, long Total, string File)>(p =>
        {
            if (p.Received % 1048576 < 81920 || p.Received == p.Total)
            {
                Console.WriteLine($"  下载 {p.File}: {p.Received / 1048576.0:0.0}MB / {(p.Total > 0 ? p.Total / 1048576.0 : 0):0.0}MB");
            }
        });
        var downloader = new AiModelDownloader();
        var path = await downloader.DownloadAsync(model, dlProgress);
        if (path is null)
        {
            Console.WriteLine("下载失败。");
            return 3;
        }
        Console.WriteLine($"  模型就绪: {path}");
    }

    var tagger = new SceneTaggerService(db);
    var photos = await db.GetPhotosWithoutAutoTagsAsync(limit: int.MaxValue);
    Console.WriteLine($"待打标 {photos.Count} 张（跳过已有 AI 标签的照片）…");
    if (photos.Count == 0)
    {
        Console.WriteLine("全部照片都已打标。");
        return 0;
    }

    var progress = new Progress<(int Done, int Total, string File)>(p =>
    {
        if (p.Done % 5 == 0 || p.Done == p.Total)
        {
            Console.WriteLine($"  [{p.Done}/{p.Total}] {p.File}");
        }
    });
    var (tagged, failed) = await tagger.TagLibraryAsync(photos, progress);
    Console.WriteLine($"打标完成: 成功 {tagged}，失败 {failed}");
    if (tagger.LastError is not null)
    {
        Console.WriteLine($"  最近错误: {tagger.LastError}");
    }

    var autoTags = await db.GetTagsWithCountsAsync(isAuto: true);
    Console.WriteLine($"\n自动标签:");
    foreach (var t in autoTags.OrderByDescending(t => t.Count).Take(30))
    {
        Console.WriteLine($"  {t.Name} ({t.Count})");
    }
    return 0;
}

static async Task<int> RunFaceTestAsync(PhotoDatabase db)
{
    Console.WriteLine("=== 人脸检测 + 聚类 ===");
    foreach (var model in new[] { AiModelDownloader.YuNet, AiModelDownloader.ArcFace })
    {
        if (!AiModelDownloader.IsInstalled(model))
        {
            Console.WriteLine($"{model.DisplayName} 未安装（可运行 --face 前先手动下载模型到 {AiEngine.ModelsDirectory}）");
            return 3;
        }
    }
    Console.WriteLine($"YuNet + ArcFace 已就绪。");

    var service = new FaceClusteringService(db, new FaceService());
    var progress = new Progress<(int Done, int Total, string File)>(p =>
    {
        if (p.Done % 5 == 0 || p.Done == p.Total)
        {
            Console.WriteLine($"  [{p.Done}/{p.Total}] {p.File}");
        }
    });
    var (facesStored, people) = await service.AnalyzeLibraryAsync(incremental: false, progress);
    Console.WriteLine($"分析完成: 存储人脸 {facesStored}，人物簇 {people}");

    var clusters = await service.ClusterAsync();
    Console.WriteLine($"\n人物簇:");
    foreach (var c in clusters.Take(30))
    {
        Console.WriteLine($"  人物 {c.PersonId}: {c.FaceCount} 张脸 / {c.PhotoCount} 张照片  代表: {c.RepresentativePhoto}");
    }
    return 0;
}

static async Task<int> RunGpsGroupTestAsync(PhotoDatabase db)
{
    var service = new GpsGroupingService();
    var photos = await db.QueryPhotosAsync(limit: int.MaxValue);
    var threshold = TimeSpan.FromHours(3);
    var result = service.Group(photos, threshold);
    Console.WriteLine($"分组间隔 3 小时：带 GPS 锚点 {result.AnchorCount}，缺 GPS {result.GpnCount}，无拍摄时间 {result.NoTimePhotos.Count}");
    foreach (var g in result.Groups)
    {
        string kind = g.Kind == GpsGroupKind.Auto ? "可自动" : "需手动";
        string range = g.StartUtc is null ? "—" : $"{g.StartUtc:yyyy-MM-dd HH:mm} ~ {g.EndUtc:yyyy-MM-dd HH:mm}";
        Console.WriteLine($"[{kind}] {range}  锚点 {g.AnchorCount}  待设置 {g.GpnItems.Count}");
        foreach (var a in g.GpnItems.Take(5))
        {
            string pos = a.AssignedLat is null ? "无位置" : $"{a.AssignedLat:0.00000},{a.AssignedLon:0.00000}";
            string warn = a.NeedsReview ? " [间隔远]" : "";
            string seq = a.FilenameCircularDistance is { } d ? $" seq={d}" : "";
            Console.WriteLine($"    {a.Photo.FileName}  {a.Photo.TakenAtUtc:HH:mm:ss} -> {pos}{warn}{seq}");
        }
    }
    return 0;
}

static async Task<int> RunRenameTestAsync(PhotoDatabase db)
{
    var service = new BatchFileService(db);
    var photos = (await db.GetPhotosAsync(100)).Take(3).ToList();
    if (photos.Count == 0)
    {
        Console.WriteLine("库内无照片。");
        return 2;
    }
    Console.WriteLine("模板 {date}_{time}_{camera}_{index} 示例：");
    for (int i = 0; i < photos.Count; i++)
    {
        Console.WriteLine($"  {photos[i].FileName} -> {BatchFileService.BuildName("{date}_{time}_{camera}_{index}", photos[i], i)}");
    }
    Console.WriteLine("（仅预览，不实际改名。UI 集成时会执行真实改名。）");
    return 0;
}

static async Task<int> RunExportTestAsync(PhotoDatabase db)
{
    var service = new BatchFileService(db);
    var photos = (await db.GetPhotosAsync(100)).Take(3).ToList();
    if (photos.Count == 0)
    {
        Console.WriteLine("库内无照片。");
        return 2;
    }
    var target = Path.Combine(Path.GetTempPath(), "Atlumina_ExportTest");
    Directory.CreateDirectory(target);
    var results = await service.ExportBatchAsync(photos, target);
    foreach (var r in results)
    {
        Console.WriteLine($"  {(r.Success ? "OK" : "失败")}: {Path.GetFileName(r.SourcePath)} -> {r.DestinationPath}  {r.Message}");
    }
    Console.WriteLine($"导出目录: {target}");
    return 0;
}

static async Task<int> RunDeepTestAsync(PhotoDatabase db)
{
    Console.WriteLine("=== 深度分析（颜色 / NIMA 美学 / 嵌入 / YOLO / CLIP）===");

    var deep = new DeepAnalysisService(
        db,
        new AestheticScoreService(),
        new FeatureEmbeddingService(),
        new ObjectDetectionService(),
        new ClipService());

    Console.WriteLine($"模型状态: NIMA {(AestheticScoreService.InstalledModelPath is not null ? "已装" : "未装")} | " +
                      $"YOLO {(ObjectDetectionService.InstalledModelPath is not null ? "已装" : "未装")} | " +
                      $"MobileNet {(FeatureEmbeddingService.InstalledModelPath is not null ? "已装" : "未装")} | " +
                      $"MobileCLIP {(ClipService.IsInstalled ? "已装" : "未装")}");

    var progress = new Progress<(int Done, int Total, string File)>(p =>
    {
        if (p.Done % 5 == 0 || p.Done == p.Total)
        {
            Console.WriteLine($"  [{p.Done}/{p.Total}] {p.File}");
        }
    });
    var result = await deep.AnalyzeLibraryAsync(includeClip: ClipService.IsInstalled, progress);
    Console.WriteLine($"分析完成: 待分析 {result.Total}，成功 {result.Analyzed}，失败 {result.Failed}");
    if (deep.LastError is not null)
    {
        Console.WriteLine($"  最近错误: {deep.LastError}");
    }

    long done = await db.CountDeepAnalyzedPhotosAsync();
    var low = await db.GetLowAestheticPhotosAsync(DeepAnalysisService.LowAestheticThreshold, 15);
    var mono = await db.GetMonoPhotosAsync(15);
    Console.WriteLine($"\n库内已完成深度分析: {done} 张；低分（无意义）: {low.Count} 张；黑白: {mono.Count} 张");
    foreach (var p in low.Take(8))
    {
        Console.WriteLine($"  低分 {p.AestheticScore:0.0}  {p.FileName}  [{p.DominantColors}]");
    }

    // Print a sample of stored deep-analysis values (color palette, embedding length, objects).
    var sample = (await db.GetPhotosPendingClipEmbeddingAsync(1)).ToList();
    var analyzed = await db.GetLowAestheticPhotosAsync(11.0, 5); // any scored row
    if (analyzed.Count == 0)
    {
        analyzed = (await db.GetPhotosByIdsAsync([1, 2, 3])).ToList();
    }
    foreach (var p in analyzed.Take(5))
    {
        Console.WriteLine($"  样例 {p.FileName}: colors=[{p.DominantColors}] mono={p.IsMono} " +
                          $"emb={(p.Embedding?.Length ?? 0) / 4}D aesthetic={p.AestheticScore?.ToString("0.0") ?? "-"} objects=[{p.ObjectsJson}]");
    }

    // Semantic search smoke test when CLIP is installed.
    if (ClipService.IsInstalled)
    {
        var clip = new ClipService();
        var all = await db.GetAllClipEmbeddingsAsync();
        Console.WriteLine($"\nCLIP 向量: {all.Count} 张");
        if (all.Count > 0)
        {
            var q = await Task.Run(() => clip.EmbedText("风景 山 水"));
            if (q is not null)
            {
                var top = all
                    .Select(x => (Id: x.Id, Score: ClipService.Cosine(q, x.Embedding)))
                    .OrderByDescending(x => x.Score)
                    .Take(5);
                foreach (var t in top)
                {
                    Console.WriteLine($"  相似度 {t.Score:0.000}  photo#{t.Id}");
                }
            }
            else
            {
                Console.WriteLine("文本编码失败（词表/模型缺失）");
            }
        }
    }
    return 0;
}

static async Task<int> RunGeoTestAsync(PhotoDatabase db)
{
    foreach (var q in new[] { "成都", "云南", "攀枝花" })
    {
        var photos = await db.GetGpsPhotosByPlaceAsync(q, 20);
        Console.WriteLine($"「{q}」-> {photos.Count} 张");
        foreach (var p in photos.Take(5))
        {
            Console.WriteLine($"  {p.FileName} GpsPlace=[{p.GpsPlace}] 省=[{p.PlaceProvince}] 市=[{p.PlaceCity}]");
        }
    }
    return 0;
}

static async Task<int> RunGpsStatsAsync(PhotoDatabase db)
{
    var s = await db.GetGpsStatsAsync();
    Console.WriteLine("=== GPS 反解统计 ===");
    Console.WriteLine($"总照片数(未丢失): {s.TotalPhotos}");
    Console.WriteLine($"含 GPS 的照片: {s.PhotosWithGps}");
    Console.WriteLine($"已反解出位置: {s.PhotosWithPlace}");
    Console.WriteLine($"不同位置字符串数: {s.DistinctPlaces}");
    Console.WriteLine($"来源分布: " + string.Join(", ", s.BySource.Select(kv => $"{kv.Key}={kv.Value}")));
    Console.WriteLine("含 GPS 照片按扩展名:");
    foreach (var kv in s.GpsByExtension)
    {
        Console.WriteLine($"  {kv.Key} = {kv.Value}");
    }
    return 0;
}

static async Task<int> RunPlaceTestAsync(PhotoDatabase db)
{
    Console.WriteLine("=== GPS 位置回填测试（增量 + 来源标记 + 相邻复用）===");
    var service = new GpsPlaceService(db, new ReverseGeocodeService());
    var progress = new Progress<(int Done, int Total, string File)>(p => Console.WriteLine($"  [{p.Done}/{p.Total}] {p.File}"));

    Console.WriteLine("--- 第 1 次（当前来源 osm）---");
    var result = await service.BackfillAsync(progress);
    Console.WriteLine($"回填完成: 总 {result.Total}，解析 {result.Resolved}，跳过 {result.Skipped}");
    var withPlace = await db.CountGpsPhotosWithPlaceAsync();
    Console.WriteLine($"已解析位置照片数: {withPlace}");
    var bySource = await db.CountGpsPhotosBySourceAsync();
    Console.WriteLine($"来源分布: " + string.Join(", ", bySource.Select(kv => $"{kv.Key}={kv.Value}")));

    // 记录第 1 次后仍未解析的（应全部标记了来源失败）。
    var unresolvedAfter1 = await db.GetGpsPhotosWithoutPlaceAsync(null, 100);
    Console.WriteLine($"第 1 次后仍未解析: {unresolvedAfter1.Count} 张");

    // 模拟「切换来源」→ 只应重试未成功且未试过新来源的照片。
    Console.WriteLine("--- 第 2 次（模拟切换来源，仅增量重试）---");
    var result2 = await service.BackfillAsync(progress);
    Console.WriteLine($"第 2 次: 总 {result2.Total}，解析 {result2.Resolved}，跳过 {result2.Skipped}");

    // 检查来源标记是否写入。
    var sample = await db.GetGpsPhotosAsync(10);
    foreach (var p in sample)
    {
        Console.WriteLine($"  位置: {p.FileName} -> {(string.IsNullOrWhiteSpace(p.GpsPlace) ? "(无)" : p.GpsPlace)} [来源={(string.IsNullOrWhiteSpace(p.GpsPlaceSource) ? "-" : p.GpsPlaceSource)}] 失败=[{(string.IsNullOrWhiteSpace(p.GpsPlaceFailed) ? "-" : p.GpsPlaceFailed)}]");
    }
    return 0;
}

static async Task<int> RunPlaceReuseTestAsync(PhotoDatabase db)
{
    Console.WriteLine("=== 相邻复用 + 链式防传测试 ===");
    // 构造三张 GPS 照片：A 作为真实锚点，B 距 A 400m（应复用 A），C 距 A 1km（应在线解析，不得借 B）。
    // 成都中心 (30.573, 104.067)；1km ≈ 0.009° 纬度。
    double aLat = 30.5730, aLon = 104.0670;
    double bLat = aLat + 0.0036, bLon = aLon;      // ~400m
    double cLat = aLat + 0.0090, cLon = aLon;      // ~1km
    string[] files = { "A_NEAR.JPG", "B_NEAR.JPG", "C_FAR.JPG" };
    double[] lats = { aLat, bLat, cLat };
    double[] lons = { aLon, bLon, cLon };

    var photos = await db.GetPhotosAsync(10);
    if (photos.Count < 1) { Console.WriteLine("库内无照片"); return 0; }
    var basePhoto = photos[0];
    var a = CloneWithGps(basePhoto, 9001, files[0], aLat, aLon);
    var b = CloneWithGps(basePhoto, 9002, files[1], bLat, bLon);
    var c = CloneWithGps(basePhoto, 9003, files[2], cLat, cLon);
    await db.UpsertPhotoAsync(a);
    await db.UpsertPhotoAsync(b);
    await db.UpsertPhotoAsync(c);

    // A 作为真实锚点：按 FilePath 找到其真实 Id 并写入 osm 解析结果。
    var aRow = await db.GetPhotoByPathAsync(a.FilePath);
    await db.BulkSetGpsPlaceAsync([(aRow!.Id, "四川省成都市", "osm")]);

    var anchors = await db.GetResolvedAnchorsAsync();
    Console.WriteLine($"锚点数: {anchors.Count}（应 ≥1，仅 amap/osm，且不含 reuse）");

    Console.WriteLine("开始回填（B 应复用 A 的地址，C 应在线解析）…");
    var service = new GpsPlaceService(db, new ReverseGeocodeService());
    await service.BackfillAsync();

    var reload = (await db.GetPhotosAsync(100)).Where(p => files.Contains(p.FileName)).ToList();
    foreach (var p in reload)
    {
        Console.WriteLine($"  {p.FileName} -> [{p.GpsPlace}] 来源={p.GpsPlaceSource}");
    }
    var bRow = reload.FirstOrDefault(p => p.FileName == files[1]);
    var cRow = reload.FirstOrDefault(p => p.FileName == files[2]);
    Console.WriteLine($"B 来源 = {bRow?.GpsPlaceSource}（应为 reuse）");
    Console.WriteLine($"C 来源 = {cRow?.GpsPlaceSource}（应为 amap/osm，而非 reuse——防链式）");
    Console.WriteLine($"B 地址 = {bRow?.GpsPlace}（应等于 A 的「四川省成都市」）");
    return 0;
}

static PhotoRecord CloneWithGps(PhotoRecord src, long id, string name, double lat, double lon) => new()
{
    Id = id,
    FilePath = $@"D:\test\neighbor\{name}",
    FileName = name,
    DirectoryPath = @"D:\test\neighbor",
    Extension = ".JPG",
    Kind = PhotoKind.Jpeg,
    FileSizeBytes = src.FileSizeBytes,
    FileModifiedUtc = src.FileModifiedUtc,
    TakenAtUtc = src.TakenAtUtc,
    GpsLatitude = lat,
    GpsLongitude = lon,
    IndexedAtUtc = DateTime.UtcNow,
    IsMissing = false,
};

static async Task<int> RunMigrateTestAsync(PhotoDatabase db)
{
    // Simulates a legacy library: create a fresh DB whose Photos table has the OLD column
    // layout (no GpsPlace), insert a row, then reopen it via PhotoDatabase so
    // InitializeAsync runs the ALTER that appends GpsPlace, and verify reads still align.
    Console.WriteLine("=== 迁移列序测试 ===");
    var legacyPath = Path.Combine(Path.GetTempPath(), "MyAlbum_migrate_legacy.db");
    File.Delete(legacyPath);
    using (var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + legacyPath))
    {
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE Photos (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FilePath TEXT NOT NULL UNIQUE,
                FileName TEXT NOT NULL,
                DirectoryPath TEXT NOT NULL,
                Extension TEXT NOT NULL,
                Kind INTEGER NOT NULL DEFAULT 0,
                FileSizeBytes INTEGER NOT NULL DEFAULT 0,
                FileModifiedUtc TEXT NOT NULL,
                ContentHash TEXT,
                TakenAtUtc TEXT,
                CameraMake TEXT,
                CameraModel TEXT,
                LensModel TEXT,
                Iso INTEGER,
                ShutterSpeed TEXT,
                Aperture REAL,
                FocalLengthMm REAL,
                Width INTEGER,
                Height INTEGER,
                Orientation INTEGER,
                GpsLatitude REAL,
                GpsLongitude REAL,
                GpsAltitude REAL,
                Artist TEXT,
                Description TEXT,
                Copyright TEXT,
                Rating INTEGER NOT NULL DEFAULT 0,
                Tags TEXT,
                ThumbnailCachePath TEXT,
                PHash TEXT,
                IndexedAtUtc TEXT NOT NULL,
                IsMissing INTEGER NOT NULL DEFAULT 0
            );
            INSERT INTO Photos (
                FilePath, FileName, DirectoryPath, Extension, Kind, FileSizeBytes, FileModifiedUtc,
                TakenAtUtc, CameraMake, CameraModel, Iso, Width, Height, GpsLatitude, GpsLongitude,
                Rating, IndexedAtUtc, IsMissing)
            VALUES (
                'D:\\legacy\\DSC0001.JPG', 'DSC0001.JPG', 'D:\\legacy', '.JPG', 1, 5000000,
                '2024-01-01T00:00:00Z', '2024-01-01T10:00:00Z', 'SONY', 'ILCE-6700', 100, 6000, 4000,
                30.55869, 103.98874, 0, '2024-01-01T00:00:00Z', 0);
            """;
        cmd.ExecuteNonQuery();
    }

    // Reopen through the real PhotoDatabase → runs the GpsPlace ALTER migration.
    var migrated = new PhotoDatabase(legacyPath);
    await migrated.InitializeAsync();
    var photos = await migrated.GetPhotosAsync(100);
    Console.WriteLine($"读取 {photos.Count} 张（若列序错位会抛异常或字段错乱）");
    foreach (var p in photos)
    {
        Console.WriteLine($"  {p.FileName} kind={p.Kind} iso={p.Iso} w={p.Width}x{p.Height} " +
                          $"gps=({p.GpsLatitude},{p.GpsLongitude}) rating={p.Rating} place={(string.IsNullOrEmpty(p.GpsPlace) ? "(空)" : p.GpsPlace)}");
    }
    Console.WriteLine("迁移后列序读取正常。");
    try { GC.Collect(); GC.WaitForPendingFinalizers(); File.Delete(legacyPath); } catch { }
    return 0;
}

static async Task<int> RunQualityTestAsync(PhotoDatabase db)
{
    Console.WriteLine("=== 低质量照片清理测试 ===");
    var service = new LowQualityPhotoService(
        db,
        new VisionAnalysisService(db),
        new AestheticScoreService(),
        new FormatCleanupService(db));
    Console.WriteLine("先跑分析（清晰度 + NIMA 美学）…");
    var progress = new Progress<(int Done, int Total, string File)>(p =>
    {
        if (p.Done % 5 == 0 || p.Done == p.Total) Console.WriteLine($"  [{p.Done}/{p.Total}] {p.File}");
    });
    var r = await service.AnalyzePendingAsync(progress);
    Console.WriteLine($"分析完成: 总 {r.Total}，成功 {r.Analyzed}，失败 {r.Failed}");
    if (service.LastError is not null) Console.WriteLine($"  最近错误: {service.LastError}");
    var groups = await service.GetLowQualityGroupsAsync();
    Console.WriteLine($"低质量照片组: {groups.Count} 组");
    foreach (var g in groups.Take(10))
    {
        var scores = string.Join(", ", g.Photos.Select(p => $"{(p.BlurScore.HasValue ? $"锐度{p.BlurScore:0}" : "-")}/{(p.AestheticScore.HasValue ? $"分{p.AestheticScore:0.0}" : "-")}"));
        Console.WriteLine($"  [{g.ReasonText}] {g.Stem} @ {g.Folder} ({g.Photos.Count} 文件) {scores}");
    }
    // 合成一条低分数据验证筛选 SQL。
    var photos = await db.GetPhotosAsync(10);
    if (photos.Count > 0)
    {
        var first = photos[0];
        await db.BulkSetAestheticAsync([(first.Id, 3.2)]);
        Console.WriteLine($"已给 {first.FileName} 注入美学分 3.2");
        groups = await service.GetLowQualityGroupsAsync();
        Console.WriteLine($"注入后低质量照片组: {groups.Count} 组");
        foreach (var g in groups.Take(5))
        {
            Console.WriteLine($"  [{g.ReasonText}] {g.Stem} ({g.Photos.Count} 文件)");
        }
    }
    return 0;
}

static async Task<int> RunThumbProbeAsync(PhotoDatabase db, MetadataReaderService reader)
{
    var photos = await db.GetPhotosAsync(20);
    foreach (var p in photos.Where(p => p.FileName.ToUpperInvariant().Contains("NEF")))
    {
        Console.WriteLine($"NEF: {p.FileName}");
        Console.WriteLine($"  缩略图: {p.ThumbnailCachePath} ({(File.Exists(p.ThumbnailCachePath ?? "") ? "存在" : "缺失")})");
        var info = reader.Read(p.FilePath);
        Console.WriteLine($"  尺寸: {info.Width}x{info.Height} 方向: {info.Orientation}");
        var gray = WicGrayscale.GetGrayscale(p.FilePath, 128);
        if (gray is null) { Console.WriteLine("  灰度解码: 失败"); continue; }
        double mean = gray.Pixels.Sum() / gray.Pixels.Length;
        double min = gray.Pixels.Min(), max = gray.Pixels.Max();
        Console.WriteLine($"  灰度解码: {gray.Width}x{gray.Height} mean={mean:0.00} min={min:0.00} max={max:0.00}");
    }
    return 0;
}

static async Task<int> RunPlaceIncrementalTestAsync(PhotoDatabase db)
{
    Console.WriteLine("=== 增量来源失败标记测试 ===");
    var photos = await db.GetGpsPhotosAsync(10);
    var gps = photos.Where(p => p.GpsLatitude is not null).ToList();
    if (gps.Count == 0) { Console.WriteLine("无 GPS 照片"); return 0; }
    var target = gps[0];
    // 把一张已解析照片的 source 清掉、标记 osM 失败，模拟「某来源解析失败」。
    await db.BulkSetGpsPlaceAsync([(target.Id, "", "osm")]);
    await db.BulkMarkGpsPlaceFailedAsync([target.Id], "osm");
    Console.WriteLine($"已把 {target.FileName} 设为未解析且标记 osm 失败");

    var osmPending = await db.GetGpsPhotosWithoutPlaceAsync("osm", 100);
    var amapPending = await db.GetGpsPhotosWithoutPlaceAsync("amap", 100);
    Console.WriteLine($"  osm 待解析: {osmPending.Count}（应排除 {target.FileName}）: {string.Join(",", osmPending.Select(p => p.FileName))}");
    Console.WriteLine($"  amap 待解析: {amapPending.Count}（应包含 {target.FileName}）: {string.Join(",", amapPending.Select(p => p.FileName))}");
    return 0;
}

/// <summary>
/// 验证备份是否包含「刚写入、尚未 checkpoint 到主文件（还在 WAL）的数据」。
/// 流程：写一行 → 不 checkpoint → BackupToAsync → 用只读连接读备份确认该行存在。
/// </summary>
static async Task<int> RunBackupTestAsync(PhotoDatabase db)
{
    Console.WriteLine("=== 备份包含 WAL 未 checkpoint 数据测试 ===");
    var dir = Path.Combine(Path.GetTempPath(), "MyAlbum_backup_test");
    Directory.CreateDirectory(dir);
    foreach (var f in Directory.GetFiles(dir, "*.db")) File.Delete(f);

    // 1) 写入一张照片（新行，未 checkpoint）。
    var stamp = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
    await db.UpsertPhotoAsync(new PhotoRecord
    {
        FilePath = @"D:\backup_probe\FRESH.JPG",
        FileName = "FRESH.JPG",
        DirectoryPath = @"D:\backup_probe",
        Extension = ".JPG",
        Kind = PhotoKind.Jpeg,
        FileSizeBytes = 123456,
        FileModifiedUtc = DateTime.UtcNow,
        TakenAtUtc = DateTime.UtcNow,
        CameraMake = "BACKUP_TEST",
        CameraModel = "PROBE",
        IndexedAtUtc = DateTime.UtcNow,
        IsMissing = false,
    });
    Console.WriteLine("已写入 FRESH.JPG（此刻在 WAL 中，未 checkpoint）");

    // 2) 确认 WAL 文件存在（数据尚未合并到主 .db）。
    string wal = db.DatabasePath + "-wal";
    Console.WriteLine($"WAL 存在: {File.Exists(wal)}");

    // 3) 备份（在线备份 API，应把 WAL 内容一并拷入）。
    var backup = new DatabaseBackupService(db);
    var backupPath = await backup.BackupAsync(dir, "walprobe");
    Console.WriteLine("备份文件: " + backupPath);

    // 4) 用独立只读连接读备份，确认新行在里面。
    await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={backupPath};Mode=ReadOnly"))
    {
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Photos WHERE FileName = 'FRESH.JPG';";
        long count = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine($"备份里 FRESH.JPG 行数: {count}  →  {(count > 0 ? "包含刚写入数据 ✅" : "丢失 ❌")}");
    }
    return 0;
}

/// <summary>
/// 验证「修改 EXIF / 文件修改时间后重新扫描不丢用户数据」：
/// 扫描 → 设评分 → 改文件修改时间触发重扫 → 确认评分仍在。
/// </summary>
static async Task<int> RunRescanPreserveTestAsync(PhotoDatabase db)
{
    Console.WriteLine("=== 重扫保留评分/地址测试 ===");
    var library = new LibraryService(db, new MetadataReaderService(), new ThumbnailService(Path.Combine(Path.GetDirectoryName(db.DatabasePath) ?? ".", "cache", "thumbs")));
    var photo = (await db.GetPhotosAsync(5)).FirstOrDefault();
    if (photo is null) { Console.WriteLine("库内无照片"); return 0; }

    // 1) 给照片设评分 + 模拟已解析地址。
    await db.UpsertPhotoAsync(photo); // 确保行存在
    var existing = await db.GetPhotoByPathAsync(photo.FilePath);
    existing!.Rating = 5;
    existing.GpsPlace = "四川省成都市";
    existing.PlaceCity = "成都市";
    existing.AestheticScore = 6.5;
    await db.UpsertPhotoAsync(existing);

    // 2) 改文件修改时间（+1 分钟），让指纹判定为"已变更"。
    var fi = new FileInfo(photo.FilePath);
    File.SetLastWriteTimeUtc(photo.FilePath, fi.LastWriteTimeUtc.AddMinutes(1));
    Console.WriteLine("已修改文件修改时间（+1 分钟）");

    // 3) 重扫该文件所在目录。
    var dir = photo.DirectoryPath;
    await library.ScanFolderAsync(dir, null);
    var after = await db.GetPhotoByPathAsync(photo.FilePath);
    Console.WriteLine($"重扫后 Rating = {after?.Rating}（应为 5）");
    Console.WriteLine($"重扫后 GpsPlace = {after?.GpsPlace}（应为 四川省成都市）");
    Console.WriteLine($"重扫后 PlaceCity = {after?.PlaceCity}（应为 成都市）");
    Console.WriteLine($"重扫后 AestheticScore = {after?.AestheticScore}（应为 6.5）");
    bool ok = after is { Rating: 5 } && after.GpsPlace == "四川省成都市" && after.PlaceCity == "成都市" && after.AestheticScore == 6.5;
    Console.WriteLine(ok ? "✅ 用户数据全部保留" : "❌ 有字段被清空");
    return ok ? 0 : 1;
}

/// <summary>验证数据库维护清理流程带进度、能正常完成且不重复全量校验。</summary>
static async Task<int> RunMaintenanceTestAsync(PhotoDatabase db)
{
    Console.WriteLine("=== 数据库维护测试 ===");
    var maintenance = new DatabaseMaintenanceService(db, new ThumbnailService(Path.Combine(Path.GetDirectoryName(db.DatabasePath) ?? ".", "cache", "thumbs")));

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var progress = new Progress<string>(s => Console.WriteLine($"  进度: {s}"));
    Console.WriteLine("--- 校验 ---");
    var report = await maintenance.VerifyAsync(progress);
    sw.Stop();
    Console.WriteLine($"校验完成 ({sw.ElapsedMilliseconds}ms): 照片 {report.PhotoCount}，孤立缩略图 {report.OrphanThumbnailCount}");

    sw.Restart();
    Console.WriteLine("--- 清理 ---");
    var result = await maintenance.CleanupAsync(progress);
    sw.Stop();
    Console.WriteLine($"清理完成 ({sw.ElapsedMilliseconds}ms): 缺失 {result.RemovedMissingPhotos}，重复 {result.RemovedCaseDuplicates}，孤立缩略图 {result.RemovedOrphanThumbnails}，标签 {result.RemovedOrphanPhotoTags}，人脸 {result.RemovedOrphanFaces}");
    return 0;
}

/// <summary>用本地假 LLM 服务验证：① 429 后自动重试成功 ② 并行/批大小走配置。</summary>
static async Task<int> RunLlmRetryTestAsync(PhotoDatabase db)
{
    Console.WriteLine("=== LLM 重试 + 配置测试 ===");
    // 本地 mock：第一次返回 429，之后返回合法 JSON。
    using var listener = new System.Net.HttpListener();
    listener.Prefixes.Add("http://127.0.0.1:18555/");
    listener.Start();
    int hits = 0;
    _ = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            var ctx = await listener.GetContextAsync();
            using var body = new System.IO.StreamReader(ctx.Request.InputStream);
            var request = await body.ReadToEndAsync();
            hits++;
            if (hits == 1)
            {
                ctx.Response.StatusCode = 429;
                ctx.Response.Headers["Retry-After"] = "1";
            }
            else
            {
                ctx.Response.StatusCode = 200;
                var reply = """{"choices":[{"message":{"content":"{\"四川省成都市\":{\"country\":\"中国\",\"province\":\"四川省\",\"city\":\"成都市\",\"district\":\"\",\"landmark\":\"\"}}"}}]}""";
                var bytes = System.Text.Encoding.UTF8.GetBytes(reply);
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            ctx.Response.Close();
        }
    });

    LlmConfig.Set("test-model", "sk-test", "http://127.0.0.1:18555/v1");
    var llm = new LlmService();
    var result = await llm.NormalizeAsync(new[] { "四川省成都市" });
    Console.WriteLine($"请求命中次数: {hits}（应 ≥2，说明 429 后重试）");
    Console.WriteLine($"解析结果: 省={result.Map.GetValueOrDefault("四川省成都市")?.Province} 市={result.Map.GetValueOrDefault("四川省成都市")?.City}");
    listener.Stop();
    Console.WriteLine($"重试成功: {hits >= 2 && result.Map.GetValueOrDefault("四川省成都市")?.City == "成都市"}");
    return 0;
}
