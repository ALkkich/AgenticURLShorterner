using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace UrlShortener.Api.Services;

/// <summary>Pseudonymises client addresses so unique-visitor counts never require storing IPs.</summary>
public sealed class VisitorHasher(IOptions<ShortenerOptions> options)
{
    private readonly byte[] _salt = Encoding.UTF8.GetBytes(options.Value.VisitorHashSalt);

    public string? Hash(IPAddress? address)
    {
        if (address is null)
            return null;
        var hash = HMACSHA256.HashData(_salt, address.GetAddressBytes());
        return Convert.ToHexString(hash, 0, 16);
    }
}
