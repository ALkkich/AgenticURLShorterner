using System.Text;
using System.Text.Json;
using Agentic.Orchestrator.Context;

namespace Agentic.Orchestrator.Observability;

public sealed record AuditEvent(
    long Seq,
    DateTimeOffset Timestamp,
    string RunId,
    string Type,
    string? NodeId,
    string Actor,
    string Message,
    IReadOnlyDictionary<string, string> Data,
    string PrevHash,
    string Hash);

/// <summary>
/// Append-only, hash-chained audit trail (JSON Lines). Each event's hash covers its content and the
/// previous event's hash, so any edit, deletion or reordering is detectable by <see cref="Verify"/>.
/// </summary>
public sealed class AuditLog : IDisposable
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly List<AuditEvent> _events = [];
    private readonly StreamWriter? _writer;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private string _lastHash = Genesis;

    public AuditLog(string runId, string? filePath = null, TimeProvider? time = null)
    {
        RunId = runId;
        _time = time ?? TimeProvider.System;
        if (filePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            _writer = new StreamWriter(filePath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }
    }

    public string RunId { get; }

    /// <summary>Live stream of events (console progress, external sinks).</summary>
    public event Action<AuditEvent>? Appended;

    public IReadOnlyList<AuditEvent> Events
    {
        get { lock (_gate) return _events.ToList(); }
    }

    public AuditEvent Append(string type, string actor, string message, string? nodeId = null, IReadOnlyDictionary<string, string>? data = null)
    {
        AuditEvent audit;
        lock (_gate)
        {
            var payload = new SortedDictionary<string, string>(data?.ToDictionary() ?? [], StringComparer.Ordinal);
            var seq = _events.Count + 1;
            var timestamp = _time.GetUtcNow();
            var hash = ComputeHash(seq, timestamp, RunId, type, nodeId, actor, message, payload, _lastHash);
            audit = new AuditEvent(seq, timestamp, RunId, type, nodeId, actor, message, payload, _lastHash, hash);
            _events.Add(audit);
            _lastHash = hash;
            _writer?.WriteLine(JsonSerializer.Serialize(audit, Json.Options).ReplaceLineEndings(" "));
        }
        Appended?.Invoke(audit);
        return audit;
    }

    public static (bool Valid, long? BrokenAtSeq) Verify(IEnumerable<AuditEvent> events)
    {
        var previous = Genesis;
        foreach (var e in events)
        {
            var expected = ComputeHash(e.Seq, e.Timestamp, e.RunId, e.Type, e.NodeId, e.Actor, e.Message,
                new SortedDictionary<string, string>(e.Data.ToDictionary(), StringComparer.Ordinal), previous);
            if (e.PrevHash != previous || e.Hash != expected)
                return (false, e.Seq);
            previous = e.Hash;
        }
        return (true, null);
    }

    public static IReadOnlyList<AuditEvent> ReadFile(string path) =>
        File.ReadLines(path).Where(l => l.Length > 0).Select(l => Json.Deserialize<AuditEvent>(l)).ToList();

    private static string ComputeHash(long seq, DateTimeOffset at, string runId, string type, string? nodeId, string actor,
        string message, SortedDictionary<string, string> data, string previous)
    {
        var canonical = JsonSerializer.Serialize(new object?[] { seq, at.ToUnixTimeMilliseconds(), runId, type, nodeId, actor, message, data, previous });
        return Json.Sha256(canonical);
    }

    public void Dispose() => _writer?.Dispose();
}
