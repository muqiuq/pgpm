using System.Net;

namespace PGPM.Auth;

/// <summary>
/// <c>Auth:LocalLoginNetworks</c> (GGITSV, docs/03 §4.3): where the local password login is allowed. Empty (the default)
/// or <c>all</c>: from everywhere; <c>none</c>: nowhere (Microsoft SSO only); otherwise a comma-separated list of
/// networks and addresses (<c>10.0.0.0/8, 192.168.1.20</c>). Microsoft SSO works from anywhere. Behind a reverse proxy
/// the client's address needs the Proxy setting.
/// </summary>
public sealed class LocalLoginRule
{
    public static readonly LocalLoginRule Everywhere = new(LocalLoginMode.All, []);

    private LocalLoginRule(LocalLoginMode mode, IReadOnlyList<IPNetwork> networks)
    {
        Mode = mode;
        Networks = networks;
    }

    public LocalLoginMode Mode { get; }

    public IReadOnlyList<IPNetwork> Networks { get; }

    /// <summary>The setting, parsed: a bad entry stops the start rather than letting nobody (or everybody) in.</summary>
    public static LocalLoginRule Parse(string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0 || text.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return Everywhere;
        }
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalLoginRule(LocalLoginMode.None, []);
        }
        var networks = new List<IPNetwork>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(part, out var network))
            {
                networks.Add(network);
            }
            else if (IPAddress.TryParse(part, out var address))
            {
                networks.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            }
            else
            {
                throw new InvalidOperationException(
                    $"Auth:LocalLoginNetworks: \"{part}\" is no network or address (e.g. 10.0.0.0/8, 192.168.1.20; or all, none).");
            }
        }
        return new LocalLoginRule(LocalLoginMode.Networks, networks);
    }

    /// <summary>Whether a client at <paramref name="address"/> may sign in with a password. An unknown address only with <c>all</c>.</summary>
    public bool IsAllowed(IPAddress? address)
    {
        if (address is { IsIPv4MappedToIPv6: true })
        {
            address = address.MapToIPv4();
        }
        return Mode switch
        {
            LocalLoginMode.All => true,
            LocalLoginMode.None => false,
            _ => address is not null && Networks.Any(n => n.Contains(address)),
        };
    }
}

public enum LocalLoginMode { All, None, Networks }
