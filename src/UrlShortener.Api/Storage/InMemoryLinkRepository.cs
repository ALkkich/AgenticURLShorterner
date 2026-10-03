using System.Collections.Concurrent;
using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Storage;

/// <summary>Process-local store for the prototype. Codes are case-sensitive (base62).</summary>
public sealed class InMemoryLinkRepository : ILinkRepository
{
    private readonly ConcurrentDictionary<string, ShortLink> _links = new(StringComparer.Ordinal);

    public bool TryAdd(ShortLink link) => _links.TryAdd(link.Code, link);

    public ShortLink? Find(string code) => _links.TryGetValue(code, out var link) ? link : null;

    public bool Remove(string code) => _links.TryRemove(code, out _);
}
