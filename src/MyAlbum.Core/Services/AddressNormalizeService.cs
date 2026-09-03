using System.Collections.Concurrent;
using MyAlbum.Core.Data;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>One photo whose place name could not be normalized this pass (no usable LLM result).</summary>
public sealed record SkippedAddress(long Id, string FileName, string Place);

/// <summary>Result of one address-normalization pass.</summary>
public sealed record AddressNormalizeResult(int Total, int Resolved, int Skipped, int FailedBatches)
{
    /// <summary>First error captured this pass (e.g. a 4xx from the LLM), for diagnostics.</summary>
    public string? LastError { get; init; }

    /// <summary>Photos that were pending but produced no usable normalized address (model returned
    /// nothing or an empty country). Useful for showing the user what was skipped.</summary>
    public IReadOnlyList<SkippedAddress> SkippedItems { get; init; } = Array.Empty<SkippedAddress>();
}

/// <summary>
/// Background pass that uses the configured LLM (<see cref="LlmService"/>) to normalize every
/// photo's reverse-geocoded <c>GpsPlace</c> into the structured five-level address
/// (国家/地区 → 一级行政区 → 二级行政区 → 三级行政区/街区 → 地标/POI), then bulk-writes it. Distinct place names are
/// batched into single LLM requests to minimize API calls. Parallelism and batch size are
/// controlled by <see cref="ProcessingConfig"/> (LlmParallelism / LlmBatchSize).
/// </summary>
public sealed class AddressNormalizeService
{
    private readonly PhotoDatabase _db;
    private readonly LlmService _llm;

    public AddressNormalizeService(PhotoDatabase db, LlmService llm)
    {
        _db = db;
        _llm = llm;
    }

    /// <summary>True when an LLM is configured for address normalization.</summary>
    public bool IsConfigured => _llm.IsConfigured;

    public async Task<AddressNormalizeResult> NormalizePendingAsync(
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        if (!_llm.IsConfigured)
        {
            return new AddressNormalizeResult(0, 0, 0, 0);
        }

        var pending = await _db.GetPhotosPendingPlaceNormalizationAsync(limit: int.MaxValue);
        if (pending.Count == 0)
        {
            return new AddressNormalizeResult(0, 0, 0, 0);
        }

        // Normalize distinct place names (one LLM call per batch), then map back to photos.
        var names = pending.Select(p => p.GpsPlace!)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var normalized = new Dictionary<string, NormalizedAddress>(StringComparer.Ordinal);
        var batches = names.Chunk(ProcessingConfig.LlmBatchSize).ToList();
        var batchLock = new object();
        var errors = new ConcurrentBag<string>();
        var rawSamples = new ConcurrentBag<string>();
        int done = 0;
        int failedBatches = 0;
        long lastReport = 0;

        try
        {
            await Parallel.ForEachAsync(batches, new ParallelOptions
            {
                MaxDegreeOfParallelism = ProcessingConfig.LlmParallelism,
                CancellationToken = ct,
            }, async (batch, token) =>
            {
                try
                {
                    // In-flight LLM calls ignore the cancel token so an already-sent batch still
                    // completes and is persisted on a graceful stop (see catch below).
                    var outcome = await _llm.NormalizeAsync(batch, CancellationToken.None);
                    if (outcome.Map.Count == 0 && rawSamples.Count < 2 && outcome.Raw is { } raw)
                    {
                        rawSamples.Add(raw.Length > 2000 ? raw[..2000] : raw);
                    }
                    lock (batchLock)
                    {
                        foreach (var (name, addr) in outcome.Map)
                        {
                            normalized[name] = addr;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failedBatches); // will be re-tried on the next run
                    errors.Add(ex.Message);
                }
                int d = Interlocked.Increment(ref done);
                if (d == batches.Count || Environment.TickCount64 - lastReport >= 100)
                {
                    Interlocked.Exchange(ref lastReport, Environment.TickCount64);
                    progress?.Report((d, batches.Count, $"第 {d} / {batches.Count} 批（共 {names.Count} 个不同地点名）"));
                }
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful stop: Parallel.ForEachAsync has let every in-flight LLM batch finish, so
            // `normalized` already holds all completed work. Persist it below instead of discarding.
        }

        var writes = new List<(long Id, NormalizedAddress Address)>(pending.Count);
        var writtenIds = new HashSet<long>();
        foreach (var p in pending)
        {
            if (p.GpsPlace is { } place && normalized.TryGetValue(place, out var addr) && !addr.IsEmpty)
            {
                writes.Add((p.Id, addr));
                writtenIds.Add(p.Id);
            }
        }

        // Anything still pending after this pass (model gave nothing / empty country) is "skipped".
        var skipped = pending
            .Where(p => !writtenIds.Contains(p.Id))
            .Select(p => new SkippedAddress(p.Id, p.FileName, p.GpsPlace ?? ""))
            .ToList();

        await _db.BulkSetPlaceAddressAsync(writes);
        progress?.Report((batches.Count, batches.Count, ""));

        return new AddressNormalizeResult(pending.Count, writes.Count, pending.Count - writes.Count, failedBatches)
        {
            SkippedItems = skipped,
            LastError = errors.Count > 0
                ? errors.FirstOrDefault()
                : (writes.Count == 0 && rawSamples.Count > 0
                    ? "批量返回为空（模型未给出可用结果）。原始返回样例：\n" + string.Join("\n---\n", rawSamples)
                    : null),
        };
    }

    /// <summary>
    /// Normalizes a single raw place name into the five-level address via the configured LLM.
    /// Returns <c>null</c> when the LLM is not configured or the model produced no usable
    /// address. Used by the per-photo "refresh location" action on the home page.
    /// </summary>
    public async Task<NormalizedAddress?> NormalizeOneAsync(string place, CancellationToken ct = default)
    {
        if (!_llm.IsConfigured || string.IsNullOrWhiteSpace(place))
        {
            return null;
        }
        var map = await _llm.NormalizeAsync(new[] { place }, ct);
        return map.Map.TryGetValue(place, out var addr) && !addr.IsEmpty ? addr : null;
    }

    /// <summary>
    /// Five-level LLM normalization scoped to a caller-supplied set of photos — the "初次规范"
    /// (five-level) path used by the home-page multi-select "重新获取地理位置及规范" action.
    /// Every photo that has a reverse-geocoded <c>GpsPlace</c> (just refreshed by the caller) is
    /// re-derived into the five-level address; photos without a place are skipped. This is the
    /// first-pass normalization, distinct from <see cref="VerifyAddressesAsync"/> (error-correction).
    /// Returns how many were resolved vs. not (model gave nothing).
    /// </summary>
    public async Task<AddressNormalizeResult> NormalizePhotosAsync(
        IReadOnlyList<PhotoRecord> photos,
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        if (!_llm.IsConfigured)
        {
            return new AddressNormalizeResult(0, 0, 0, 0);
        }

        // 五级规范：对刚反解出 GpsPlace 的照片重新推导五级地址（不论是否已规范过）。
        var candidates = photos
            .Where(p => !string.IsNullOrWhiteSpace(p.GpsPlace))
            .ToList();
        int total = candidates.Count;
        if (total == 0)
        {
            progress?.Report((0, 0, ""));
            return new AddressNormalizeResult(0, 0, 0, 0);
        }

        var names = candidates
            .Select(p => p.GpsPlace!)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var normalized = new Dictionary<string, NormalizedAddress>(StringComparer.Ordinal);
        var batches = names.Chunk(ProcessingConfig.LlmBatchSize).ToList();
        int done = 0;
        int failedBatches = 0;
        long lastReport = 0;
        await Parallel.ForEachAsync(batches, new ParallelOptions
        {
            MaxDegreeOfParallelism = ProcessingConfig.LlmParallelism,
            CancellationToken = ct,
        }, async (batch, token) =>
        {
            try
            {
                // In-flight LLM calls ignore the cancel token so a started batch still completes.
                var outcome = await _llm.NormalizeAsync(batch, CancellationToken.None);
                lock (normalized)
                {
                    foreach (var (name, addr) in outcome.Map)
                    {
                        normalized[name] = addr;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                Interlocked.Increment(ref failedBatches);
            }
            int d = Interlocked.Increment(ref done);
            long now = Environment.TickCount64;
            if (d == batches.Count || now - lastReport >= 100)
            {
                lastReport = now;
                progress?.Report((d, batches.Count, ""));
            }
        });

        // Map the normalized addresses back onto the candidate photos and bulk-write.
        var writes = new List<(long Id, NormalizedAddress Address)>(total);
        foreach (var p in candidates)
        {
            if (p.GpsPlace is { } place && normalized.TryGetValue(place, out var addr) && !addr.IsEmpty)
            {
                writes.Add((p.Id, addr));
                // Update the in-memory record so the preview panel / location tree refresh.
                p.PlaceCountry = addr.Country;
                p.PlaceProvince = addr.Province;
                p.PlaceCity = addr.City;
                p.PlaceDistrict = addr.District;
                p.PlaceLandmark = addr.Landmark;
            }
        }
        await _db.BulkSetPlaceAddressAsync(writes);
        progress?.Report((batches.Count, batches.Count, ""));

        return new AddressNormalizeResult(total, writes.Count, total - writes.Count, failedBatches);
    }

    /// <summary>Result of the second-pass verification (error-correction) run.</summary>
    public sealed record AddressVerifyResult(int Total, int Corrected, int Unchanged, int FailedBatches)
    {
        public string? LastError { get; init; }
    }

    /// <summary>
    /// Second-pass verification: re-sends each photo's correct reverse-geocoded <c>GpsPlace</c>
    /// together with its (possibly wrong) first-pass normalized address to the LLM, asking it to
    /// correct obvious errors (e.g. a district misclassified as a province). Overwrites only the
    /// photos the model returned a non-empty result for. Distinct from <see cref="NormalizePendingAsync"/>,
    /// which runs on photos that have no normalized address yet.
    /// </summary>
    public async Task<AddressVerifyResult> VerifyAddressesAsync(
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        if (!_llm.IsConfigured)
        {
            return new AddressVerifyResult(0, 0, 0, 0);
        }

        var photos = await _db.GetPhotosWithNormalizedAddressAsync(int.MaxValue);
        if (photos.Count == 0)
        {
            return new AddressVerifyResult(0, 0, 0, 0);
        }

        // De-duplicate: the correction only depends on (gpsPlace + current address), so photos
        // sharing the exact same raw place and the same (wrong) normalized result need be sent
        // to the LLM only once. We key each group by that composite and use the first photo's id
        // as the LLM "id", then expand the returned address back to every member of the group.
        // This collapses thousands of batches down to the number of distinct locations.
        var groups = new Dictionary<string, (List<long> Ids, string GpsPlace, NormalizedAddress Current)>(StringComparer.Ordinal);
        foreach (var p in photos)
        {
            var current = new NormalizedAddress(p.PlaceCountry, p.PlaceProvince, p.PlaceCity, p.PlaceDistrict, p.PlaceLandmark);
            var key = $"{p.GpsPlace ?? ""}\u0001{current.Country}\u0001{current.Province}\u0001{current.City}\u0001{current.District}\u0001{current.Landmark}";
            if (!groups.TryGetValue(key, out var g))
            {
                g = (new List<long>(), p.GpsPlace ?? "", current);
                groups[key] = g;
            }
            g.Ids.Add(p.Id);
        }

        var items = groups.Select(g => new LlmService.AddressToVerify(g.Value.Ids[0], g.Value.GpsPlace, g.Value.Current)).ToList();

        var corrected = new ConcurrentDictionary<long, NormalizedAddress>();
        var errors = new ConcurrentBag<string>();
        var batches = items.Chunk(ProcessingConfig.LlmBatchSize).ToList();
        int done = 0;
        int failedBatches = 0;
        long lastReport = 0;

        try
        {
            await Parallel.ForEachAsync(batches, new ParallelOptions
            {
                MaxDegreeOfParallelism = ProcessingConfig.LlmParallelism,
                CancellationToken = ct,
            }, async (batch, token) =>
            {
                try
                {
                    var outcome = await _llm.VerifyAsync(batch, CancellationToken.None);
                    foreach (var (id, addr) in outcome.Map)
                    {
                        if (!addr.IsEmpty)
                        {
                            corrected[id] = addr;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failedBatches);
                    errors.Add(ex.Message);
                }
                int d = Interlocked.Increment(ref done);
                if (d == batches.Count || Environment.TickCount64 - lastReport >= 100)
                {
                    Interlocked.Exchange(ref lastReport, Environment.TickCount64);
                    progress?.Report((d, batches.Count, $"第 {d} / {batches.Count} 批（共 {photos.Count} 张照片，去重为 {groups.Count} 组）"));
                }
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // graceful stop — persist whatever completed
        }

        var writes = new List<(long Id, NormalizedAddress Address)>();
        int unchanged = 0;
        foreach (var g in groups.Values)
        {
            var repId = g.Ids[0];
            if (!corrected.TryGetValue(repId, out var addr))
            {
                unchanged += g.Ids.Count; // omitted by the model → treat as unchanged
                continue;
            }
            if (addr.Equals(g.Current))
            {
                unchanged += g.Ids.Count;
            }
            else
            {
                foreach (var id in g.Ids)
                {
                    writes.Add((id, addr));
                }
            }
        }

        await _db.BulkSetPlaceAddressAsync(writes);
        progress?.Report((batches.Count, batches.Count, ""));

        return new AddressVerifyResult(photos.Count, writes.Count, unchanged, failedBatches)
        {
            LastError = errors.Count > 0 ? errors.FirstOrDefault() : null,
        };
    }
}
