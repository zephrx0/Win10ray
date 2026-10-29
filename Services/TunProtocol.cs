using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XrayMetro.Services;

public sealed record TunRequest(string Command, string? Token = null, bool UseDnsOverHttps = false, string? DnsOverHttpsUrl = null, bool BlockAds = true, Models.ProcessRouteRule[]? ProcessRules = null, bool DefaultRouteDirect = false);
public sealed record TunResponse(bool Ok, bool Running = false, string Code = "", string? Token = null, string[]? Logs = null, int ProtocolVersion = 0);

public static class TunProtocol
{
    public const string ServiceName = "Win10ray.Tun";
    public const string PipeName = "Win10ray.Tun.v1";
    public const int MaximumFrame = 262144;
    public const int CurrentVersion = 6;
    // Wintun itself can wait 15 seconds for PnP. Outer deadlines include cleanup and IPC.
    public const int AdapterStartSeconds = 35;
    public const int ServiceRequestSeconds = 45;
    public const int ClientRequestSeconds = 50;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > MaximumFrame) throw new InvalidDataException();
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > MaximumFrame) throw new InvalidDataException();
        byte[] payload = new byte[size]; await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException();
    }
}
