using System.Security.Cryptography;

namespace UrlShortener.Api.Domain;

public interface IShortCodeGenerator
{
    string Generate(int length);
}

/// <summary>
/// Cryptographically random base62 codes. Unpredictable codes prevent enumeration of
/// other users' links (a sequential/hash-of-id scheme would leak them).
/// </summary>
public sealed class RandomShortCodeGenerator : IShortCodeGenerator
{
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public string Generate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        return RandomNumberGenerator.GetString(Alphabet, length);
    }
}
