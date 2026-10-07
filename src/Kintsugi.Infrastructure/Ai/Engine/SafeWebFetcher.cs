using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Kintsugi.Infrastructure.Ai.Engine;

/// <summary>
/// Fetches a public web page for the model's <c>web_fetch</c> tool — and refuses anything that is
/// not public.
/// </summary>
/// <remarks>
/// <para>
/// The URL comes from a model, and the model's input includes pages it has read. So a page can
/// tell it to fetch <c>http://169.254.169.254/…</c> — this server's cloud metadata endpoint, which
/// on GKE hands out the pod's Google credentials — or anything else on the cluster network. That is
/// server-side request forgery driven by prompt injection, and the defence has to hold whatever the
/// model was persuaded to ask for.
/// </para>
/// <para>
/// The check is made where the socket connects (<see cref="SocketsHttpHandler.ConnectCallback"/>),
/// against the address actually being dialled — not against a lookup made earlier — so DNS
/// rebinding cannot swap a public answer for a private one between check and use. Redirects are
/// followed by hand so every hop goes through the same callback. Refused: loopback, private
/// (RFC 1918, ULA), link-local (which is where 169.254.169.254 lives), CGNAT, multicast,
/// unspecified and documentation ranges, in IPv4, IPv6 and IPv4-mapped IPv6.
/// </para>
/// </remarks>
public class SafeWebFetcher
{
    public const int MaxRedirects = 3;
    public const int MaxBytes = 1_000_000;
    public const int MaxReturnedChars = 12_000;

    private readonly HttpClient _client;

    public SafeWebFetcher()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectToPublicAddressAsync,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Kintsugi-Server (upgrade-path research)");
    }

    /// <summary>The page's text, or a sentence saying why there is none — never an exception, since
    /// the answer goes back to the model as a tool result.</summary>
    public async Task<string> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "Refused: only absolute http(s) URLs can be fetched.";
        }

        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (IsForbiddenHostName(uri.Host))
                {
                    return $"Refused: {uri.Host} is not a public address.";
                }

                using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                    {
                        return "Refused: the page redirected to a non-http(s) URL.";
                    }

                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return $"Fetch failed: HTTP {(int)response.StatusCode} from {uri}.";
                }

                var text = await ReadBoundedAsync(response, cancellationToken);
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                var readable = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ? HtmlToText(text) : text;
                return readable.Length <= MaxReturnedChars ? readable : readable[..MaxReturnedChars] + "\n[truncated]";
            }

            return "Fetch failed: too many redirects.";
        }
        catch (PublicAddressRequiredException ex)
        {
            return $"Refused: {ex.Message}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ex.InnerException is PublicAddressRequiredException inner
                ? $"Refused: {inner.Message}"
                : $"Fetch failed: {ex.Message}";
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxBytes];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
        {
            read += n;
        }

        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var permitted = addresses.Where(IsPublic).ToList();
        if (permitted.Count == 0)
        {
            throw new PublicAddressRequiredException($"{context.DnsEndPoint.Host} does not resolve to a public address.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // Dial the checked addresses themselves, not the name — a second lookup is exactly
            // what DNS rebinding relies on.
            await socket.ConnectAsync(permitted.ToArray(), context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsForbiddenHostName(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".cluster.local", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether an address is on the public internet. Public for tests.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0
                || b[0] == 10
                || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)      // CGNAT
                || (b[0] == 169 && b[1] == 254)                    // link-local, incl. cloud metadata
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)          // IETF protocol assignments
                || (b[0] == 192 && b[1] == 0 && b[2] == 2)          // TEST-NET-1
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))      // benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)       // TEST-NET-2
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)        // TEST-NET-3
                || b[0] >= 224);                                    // multicast, reserved, broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC                            // fc00::/7 unique local
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)); // documentation
        }

        return false;
    }

    /// <summary>Crude but adequate for reading a release page: scripts, styles and tags out,
    /// entities decoded, whitespace collapsed.</summary>
    public static string HtmlToText(string html)
    {
        var withoutBlocks = Regex.Replace(html, @"<(script|style|noscript|svg)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var withLinks = Regex.Replace(withoutBlocks, @"<a\s[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", "$2 ($1)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var withoutTags = Regex.Replace(withLinks, "<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, @"\s{2,}", " ").Trim();
    }

    private sealed class PublicAddressRequiredException(string message) : Exception(message);
}
