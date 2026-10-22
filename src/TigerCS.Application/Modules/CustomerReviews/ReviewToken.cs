using System.Security.Cryptography;
using System.Text;

namespace TigerCS.Application.Modules.CustomerReviews;

/// <summary>
/// The opaque secret in a review link: 256 bits from a cryptographic RNG, base64url-encoded (43 characters). It encodes no
/// ticket id, name or contact detail. Only its SHA-256 hash is stored, and the hash is the lookup key — so a guessed, altered,
/// truncated or extended token simply matches no row, and nothing about the ticket is derivable from the URL.
/// </summary>
public static class ReviewToken
{
    private const int ByteLength = 32;
    public const int EncodedLength = 43;

    public static string Generate() => Base64Url(RandomNumberGenerator.GetBytes(ByteLength));

    /// <summary>The SHA-256 hash to store/look up, or <c>null</c> when <paramref name="token"/> cannot be a token we issued.</summary>
    public static byte[]? Hash(string? token)
    {
        if (!IsWellFormed(token))
        {
            return null;
        }

        return SHA256.HashData(Encoding.ASCII.GetBytes(token!));
    }

    public static bool IsWellFormed(string? token)
    {
        if (token is null || token.Length != EncodedLength)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
