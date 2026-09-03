using System.Collections.Concurrent;
using MyAlbum.Core.Data;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>Result of one place backfill pass.</summary>
public sealed record GpsPlaceResult(int Total, int Resolved, int Skipped);

/// <summary>
/// Background pass that reverse-geocodes GPS photos lacking a stored place name and bulk-writes
/// <c>GpsPlace</c>. Runs off the UI thread; the configured source (高德 or OSM) is used with
/// automatic fallback inside <see cref="ReverseGeocodeService"/>.
///
/// <para>Offline shortcut — neighbor reuse: a photo whose GPS is within <see cref="ReuseRadiusMeters"/>
/// of a photo already resolved directly (amap/osm) copies that place instead of hitting the network.
/// Only directly-resolved anchors are eligible; reused photos are never added to the anchor pool,
/// so an address can never propagate through a chain of neighbors (no "walking trip collapses into
/// one address").</para>
/// </summary>
public sealed class GpsPlaceService
{
    /// <summary>Within this distance a photo reuses a neighbor's resolved place (no network).</summary>
    public const double ReuseRadiusMeters = 150.0;

    /// <summary>Grid cell size in degrees (~1.1 km); neighbor lookup scans a 3×3 window.</summary>
    private const double CellDeg = 0.01;

    private readonly PhotoDatabase _db;
    private readonly ReverseGeocodeService _geocoder;

    public GpsPlaceService(PhotoDatabase db, ReverseGeocodeService geocoder)
    {
        _db = db;
        _geocoder = geocoder;
    }

    /// <summary>
    /// Resolves place names for all photos that have GPS but no stored place and have not yet
    /// failed with the currently-configured source. Neighbor reuse (500 m, real anchors only)
    /// skips the network; the rest go through 高德→OSM fallback. Failed photos are marked for the
    /// current source so switching source only re-tries the others — incremental, no overwrite.
    /// </summary>
    public async Task<GpsPlaceResult> BackfillAsync(
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        var source = GeocodeConfig.Source == "amap" ? "amap" : "osm";
        var pending = await _db.GetGpsPhotosWithoutPlaceAsync(source, int.MaxValue);
        if (pending.Count == 0)
        {
            return new GpsPlaceResult(0, 0, 0);
        }

        // Seed the anchor pool with previously resolved photos (real anchors only, no reuse).
        var anchors = new AnchorIndex();
        foreach (var a in await _db.GetResolvedAnchorsAsync())
        {
            if (a.GpsLatitude is { } la && a.GpsLongitude is { } lo && !string.IsNullOrWhiteSpace(a.GpsPlace))
            {
                anchors.Add(la, lo, a.GpsPlace);
            }
        }

        var resolved = new ConcurrentBag<(long Id, string Place, string Source)>();
        var failedIds = new ConcurrentBag<long>();
        int done = 0;
        long lastReport = 0;

        try
        {
            await Parallel.ForEachAsync(pending, new ParallelOptions
            {
                MaxDegreeOfParallelism = ProcessingConfig.GeocodeParallelism,
                CancellationToken = ct,
            }, async (photo, token) =>
            {
                string? place = null;
                string? usedSource = null;

                if (photo.GpsLatitude is { } lat && photo.GpsLongitude is { } lon)
                {
                    // Neighbor reuse: if a directly-resolved anchor is within the radius, copy it
                    // and mark the photo as reused (never an anchor itself → no chain transmission).
                    var reuse = anchors.FindNearest(lat, lon, ReuseRadiusMeters);
                    if (reuse is not null)
                    {
                        place = reuse;
                        usedSource = "reuse";
                    }
                    else
                    {
                        try
                        {
                            var result = await _geocoder.ResolveAsync(lat, lon, token);
                            place = result.Place;
                            usedSource = result.Source;
                            if (place is not null && usedSource is "amap" or "osm")
                            {
                                anchors.Add(lat, lon, place); // newly resolved → eligible for reuse now
                            }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (OperationCanceledException)
                        {
                            place = null; // timeout or unrelated cancel → this photo fails, batch continues
                        }
                        catch
                        {
                            place = null;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(place))
                {
                    resolved.Add((photo.Id, place!, usedSource ?? source));
                }
                else
                {
                    failedIds.Add(photo.Id);
                }

                int d = Interlocked.Increment(ref done);
                // 节流：至少每 100ms 报一次，结束时强制补一帧。
                if (d == pending.Count || Environment.TickCount64 - lastReport >= 100)
                {
                    Interlocked.Exchange(ref lastReport, Environment.TickCount64);
                    progress?.Report((d, pending.Count, Path.GetFileName(photo.FilePath)));
                }
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful stop: Parallel.ForEachAsync has already let every in-flight request
            // finish (the network calls ignore this token), so `resolved`/`failedIds` contain
            // all completed work. Fall through and persist it instead of discarding.
        }

        await _db.BulkSetGpsPlaceAsync(resolved.ToList());
        await _db.BulkMarkGpsPlaceFailedAsync(failedIds.ToList(), source);

        progress?.Report((pending.Count, pending.Count, ""));

        return new GpsPlaceResult(pending.Count, resolved.Count, pending.Count - resolved.Count);
    }

    /// <summary>
    /// Resolves place names for a caller-supplied list of GPS photos, applying the same
    /// neighbor-reuse shortcut as <see cref="BackfillAsync"/> (150 m, real anchors only — never
    /// cascades through reused results). Used by the home-page right-click "批量反解+解析（相似复用）":
    /// unlike Backfill it re-resolves photos that may already have a place, and unlike the plain
    /// batch it prefers copying a neighbor's place over hitting the network. Returns only the
    /// successfully-resolved items; the caller persists them (this method writes nothing).
    /// </summary>
    public async Task<List<(PhotoRecord Photo, string Place, string Source)>> ResolvePhotosAsync(
        IReadOnlyList<PhotoRecord> photos,
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        var withGps = photos.Where(p => p.GpsLatitude is not null && p.GpsLongitude is not null).ToList();
        var resolved = new ConcurrentBag<(PhotoRecord Photo, string Place, string Source)>();
        if (withGps.Count == 0)
        {
            progress?.Report((0, 0, ""));
            return resolved.ToList();
        }

        // Seed the anchor pool with previously resolved photos (real anchors only, no reuse).
        var anchors = new AnchorIndex();
        foreach (var a in await _db.GetResolvedAnchorsAsync())
        {
            if (a.GpsLatitude is { } la && a.GpsLongitude is { } lo && !string.IsNullOrWhiteSpace(a.GpsPlace))
            {
                anchors.Add(la, lo, a.GpsPlace);
            }
        }

        int done = 0;
        long lastReport = 0;
        await Parallel.ForEachAsync(withGps, new ParallelOptions
        {
            MaxDegreeOfParallelism = ProcessingConfig.GeocodeParallelism,
            CancellationToken = ct,
        }, async (photo, token) =>
        {
            if (photo.GpsLatitude is not { } lat || photo.GpsLongitude is not { } lon)
            {
                return;
            }
            string? place = null;
            string? usedSource = null;

            var reuse = anchors.FindNearest(lat, lon, ReuseRadiusMeters);
            if (reuse is not null)
            {
                place = reuse;
                usedSource = "reuse";
            }
            else
            {
                try
                {
                    var result = await _geocoder.ResolveAsync(lat, lon, token);
                    place = result.Place;
                    usedSource = result.Source;
                    if (place is not null && usedSource is "amap" or "osm")
                    {
                        anchors.Add(lat, lon, place); // newly resolved → eligible for reuse now
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    place = null; // timeout or unrelated cancel → this photo fails, batch continues
                }
                catch
                {
                    place = null;
                }
            }

            if (!string.IsNullOrWhiteSpace(place))
            {
                resolved.Add((photo, place!, usedSource ?? (GeocodeConfig.Source == "amap" ? "amap" : "osm")));
            }

            int d = Interlocked.Increment(ref done);
            // 节流：至少每 100ms 报一次，结束时强制补一帧。
            if (d == withGps.Count || Environment.TickCount64 - lastReport >= 100)
            {
                Interlocked.Exchange(ref lastReport, Environment.TickCount64);
                progress?.Report((d, withGps.Count, Path.GetFileName(photo.FilePath)));
            }
        });

        return resolved.ToList();
    }

    /// <summary>
    /// Spatial index of resolved anchors by a coarse lat/lon grid. Only <see cref="GpsPlaceService"/>
    /// adds real anchors (never reused results), so reuse can never cascade.
    /// </summary>
    private sealed class AnchorIndex
    {
        private readonly ConcurrentDictionary<long, List<(double Lat, double Lon, string Place)>> _cells = new();

        public void Add(double lat, double lon, string place)
        {
            var key = CellKey(lat, lon);
            var cell = _cells.GetOrAdd(key, _ => new List<(double, double, string)>());
            lock (cell)
            {
                cell.Add((lat, lon, place));
            }
        }

        /// <summary>Nearest place within <paramref name="radiusMeters"/>, or null.</summary>
        public string? FindNearest(double lat, double lon, double radiusMeters)
        {
            long kLat = (long)Math.Floor(lat / CellDeg);
            long kLon = (long)Math.Floor(lon / CellDeg);
            string? best = null;
            double bestM = radiusMeters;
            double rKm = radiusMeters / 1000.0;
            for (long dl = -1; dl <= 1; dl++)
            {
                for (long dn = -1; dn <= 1; dn++)
                {
                    if (_cells.TryGetValue((kLat + dl) * 1_000_000L + (kLon + dn), out var cell))
                    {
                        lock (cell)
                        {
                            foreach (var (aLat, aLon, place) in cell)
                            {
                                double m = HaversineKm(lat, lon, aLat, aLon) * 1000.0;
                                if (m <= bestM)
                                {
                                    bestM = m;
                                    best = place;
                                }
                            }
                        }
                    }
                }
            }
            return best;
        }

        private static long CellKey(double lat, double lon) =>
            (long)Math.Floor(lat / CellDeg) * 1_000_000L + (long)Math.Floor(lon / CellDeg);
    }

    /// <summary>Great-circle distance in km.</summary>
    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return r * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
