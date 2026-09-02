using MyAlbum.Core.Data;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>One person cluster: faces sharing a high-similarity identity.</summary>
public sealed record PersonCluster(long PersonId, int FaceCount, int PhotoCount, string RepresentativePhoto);

/// <summary>
/// End-to-end face pipeline: runs YuNet + ArcFace over the library, persists faces to the
/// index, then clusters embeddings (cosine similarity) into people. Clustering uses a
/// greedy connected-components approach with an ArcFace cosine-distance threshold, which
/// is the standard way to build "person" albums without labeled data.
/// </summary>
public sealed class FaceClusteringService
{
    /// <summary>Cosine distance below which two faces are treated as the same person.</summary>
    public const double SamePersonDistance = 0.12;

    private readonly PhotoDatabase _db;
    private readonly FaceService _faces;

    public FaceClusteringService(PhotoDatabase db, FaceService faces)
    {
        _db = db;
        _faces = faces;
    }

    /// <summary>
    /// Analyzes every photo (or only those without stored faces when <paramref name="incremental"/>
    /// is true), then re-clusters. Returns the number of faces persisted and people found.
    /// </summary>
    public async Task<(int FacesStored, int PeopleFound)> AnalyzeLibraryAsync(
        bool incremental = false,
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        if (!FaceService.IsInstalled)
        {
            return (0, 0);
        }

        var photos = await _db.GetPhotosAsync(int.MaxValue);
        if (incremental)
        {
            var stored = await _db.GetAllFacesAsync();
            var analyzed = new HashSet<long>(stored.Select(f => f.PhotoId));
            photos = photos.Where(p => !analyzed.Contains(p.Id)).ToList();
        }
        else
        {
            await _db.DeleteAllFacesAsync();
        }

        int done = 0;
        var newFaces = new List<FaceRow>();
        int degree = Math.Clamp(Environment.ProcessorCount, 2, 6);
        await Parallel.ForEachAsync(photos, new ParallelOptions
        {
            MaxDegreeOfParallelism = degree,
            CancellationToken = ct,
        }, async (photo, token) =>
        {
            token.ThrowIfCancellationRequested();
            var embeddings = await _faces.ExtractAsync(photo, token).ConfigureAwait(false);
            lock (newFaces)
            {
                foreach (var e in embeddings)
                {
                    newFaces.Add(new FaceRow(
                        0, photo.Id,
                        e.Box.X, e.Box.Y, e.Box.Width, e.Box.Height, e.Box.Score,
                        FaceRow.FromVector(e.Vector),
                        null,
                        DateTime.UtcNow));
                }
                done++;
            }
            if (progress is not null)
            {
                int d;
                lock (newFaces) { d = done; }
                if (d % 5 == 0)
                {
                    progress.Report((d, photos.Count, Path.GetFileName(photo.FilePath)));
                }
            }
        });

        // Persist new faces (existing faces stay; PersonId re-clustered next).
        await _db.BulkUpsertFacesAsync(newFaces);

        // Cluster ALL faces (existing + new) and write PersonId.
        var clusters = await ClusterAsync(ct);
        return (newFaces.Count, clusters.Count);
    }

    /// <summary>
    /// Loads every stored face and greedily assigns person ids by cosine similarity.
    /// Returns the resulting clusters ordered by size (largest first).
    /// </summary>
    public async Task<List<PersonCluster>> ClusterAsync(CancellationToken ct = default)
    {
        var faces = await _db.GetAllFacesAsync();
        if (faces.Count == 0)
        {
            return [];
        }

        // Greedy connected components: each face becomes the seed of a person; any face within
        // the distance threshold joins. Simpler and robust enough for album clustering.
        var photoNames = new Dictionary<long, string>();
        foreach (var p in await _db.GetPhotosAsync(int.MaxValue))
        {
            photoNames[p.Id] = p.FileName;
        }

        var assignments = new List<(long FaceId, long PersonId, string? PersonName)>(faces.Count);
        var clusters = new List<PersonCluster>();
        long nextPerson = 1;
        var seen = new bool[faces.Count];
        for (int i = 0; i < faces.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (seen[i])
            {
                continue;
            }
            var seedVec = faces[i].ToVector();
            var members = new List<FaceRow> { faces[i] };
            seen[i] = true;
            for (int j = i + 1; j < faces.Count; j++)
            {
                if (seen[j])
                {
                    continue;
                }
                if (CosineDistance(seedVec, faces[j].ToVector()) < SamePersonDistance)
                {
                    members.Add(faces[j]);
                    seen[j] = true;
                }
            }

            // Propagate a previously-assigned name to the whole cluster so re-clustering
            // (which reassigns PersonIds) never loses the user's names.
            string? clusterName = members.Select(m => m.PersonName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            foreach (var m in members)
            {
                assignments.Add((m.Id, nextPerson, clusterName));
            }
            var photoCount = members.Select(m => m.PhotoId).Distinct().Count();
            string rep = photoNames.TryGetValue(members[0].PhotoId, out var name) ? name : "";
            clusters.Add(new PersonCluster(nextPerson, members.Count, photoCount, rep));
            nextPerson++;
        }

        await _db.BulkSetPersonAsync(assignments);
        return clusters.OrderByDescending(c => c.FaceCount).ToList();
    }

    /// <summary>Cosine distance between two L2-normalized embeddings (0 = identical, 2 = opposite).</summary>
    public static float CosineDistance(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        float dot = 0;
        for (int i = 0; i < n; i++)
        {
            dot += a[i] * b[i];
        }
        return 1f - dot;
    }
}
