using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentic.Orchestrator.Context;

public sealed record ArtifactRef(string Key, int Version, string Hash);

/// <summary>
/// Immutable, versioned output of a node. <see cref="DerivedFrom"/> records exactly which input
/// versions were read to produce it — the lineage used for audit and for stale-input detection.
/// </summary>
public sealed record Artifact(
    string Key,
    int Version,
    string ContentType,
    string Content,
    string Hash,
    string ProducedBy,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ArtifactRef> DerivedFrom)
{
    public ArtifactRef Ref => new(Key, Version, Hash);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name}.");

    public static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

/// <summary>Thread-safe blackboard holding every version of every artifact (cross-stage context).</summary>
public sealed class ArtifactStore
{
    private readonly ConcurrentDictionary<string, List<Artifact>> _history = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public ArtifactStore(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Raised when a key receives a version whose content differs from the previous one.</summary>
    public event Action<Artifact, Artifact?>? Changed;

    /// <summary>Stores content; identical content to the latest version is a no-op (idempotent re-runs).</summary>
    public Artifact Put(string key, string content, string contentType, string producedBy, IReadOnlyList<ArtifactRef>? derivedFrom = null)
    {
        var hash = Json.Sha256(content);
        Artifact? previous;
        Artifact artifact;
        var versions = _history.GetOrAdd(key, _ => []);
        lock (versions)
        {
            previous = versions.Count > 0 ? versions[^1] : null;
            if (previous is not null && previous.Hash == hash)
                return previous;
            artifact = new Artifact(key, versions.Count + 1, contentType, content, hash, producedBy, _time.GetUtcNow(), derivedFrom ?? []);
            versions.Add(artifact);
        }
        Changed?.Invoke(artifact, previous);
        return artifact;
    }

    public Artifact PutJson<T>(string key, T value, string producedBy, IReadOnlyList<ArtifactRef>? derivedFrom = null) =>
        Put(key, Json.Serialize(value), "application/json", producedBy, derivedFrom);

    public Artifact? Latest(string key)
    {
        if (!_history.TryGetValue(key, out var versions))
            return null;
        lock (versions)
            return versions.Count > 0 ? versions[^1] : null;
    }

    public T? LatestJson<T>(string key) where T : class =>
        Latest(key) is { } artifact ? Json.Deserialize<T>(artifact.Content) : null;

    public IReadOnlyList<Artifact> History(string key)
    {
        if (!_history.TryGetValue(key, out var versions))
            return [];
        lock (versions)
            return versions.ToList();
    }

    public IReadOnlyList<Artifact> AllLatest() =>
        _history.Keys.Order(StringComparer.Ordinal).Select(Latest).OfType<Artifact>().ToList();
}

public static class WellKnownArtifacts
{
    /// <summary>Hash of build-relevant workspace files, republished by the engine after every change/rollback.</summary>
    public const string WorkspaceState = "workspace.state";

    /// <summary>The raw requirement text as provided by the requester (or later changed upstream).</summary>
    public const string RequirementsRaw = "requirements.raw";
}
