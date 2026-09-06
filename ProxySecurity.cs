using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace ImageChat;

public static class ProxySecurity
{
    public static void Configure(ForwardedHeadersOptions options, IConfiguration config)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var address in config.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
        {
            if (!IPAddress.TryParse(address, out var parsed)) throw new InvalidOperationException("ReverseProxy:KnownProxies must contain IP addresses.");
            options.KnownProxies.Add(parsed);
        }
        foreach (var network in config.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [])
        {
            if (!System.Net.IPNetwork.TryParse(network, out var parsed)) throw new InvalidOperationException("ReverseProxy:KnownNetworks must contain CIDR networks.");
            if (parsed.PrefixLength == 0) throw new InvalidOperationException("Trusting every proxy address is not allowed.");
            options.KnownIPNetworks.Add(parsed);
        }
        if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
            throw new InvalidOperationException("When ReverseProxy:Enabled is true, configure the exact trusted proxy addresses or networks.");
    }
}