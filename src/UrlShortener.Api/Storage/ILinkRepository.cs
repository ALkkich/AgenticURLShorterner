using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Storage;

/// <summary>
/// Persistence port. <see cref="TryAdd"/> must be atomic so concurrent creates of the same
/// code cannot both succeed (maps to a unique-key insert in a relational/KV store).
/// </summary>
public interface ILinkRepository
{
    bool TryAdd(ShortLink link);
    ShortLink? Find(string code);
    bool Remove(string code);
}
