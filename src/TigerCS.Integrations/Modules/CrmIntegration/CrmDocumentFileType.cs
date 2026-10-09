namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// Decides what a downloaded CRM document <b>really is</b>, so the emailed
/// attachment gets its true extension and media type — never a guessed one.
/// A CRM "Layout Plan" may arrive with no extension in its name, an
/// <c>application/octet-stream</c> content type, and a storage path that says
/// nothing; the bytes are then the authority.
///
/// <para>
/// Order of evidence: the <b>file signature</b> (magic bytes) of PDF, PNG,
/// JPEG, GIF, WebP and TIFF decides on its own and overrides a conflicting
/// declared type or name. Container formats whose signature is ambiguous
/// (a ZIP could be anything; <c>BM</c> is two bytes) are accepted only when a
/// <i>declared</i> type or a name hint (Content-Disposition, URL, attachment
/// name) agrees with the signature. Anything else — an executable, an HTML
/// page, text, an unrecognised blob — is refused rather than mailed to a
/// customer under a made-up extension. PDF is never the default.
/// </para>
/// </summary>
internal static class CrmDocumentFileType
{
    internal sealed record Detected(string MediaType, string Extension);

    private static readonly Detected Pdf = new("application/pdf", ".pdf");
    private static readonly Detected Png = new("image/png", ".png");
    private static readonly Detected Jpeg = new("image/jpeg", ".jpg");
    private static readonly Detected Gif = new("image/gif", ".gif");
    private static readonly Detected Webp = new("image/webp", ".webp");
    private static readonly Detected Tiff = new("image/tiff", ".tif");
    private static readonly Detected Bmp = new("image/bmp", ".bmp");
    private static readonly Detected Doc = new("application/msword", ".doc");
    private static readonly Detected Docx = new("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx");

    private static readonly Detected[] All = [Pdf, Png, Jpeg, Gif, Webp, Tiff, Bmp, Doc, Docx];

    /// <summary>Extensions removed from a label before the true one is appended.</summary>
    private static readonly string[] KnownExtensions = [".pdf", ".png", ".jpg", ".jpeg", ".jpe", ".gif", ".webp", ".tif", ".tiff", ".bmp", ".doc", ".docx"];

    /// <summary>The resolved type, or null when the bytes cannot be established as an allowed document.</summary>
    internal static Detected? Resolve(byte[] bytes, string? declaredMediaType, params string?[] nameHints)
    {
        var sniffed = Sniff(bytes);
        if (sniffed is not null)
        {
            return sniffed;
        }

        // Ambiguous containers: need a declared type or a name hint that agrees with the signature.
        var candidates = new List<Detected?> { FromMediaType(declaredMediaType) };
        candidates.AddRange(nameHints.Select(FromName));

        foreach (var candidate in candidates.Where(c => c is not null))
        {
            if (candidate == Docx && IsZip(bytes)) return Docx;
            if (candidate == Doc && IsOle(bytes)) return Doc;
            if (candidate == Bmp && bytes.Length > 14 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M') return Bmp;
        }

        return null;
    }

    /// <summary>The attachment file name: the label without a stale extension, plus the true one.</summary>
    internal static string FileName(string label, Detected type)
    {
        var name = label.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars().Concat(['"', ';', ',', '\r', '\n']).ToHashSet();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');

        var ext = Path.GetExtension(name);
        if (ext.Length > 0 && KnownExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            name = name[..^ext.Length].TrimEnd('.');
        }

        if (name.Length == 0)
        {
            name = "document";
        }

        if (name.Length > 120)
        {
            name = name[..120];
        }

        return name + type.Extension;
    }

    private static Detected? Sniff(byte[] b)
    {
        if (b.Length >= 5)
        {
            // PDF: "%PDF-" within the first KiB (some producers prepend junk).
            var window = Math.Min(b.Length - 4, 1024);
            for (var i = 0; i < window; i++)
            {
                if (b[i] == 0x25 && b[i + 1] == 0x50 && b[i + 2] == 0x44 && b[i + 3] == 0x46 && b[i + 4] == 0x2D)
                {
                    return Pdf;
                }
            }
        }

        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return Png;
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return Jpeg;
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8' && (b[4] == '7' || b[4] == '9') && b[5] == 'a') return Gif;
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return Webp;
        if (b.Length >= 4 && ((b[0] == 'I' && b[1] == 'I' && b[2] == 0x2A && b[3] == 0x00) || (b[0] == 'M' && b[1] == 'M' && b[2] == 0x00 && b[3] == 0x2A))) return Tiff;
        return null;
    }

    private static bool IsZip(byte[] b) => b.Length >= 4 && b[0] == 'P' && b[1] == 'K' && b[2] == 3 && b[3] == 4;

    private static bool IsOle(byte[] b) =>
        b.Length >= 8 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0 && b[4] == 0xA1 && b[5] == 0xB1 && b[6] == 0x1A && b[7] == 0xE1;

    private static Detected? FromMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        var t = mediaType.Trim().ToLowerInvariant();
        return t switch
        {
            "image/jpg" => Jpeg,
            "image/x-ms-bmp" => Bmp,
            "image/tif" => Tiff,
            _ => All.FirstOrDefault(d => d.MediaType == t)
        };
    }

    private static Detected? FromName(string? name)
    {
        var ext = Path.GetExtension(name ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => Pdf,
            ".png" => Png,
            ".jpg" or ".jpeg" or ".jpe" => Jpeg,
            ".gif" => Gif,
            ".webp" => Webp,
            ".tif" or ".tiff" => Tiff,
            ".bmp" => Bmp,
            ".doc" => Doc,
            ".docx" => Docx,
            _ => null
        };
    }
}
