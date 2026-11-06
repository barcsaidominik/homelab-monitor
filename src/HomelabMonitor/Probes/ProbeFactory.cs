using System.Net;

namespace HomelabMonitor.Probes;

/// <summary>Builds the light probes from configuration. Every probe kind is optional; an empty section adds nothing.</summary>
public static class ProbeFactory
{
    public static ProbeRunner Build(ProbeOptions o, TimeProvider time)
    {
        var timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
        var http = ProbeHttpClientFactory.Create(o);
        var probes = new List<IProbe>();
        probes.AddRange(o.Http.Select(h => new HttpEndpointProbe(h, http, time, timeout)));

        var dns = new UdpDnsClient();
        probes.AddRange(o.Dns.Select(d => new DnsProbe(d.CheckId, new IPEndPoint(IPAddress.Parse(d.Server), d.Port), d.Names, dns, time, timeout)));

        if (o.Services.Count > 0)
        {
            probes.Add(new ServiceProbe(o.Services, new WindowsServiceStatusReader(), time));
        }

        if (o.Processes.Count > 0)
        {
            probes.Add(new ProcessProbe(o.Processes, new SystemProcessLister(), time));
        }

        var tls = new TlsConnector();
        probes.AddRange(o.Tls.Select(t => new TlsHandshakeProbe(t, tls, time, timeout)));
        probes.AddRange(o.JsonApi.Select(j => new JsonApiProbe(j, http, time, timeout)));
        if (o.FanState.Enabled)
        {
            probes.Add(new FanStateProbe(o.FanState, () => FanStateProbe.ReadFile(o.FanState.StatePath), time));
        }

        var duplicate = probes.SelectMany(p => p.CheckIds).GroupBy(id => id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Check id '{duplicate.Key}' is produced by more than one probe.");
        }

        return new ProbeRunner(probes, time, timeout);
    }
}
