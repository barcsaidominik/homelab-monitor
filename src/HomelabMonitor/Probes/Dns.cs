using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HomelabMonitor.Probes;

public sealed record DnsResponse(ushort Id, bool IsResponse, int ResponseCode, bool Truncated, IReadOnlyList<IPAddress> Addresses);

/// <summary>Minimal DNS message codec (A queries only, RFC 1035).</summary>
public static class DnsMessage
{
    public static byte[] EncodeQuery(ushort id, string name)
    {
        var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100); // RD
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);      // QDCOUNT
        ms.Write(header);
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new ArgumentException("Invalid DNS label", nameof(name));
            }

            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }

        ms.WriteByte(0);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail, 1);     // QTYPE A
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1); // QCLASS IN
        ms.Write(tail);
        return ms.ToArray();
    }

    public static DnsResponse Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            throw new FormatException("DNS packet too short");
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(data);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var qd = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        var an = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        var pos = 12;
        for (var i = 0; i < qd; i++)
        {
            pos = SkipName(data, pos) + 4;
        }

        var addresses = new List<IPAddress>();
        for (var i = 0; i < an; i++)
        {
            pos = SkipName(data, pos);
            if (pos + 10 > data.Length)
            {
                throw new FormatException("Truncated answer record");
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            var cls = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
            var rdLen = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 8)..]);
            pos += 10;
            if (pos + rdLen > data.Length)
            {
                throw new FormatException("Csonka rdata");
            }

            if (type == 1 && cls == 1 && rdLen == 4)
            {
                addresses.Add(new IPAddress(data.Slice(pos, 4)));
            }

            pos += rdLen;
        }

        return new DnsResponse(id, (flags & 0x8000) != 0, flags & 0x000F, (flags & 0x0200) != 0, addresses);
    }

    private static int SkipName(ReadOnlySpan<byte> data, int pos)
    {
        while (true)
        {
            if (pos >= data.Length)
            {
                throw new FormatException("Truncated name");
            }

            var len = data[pos];
            if (len == 0)
            {
                return pos + 1;
            }

            if ((len & 0xC0) == 0xC0)
            {
                return pos + 2; // compression pointer
            }

            pos += 1 + len;
        }
    }
}

public interface IDnsClient
{
    Task<DnsResponse> QueryAAsync(IPEndPoint server, string name, TimeSpan timeout, CancellationToken ct);
}

public sealed class UdpDnsClient : IDnsClient
{
    public async Task<DnsResponse> QueryAAsync(IPEndPoint server, string name, TimeSpan timeout, CancellationToken ct)
    {
        var id = (ushort)Random.Shared.Next(0, 65536);
        using var udp = new UdpClient(server.AddressFamily);
        udp.Connect(server);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await udp.SendAsync(DnsMessage.EncodeQuery(id, name), cts.Token);
        while (true)
        {
            var received = await udp.ReceiveAsync(cts.Token);
            var resp = DnsMessage.Decode(received.Buffer);
            if (resp.Id == id && resp.IsResponse)
            {
                return resp;
            }
        }
    }
}
