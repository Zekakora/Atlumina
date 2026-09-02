using System.Globalization;
using Microsoft.Data.Sqlite;
using MyAlbum.Core.Models;
using MyAlbum.Core.Services;

namespace MyAlbum.Core.Data;

/// <summary>
/// SQLite-backed index (the "L2" layer of the cache architecture).
/// All date/time values are persisted as ISO-8601 UTC strings.
/// Microsoft.Data.Sqlite pools connections, so open/close per call is cheap.
/// </summary>
public sealed class PhotoDatabase
{
    /// <summary>SQL fragment excluding photos under folders marked hidden in the settings dialog.</summary>
    private const string HiddenFolderExclusion =
        " AND NOT EXISTS (SELECT 1 FROM Folders f WHERE f.IsHidden = 1" +
        " AND (Photos.DirectoryPath = f.Path OR Photos.DirectoryPath LIKE f.Path || '\\' || '%'))";

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PhotoDatabase(string dbPath)
    {
        _databasePath = Path.GetFullPath(dbPath);
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath => _databasePath;

    public string ConnectionString => _connectionString;

    public async Task InitializeAsync()
    {
        await ExecuteAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS Photos (
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
                    IsMissing INTEGER NOT NULL DEFAULT 0,
                    BlurScore REAL,
                    AiAnalyzedAtUtc TEXT,
                    AestheticScore REAL,
                    DominantColors TEXT,
                    IsMono INTEGER NOT NULL DEFAULT 0,
                    Embedding BLOB,
                    ClipEmbedding BLOB,
                    ObjectsJson TEXT,
                    DeepAnalyzedAtUtc TEXT,
                    GpsPlace TEXT,
                    PlaceCountry TEXT,
                    PlaceProvince TEXT,
                    PlaceCity TEXT,
                    PlaceDistrict TEXT,
                    PlaceLandmark TEXT,
                    GpsPlaceSource TEXT,
                    GpsPlaceFailed TEXT
                );
                CREATE INDEX IF NOT EXISTS IX_Photos_TakenAtUtc ON Photos(TakenAtUtc);
                CREATE INDEX IF NOT EXISTS IX_Photos_Rating ON Photos(Rating);
                CREATE INDEX IF NOT EXISTS IX_Photos_CameraModel ON Photos(CameraModel);
                CREATE INDEX IF NOT EXISTS IX_Photos_DirectoryPath ON Photos(DirectoryPath);
                CREATE INDEX IF NOT EXISTS IX_Photos_Kind ON Photos(Kind);

                CREATE TABLE IF NOT EXISTS Folders (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Path TEXT NOT NULL UNIQUE,
                    LastScannedUtc TEXT,
                    IsWatched INTEGER NOT NULL DEFAULT 0,
                    IsHidden INTEGER NOT NULL DEFAULT 0,
                    AddedUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS SmartAlbums (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    FilterJson TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Tags (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL UNIQUE,
                    IsAuto INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS PhotoTags (
                    PhotoId INTEGER NOT NULL,
                    TagId INTEGER NOT NULL,
                    PRIMARY KEY (PhotoId, TagId)
                );
                CREATE INDEX IF NOT EXISTS IX_PhotoTags_TagId ON PhotoTags(TagId);

                CREATE TABLE IF NOT EXISTS Faces (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PhotoId INTEGER NOT NULL,
                    BoxX REAL NOT NULL,
                    BoxY REAL NOT NULL,
                    BoxW REAL NOT NULL,
                    BoxH REAL NOT NULL,
                    Score REAL NOT NULL,
                    Embedding BLOB NOT NULL,
                    PersonId INTEGER,
                    FaceAnalyzedAtUtc TEXT NOT NULL,
                    PersonName TEXT
                );
                CREATE INDEX IF NOT EXISTS IX_Faces_PhotoId ON Faces(PhotoId);
                CREATE INDEX IF NOT EXISTS IX_Faces_PersonId ON Faces(PersonId);
                """;
            cmd.ExecuteNonQuery();

            // Migration for databases created before IsHidden existed on Folders.
            try
            {
                using var mig = conn.CreateCommand();
                mig.CommandText = "ALTER TABLE Folders ADD COLUMN IsHidden INTEGER NOT NULL DEFAULT 0;";
                mig.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // column already exists
            }

            // Migration for databases created before the AI/vision columns existed on Photos.
            try
            {
                using var mig = conn.CreateCommand();
                mig.CommandText = "ALTER TABLE Photos ADD COLUMN BlurScore REAL;";
                mig.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // column already exists
            }
            try
            {
                using var mig = conn.CreateCommand();
                mig.CommandText = "ALTER TABLE Photos ADD COLUMN AiAnalyzedAtUtc TEXT;";
                mig.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // column already exists
            }
            // Migration for databases created before the deep-analysis columns existed.
            try
            {
                using var mig = conn.CreateCommand();
                mig.CommandText = "ALTER TABLE Photos ADD COLUMN AestheticScore REAL;";
                mig.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // column already exists
            }
            try
            {
                using var mig = conn.CreateCommand();
                mig.CommandText = "ALTER TABLE Photos ADD COLUMN DominantColors TEXT;";
                mig.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // column already exists
            }
            try
            {
                using var mig = conn.CreateCommand();
                mig.CommandText = "ALTER TABLE Photos ADD COLUMN IsMono INTEGER NOT NULL DEFAULT 0;";
                mig.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // column already exists
            }
            try
            {
                using var mig = conn.CreateCommand();
                mig.CommandText = "ALTER TABLE Photos ADD COLUMN Embedding BLOB;";
                mig.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // column already exists
            }
            try
            {
                using var mig = conn.CreateCommand();
                mig.CommandText = "ALTER TABLE Photos ADD COLUMN ClipEmbedding BLOB;";
                mig.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // column already exists
            }
            try
            {
                using var mig = conn.CreateCommand();
                mig.CommandText = "ALTER TABLE Photos ADD COLUMN ObjectsJson TEXT;";
                mig.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // column already exists
            }
            try
            {
                using var mig = conn.CreateCommand();
                mig.CommandText = "ALTER TABLE Photos ADD COLUMN DeepAnalyzedAtUtc TEXT;";
                mig.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // column already exists
            }
            // Migration for databases created before GpsPlace existed (reverse-geocoded place name).
            try
            {
                using var mig = conn.CreateCommand();
                mig.CommandText = "ALTER TABLE Photos ADD COLUMN GpsPlace TEXT;";
                mig.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // column already exists
            }
            // Migration for the LLM-normalized five-level address columns.
            foreach (var col in new[] { "PlaceCountry", "PlaceProvince", "PlaceCity", "PlaceDistrict", "PlaceLandmark" })
            {
                try
                {
                    using var mig = conn.CreateCommand();
                    mig.CommandText = $"ALTER TABLE Photos ADD COLUMN {col} TEXT;";
                    mig.ExecuteNonQuery();
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // column already exists
                }
            }
            // Migration for the geocode source markers (which source resolved / which failed).
            foreach (var col in new[] { "GpsPlaceSource", "GpsPlaceFailed" })
            {
                try
                {
                    using var mig = conn.CreateCommand();
                    mig.CommandText = $"ALTER TABLE Photos ADD COLUMN {col} TEXT;";
                    mig.ExecuteNonQuery();
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // column already exists
                }
            }

            // Migration for databases created before PersonName existed on Faces.
            try
            {
                using var mig = conn.CreateCommand();
                mig.CommandText = "ALTER TABLE Faces ADD COLUMN PersonName TEXT;";
                mig.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // column already exists
            }
        });
    }

    public async Task<long> UpsertPhotoAsync(PhotoRecord p)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Photos (
                    FilePath, FileName, DirectoryPath, Extension, Kind,
                    FileSizeBytes, FileModifiedUtc, ContentHash,
                    TakenAtUtc, CameraMake, CameraModel, LensModel,
                    Iso, ShutterSpeed, Aperture, FocalLengthMm,
                    Width, Height, Orientation,
                    GpsLatitude, GpsLongitude, GpsAltitude, GpsPlace,
                    PlaceCountry, PlaceProvince, PlaceCity, PlaceDistrict, PlaceLandmark,
                    GpsPlaceSource, GpsPlaceFailed,
                    Artist, Description, Copyright, Rating, Tags,
                    ThumbnailCachePath, PHash, BlurScore, AiAnalyzedAtUtc, IndexedAtUtc, IsMissing,
                    AestheticScore, DominantColors, IsMono, Embedding, ClipEmbedding, ObjectsJson, DeepAnalyzedAtUtc)
                VALUES (
                    $path, $fileName, $dir, $ext, $kind,
                    $size, $modified, $hash,
                    $takenAt, $make, $model, $lens,
                    $iso, $shutter, $aperture, $focal,
                    $width, $height, $orientation,
                    $lat, $lon, $alt, $place,
                    $placeCountry, $placeProvince, $placeCity, $placeDistrict, $placeLandmark,
                    $placeSource, $placeFailed,
                    $artist, $description, $copyright, $rating, $tags,
                    $thumb, $phash, $blurScore, $aiAnalyzedAt, $indexedAt, $missing,
                    $aestheticScore, $dominantColors, $isMono, $embedding, $clipEmbedding, $objectsJson, $deepAnalyzedAt)
                ON CONFLICT(FilePath) DO UPDATE SET
                    FileName = $fileName,
                    DirectoryPath = $dir,
                    Extension = $ext,
                    Kind = $kind,
                    FileSizeBytes = $size,
                    FileModifiedUtc = $modified,
                    ContentHash = $hash,
                    TakenAtUtc = $takenAt,
                    CameraMake = $make,
                    CameraModel = $model,
                    LensModel = $lens,
                    Iso = $iso,
                    ShutterSpeed = $shutter,
                    Aperture = $aperture,
                    FocalLengthMm = $focal,
                    Width = $width,
                    Height = $height,
                    Orientation = $orientation,
                    GpsLatitude = $lat,
                    GpsLongitude = $lon,
                    GpsAltitude = $alt,
                    GpsPlace = $place,
                    PlaceCountry = $placeCountry,
                    PlaceProvince = $placeProvince,
                    PlaceCity = $placeCity,
                    PlaceDistrict = $placeDistrict,
                    PlaceLandmark = $placeLandmark,
                    GpsPlaceSource = $placeSource,
                    GpsPlaceFailed = $placeFailed,
                    Artist = $artist,
                    Description = $description,
                    Copyright = $copyright,
                    Rating = $rating,
                    Tags = $tags,
                    ThumbnailCachePath = $thumb,
                    PHash = $phash,
                    BlurScore = $blurScore,
                    AiAnalyzedAtUtc = $aiAnalyzedAt,
                    IndexedAtUtc = $indexedAt,
                    IsMissing = $missing,
                    AestheticScore = $aestheticScore,
                    DominantColors = $dominantColors,
                    IsMono = $isMono,
                    Embedding = $embedding,
                    ClipEmbedding = $clipEmbedding,
                    ObjectsJson = $objectsJson,
                    DeepAnalyzedAtUtc = $deepAnalyzedAt;
                """;
            BindPhotoParams(cmd, p);
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Bulk upserts many photos inside a single transaction. Prepared-command reuse
    /// plus one commit per batch is an order of magnitude faster than one autocommit
    /// transaction per photo for large imports.
    /// </summary>
    public async Task BulkUpsertPhotosAsync(IReadOnlyList<PhotoRecord> photos, CancellationToken ct = default)
    {
        if (photos.Count == 0)
        {
            return;
        }
        await _gate.WaitAsync(ct);
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO Photos (
                    FilePath, FileName, DirectoryPath, Extension, Kind,
                    FileSizeBytes, FileModifiedUtc, ContentHash,
                    TakenAtUtc, CameraMake, CameraModel, LensModel,
                    Iso, ShutterSpeed, Aperture, FocalLengthMm,
                    Width, Height, Orientation,
                    GpsLatitude, GpsLongitude, GpsAltitude, GpsPlace,
                    PlaceCountry, PlaceProvince, PlaceCity, PlaceDistrict, PlaceLandmark,
                    GpsPlaceSource, GpsPlaceFailed,
                    Artist, Description, Copyright, Rating, Tags,
                    ThumbnailCachePath, PHash, BlurScore, AiAnalyzedAtUtc, IndexedAtUtc, IsMissing,
                    AestheticScore, DominantColors, IsMono, Embedding, ClipEmbedding, ObjectsJson, DeepAnalyzedAtUtc)
                VALUES (
                    $path, $fileName, $dir, $ext, $kind,
                    $size, $modified, $hash,
                    $takenAt, $make, $model, $lens,
                    $iso, $shutter, $aperture, $focal,
                    $width, $height, $orientation,
                    $lat, $lon, $alt, $place,
                    $placeCountry, $placeProvince, $placeCity, $placeDistrict, $placeLandmark,
                    $placeSource, $placeFailed,
                    $artist, $description, $copyright, $rating, $tags,
                    $thumb, $phash, $blurScore, $aiAnalyzedAt, $indexedAt, $missing,
                    $aestheticScore, $dominantColors, $isMono, $embedding, $clipEmbedding, $objectsJson, $deepAnalyzedAt)
                ON CONFLICT(FilePath) DO UPDATE SET
                    FileName = $fileName,
                    DirectoryPath = $dir,
                    Extension = $ext,
                    Kind = $kind,
                    FileSizeBytes = $size,
                    FileModifiedUtc = $modified,
                    ContentHash = $hash,
                    TakenAtUtc = $takenAt,
                    CameraMake = $make,
                    CameraModel = $model,
                    LensModel = $lens,
                    Iso = $iso,
                    ShutterSpeed = $shutter,
                    Aperture = $aperture,
                    FocalLengthMm = $focal,
                    Width = $width,
                    Height = $height,
                    Orientation = $orientation,
                    GpsLatitude = $lat,
                    GpsLongitude = $lon,
                    GpsAltitude = $alt,
                    GpsPlace = $place,
                    PlaceCountry = $placeCountry,
                    PlaceProvince = $placeProvince,
                    PlaceCity = $placeCity,
                    PlaceDistrict = $placeDistrict,
                    PlaceLandmark = $placeLandmark,
                    GpsPlaceSource = $placeSource,
                    GpsPlaceFailed = $placeFailed,
                    Artist = $artist,
                    Description = $description,
                    Copyright = $copyright,
                    Rating = $rating,
                    Tags = $tags,
                    ThumbnailCachePath = $thumb,
                    PHash = $phash,
                    BlurScore = $blurScore,
                    AiAnalyzedAtUtc = $aiAnalyzedAt,
                    IndexedAtUtc = $indexedAt,
                    IsMissing = $missing,
                    AestheticScore = $aestheticScore,
                    DominantColors = $dominantColors,
                    IsMono = $isMono,
                    Embedding = $embedding,
                    ClipEmbedding = $clipEmbedding,
                    ObjectsJson = $objectsJson,
                    DeepAnalyzedAtUtc = $deepAnalyzedAt;
                """;
            foreach (var photo in photos)
            {
                ct.ThrowIfCancellationRequested();
                cmd.Parameters.Clear();
                BindPhotoParams(cmd, photo);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void BindPhotoParams(SqliteCommand cmd, PhotoRecord p)
    {
        cmd.Parameters.AddWithValue("$path", p.FilePath);
        cmd.Parameters.AddWithValue("$fileName", p.FileName);
        cmd.Parameters.AddWithValue("$dir", p.DirectoryPath);
        cmd.Parameters.AddWithValue("$ext", p.Extension);
        cmd.Parameters.AddWithValue("$kind", (int)p.Kind);
        cmd.Parameters.AddWithValue("$size", p.FileSizeBytes);
        cmd.Parameters.AddWithValue("$modified", ToIso(p.FileModifiedUtc));
        cmd.Parameters.AddWithValue("$hash", (object?)p.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$takenAt", (object?)ToIsoNullable(p.TakenAtUtc) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$make", (object?)p.CameraMake ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)p.CameraModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lens", (object?)p.LensModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$iso", (object?)p.Iso ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$shutter", (object?)p.ShutterSpeed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$aperture", (object?)p.Aperture ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$focal", (object?)p.FocalLengthMm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$width", (object?)p.Width ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$height", (object?)p.Height ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$orientation", (object?)p.Orientation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lat", (object?)p.GpsLatitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lon", (object?)p.GpsLongitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$alt", (object?)p.GpsAltitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$place", (object?)p.GpsPlace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$placeCountry", (object?)p.PlaceCountry ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$placeProvince", (object?)p.PlaceProvince ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$placeCity", (object?)p.PlaceCity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$placeDistrict", (object?)p.PlaceDistrict ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$placeLandmark", (object?)p.PlaceLandmark ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$placeSource", (object?)p.GpsPlaceSource ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$placeFailed", (object?)p.GpsPlaceFailed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$artist", (object?)p.Artist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$description", (object?)p.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$copyright", (object?)p.Copyright ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rating", p.Rating);
        cmd.Parameters.AddWithValue("$tags", (object?)p.Tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thumb", (object?)p.ThumbnailCachePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$phash", (object?)p.PHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$blurScore", (object?)p.BlurScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$aiAnalyzedAt", (object?)ToIsoNullable(p.AiAnalyzedAtUtc) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$aestheticScore", (object?)p.AestheticScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dominantColors", (object?)p.DominantColors ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$isMono", p.IsMono ? 1 : 0);
        cmd.Parameters.AddWithValue("$embedding", (object?)p.Embedding ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$clipEmbedding", (object?)p.ClipEmbedding ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$objectsJson", (object?)p.ObjectsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$deepAnalyzedAt", (object?)ToIsoNullable(p.DeepAnalyzedAtUtc) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$indexedAt", ToIso(p.IndexedAtUtc));
        cmd.Parameters.AddWithValue("$missing", p.IsMissing ? 1 : 0);
    }

    public async Task<PhotoRecord?> GetPhotoByPathAsync(string path)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Photos WHERE FilePath = $path LIMIT 1;";
            cmd.Parameters.AddWithValue("$path", path);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadPhoto(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PhotoRecord?> GetPhotoByIdAsync(long id)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Photos WHERE Id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadPhoto(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the photos whose ids are in <paramref name="ids"/>, in the same order,
    /// skipping ids that don't exist. Queries are chunked because SQLite caps the
    /// number of bound parameters per statement.
    /// </summary>
    public async Task<List<PhotoRecord>> GetPhotosByIdsAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
        {
            return new List<PhotoRecord>();
        }
        await _gate.WaitAsync();
        try
        {
            var byId = new Dictionary<long, PhotoRecord>();
            foreach (var chunk in ids.Chunk(500))
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                var placeholders = string.Join(",", Enumerable.Range(0, chunk.Length).Select(i => $"$id{i}"));
                cmd.CommandText = $"SELECT * FROM Photos WHERE Id IN ({placeholders});";
                for (int i = 0; i < chunk.Length; i++)
                {
                    cmd.Parameters.AddWithValue($"$id{i}", chunk[i]);
                }
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var p = ReadPhoto(reader);
                    byId[p.Id] = p;
                }
            }
            var result = new List<PhotoRecord>(ids.Count);
            foreach (var id in ids)
            {
                if (byId.TryGetValue(id, out var p))
                {
                    result.Add(p);
                }
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<PhotoRecord>> GetPhotosAsync(int limit = 10000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Photos ORDER BY TakenAtUtc DESC, Id DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns photos that have GPS coordinates in the index (used to write the DB's
    /// GPS back into the source files when ExifTool becomes available, and to build the
    /// 地点 sidebar). Photos under hidden folders are excluded.
    /// </summary>
    public async Task<List<PhotoRecord>> GetGpsPhotosAsync(int limit = 100000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Photos WHERE GpsLatitude IS NOT NULL AND GpsLongitude IS NOT NULL"
                + HiddenFolderExclusion
                + " ORDER BY TakenAtUtc DESC, Id DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns photos whose GPS position falls inside the given bounding box
    /// (used by place-name search, e.g. "成都" → photos shot around Chengdu).
    /// </summary>
    public async Task<List<PhotoRecord>> GetGpsPhotosInBoxAsync(double minLat, double maxLat, double minLon, double maxLon, int limit = 10000)
    {        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM Photos
                WHERE GpsLatitude IS NOT NULL AND GpsLongitude IS NOT NULL
                  AND GpsLatitude BETWEEN $minLat AND $maxLat
                  AND GpsLongitude BETWEEN $minLon AND $maxLon
                ORDER BY TakenAtUtc DESC, Id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$minLat", minLat);
            cmd.Parameters.AddWithValue("$maxLat", maxLat);
            cmd.Parameters.AddWithValue("$minLon", minLon);
            cmd.Parameters.AddWithValue("$maxLon", maxLon);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns photos that have GPS but no stored reverse-geocoded place name yet, AND that the
    /// given <paramref name="source"/> has not already failed for (incremental: switching from
    /// Amap to OSM only re-tries photos that Amap never successfully resolved and that weren't
    /// already attempted by OSM). Candidates for the background place backfill.
    /// </summary>
    public async Task<List<PhotoRecord>> GetGpsPhotosWithoutPlaceAsync(string? source = null, int limit = 100000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            string sql = """
                SELECT * FROM Photos
                WHERE IsMissing = 0
                  AND GpsLatitude IS NOT NULL AND GpsLongitude IS NOT NULL
                  AND (GpsPlace IS NULL OR GpsPlace = '')
                """;
            if (!string.IsNullOrWhiteSpace(source))
            {
                // Skip photos where this source already failed (e.g. "amap" already tried → don't re-hit).
                sql += " AND (GpsPlaceFailed IS NULL OR GpsPlaceFailed NOT LIKE '%' || $source || '%')";
            }
            sql += " ORDER BY TakenAtUtc DESC, Id DESC LIMIT $limit;";
            cmd.CommandText = sql;
            if (!string.IsNullOrWhiteSpace(source))
            {
                cmd.Parameters.AddWithValue("$source", source);
            }
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Bulk-writes the reverse-geocoded place name for the given photo ids, recording which
    /// source resolved it. Photos that still have no place are NOT updated (incremental).
    /// </summary>
    public async Task BulkSetGpsPlaceAsync(IReadOnlyList<(long Id, string Place, string Source)> items)
    {
        if (items.Count == 0)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Photos SET GpsPlace = $place, GpsPlaceSource = $source WHERE Id = $id;";
            foreach (var (id, place, source) in items)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$place", place);
                cmd.Parameters.AddWithValue("$source", source);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks the given photos' GpsPlaceFailed with <paramref name="source"/> (appended to the
    /// comma-separated list) so that source is not re-tried for them on the next incremental run.
    /// Photos that did resolve (or that already list the source) are untouched.
    /// </summary>
    public async Task BulkMarkGpsPlaceFailedAsync(IReadOnlyList<long> ids, string source)
    {
        if (ids.Count == 0)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE Photos SET GpsPlaceFailed =
                    CASE WHEN GpsPlaceFailed IS NULL OR GpsPlaceFailed = '' THEN $source
                         WHEN GpsPlaceFailed LIKE '%' || $source || '%' THEN GpsPlaceFailed
                         ELSE GpsPlaceFailed || ',' || $source END
                WHERE Id = $id AND (GpsPlace IS NULL OR GpsPlace = '');
                """;
            foreach (var id in ids)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$source", source);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Breakdown of resolved photos by source ("amap"/"osm"/"offline").</summary>
    public async Task<Dictionary<string, long>> CountGpsPhotosBySourceAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT GpsPlaceSource, COUNT(*) FROM Photos WHERE IsMissing = 0 AND GpsPlace IS NOT NULL AND GpsPlace != '' AND GpsPlaceSource IS NOT NULL GROUP BY GpsPlaceSource;";
            using var reader = cmd.ExecuteReader();
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                result[reader.GetString(0)] = reader.GetInt64(1);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns photos that have a reverse-geocoded place name but no LLM-normalized address
    /// yet (candidates for the address-normalization pass).
    /// </summary>
    public async Task<List<PhotoRecord>> GetPhotosPendingPlaceNormalizationAsync(int limit = 100000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM Photos
                WHERE IsMissing = 0
                  AND GpsPlace IS NOT NULL AND GpsPlace != ''
                  AND (PlaceCountry IS NULL OR PlaceCountry = '')
                ORDER BY TakenAtUtc DESC, Id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Bulk-writes the LLM-normalized five-level address for the given photo ids.</summary>
    public async Task BulkSetPlaceAddressAsync(IReadOnlyList<(long Id, NormalizedAddress Address)> items)
    {
        if (items.Count == 0)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE Photos SET
                    PlaceCountry = $country, PlaceProvince = $province, PlaceCity = $city,
                    PlaceDistrict = $district, PlaceLandmark = $landmark
                WHERE Id = $id;
                """;
            foreach (var (id, addr) in items)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$country", (object?)NullIfEmpty(addr.Country) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$province", (object?)NullIfEmpty(addr.Province) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$city", (object?)NullIfEmpty(addr.City) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$district", (object?)NullIfEmpty(addr.District) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$landmark", (object?)NullIfEmpty(addr.Landmark) ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads back the normalized five-level address (PlaceCountry…PlaceLandmark) for the given
    /// photo ids. Used to refresh the in-memory grid after an out-of-band bulk write (e.g. the
    /// LLM address-normalization pass) without reloading the whole photo collection — the home
    /// page is cached and would otherwise keep stale place fields.
    /// </summary>
    public async Task<Dictionary<long, NormalizedAddress>> GetPlaceAddressesAsync(IReadOnlyList<long> ids)
    {
        var result = new Dictionary<long, NormalizedAddress>();
        if (ids.Count == 0)
        {
            return result;
        }
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var placeholders = string.Join(",", ids.Select((_, i) => $"$id{i}"));
            cmd.CommandText = $"""
                SELECT Id, PlaceCountry, PlaceProvince, PlaceCity, PlaceDistrict, PlaceLandmark
                FROM Photos WHERE Id IN ({placeholders});
                """;
            for (int i = 0; i < ids.Count; i++)
            {
                cmd.Parameters.AddWithValue($"$id{i}", ids[i]);
            }
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                result[id] = new NormalizedAddress(
                    ReadNullable(reader, 1), ReadNullable(reader, 2), ReadNullable(reader, 3),
                    ReadNullable(reader, 4), ReadNullable(reader, 5));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Force-writes the reverse-geocoded place (and optional normalized address) for a single
    /// photo, clearing any prior failed-source marker so it won't be skipped on the next
    /// incremental backfill. Used by the per-photo "refresh location" action on the home page.
    /// </summary>
    public async Task UpdatePhotoPlaceAsync(long id, string place, string source, NormalizedAddress? address)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE Photos SET
                    GpsPlace = $place,
                    GpsPlaceSource = $source,
                    GpsPlaceFailed = NULL
                WHERE Id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$place", place);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.ExecuteNonQuery();
            if (address is not null)
            {
                cmd.Parameters.Clear();
                cmd.CommandText = """
                    UPDATE Photos SET
                        PlaceCountry = $country, PlaceProvince = $province, PlaceCity = $city,
                        PlaceDistrict = $district, PlaceLandmark = $landmark
                    WHERE Id = $id;
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$country", (object?)NullIfEmpty(address.Country) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$province", (object?)NullIfEmpty(address.Province) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$city", (object?)NullIfEmpty(address.City) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$district", (object?)NullIfEmpty(address.District) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$landmark", (object?)NullIfEmpty(address.Landmark) ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Clears every derived location field for all photos (reverse-geocoded place + normalized
    /// five-level address + failed markers), keeping the raw GPS. Lets the user re-run the
    /// backfill / normalize passes from scratch after a reset.
    /// </summary>
    public async Task ResetAllPlacesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE Photos SET
                    GpsPlace = NULL, GpsPlaceSource = NULL, GpsPlaceFailed = NULL,
                    PlaceCountry = NULL, PlaceProvince = NULL, PlaceCity = NULL,
                    PlaceDistrict = NULL, PlaceLandmark = NULL;
                """;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadNullable(System.Data.Common.DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>Count of photos that already have a stored place name.</summary>
    public async Task<long> CountGpsPhotosWithPlaceAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Photos WHERE IsMissing = 0 AND GpsPlace IS NOT NULL AND GpsPlace != '';";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Diagnostic: GPS / reverse-geocode coverage broken down usefully for debugging — total
    /// photos, how many have GPS, how many have a place, the per-source split, distinct place
    /// strings, and GPS coverage by file extension (to verify RAW/HEIF are included).
    /// </summary>
    public async Task<GpsStats> GetGpsStatsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            long Scalar(string sql)
            {
                var c = conn.CreateCommand();
                c.CommandText = sql;
                return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            long total = Scalar("SELECT COUNT(*) FROM Photos WHERE IsMissing = 0;");
            long withGps = Scalar("SELECT COUNT(*) FROM Photos WHERE IsMissing = 0 AND GpsLatitude IS NOT NULL AND GpsLongitude IS NOT NULL;");
            long withPlace = Scalar("SELECT COUNT(*) FROM Photos WHERE IsMissing = 0 AND GpsPlace IS NOT NULL AND GpsPlace != '';");
            long distinctPlaces = Scalar("SELECT COUNT(DISTINCT GpsPlace) FROM Photos WHERE IsMissing = 0 AND GpsPlace IS NOT NULL AND GpsPlace != '';");

            var bySource = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT GpsPlaceSource, COUNT(*) FROM Photos WHERE IsMissing = 0 AND GpsPlace IS NOT NULL AND GpsPlace != '' AND GpsPlaceSource IS NOT NULL GROUP BY GpsPlaceSource;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) bySource[r.GetString(0)] = r.GetInt64(1);
            }

            var gpsByExt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT Extension, COUNT(*) FROM Photos WHERE IsMissing = 0 AND GpsLatitude IS NOT NULL AND GpsLongitude IS NOT NULL GROUP BY Extension ORDER BY COUNT(*) DESC;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) gpsByExt[r.GetString(0)] = r.GetInt64(1);
            }

            return new GpsStats(total, withGps, withPlace, distinctPlaces, bySource, gpsByExt);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Result of <see cref="GetGpsStatsAsync"/>.</summary>
    public sealed record GpsStats(
        long TotalPhotos,
        long PhotosWithGps,
        long PhotosWithPlace,
        long DistinctPlaces,
        IReadOnlyDictionary<string, long> BySource,
        IReadOnlyDictionary<string, long> GpsByExtension);

    /// <summary>Counts photos that have an LLM-normalized address (PlaceCountry set).</summary>
    public async Task<long> CountPhotosWithNormalizedAddressAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Photos WHERE IsMissing = 0 AND PlaceCountry IS NOT NULL AND PlaceCountry != '';";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns photos whose stored place matches <paramref name="keyword"/> in any of the
    /// address fields: the reverse-geocoded <c>GpsPlace</c> string OR any LLM-normalized
    /// level (country / province / city / district / landmark). Lets the user search by
    /// city, country, street or landmark ("成都", "法国", "武侯", "天府广场").
    /// </summary>
    public async Task<List<PhotoRecord>> GetGpsPhotosByPlaceAsync(string keyword, int limit = 10000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {GridPhotoColumns} FROM Photos
                WHERE IsMissing = 0
                  AND (
                        GpsPlace LIKE '%' || $kw || '%'
                     OR PlaceCountry LIKE '%' || $kw || '%'
                     OR PlaceProvince LIKE '%' || $kw || '%'
                     OR PlaceCity LIKE '%' || $kw || '%'
                     OR PlaceDistrict LIKE '%' || $kw || '%'
                     OR PlaceLandmark LIKE '%' || $kw || '%'
                  )
                ORDER BY TakenAtUtc DESC, Id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$kw", keyword);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadGridPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns distinct place-name suggestions matching <paramref name="prefix"/> across the
    /// reverse-geocoded and normalized address fields (used for the top search bar's "📍" hints).
    /// </summary>
    public async Task<List<string>> GetPlaceSuggestionsAsync(string prefix, int limit = 10)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT val FROM (
                    SELECT GpsPlace AS val FROM Photos WHERE IsMissing = 0 AND GpsPlace IS NOT NULL AND GpsPlace != '' AND GpsPlace LIKE '%' || $p || '%'
                    UNION
                    SELECT PlaceProvince AS val FROM Photos WHERE IsMissing = 0 AND PlaceProvince IS NOT NULL AND PlaceProvince != '' AND PlaceProvince LIKE '%' || $p || '%'
                    UNION
                    SELECT PlaceCity AS val FROM Photos WHERE IsMissing = 0 AND PlaceCity IS NOT NULL AND PlaceCity != '' AND PlaceCity LIKE '%' || $p || '%'
                    UNION
                    SELECT PlaceDistrict AS val FROM Photos WHERE IsMissing = 0 AND PlaceDistrict IS NOT NULL AND PlaceDistrict != '' AND PlaceDistrict LIKE '%' || $p || '%'
                    UNION
                    SELECT PlaceLandmark AS val FROM Photos WHERE IsMissing = 0 AND PlaceLandmark IS NOT NULL AND PlaceLandmark != '' AND PlaceLandmark LIKE '%' || $p || '%'
                )
                WHERE val IS NOT NULL
                ORDER BY val
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$p", prefix);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<string>();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns photos that were resolved directly by a geocoding source (amap/osm), i.e. the
    /// "real anchors" that neighbor-reuse may copy from. Reused ("reuse") photos are excluded
    /// so a reused address can never propagate further (no chain transmission).
    /// </summary>
    public async Task<List<PhotoRecord>> GetResolvedAnchorsAsync(int limit = 200000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM Photos
                WHERE IsMissing = 0
                  AND GpsLatitude IS NOT NULL AND GpsLongitude IS NOT NULL
                  AND GpsPlace IS NOT NULL AND GpsPlace != ''
                  AND GpsPlaceSource IN ('amap', 'osm')
                ORDER BY TakenAtUtc DESC, Id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
    /// <summary>
    /// Returns existing photos that do not yet have any auto tag (used by the scene
    /// auto-tagging pass so already-tagged photos are skipped on re-runs).
    /// </summary>
    public async Task<List<PhotoRecord>> GetPhotosWithoutAutoTagsAsync(int limit = 10000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Photos.* FROM Photos
                WHERE Photos.IsMissing = 0
                  AND NOT EXISTS (
                      SELECT 1 FROM PhotoTags pt
                      JOIN Tags t ON t.Id = pt.TagId
                      WHERE pt.PhotoId = Photos.Id AND t.IsAuto = 1)
                ORDER BY Photos.TakenAtUtc DESC, Photos.Id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> GetPhotoCountAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Photos WHERE IsMissing = 0" + HiddenFolderExclusion + ";";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Count of existing (non-missing) photos in a folder including sub-folders.</summary>
    public async Task<long> CountPhotosByDirectoryPrefixAsync(string directoryPath)
    {
        await _gate.WaitAsync();
        try
        {
            string prefix = directoryPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Photos WHERE IsMissing = 0 AND (DirectoryPath = $dir OR DirectoryPath LIKE $prefix)" + HiddenFolderExclusion + ";";
            cmd.Parameters.AddWithValue("$dir", directoryPath);
            cmd.Parameters.AddWithValue("$prefix", prefix + "%");
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Lightweight fingerprint of a previously indexed photo, enough for the
    /// incremental-skip check in <see cref="LibraryService.ScanFolderAsync"/>.
    /// </summary>
    public sealed record PhotoFingerprint(string Path, long Size, DateTime ModifiedUtc, string? ThumbnailCachePath);

    /// <summary>
    /// Returns size/modified-time/thumbnail fingerprints for every photo whose directory
    /// is exactly <paramref name="directoryPath"/> (sub-directories are indexed by their
    /// own scans). Much lighter than <see cref="GetPhotosByDirectoryAsync"/> for huge folders.
    /// </summary>
    public async Task<List<PhotoFingerprint>> GetPhotoFingerprintsByDirectoryAsync(string directoryPath)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT FilePath, FileSizeBytes, FileModifiedUtc, ThumbnailCachePath FROM Photos WHERE DirectoryPath = $dir;";
            cmd.Parameters.AddWithValue("$dir", directoryPath);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoFingerprint>();
            while (reader.Read())
            {
                result.Add(new PhotoFingerprint(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    FromIso(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns every photo whose directory is exactly <paramref name="directoryPath"/>
    /// (sub-directories are indexed by their own scans).
    /// </summary>
    public async Task<List<PhotoRecord>> GetPhotosByDirectoryAsync(string directoryPath)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Photos WHERE DirectoryPath = $dir;";
            cmd.Parameters.AddWithValue("$dir", directoryPath);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns every photo in <paramref name="directoryPath"/> or any of its sub-folders.
    /// </summary>
    public async Task<List<PhotoRecord>> GetPhotosByDirectoryPrefixAsync(string directoryPath)
    {
        await _gate.WaitAsync();
        try
        {
            string prefix = directoryPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Photos WHERE DirectoryPath = $dir OR DirectoryPath LIKE $prefix;";
            cmd.Parameters.AddWithValue("$dir", directoryPath);
            cmd.Parameters.AddWithValue("$prefix", prefix + "%");
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Queries photos with optional folder (sub-tree), camera model, minimum rating, tag,
    /// free-text search and date-range filters. Photos inside folders hidden via the
    /// settings dialog are excluded unless they are explicitly the filter target.
    /// </summary>
    public async Task<List<PhotoRecord>> QueryPhotosAsync(
        string? directoryPath = null,
        string? cameraModel = null,
        int? ratingMin = null,
        string? tag = null,
        string? searchText = null,
        string? dateFrom = null,
        string? dateTo = null,
        int limit = 10000,
        bool excludeHiddenFolders = true)
    {
        return await QueryPhotosCoreAsync(
            SelectAllColumns, directoryPath, cameraModel, ratingMin, tag, searchText, dateFrom, dateTo, limit, excludeHiddenFolders);
    }

    /// <summary>
    /// Lightweight variant of <see cref="QueryPhotosAsync"/> for the home grid: only the
    /// columns needed for thumbnails / dates / aspect / rating are fetched, so 30k photos
    /// never pull the big BLOB columns (CLIP embeddings) or GPS/place text into memory.
    /// </summary>
    public async Task<List<PhotoRecord>> QueryGridPhotosAsync(
        string? directoryPath = null,
        string? cameraModel = null,
        int? ratingMin = null,
        string? tag = null,
        string? searchText = null,
        string? dateFrom = null,
        string? dateTo = null,
        int limit = 10000,
        bool excludeHiddenFolders = true)
    {
        return await QueryPhotosCoreAsync(
            GridPhotoColumns, directoryPath, cameraModel, ratingMin, tag, searchText, dateFrom, dateTo, limit, excludeHiddenFolders);
    }

    /// <summary>
    /// Per-day photo histogram (one representative thumbnail per day) for the calendar view.
    /// Lightweight: groups by date so the whole month is summarized without loading every photo.
    /// </summary>
    public async Task<List<(DateTime Day, int Count, string? Thumb)>> GetDailyHistogramAsync(
        string? directoryPath = null,
        string? cameraModel = null,
        int? ratingMin = null,
        string? tag = null,
        string? searchText = null,
        string? dateFrom = null,
        string? dateTo = null,
        bool excludeHiddenFolders = true)
    {
        var rows = await QueryDailyAsync(directoryPath, cameraModel, ratingMin, tag, searchText, dateFrom, dateTo, includeCity: false, excludeHiddenFolders);
        return rows.Select(r => (r.Day, r.Count, r.Thumb)).ToList();
    }

    /// <summary>
    /// Per-day photo aggregation for the horizontal timeline (date + count + representative thumb + city).
    /// The city feeds the continuous place band on the timeline's top axis.
    /// </summary>
    public async Task<List<(DateTime Day, int Count, string? Thumb, string? City)>> GetDailyTimelineAsync(
        string? directoryPath = null,
        string? cameraModel = null,
        int? ratingMin = null,
        string? tag = null,
        string? searchText = null,
        string? dateFrom = null,
        string? dateTo = null,
        bool excludeHiddenFolders = true)
    {
        return await QueryDailyAsync(directoryPath, cameraModel, ratingMin, tag, searchText, dateFrom, dateTo, includeCity: true, excludeHiddenFolders);
    }

    private async Task<List<(DateTime Day, int Count, string? Thumb, string? City)>> QueryDailyAsync(
        string? directoryPath,
        string? cameraModel,
        int? ratingMin,
        string? tag,
        string? searchText,
        string? dateFrom,
        string? dateTo,
        bool includeCity,
        bool excludeHiddenFolders)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            string cityCol = includeCity ? ", MAX(PlaceCity) AS City" : ", NULL AS City";
            // COALESCE keeps photos whose TakenAtUtc is missing (falls back to FileModifiedUtc),
            // matching the home grid's day attribution so the calendar/timeline never drops them.
            cmd.CommandText = $"""
                SELECT substr(COALESCE(Photos.TakenAtUtc, Photos.FileModifiedUtc), 1, 10) AS Day, COUNT(*) AS Cnt,
                       MAX(CASE WHEN Photos.ThumbnailCachePath IS NOT NULL THEN Photos.ThumbnailCachePath END) AS Thumb
                       {cityCol}
                FROM Photos
                WHERE IsMissing = 0
                  AND ($dir IS NULL OR DirectoryPath = $dir OR DirectoryPath LIKE $prefix)
                  AND ($camera IS NULL OR CameraModel = $camera)
                  AND ($rating IS NULL OR Rating >= $rating)
                  AND ($tag IS NULL OR EXISTS(
                        SELECT 1 FROM PhotoTags pt JOIN Tags t ON t.Id = pt.TagId
                        WHERE pt.PhotoId = Photos.Id AND t.Name = $tag))
                  AND ($search IS NULL OR $search = '' OR
                        Photos.FileName LIKE '%' || $search || '%'
                        OR Photos.CameraModel LIKE '%' || $search || '%'
                        OR Photos.LensModel LIKE '%' || $search || '%'
                        OR Photos.DirectoryPath LIKE '%' || $search || '%'
                        OR Photos.TakenAtUtc LIKE $search || '%'
                        OR EXISTS(SELECT 1 FROM PhotoTags pt2 JOIN Tags t2 ON t2.Id = pt2.TagId
                                  WHERE pt2.PhotoId = Photos.Id AND t2.Name LIKE '%' || $search || '%'))
                  AND ($dateFrom IS NULL OR substr(COALESCE(Photos.TakenAtUtc, Photos.FileModifiedUtc), 1, 10) >= $dateFrom)
                  AND ($dateTo IS NULL OR substr(COALESCE(Photos.TakenAtUtc, Photos.FileModifiedUtc), 1, 10) <= $dateTo)
                """ + (excludeHiddenFolders ? HiddenFolderExclusion : "") + """
                GROUP BY substr(COALESCE(Photos.TakenAtUtc, Photos.FileModifiedUtc), 1, 10)
                ORDER BY Day ASC;
                """;
            cmd.Parameters.AddWithValue("$dir", (object?)directoryPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$prefix", (object?)(directoryPath?.TrimEnd('\\', '/') + Path.DirectorySeparatorChar + "%") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$camera", (object?)cameraModel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rating", (object?)ratingMin ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tag", (object?)tag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$search", (object?)searchText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dateFrom", (object?)dateFrom ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dateTo", (object?)dateTo ?? DBNull.Value);
            using var reader = cmd.ExecuteReader();
            var result = new List<(DateTime Day, int Count, string? Thumb, string? City)>();
            while (reader.Read())
            {
                var dayStr = reader.GetString(0);
                var count = reader.GetInt64(1);
                var thumb = reader.IsDBNull(2) ? null : reader.GetString(2);
                var city = includeCity && !reader.IsDBNull(3) ? reader.GetString(3) : null;
                if (DateTime.TryParse(dayStr, out var day))
                {
                    result.Add((day.Date, (int)count, thumb, city));
                }
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private const string SelectAllColumns = "*";

    /// <summary>Column list for the grid: excludes embeddings, GPS/place text, AI scores, etc.</summary>
    private const string GridPhotoColumns = """
        Photos.Id, Photos.FilePath, Photos.FileName, Photos.DirectoryPath, Photos.Extension,
        Photos.Kind, Photos.FileSizeBytes, Photos.FileModifiedUtc, Photos.ContentHash,
        Photos.TakenAtUtc, Photos.CameraMake, Photos.CameraModel, Photos.LensModel, Photos.Iso,
        Photos.ShutterSpeed, Photos.Aperture, Photos.FocalLengthMm, Photos.Width, Photos.Height,
        Photos.Orientation, Photos.GpsLatitude, Photos.GpsLongitude, Photos.GpsAltitude,
        Photos.Artist, Photos.Description, Photos.Copyright, Photos.Rating, Photos.Tags,
        Photos.ThumbnailCachePath, Photos.PHash, Photos.IndexedAtUtc, Photos.IsMissing,
        Photos.BlurScore, Photos.AiAnalyzedAtUtc, Photos.AestheticScore, Photos.DominantColors,
        Photos.IsMono, Photos.GpsPlace, Photos.PlaceCountry, Photos.PlaceProvince,
        Photos.PlaceCity, Photos.PlaceDistrict, Photos.PlaceLandmark
        """;

    private async Task<List<PhotoRecord>> QueryPhotosCoreAsync(
        string columns,
        string? directoryPath,
        string? cameraModel,
        int? ratingMin,
        string? tag,
        string? searchText,
        string? dateFrom,
        string? dateTo,
        int limit,
        bool excludeHiddenFolders)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {columns} FROM Photos
                WHERE IsMissing = 0
                  AND ($dir IS NULL OR DirectoryPath = $dir OR DirectoryPath LIKE $prefix)
                  AND ($camera IS NULL OR CameraModel = $camera)
                  AND ($rating IS NULL OR Rating >= $rating)
                  AND ($tag IS NULL OR EXISTS(
                        SELECT 1 FROM PhotoTags pt JOIN Tags t ON t.Id = pt.TagId
                        WHERE pt.PhotoId = Photos.Id AND t.Name = $tag))
                  AND ($search IS NULL OR $search = '' OR
                        Photos.FileName LIKE '%' || $search || '%'
                        OR Photos.CameraModel LIKE '%' || $search || '%'
                        OR Photos.LensModel LIKE '%' || $search || '%'
                        OR Photos.DirectoryPath LIKE '%' || $search || '%'
                        OR Photos.TakenAtUtc LIKE $search || '%'
                        OR EXISTS(SELECT 1 FROM PhotoTags pt2 JOIN Tags t2 ON t2.Id = pt2.TagId
                                  WHERE pt2.PhotoId = Photos.Id AND t2.Name LIKE '%' || $search || '%'))
                  AND ($dateFrom IS NULL OR substr(Photos.TakenAtUtc, 1, 10) >= $dateFrom)
                  AND ($dateTo IS NULL OR substr(Photos.TakenAtUtc, 1, 10) <= $dateTo)
                """ + (excludeHiddenFolders ? HiddenFolderExclusion : "") + """
                ORDER BY TakenAtUtc DESC, Id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$dir", (object?)directoryPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$prefix", (object?)(directoryPath?.TrimEnd('\\', '/') + Path.DirectorySeparatorChar + "%") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$camera", (object?)cameraModel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rating", (object?)ratingMin ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tag", (object?)tag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$search", (object?)searchText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dateFrom", (object?)dateFrom ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dateTo", (object?)dateTo ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            bool full = columns == SelectAllColumns;
            while (reader.Read())
            {
                result.Add(full ? ReadPhoto(reader) : ReadGridPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the distinct matching file names for the search suggestion box
    /// (searching by file name, tag, camera and date).
    /// </summary>
    public async Task<List<string>> GetSearchSuggestionsAsync(string query, int limit = 10)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT Photos.FileName FROM Photos
                WHERE IsMissing = 0
                  AND (Photos.FileName LIKE '%' || $q || '%'
                       OR Photos.CameraModel LIKE '%' || $q || '%'
                       OR Photos.TakenAtUtc LIKE $q || '%'
                       OR EXISTS(SELECT 1 FROM PhotoTags pt JOIN Tags t ON t.Id = pt.TagId
                                 WHERE pt.PhotoId = Photos.Id AND t.Name LIKE '%' || $q || '%'))
                """ + HiddenFolderExclusion + """
                ORDER BY Photos.TakenAtUtc DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$q", query);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<string>();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Distinct camera models with photo counts (most common first).</summary>
    public async Task<List<(string Model, long Count)>> GetCameraModelsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT CameraModel, COUNT(*) FROM Photos
                WHERE IsMissing = 0 AND CameraModel IS NOT NULL AND CameraModel != ''
                """ + HiddenFolderExclusion + """
                GROUP BY CameraModel
                ORDER BY COUNT(*) DESC, CameraModel;
                """;
            using var reader = cmd.ExecuteReader();
            var result = new List<(string, long)>();
            while (reader.Read())
            {
                result.Add((reader.GetString(0), reader.GetInt64(1)));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Directory path → photo count (existing photos only).</summary>
    /// <summary>
    /// Directory → photo count, where every folder counts its own photos AND those in any
    /// subfolder (matching how the recursive folder filter works). The SQL only groups by the
    /// exact path; the roll-up to include descendants is done in memory over the (small)
    /// set of distinct directories, so it stays fast even for large libraries.
    /// </summary>
    public async Task<Dictionary<string, long>> GetDirectoryCountsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DirectoryPath, COUNT(*) FROM Photos WHERE IsMissing = 0" + HiddenFolderExclusion + " GROUP BY DirectoryPath;";
            using var reader = cmd.ExecuteReader();
            var exact = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                exact[reader.GetString(0)] = reader.GetInt64(1);
            }
            return RollUpDirectoryCounts(exact);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Each folder's total = its own photos + all descendant folders' photos.
    /// Intermediate directories with no direct photos still get an entry (count 0 plus
    /// the roll-up from their subfolders), so the sidebar tree shows them correctly.</summary>
    private static Dictionary<string, long> RollUpDirectoryCounts(Dictionary<string, long> exact)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, count) in exact)
        {
            var current = dir.TrimEnd('\\', '/');
            while (current.Length > 0)
            {
                result[current] = result.GetValueOrDefault(current) + count;
                var parent = GetParentPath(current);
                if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = parent;
            }
        }
        return result;
    }

    private static string? GetParentPath(string path)
    {
        var i = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        if (i <= 0)
        {
            return null;
        }
        return path[..i];
    }

    /// <summary>
    /// Updates a photo's path after a file rename, preserving the row id, tags and rating.
    /// </summary>
    public async Task RenamePhotoPathAsync(string oldPath, string newPath)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Photos SET FilePath = $new, FileName = $name, DirectoryPath = $dir WHERE FilePath = $old;";
            cmd.Parameters.AddWithValue("$new", newPath);
            cmd.Parameters.AddWithValue("$name", Path.GetFileName(newPath));
            cmd.Parameters.AddWithValue("$dir", Path.GetDirectoryName(newPath) ?? "");
            cmd.Parameters.AddWithValue("$old", oldPath);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Bulk-writes GPS coordinates for the given photo ids (used by GPX back-fill).</summary>
    public async Task BulkSetGpsAsync(IReadOnlyList<(long Id, double Lat, double Lon, double? Alt)> items)
    {
        if (items.Count == 0)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Photos SET GpsLatitude = $lat, GpsLongitude = $lon, GpsAltitude = $alt WHERE Id = $id;";
            foreach (var (id, lat, lon, alt) in items)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$lat", lat);
                cmd.Parameters.AddWithValue("$lon", lon);
                cmd.Parameters.AddWithValue("$alt", (object?)alt ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkMissingAsync(string path, bool missing = true)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Photos SET IsMissing = $missing WHERE FilePath = $path;";
            cmd.Parameters.AddWithValue("$missing", missing ? 1 : 0);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes a photo row (used when the file itself is removed by the user).</summary>
    public async Task DeletePhotoAsync(string path)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM PhotoTags WHERE PhotoId = (SELECT Id FROM Photos WHERE FilePath = $path); DELETE FROM Photos WHERE FilePath = $path;";
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs <c>PRAGMA integrity_check</c> and returns its output ("ok" when healthy).</summary>
    public async Task<string> RunIntegrityCheckAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            // quick_check 只做核心结构/btree 校验，比 integrity_check 快很多（后者还会逐页
            // 校验 freelist 与文本编码），对大型库能显著缩短「数据库维护 → 清理」的等待。
            cmd.CommandText = "PRAGMA quick_check;";
            using var reader = cmd.ExecuteReader();
            var lines = new List<string>();
            while (reader.Read())
            {
                lines.Add(reader.GetString(0));
            }
            return string.Join("\n", lines);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Total and active (not missing) photo row counts.</summary>
    public async Task<(long Total, long Active)> GetTotalAndActivePhotoCountsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN IsMissing = 0 THEN 1 ELSE 0 END), 0)
                FROM Photos;
                """;
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return (reader.GetInt64(0), reader.GetInt64(1));
            }
            return (0, 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Every photo row marked missing (file deleted externally), with its grid thumbnail path.</summary>
    public async Task<List<(string FilePath, string? ThumbnailCachePath)>> GetMissingPhotosAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT FilePath, ThumbnailCachePath FROM Photos WHERE IsMissing = 1;";
            using var reader = cmd.ExecuteReader();
            var result = new List<(string, string?)>();
            while (reader.Read())
            {
                result.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>All indexed photo file paths (used to detect orphaned thumbnail cache files).</summary>
    public async Task<List<string>> GetAllPhotoFilePathsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT FilePath FROM Photos;";
            using var reader = cmd.ExecuteReader();
            var result = new List<string>();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Counts tag/face links whose referenced photo row no longer exists.</summary>
    public async Task<(long Tags, long Faces)> CountOrphanLinksAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM PhotoTags pt WHERE NOT EXISTS (SELECT 1 FROM Photos p WHERE p.Id = pt.PhotoId)),
                    (SELECT COUNT(*) FROM Faces f WHERE NOT EXISTS (SELECT 1 FROM Photos p WHERE p.Id = f.PhotoId));
                """;
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return (reader.GetInt64(0), reader.GetInt64(1));
            }
            return (0, 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes tag/face links whose referenced photo row no longer exists. Returns the removed counts.</summary>
    public async Task<(long Tags, long Faces)> DeleteOrphanLinksAsync()
    {
        await _gate.WaitAsync();
        try
        {
            long orphanTags, orphanFaces;
            using (var conn = Open())
            {
                using (var tags = conn.CreateCommand())
                {
                    tags.CommandText = "DELETE FROM PhotoTags WHERE NOT EXISTS (SELECT 1 FROM Photos p WHERE p.Id = PhotoTags.PhotoId);";
                    tags.ExecuteNonQuery();
                }
                using (var faces = conn.CreateCommand())
                {
                    faces.CommandText = "DELETE FROM Faces WHERE NOT EXISTS (SELECT 1 FROM Photos p WHERE p.Id = Faces.PhotoId);";
                    faces.ExecuteNonQuery();
                }
                // Count the freshly-removed orphans on the same connection (do NOT re-enter
                // the gate — SemaphoreSlim is non-reentrant and would deadlock).
                using (var count = conn.CreateCommand())
                {
                    count.CommandText = "SELECT (SELECT COUNT(*) FROM PhotoTags WHERE NOT EXISTS (SELECT 1 FROM Photos p WHERE p.Id = PhotoTags.PhotoId))," +
                                        "       (SELECT COUNT(*) FROM Faces    WHERE NOT EXISTS (SELECT 1 FROM Photos p WHERE p.Id = Faces.PhotoId));";
                    using var r = count.ExecuteReader();
                    r.Read();
                    orphanTags = r.GetInt64(0);
                    orphanFaces = r.GetInt64(1);
                }
            }
            return (orphanTags, orphanFaces);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Empties the whole library index in one transaction: photos, folders, tags,
    /// tag links, faces and smart albums are all deleted. The database file itself
    /// (and its settings) is kept — callers should also clear the thumbnail cache
    /// and stop the folder watchers. Not reversible.
    /// </summary>
    public async Task ResetLibraryAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM PhotoTags;
                DELETE FROM Faces;
                DELETE FROM Photos;
                DELETE FROM Tags;
                DELETE FROM SmartAlbums;
                DELETE FROM Folders;
                """;
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes the given photo rows plus their tag and face links in one transaction.
    /// Returns how many photo rows were actually removed.
    /// </summary>
    public async Task<int> DeleteMissingPhotosAsync(IReadOnlyList<string> filePaths)    {
        if (filePaths.Count == 0)
        {
            return 0;
        }
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM Faces WHERE PhotoId IN (SELECT Id FROM Photos WHERE FilePath = $path);
                DELETE FROM PhotoTags WHERE PhotoId IN (SELECT Id FROM Photos WHERE FilePath = $path);
                DELETE FROM Photos WHERE FilePath = $path;
                SELECT changes();
                """;
            int removed = 0;
            foreach (var path in filePaths)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$path", path);
                removed += Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            tx.Commit();
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Bulk-writes vision/AI analysis results (pHash, Laplacian blur score, analyzed-at)
    /// for the given photo ids. Used by the phase-4 analysis pass.
    /// </summary>
    public async Task BulkSetVisionAsync(IReadOnlyList<(long Id, string? PHash, double? BlurScore, DateTime AnalyzedAtUtc)> items)
    {
        if (items.Count == 0)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Photos SET PHash = $phash, BlurScore = $blur, AiAnalyzedAtUtc = $analyzed WHERE Id = $id;";
            foreach (var (id, phash, blur, analyzed) in items)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$phash", (object?)phash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$blur", (object?)blur ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$analyzed", ToIso(analyzed));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Bulk-writes the NIMA aesthetic score for the given photo ids (used by the
    /// low-quality-cleanup tool when only the aesthetic pass needs to catch up).
    /// </summary>
    public async Task BulkSetAestheticAsync(IReadOnlyList<(long Id, double Score)> items)
    {
        if (items.Count == 0)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Photos SET AestheticScore = $score, DeepAnalyzedAtUtc = COALESCE(DeepAnalyzedAtUtc, $now) WHERE Id = $id;";
            foreach (var (id, score) in items)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$score", score);
                cmd.Parameters.AddWithValue("$now", ToIso(DateTime.UtcNow));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns existing (non-missing) photos whose vision analysis has not run yet
    /// (pHash and blur score both null). Sorted newest-first, capped at <paramref name="limit"/>.
    /// </summary>
    public async Task<List<PhotoRecord>> GetPhotosPendingVisionAsync(int limit = 10000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Photos WHERE IsMissing = 0 AND PHash IS NULL AND BlurScore IS NULL ORDER BY TakenAtUtc DESC, Id DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns photos whose Laplacian blur score is at or below <paramref name="maxBlurScore"/>
    /// (lower = more blurry), newest first, capped at <paramref name="limit"/>.
    /// </summary>
    public async Task<List<PhotoRecord>> GetBlurryPhotosAsync(double maxBlurScore, int limit = 10000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Photos WHERE IsMissing = 0 AND BlurScore IS NOT NULL AND BlurScore <= $max ORDER BY BlurScore ASC, TakenAtUtc DESC, Id DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$max", maxBlurScore);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Count of existing photos that have a stored blur score.</summary>
    public async Task<long> CountAnalyzedPhotosAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Photos WHERE IsMissing = 0 AND BlurScore IS NOT NULL" + HiddenFolderExclusion + ";";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- Deep analysis (color / aesthetic / embedding / objects) ----------

    /// <summary>
    /// Bulk-writes the deep-analysis results (color palette, mono flag, aesthetic score,
    /// feature embeddings, YOLO objects) for the given photo ids. A row is only written for
    /// ids present in <paramref name="items"/>; null fields are written as NULL.
    /// </summary>
    public async Task BulkSetDeepAnalysisAsync(IReadOnlyList<DeepAnalysisRow> items)
    {
        if (items.Count == 0)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE Photos SET
                    AestheticScore = $aesthetic,
                    DominantColors = $colors,
                    IsMono = $mono,
                    Embedding = $embedding,
                    ClipEmbedding = $clip,
                    ObjectsJson = $objects,
                    DeepAnalyzedAtUtc = $deepAnalyzed
                WHERE Id = $id;
                """;
            foreach (var r in items)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", r.Id);
                cmd.Parameters.AddWithValue("$aesthetic", (object?)r.AestheticScore ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$colors", (object?)r.DominantColors ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$mono", r.IsMono ? 1 : 0);
                cmd.Parameters.AddWithValue("$embedding", (object?)r.Embedding ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$clip", (object?)r.ClipEmbedding ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$objects", (object?)r.ObjectsJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$deepAnalyzed", ToIso(r.EffectiveAnalyzedAt));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns existing photos whose deep analysis has not run yet (DeepAnalyzedAtUtc null).
    /// Sorted newest-first, capped at <paramref name="limit"/>.
    /// </summary>
    public async Task<List<PhotoRecord>> GetPhotosPendingDeepAnalysisAsync(int limit = 10000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Photos WHERE IsMissing = 0 AND DeepAnalyzedAtUtc IS NULL ORDER BY TakenAtUtc DESC, Id DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns existing photos that lack a NIMA aesthetic score (AestheticScore null).
    /// Sorted newest-first, capped at <paramref name="limit"/>.
    /// </summary>
    public async Task<List<PhotoRecord>> GetPhotosPendingAestheticAsync(int limit = 10000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Photos WHERE IsMissing = 0 AND AestheticScore IS NULL ORDER BY TakenAtUtc DESC, Id DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns existing photos whose MobileCLIP image embedding has not been computed yet.
    /// Used by the semantic-search indexing pass (deep analysis computes colors/aesthetic
    /// regardless; clip embeddings are optional and depend on the CLIP models being installed).
    /// </summary>
    public async Task<List<PhotoRecord>> GetPhotosPendingClipEmbeddingAsync(int limit = 10000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Photos WHERE IsMissing = 0 AND ClipEmbedding IS NULL ORDER BY TakenAtUtc DESC, Id DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Low-scoring (aesthetically weak / "无意义") photos, worst first.</summary>
    public async Task<List<PhotoRecord>> GetLowAestheticPhotosAsync(double maxScore, int limit = 10000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Photos WHERE IsMissing = 0 AND AestheticScore IS NOT NULL AND AestheticScore <= $max ORDER BY AestheticScore ASC, TakenAtUtc DESC, Id DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$max", maxScore);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Monochrome (B/W or sepia) photos, newest first.</summary>
    public async Task<List<PhotoRecord>> GetMonoPhotosAsync(int limit = 10000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Photos WHERE IsMissing = 0 AND IsMono = 1 ORDER BY TakenAtUtc DESC, Id DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>All photos that have a MobileCLIP embedding, for in-memory similarity search.</summary>
    public async Task<List<(long Id, byte[] Embedding)>> GetAllClipEmbeddingsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, ClipEmbedding FROM Photos WHERE IsMissing = 0 AND ClipEmbedding IS NOT NULL;";
            using var reader = cmd.ExecuteReader();
            var result = new List<(long, byte[])>();
            while (reader.Read())
            {
                result.Add((reader.GetInt64(0), (byte[])reader.GetValue(1)));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Count of existing photos that have had the deep analysis pass run.</summary>
    public async Task<long> CountDeepAnalyzedPhotosAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Photos WHERE IsMissing = 0 AND DeepAnalyzedAtUtc IS NOT NULL;";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns photos flagged as low quality: either blurry (BlurScore at or below
    /// <paramref name="maxBlur"/>) or aesthetically weak (AestheticScore at or below
    /// <paramref name="maxAesthetic"/>). Used by the 低质量照片清理 tool.
    /// </summary>
    public async Task<List<PhotoRecord>> GetLowQualityPhotosAsync(double maxBlur, double maxAesthetic, int limit = 100000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM Photos
                WHERE IsMissing = 0
                  AND ((BlurScore IS NOT NULL AND BlurScore <= $maxBlur)
                       OR (AestheticScore IS NOT NULL AND AestheticScore <= $maxAesthetic))
                ORDER BY TakenAtUtc DESC, Id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$maxBlur", maxBlur);
            cmd.Parameters.AddWithValue("$maxAesthetic", maxAesthetic);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Bulk-inserts detected faces (bounding box + 512-dim embedding) for the given photos.
    /// Existing faces of the same photo are replaced (a re-run overwrites stale detections).
    /// </summary>
    public async Task BulkUpsertFacesAsync(IReadOnlyList<FaceRow> faces)
    {
        if (faces.Count == 0)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO Faces (PhotoId, BoxX, BoxY, BoxW, BoxH, Score, Embedding, PersonId, FaceAnalyzedAtUtc)
                VALUES ($photoId, $boxX, $boxY, $boxW, $boxH, $score, $emb, NULL, $analyzed)
                """;
            foreach (var f in faces)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$photoId", f.PhotoId);
                cmd.Parameters.AddWithValue("$boxX", f.BoxX);
                cmd.Parameters.AddWithValue("$boxY", f.BoxY);
                cmd.Parameters.AddWithValue("$boxW", f.BoxW);
                cmd.Parameters.AddWithValue("$boxH", f.BoxH);
                cmd.Parameters.AddWithValue("$score", f.Score);
                cmd.Parameters.AddWithValue("$emb", f.EmbeddingBytes);
                cmd.Parameters.AddWithValue("$analyzed", ToIso(f.AnalyzedAtUtc));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes every stored face (used when re-running face analysis from scratch).</summary>
    public async Task DeleteAllFacesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Faces;";
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns every stored face with its embedding (all photos).</summary>
    public async Task<List<FaceRow>> GetAllFacesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, PhotoId, BoxX, BoxY, BoxW, BoxH, Score, Embedding, PersonId, FaceAnalyzedAtUtc, PersonName FROM Faces ORDER BY Id;";
            using var reader = cmd.ExecuteReader();
            var result = new List<FaceRow>();
            while (reader.Read())
            {
                result.Add(new FaceRow(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5),
                    reader.GetDouble(6),
                    (byte[])reader.GetValue(7),
                    reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    FromIso(reader.GetString(9)),
                    reader.IsDBNull(10) ? null : reader.GetString(10)));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Bulk-assigns person clusters (and their display name) to face ids.</summary>
    public async Task BulkSetPersonAsync(IReadOnlyList<(long FaceId, long PersonId, string? PersonName)> items)
    {
        if (items.Count == 0)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Faces SET PersonId = $person, PersonName = $name WHERE Id = $id;";
            foreach (var (id, person, name) in items)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$person", person);
                cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns every person cluster (faces grouped by <c>PersonId</c>) with face/photo counts
    /// and the path of a representative photo for the album card. Ordered by face count desc.
    /// </summary>
    public async Task<List<PersonClusterInfo>> GetPersonClustersAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT f.PersonId,
                       COUNT(f.Id) AS FaceCount,
                       COUNT(DISTINCT f.PhotoId) AS PhotoCount,
                       (SELECT Photos.FilePath FROM Photos
                        WHERE Photos.Id = (SELECT MIN(f2.PhotoId) FROM Faces f2 WHERE f2.PersonId = f.PersonId))
                       AS RepPath,
                       MAX(f.PersonName) AS PersonName
                FROM Faces f
                WHERE f.PersonId IS NOT NULL
                GROUP BY f.PersonId
                ORDER BY FaceCount DESC, f.PersonId;
                """;
            using var reader = cmd.ExecuteReader();
            var result = new List<PersonClusterInfo>();
            while (reader.Read())
            {
                result.Add(new PersonClusterInfo(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns every photo that has at least one face assigned to <paramref name="personId"/>,
    /// newest first. Used to filter the photo view by a person.
    /// </summary>
    public async Task<List<PhotoRecord>> GetPhotosByPersonAsync(long personId, int limit = 10000)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT DISTINCT {GridPhotoColumns} FROM Photos
                JOIN Faces f ON f.PhotoId = Photos.Id
                WHERE Photos.IsMissing = 0 AND f.PersonId = $person
                ORDER BY Photos.TakenAtUtc DESC, Photos.Id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$person", personId);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var result = new List<PhotoRecord>();
            while (reader.Read())
            {
                result.Add(ReadGridPhoto(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Assigns (or clears, when <paramref name="name"/> is empty) a display name to every
    /// face of a person cluster.
    /// </summary>
    public async Task RenamePersonAsync(long personId, string? name)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Faces SET PersonName = $name WHERE PersonId = $person;";
            cmd.Parameters.AddWithValue("$name", (object?)(string.IsNullOrWhiteSpace(name) ? null : name.Trim()) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$person", personId);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Merges the <paramref name="fromPersonId"/> cluster into <paramref name="toPersonId"/>:
    /// all of the source cluster's faces are re-assigned to the target (adopting the target's
    /// name). The source cluster no longer exists afterward.
    /// </summary>
    public async Task MergePeopleAsync(long fromPersonId, long toPersonId)
    {
        if (fromPersonId == toPersonId)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Faces SET PersonId = $to, PersonName = (SELECT PersonName FROM Faces WHERE PersonId = $to LIMIT 1) WHERE PersonId = $from;";
            cmd.Parameters.AddWithValue("$to", toPersonId);
            cmd.Parameters.AddWithValue("$from", fromPersonId);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes a person cluster entirely: removes every face row assigned to that
    /// <paramref name="personId"/>. The photos themselves are untouched.
    /// </summary>
    public async Task DeletePersonAsync(long personId)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Faces WHERE PersonId = $person;";
            cmd.Parameters.AddWithValue("$person", personId);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> UpsertFolderAsync(FolderRecord f)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Folders (Path, LastScannedUtc, IsWatched, IsHidden, AddedUtc)
                VALUES ($path, $lastScanned, $watched, $hidden, $added)
                ON CONFLICT(Path) DO UPDATE SET
                    LastScannedUtc = $lastScanned,
                    IsWatched = $watched,
                    IsHidden = $hidden;
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$path", f.Path);
            cmd.Parameters.AddWithValue("$lastScanned", (object?)ToIsoNullable(f.LastScannedUtc) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$watched", f.IsWatched ? 1 : 0);
            cmd.Parameters.AddWithValue("$hidden", f.IsHidden ? 1 : 0);
            cmd.Parameters.AddWithValue("$added", ToIso(f.AddedUtc));
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<FolderRecord>> GetFoldersAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Folders ORDER BY Path;";
            using var reader = cmd.ExecuteReader();
            int iId = reader.GetOrdinal("Id");
            int iPath = reader.GetOrdinal("Path");
            int iLast = reader.GetOrdinal("LastScannedUtc");
            int iWatched = reader.GetOrdinal("IsWatched");
            int iHidden = reader.GetOrdinal("IsHidden");
            int iAdded = reader.GetOrdinal("AddedUtc");
            var result = new List<FolderRecord>();
            while (reader.Read())
            {
                result.Add(new FolderRecord
                {
                    Id = reader.GetInt64(iId),
                    Path = reader.GetString(iPath),
                    LastScannedUtc = reader.IsDBNull(iLast) ? null : FromIso(reader.GetString(iLast)),
                    IsWatched = reader.GetInt64(iWatched) != 0,
                    IsHidden = reader.GetInt64(iHidden) != 0,
                    AddedUtc = FromIso(reader.GetString(iAdded)),
                });
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetFolderHiddenAsync(string path, bool hidden)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Folders SET IsHidden = $hidden WHERE Path = $path;";
            cmd.Parameters.AddWithValue("$hidden", hidden ? 1 : 0);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Distinct directories that contain indexed photos (used to self-heal folder records).</summary>
    public async Task<List<string>> GetPhotoDirectoriesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT DirectoryPath FROM Photos;";
            using var reader = cmd.ExecuteReader();
            var result = new List<string>();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes a folder from the library: its Folders row (including any nested folder
    /// rows), every photo under it and their tag links. Returns the thumbnail cache
    /// paths that were removed so the caller can delete the files on disk.
    /// </summary>
    public async Task<List<string>> RemoveFolderAsync(string path)
    {
        await _gate.WaitAsync();
        try
        {
            string prefix = path.TrimEnd('\\', '/') + "\\%";
            var thumbs = new List<string>();
            using (var conn = Open())
            {
                using var tx = conn.BeginTransaction();
                using (var q = conn.CreateCommand())
                {
                    q.Transaction = tx;
                    q.CommandText = "SELECT ThumbnailCachePath FROM Photos WHERE DirectoryPath = $path OR DirectoryPath LIKE $prefix;";
                    q.Parameters.AddWithValue("$path", path);
                    q.Parameters.AddWithValue("$prefix", prefix);
                    using var reader = q.ExecuteReader();
                    while (reader.Read())
                    {
                        if (!reader.IsDBNull(0) && !string.IsNullOrEmpty(reader.GetString(0)))
                        {
                            thumbs.Add(reader.GetString(0));
                        }
                    }
                }
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = """
                    DELETE FROM PhotoTags WHERE PhotoId IN (
                        SELECT Id FROM Photos WHERE DirectoryPath = $path OR DirectoryPath LIKE $prefix);
                    DELETE FROM Photos WHERE DirectoryPath = $path OR DirectoryPath LIKE $prefix;
                    DELETE FROM Folders WHERE Path = $path OR Path LIKE $prefix;
                    """;
                del.Parameters.AddWithValue("$path", path);
                del.Parameters.AddWithValue("$prefix", prefix);
                del.ExecuteNonQuery();
                tx.Commit();
            }
            return thumbs;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> UpsertSmartAlbumAsync(SmartAlbum album)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO SmartAlbums (Name, FilterJson, CreatedUtc)
                VALUES ($name, $filter, $created)
                ON CONFLICT(Id) DO UPDATE SET
                    Name = $name,
                    FilterJson = $filter;
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$name", album.Name);
            cmd.Parameters.AddWithValue("$filter", album.FilterJson);
            cmd.Parameters.AddWithValue("$created", ToIso(album.CreatedUtc));
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<SmartAlbum>> GetSmartAlbumsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, FilterJson, CreatedUtc FROM SmartAlbums ORDER BY Name;";
            using var reader = cmd.ExecuteReader();
            var result = new List<SmartAlbum>();
            while (reader.Read())
            {
                result.Add(new SmartAlbum
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    FilterJson = reader.GetString(2),
                    CreatedUtc = FromIso(reader.GetString(3)),
                });
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteSmartAlbumAsync(long id)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM SmartAlbums WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddTagAsync(long photoId, string name, bool isAuto)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Tags (Name, IsAuto) VALUES ($name, $isAuto)
                ON CONFLICT(Name) DO UPDATE SET IsAuto = MIN(Tags.IsAuto, $isAuto);
                SELECT Id FROM Tags WHERE Name = $name;
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$isAuto", isAuto ? 1 : 0);
            long tagId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

            using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "INSERT INTO PhotoTags (PhotoId, TagId) VALUES ($pid, $tid) ON CONFLICT DO NOTHING;";
            cmd2.Parameters.AddWithValue("$pid", photoId);
            cmd2.Parameters.AddWithValue("$tid", tagId);
            cmd2.ExecuteNonQuery();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveTagAsync(long photoId, string name)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM PhotoTags WHERE PhotoId = $pid
                  AND TagId = (SELECT Id FROM Tags WHERE Name = $name);
                """;
            cmd.Parameters.AddWithValue("$pid", photoId);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<TagRecord>> GetPhotoTagsAsync(long photoId)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT t.Name, t.IsAuto FROM PhotoTags pt
                JOIN Tags t ON t.Id = pt.TagId
                WHERE pt.PhotoId = $pid ORDER BY t.Name;
                """;
            cmd.Parameters.AddWithValue("$pid", photoId);
            using var reader = cmd.ExecuteReader();
            var result = new List<TagRecord>();
            while (reader.Read())
            {
                result.Add(new TagRecord(reader.GetString(0), reader.GetInt64(1) != 0));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<TagRecord>> GetTagsWithCountsAsync(bool isAuto)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT t.Name, COUNT(pt.PhotoId) FROM Tags t
                JOIN PhotoTags pt ON pt.TagId = t.Id
                JOIN Photos ON Photos.Id = pt.PhotoId
                WHERE t.IsAuto = $isAuto AND Photos.IsMissing = 0
                """ + HiddenFolderExclusion + """
                GROUP BY t.Name ORDER BY COUNT(pt.PhotoId) DESC, t.Name;
                """;
            cmd.Parameters.AddWithValue("$isAuto", isAuto ? 1 : 0);
            using var reader = cmd.ExecuteReader();
            var result = new List<TagRecord>();
            while (reader.Read())
            {
                result.Add(new TagRecord(reader.GetString(0), isAuto) { Count = reader.GetInt64(1) });
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes every auto (AI) tag and its photo links, so photos can be re-tagged from
    /// scratch. Tags that were also created manually (IsAuto = 0) are left untouched.
    /// </summary>
    public async Task DeleteAllAutoTagsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM PhotoTags WHERE TagId IN (SELECT Id FROM Tags WHERE IsAuto = 1);
                DELETE FROM Tags WHERE IsAuto = 1;
                """;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Writes a consistent snapshot of the database to <paramref name="destinationPath"/>
    /// using the SQLite online-backup API. Safe under WAL: un-checkpointed frames are
    /// included in the snapshot, so the copy is never missing recent writes.
    /// </summary>
    public async Task BackupToAsync(string destinationPath)
    {
        await _gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                var dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                using var source = new SqliteConnection(_connectionString);
                source.Open();
                using var destination = new SqliteConnection($"Data Source={destinationPath}");
                destination.Open();
                source.BackupDatabase(destination);
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces the live database with the contents of <paramref name="sourcePath"/>
    /// (a backup produced by <see cref="BackupToAsync"/>). The live DB and its WAL/SHM
    /// sidecars are deleted and the backup copied over so the next connection starts fresh.
    /// Rejects files that are not a valid MyAlbum backup.
    /// </summary>
    public async Task RestoreFromAsync(string sourcePath)
    {
        await _gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException("找不到备份文件。", sourcePath);
                }

                // Probe the file before touching the live DB: it must open as a SQLite
                // database that contains our Photos table.
                using (var probe = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly"))
                {
                    probe.Open();
                    using var check = probe.CreateCommand();
                    check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Photos';";
                    if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                    {
                        throw new InvalidDataException("所选文件不是有效的 MyAlbum 数据库备份。");
                    }
                }

                // Drop every pooled connection so no live handle pins the files we delete.
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var live = _databasePath + suffix;
                    if (File.Exists(live))
                    {
                        File.Delete(live);
                    }
                }
                File.Copy(sourcePath, _databasePath, overwrite: true);
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private async Task ExecuteAsync(Action<SqliteConnection> action)
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = Open();
            action(conn);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static PhotoRecord ReadPhoto(SqliteDataReader r)
    {
        int i = 0;
        PhotoRecord p = new()
        {
            Id = r.GetInt64(i++),
            FilePath = r.GetString(i++),
            FileName = r.GetString(i++),
            DirectoryPath = r.GetString(i++),
            Extension = r.GetString(i++),
            Kind = (PhotoKind)r.GetInt32(i++),
            FileSizeBytes = r.GetInt64(i++),
            FileModifiedUtc = FromIso(r.GetString(i++)),
            ContentHash = GetNullableString(r, ref i),
            TakenAtUtc = FromIsoNullable(GetNullableString(r, ref i)),
            CameraMake = GetNullableString(r, ref i),
            CameraModel = GetNullableString(r, ref i),
            LensModel = GetNullableString(r, ref i),
            Iso = GetNullableInt(r, ref i),
            ShutterSpeed = GetNullableString(r, ref i),
            Aperture = GetNullableDouble(r, ref i),
            FocalLengthMm = GetNullableDouble(r, ref i),
            Width = GetNullableInt(r, ref i),
            Height = GetNullableInt(r, ref i),
            Orientation = GetNullableInt(r, ref i),
            GpsLatitude = GetNullableDouble(r, ref i),
            GpsLongitude = GetNullableDouble(r, ref i),
            GpsAltitude = GetNullableDouble(r, ref i),
            Artist = GetNullableString(r, ref i),
            Description = GetNullableString(r, ref i),
            Copyright = GetNullableString(r, ref i),
            Rating = r.GetInt32(i++),
            Tags = GetNullableString(r, ref i),
            ThumbnailCachePath = GetNullableString(r, ref i),
            PHash = GetNullableString(r, ref i),
            IndexedAtUtc = FromIso(r.GetString(i++)),
            IsMissing = r.GetInt64(i++) != 0,
            BlurScore = GetNullableDouble(r, ref i),
            AiAnalyzedAtUtc = FromIsoNullable(GetNullableString(r, ref i)),
            AestheticScore = GetNullableDouble(r, ref i),
            DominantColors = GetNullableString(r, ref i),
            IsMono = r.GetInt64(i++) != 0,
            Embedding = GetNullableBlob(r, ref i),
            ClipEmbedding = GetNullableBlob(r, ref i),
            ObjectsJson = GetNullableString(r, ref i),
            DeepAnalyzedAtUtc = FromIsoNullable(GetNullableString(r, ref i)),
            GpsPlace = GetNullableString(r, ref i),
            PlaceCountry = GetNullableString(r, ref i),
            PlaceProvince = GetNullableString(r, ref i),
            PlaceCity = GetNullableString(r, ref i),
            PlaceDistrict = GetNullableString(r, ref i),
            PlaceLandmark = GetNullableString(r, ref i),
            GpsPlaceSource = GetNullableString(r, ref i),
            GpsPlaceFailed = GetNullableString(r, ref i),
        };
        return p;
    }

    /// <summary>
    /// Reads only the grid columns (<see cref="GridPhotoColumns"/>). The heavy BLOB / GPS /
    /// place / AI columns are left at their defaults; this mirrors <see cref="ReadPhoto"/>
    /// column order for the first 38 columns.
    /// </summary>
    private static PhotoRecord ReadGridPhoto(SqliteDataReader r)
    {
        int i = 0;
        PhotoRecord p = new()
        {
            Id = r.GetInt64(i++),
            FilePath = r.GetString(i++),
            FileName = r.GetString(i++),
            DirectoryPath = r.GetString(i++),
            Extension = r.GetString(i++),
            Kind = (PhotoKind)r.GetInt32(i++),
            FileSizeBytes = r.GetInt64(i++),
            FileModifiedUtc = FromIso(r.GetString(i++)),
            ContentHash = GetNullableString(r, ref i),
            TakenAtUtc = FromIsoNullable(GetNullableString(r, ref i)),
            CameraMake = GetNullableString(r, ref i),
            CameraModel = GetNullableString(r, ref i),
            LensModel = GetNullableString(r, ref i),
            Iso = GetNullableInt(r, ref i),
            ShutterSpeed = GetNullableString(r, ref i),
            Aperture = GetNullableDouble(r, ref i),
            FocalLengthMm = GetNullableDouble(r, ref i),
            Width = GetNullableInt(r, ref i),
            Height = GetNullableInt(r, ref i),
            Orientation = GetNullableInt(r, ref i),
            GpsLatitude = GetNullableDouble(r, ref i),
            GpsLongitude = GetNullableDouble(r, ref i),
            GpsAltitude = GetNullableDouble(r, ref i),
            Artist = GetNullableString(r, ref i),
            Description = GetNullableString(r, ref i),
            Copyright = GetNullableString(r, ref i),
            Rating = r.GetInt32(i++),
            Tags = GetNullableString(r, ref i),
            ThumbnailCachePath = GetNullableString(r, ref i),
            PHash = GetNullableString(r, ref i),
            IndexedAtUtc = FromIso(r.GetString(i++)),
            IsMissing = r.GetInt64(i++) != 0,
            BlurScore = GetNullableDouble(r, ref i),
            AiAnalyzedAtUtc = FromIsoNullable(GetNullableString(r, ref i)),
            AestheticScore = GetNullableDouble(r, ref i),
            DominantColors = GetNullableString(r, ref i),
            IsMono = r.GetInt64(i++) != 0,
            GpsPlace = GetNullableString(r, ref i),
            PlaceCountry = GetNullableString(r, ref i),
            PlaceProvince = GetNullableString(r, ref i),
            PlaceCity = GetNullableString(r, ref i),
            PlaceDistrict = GetNullableString(r, ref i),
            PlaceLandmark = GetNullableString(r, ref i),
        };
        return p;
    }

    private static byte[]? GetNullableBlob(SqliteDataReader r, ref int i)
    {
        byte[]? v = r.IsDBNull(i) ? null : (byte[])r.GetValue(i);
        i++;
        return v;
    }

    private static string? GetNullableString(SqliteDataReader r, ref int i)
    {
        string? v = r.IsDBNull(i) ? null : r.GetString(i);
        i++;
        return v;
    }

    private static int? GetNullableInt(SqliteDataReader r, ref int i)
    {
        int? v = r.IsDBNull(i) ? null : r.GetInt32(i);
        i++;
        return v;
    }

    private static double? GetNullableDouble(SqliteDataReader r, ref int i)
    {
        double? v = r.IsDBNull(i) ? null : r.GetDouble(i);
        i++;
        return v;
    }

    private static string ToIso(DateTime value) => value.ToString("o", CultureInfo.InvariantCulture);
    private static string? ToIsoNullable(DateTime? value) => value?.ToString("o", CultureInfo.InvariantCulture);
    private static DateTime FromIso(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static DateTime? FromIsoNullable(string? value) =>
        string.IsNullOrEmpty(value) ? null : DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
