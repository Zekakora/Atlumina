using System.Text.RegularExpressions;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>
/// Groups photos by shooting-time continuity and suggests GPS coordinates for the
/// photos that lack them. Photos WITH GPS act as anchors; photos WITHOUT GPS are
/// chained to the temporally nearest anchor inside the same continuous time group.
/// </summary>
public sealed class GpsGroupingService
{
    /// <summary>序列号位数对应的循环模数（Sony DSC00001..DSC99999 循环）。</summary>
    public const int SequenceWrap = 100000;

    /// <summary>序号循环距离超过此值视为"序号断层"，作为人工确认提示。</summary>
    public const int FilenameWarnThreshold = 100;

    private static readonly Regex TrailingDigits = new(@"(\d+)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Classifies the library into GPX anchors / GPN photos, builds chained time groups
    /// (consecutive photos within <paramref name="threshold"/>), and assigns each GPN the
    /// coordinates of its temporally nearest anchor in the same group.
    /// </summary>
    public GpsGroupingResult Group(IReadOnlyList<PhotoRecord> photos, TimeSpan threshold)
    {
        var result = new GpsGroupingResult
        {
            AnchorCount = photos.Count(IsAnchor),
            GpnCount = photos.Count(p => !IsAnchor(p)),
        };

        var withTime = new List<PhotoRecord>();
        foreach (var p in photos)
        {
            if (p.TakenAtUtc is null)
            {
                result.NoTimePhotos.Add(p);
                continue;
            }
            withTime.Add(p);
        }
        withTime.Sort((a, b) =>
        {
            int c = a.TakenAtUtc!.Value.CompareTo(b.TakenAtUtc!.Value);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });

        foreach (var component in BuildChains(withTime, threshold))
        {
            var anchors = component.Where(IsAnchor).ToList();
            var gpn = component.Where(p => !IsAnchor(p)).ToList();

            // 没有待设置照片的组（纯锚点覆盖）不展示。
            if (gpn.Count == 0)
            {
                continue;
            }

            if (anchors.Count == 0)
            {
                result.Groups.Add(new GpsGroup
                {
                    Kind = GpsGroupKind.Manual,
                    StartUtc = component[0].TakenAtUtc,
                    EndUtc = component[^1].TakenAtUtc,
                    GpnItems = gpn.Select(p => new GpnAssignment { Photo = p }).ToList(),
                });
                continue;
            }

            var group = new GpsGroup
            {
                Kind = GpsGroupKind.Auto,
                StartUtc = component[0].TakenAtUtc,
                EndUtc = component[^1].TakenAtUtc,
                AnchorCount = anchors.Count,
            };
            foreach (var p in gpn)
            {
                var nearest = FindNearestAnchor(p, anchors);
                double gapSeconds = Math.Abs((p.TakenAtUtc!.Value - nearest.TakenAtUtc!.Value).TotalSeconds);
                group.GpnItems.Add(new GpnAssignment
                {
                    Photo = p,
                    NearestAnchor = nearest,
                    AssignedLat = nearest.GpsLatitude,
                    AssignedLon = nearest.GpsLongitude,
                    AssignedAlt = nearest.GpsAltitude,
                    TimeGapSeconds = gapSeconds,
                    FilenameCircularDistance = CircularFilenameDistance(p.FileName, nearest.FileName),
                    NeedsReview = gapSeconds > threshold.TotalSeconds,
                });
            }
            result.Groups.Add(group);
        }

        return result;
    }

    /// <summary>
    /// Splits a time-sorted photo list into chains: consecutive photos whose time gap is
    /// within the threshold stay in the same group, a gap larger than the threshold breaks
    /// the chain. Gaps are bridged through intermediate photos (chained search).
    /// </summary>
    private static List<List<PhotoRecord>> BuildChains(List<PhotoRecord> sorted, TimeSpan threshold)
    {
        var chains = new List<List<PhotoRecord>>();
        List<PhotoRecord>? current = null;
        foreach (var p in sorted)
        {
            if (current is null)
            {
                current = [p];
                continue;
            }
            var prev = current[^1];
            if ((p.TakenAtUtc!.Value - prev.TakenAtUtc!.Value).Duration() <= threshold)
            {
                current.Add(p);
            }
            else
            {
                chains.Add(current);
                current = [p];
            }
        }
        if (current is not null)
        {
            chains.Add(current);
        }
        return chains;
    }

    private static bool IsAnchor(PhotoRecord p) => p.GpsLatitude is not null && p.GpsLongitude is not null;

    private static PhotoRecord FindNearestAnchor(PhotoRecord gpn, List<PhotoRecord> anchors)
    {
        PhotoRecord best = anchors[0];
        double bestGap = double.MaxValue;
        foreach (var a in anchors)
        {
            double gap = Math.Abs((gpn.TakenAtUtc!.Value - a.TakenAtUtc!.Value).TotalSeconds);
            if (gap < bestGap || (gap == bestGap && a.Id < best.Id))
            {
                bestGap = gap;
                best = a;
            }
        }
        return best;
    }

    /// <summary>
    /// Circular distance between two files' trailing sequence numbers, so DSC99999 and
    /// DSC00001 count as adjacent. Null when a sequence number cannot be extracted.
    /// </summary>
    public static int? CircularFilenameDistance(string fileA, string fileB)
    {
        int? a = ExtractSequence(fileA);
        int? b = ExtractSequence(fileB);
        if (a is null || b is null)
        {
            return null;
        }
        int direct = Math.Abs(a.Value - b.Value);
        return Math.Min(direct, SequenceWrap - direct);
    }

    private static int? ExtractSequence(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        var m = TrailingDigits.Match(name);
        if (!m.Success)
        {
            return null;
        }
        string digits = m.Groups[1].Value;
        // 序列号至少 3 位、至多 6 位，避免把文件名里的大数字（如时间戳）误当序号。
        if (digits.Length is < 3 or > 6)
        {
            return null;
        }
        return int.Parse(digits);
    }
}
