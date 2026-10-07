using System.Security.Cryptography;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;

namespace TigerCS.Application.Modules.CrmDocuments.Services;

public sealed class RandomOtpCodeGenerator : IOtpCodeGenerator
{
    public string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
}
