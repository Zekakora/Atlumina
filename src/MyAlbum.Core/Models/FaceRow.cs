namespace MyAlbum.Core.Models;

/// <summary>
/// A persisted face detection: bounding box in the photo's pixels plus the 512-dim ArcFace
/// embedding (L2-normalized). <see cref="PersonId"/> groups faces of the same person, and
/// <see cref="PersonName"/> is the optional user-assigned name for that person.
/// </summary>
public sealed record FaceRow(
    long Id,
    long PhotoId,
    double BoxX,
    double BoxY,
    double BoxW,
    double BoxH,
    double Score,
    byte[] EmbeddingBytes,
    long? PersonId,
    DateTime AnalyzedAtUtc,
    string? PersonName = null)
{
    /// <summary>Decodes the persisted float32 embedding.</summary>
    public float[] ToVector()
    {
        var result = new float[EmbeddingBytes.Length / 4];
        Buffer.BlockCopy(EmbeddingBytes, 0, result, 0, EmbeddingBytes.Length);
        return result;
    }

    /// <summary>Encodes a float32 embedding for persistence.</summary>
    public static byte[] FromVector(float[] vector)
    {
        var bytes = new byte[vector.Length * 4];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}

/// <summary>Summary of one person cluster for the people-album page.</summary>
public sealed record PersonClusterInfo(long PersonId, long FaceCount, long PhotoCount, string? RepresentativePath, string? Name = null)
{
    /// <summary>Display title: the assigned name if present, otherwise "人物 N".</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Name) ? $"人物 {PersonId}" : Name!;
}
