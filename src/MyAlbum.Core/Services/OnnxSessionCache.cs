using Microsoft.ML.OnnxRuntime;

namespace MyAlbum.Core.Services;

/// <summary>
/// Small shared helper for ONNX inference: caches sessions per model path and serializes
/// <see cref="InferenceSession.Run"/> calls (the DirectML execution provider is not safe for
/// concurrent Run() on one shared session — see SceneTaggerService). Everything else (decode,
/// preprocessing) stays on the caller.
/// </summary>
public sealed class OnnxSessionCache
{
    private readonly Dictionary<string, InferenceSession> _sessions = new();
    private readonly object _lock = new();

    /// <summary>Returns (and caches) a session for <paramref name="modelPath"/>.</summary>
    public InferenceSession Get(string modelPath)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(modelPath, out var session))
            {
                session = new InferenceSession(modelPath, AiEngine.CreateSessionOptions());
                _sessions[modelPath] = session;
            }
            return session;
        }
    }

    /// <summary>
    /// Runs <paramref name="inputs"/> on <paramref name="session"/> inside the shared lock.
    /// Returns the run results. The caller must dispose the returned results.
    /// </summary>
    public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(
        InferenceSession session,
        IReadOnlyCollection<NamedOnnxValue> inputs)
    {
        lock (_lock)
        {
            return session.Run(inputs);
        }
    }
}
