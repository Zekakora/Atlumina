using System.Collections.Concurrent;
using MyAlbum.Core.Data;

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
/// (国家 → 省/州 → 市 → 区/县/街道 → 周边地标), then bulk-writes it. Distinct place names are
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
}
