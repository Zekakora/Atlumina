using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>
/// Reads EXIF / XMP / IPTC metadata from image and RAW files via MetadataExtractor.
/// Pure read-only; any writes go through ExifTool (see ExifWriterService, later phase).
/// RAW files (e.g. Sony ARW) can expose several SubIFD directories, so values are
/// searched across all instances rather than only the first one.
/// </summary>
public sealed class MetadataReaderService
{
    public PhotoRecord Read(string filePath)
    {
        var info = new FileInfo(filePath);
        var directories = ImageMetadataReader.ReadMetadata(filePath).ToList();

        var ifd0s = directories.OfType<ExifIfd0Directory>().ToList();
        var subIfds = directories.OfType<ExifSubIfdDirectory>().ToList();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();

        var photo = new PhotoRecord
        {
            FilePath = Path.GetFullPath(filePath),
            FileName = info.Name,
            DirectoryPath = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "",
            Extension = info.Extension.ToLowerInvariant(),
            Kind = GetKind(info.Extension),
            FileSizeBytes = info.Length,
            FileModifiedUtc = info.LastWriteTimeUtc,
            IndexedAtUtc = DateTime.UtcNow,
        };

        photo.CameraMake = GetStringAcross(ifd0s, ExifDirectoryBase.TagMake);
        photo.CameraModel = GetStringAcross(ifd0s, ExifDirectoryBase.TagModel);
        photo.Orientation = GetIntAcross(ifd0s, ExifDirectoryBase.TagOrientation);
        photo.Artist = GetStringAcross(ifd0s, ExifDirectoryBase.TagArtist);
        photo.Description = GetStringAcross(ifd0s, ExifDirectoryBase.TagImageDescription);
        photo.Copyright = GetStringAcross(ifd0s, ExifDirectoryBase.TagCopyright);

        photo.LensModel = GetStringAcross(subIfds, ExifDirectoryBase.TagLensModel);
        photo.Iso = GetIntAcross(subIfds, ExifDirectoryBase.TagIsoEquivalent)
                 ?? GetIntAcross(subIfds, ExifDirectoryBase.TagIsoSpeed)
                 ?? GetIntAcross(subIfds, ExifDirectoryBase.TagRecommendedExposureIndex);
        photo.Aperture = GetDoubleAcross(subIfds, ExifDirectoryBase.TagFNumber);
        photo.FocalLengthMm = GetDoubleAcross(subIfds, ExifDirectoryBase.TagFocalLength);
        photo.ShutterSpeed = GetShutterDisplay(subIfds);

        foreach (var subIfd in subIfds)
        {
            if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var takenAt))
            {
                photo.TakenAtUtc = DateTime.SpecifyKind(takenAt, DateTimeKind.Unspecified);
                break;
            }
        }

        photo.Width = GetIntAcross(subIfds, ExifDirectoryBase.TagExifImageWidth)
                   ?? GetIntAcross(ifd0s, ExifDirectoryBase.TagExifImageWidth)
                   ?? GetIntAcross(subIfds, ExifDirectoryBase.TagImageWidth)
                   ?? GetIntAcross(ifd0s, ExifDirectoryBase.TagImageWidth);
        photo.Height = GetIntAcross(subIfds, ExifDirectoryBase.TagExifImageHeight)
                    ?? GetIntAcross(ifd0s, ExifDirectoryBase.TagExifImageHeight)
                    ?? GetIntAcross(subIfds, ExifDirectoryBase.TagImageHeight)
                    ?? GetIntAcross(ifd0s, ExifDirectoryBase.TagImageHeight);

        if (gps is not null)
        {
            var loc = gps.GetGeoLocation();
            if (loc is not null)
            {
                photo.GpsLatitude = loc.Value.Latitude;
                photo.GpsLongitude = loc.Value.Longitude;
            }
            var alt = GetDouble(gps, GpsDirectory.TagAltitude);
            if (alt is not null)
            {
                var altRef = GetString(gps, GpsDirectory.TagAltitudeRef);
                photo.GpsAltitude = altRef == "1" ? -alt : alt;
            }
        }

        return photo;
    }

    /// <summary>
    /// Raw dump of every directory and tag (used by the metadata viewer and diagnostics).
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ReadAllTags(string filePath)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var directory in ImageMetadataReader.ReadMetadata(filePath))
        {
            foreach (var tag in directory.Tags)
            {
                result.Add(new KeyValuePair<string, string>(
                    $"{directory.Name}/{tag.Name}",
                    tag.Description ?? ""));
            }
        }
        return result;
    }

    private static string? GetStringAcross(IEnumerable<MetadataExtractor.Directory> dirs, int tagType)
    {
        foreach (var dir in dirs)
        {
            if (!dir.ContainsTag(tagType)) continue;
            var value = dir.GetObject(tagType);
            if (value is not null) return value.ToString();
        }
        return null;
    }

    private static int? GetIntAcross(IEnumerable<MetadataExtractor.Directory> dirs, int tagType)
    {
        foreach (var dir in dirs)
        {
            var value = dir.GetObject(tagType);
            if (value is null) continue;
            var result = ToInt(value);
            if (result is not null) return result;
        }
        return null;
    }

    private static double? GetDoubleAcross(IEnumerable<MetadataExtractor.Directory> dirs, int tagType)
    {
        foreach (var dir in dirs)
        {
            var value = GetDouble(dir, tagType);
            if (value is not null) return value;
        }
        return null;
    }

    private static string? GetString(MetadataExtractor.Directory dir, int tagType)
    {
        var value = dir.GetObject(tagType);
        return value?.ToString();
    }

    private static int? GetInt(MetadataExtractor.Directory dir, int tagType)
    {
        var value = dir.GetObject(tagType);
        return ToInt(value);
    }

    private static int? ToInt(object? value) => value switch
    {
        int i => i,
        uint u when u <= int.MaxValue => (int)u,
        short s => s,
        ushort us => us,
        byte b => b,
        sbyte sb => sb,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        ulong ul when ul <= int.MaxValue => (int)ul,
        _ => null,
    };

    private static double? GetDouble(MetadataExtractor.Directory dir, int tagType)
    {
        var value = dir.GetObject(tagType);
        return value switch
        {
            Rational r when r.Denominator != 0 => (double)r.Numerator / r.Denominator,
            double d => d,
            float f => f,
            int i => i,
            _ => null,
        };
    }

    private static string? GetShutterDisplay(IEnumerable<ExifSubIfdDirectory> dirs)
    {
        foreach (var dir in dirs)
        {
            if (!dir.TryGetRational(ExifDirectoryBase.TagExposureTime, out var r) || r.Denominator == 0)
            {
                continue;
            }
            double seconds = (double)r.Numerator / r.Denominator;
            return r.Numerator == 1 && r.Denominator > 1
                ? $"1/{r.Denominator}"
                : seconds >= 1 ? $"{seconds:0.#}\"" : $"1/{Math.Round(1 / seconds)}";
        }
        return null;
    }

    private static PhotoKind GetKind(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => PhotoKind.Jpeg,
        ".arw" or ".cr2" or ".cr3" or ".nef" or ".raf" or ".dng" or ".orf" or ".rw2" or ".pef" or ".srw" or ".raw" => PhotoKind.Raw,
        ".hif" or ".heic" or ".heif" => PhotoKind.Heif,
        ".png" => PhotoKind.Png,
        ".webp" => PhotoKind.Webp,
        ".gif" => PhotoKind.Gif,
        ".bmp" => PhotoKind.Bmp,
        ".tif" or ".tiff" => PhotoKind.Tiff,
        _ => PhotoKind.Other,
    };
}
