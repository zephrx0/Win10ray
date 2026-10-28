using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace XrayMetro.Services;

public static class TunComponentIntegrity
{
    public static FrozenDictionary<string, string> Hashes { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["sing-box.exe"] = "8F30682EE198855440D2A7FE070D5D3653793B2D01BB8C85EEA88F93BBE7C8B9",
        ["wintun.dll"] = "E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE",
        ["libcronet.dll"] = "3217C6260FBCA5F16072E0B79735742F40109A63BB0FF88FD6B96DD6B54A2928"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    public static async Task VerifyAsync(string directory, CancellationToken cancellationToken = default)
    {
        foreach (var entry in Hashes)
        {
            await using FileStream input = new(Path.Combine(directory, entry.Key), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
            if (actual != entry.Value) throw new InvalidDataException("TUN component checksum mismatch.");
        }
    }
}
