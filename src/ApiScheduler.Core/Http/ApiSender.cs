using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ApiScheduler.Core.Http;

public sealed record SendResult(bool Success, int? StatusCode, string? Error, bool Permanent)
{
    /// <summary>Value written into SENT_CALL.</summary>
    public string StatusTag(string successValue) =>
        Success ? successValue
        : Permanent ? $"REJECTED-{StatusCode}"
        : StatusCode is { } c ? $"FAILED-{c}" : "FAILED-NETWORK";
}

/// <summary>
/// Hardened HTTP sender: TLS validation always on (optional certificate pinning for internal CAs),
/// no redirects (the API key must never be forwarded elsewhere), no cookies, bounded timeouts.
/// </summary>
public sealed class ApiSender : IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _url;
    private readonly string _headerName;
    private readonly string _apiKey;

    public ApiSender(string url, string headerName, string apiKey, int timeoutSeconds, string? pinnedThumbprint)
    {
        _url = new Uri(url);
        _headerName = headerName;
        _apiKey = apiKey;
        var pin = NormalizeThumbprint(pinnedThumbprint);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                {
                    if (errors == SslPolicyErrors.None) return true;
                    if (pin is null || cert is null) return false;
                    // Pinning only relaxes chain trust; the host name must still match.
                    if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                    using var c = new X509Certificate2(cert);
                    return string.Equals(c.GetCertHashString(HashAlgorithmName.SHA256), pin, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.Thumbprint, pin, StringComparison.OrdinalIgnoreCase);
                },
            },
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("ApiScheduler/1.0");
    }

    public async Task<SendResult> PostJsonAsync(string json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(_headerName, _apiKey);

        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) return new SendResult(true, code, null, false);

            var body = await ReadSnippetAsync(response, ct);
            // 4xx (except timeout / throttling) means the data or request is wrong → retrying won't help.
            var permanent = code is >= 400 and < 500 && code is not (408 or 429);
            return new SendResult(false, code, $"HTTP {code} {response.ReasonPhrase}: {body}", permanent);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SendResult(false, null, "Request timed out.", false);
        }
        catch (HttpRequestException ex)
        {
            return new SendResult(false, null, ex.InnerException?.Message ?? ex.Message, false);
        }
    }

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            return text.Length > 300 ? text[..300] + "…" : text;
        }
        catch { return ""; }
    }

    private static string? NormalizeThumbprint(string? t) =>
        string.IsNullOrWhiteSpace(t) ? null : new string(t.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    public void Dispose() => _client.Dispose();
}
