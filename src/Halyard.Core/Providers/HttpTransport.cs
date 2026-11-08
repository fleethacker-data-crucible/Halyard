using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Halyard.Core.Providers
{
    public sealed class HttpCall
    {
        public HttpCall(string method, string url)
        {
            Timeout = TimeSpan.FromSeconds(60);
            Method = method;
            Url = url;
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string Method { get; private set; }
        public string Url { get; private set; }
        public Dictionary<string, string> Headers { get; private set; }
        public string JsonBody { get; set; }
        public TimeSpan Timeout { get; set; }
        /// <summary>Read the reply as bytes (audio) into <see cref="HttpReply.BodyBytes"/>; Body then holds text only for an error reply.</summary>
        public bool BinaryResponse { get; set; }
    }

    public sealed class HttpReply
    {
        public HttpReply(int status, string body, IDictionary<string, string> headers)
        {
            Status = status;
            Body = body;
            Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        }

        public int Status { get; private set; }
        public string Body { get; private set; }
        public Dictionary<string, string> Headers { get; private set; }
        /// <summary>The raw reply when the call asked for <see cref="HttpCall.BinaryResponse"/>; null otherwise.</summary>
        public byte[] BodyBytes { get; set; }
        public bool IsSuccess { get { return Status >= 200 && Status < 300; } }

        public string Header(string name)
        {
            string v;
            return Headers.TryGetValue(name, out v) ? v : null;
        }
    }

    /// <summary>Thin seam over HttpClient so providers are unit-testable with a fake transport.</summary>
    public interface IHttpTransport
    {
        Task<HttpReply> SendAsync(HttpCall call, CancellationToken cancellation);
    }

    public sealed class ProviderHttpException : Exception
    {
        public ProviderHttpException(int status, string message, string body) : base(message)
        {
            Status = status;
            ResponseBody = body;
        }

        public int Status { get; private set; }
        public string ResponseBody { get; private set; }

        /// <summary>Stable fault code for the UI: AUTH_REJECTED, FORBIDDEN, NOT_FOUND, RATE_LIMITED, SERVER_ERROR, NETWORK.</summary>
        public string Code
        {
            get
            {
                if (Status == 401) return "AUTH_REJECTED";
                if (Status == 403) return "FORBIDDEN";
                if (Status == 404) return "NOT_FOUND";
                if (Status == 429) return "RATE_LIMITED";
                if (Status >= 500) return "SERVER_ERROR";
                if (Status == 0) return "NETWORK";
                return "HTTP_" + Status;
            }
        }
    }

    /// <summary>
    /// Production transport. One HttpClient per instance; TLS 1.2 is enforced for the .NET Framework
    /// build where the default can still be older. Never logs headers (they carry tokens).
    /// </summary>
    public sealed class HttpClientTransport : IHttpTransport, IDisposable
    {
        private readonly HttpClient _client;

        public HttpClientTransport() : this(null) { }

        public HttpClientTransport(HttpMessageHandler handler)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (NotSupportedException) { }
            // No automatic redirects: a redirected PATCH/DELETE would carry the token and the method to a URL
            // nobody configured. A 3xx surfaces as an error instead.
            _client = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, true) : new HttpClient(handler, true);
            _client.Timeout = TimeSpan.FromMinutes(5);
        }

        public async Task<HttpReply> SendAsync(HttpCall call, CancellationToken cancellation)
        {
            using (var request = new HttpRequestMessage(new HttpMethod(call.Method), call.Url))
            {
                foreach (var h in call.Headers)
                {
                    if (string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                if (call.JsonBody != null)
                {
                    request.Content = new StringContent(call.JsonBody, Encoding.UTF8, "application/json");
                }
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    cts.CancelAfter(call.Timeout);
                    HttpResponseMessage response;
                    try
                    {
                        response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new ProviderHttpException(0, "Network error calling " + SafeUrl(call.Url) + ": " + RootMessage(ex), null);
                    }
                    catch (TaskCanceledException)
                    {
                        if (cancellation.IsCancellationRequested) throw;
                        throw new ProviderHttpException(0, "Timed out calling " + SafeUrl(call.Url), null);
                    }
                    using (response)
                    {
                        byte[] bytes = null;
                        string body;
                        if (call.BinaryResponse)
                        {
                            bytes = response.Content == null ? new byte[0] : await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                            body = response.IsSuccessStatusCode ? string.Empty : Encoding.UTF8.GetString(bytes);
                        }
                        else body = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var h in response.Headers) headers[h.Key] = string.Join(", ", h.Value);
                        if (response.Content != null)
                        {
                            foreach (var h in response.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);
                        }
                        return new HttpReply((int)response.StatusCode, body, headers) { BodyBytes = bytes };
                    }
                }
            }
        }

        private static string RootMessage(Exception ex)
        {
            var e = ex;
            while (e.InnerException != null) e = e.InnerException;
            return e.Message;
        }

        /// <summary>Strips query strings so tokens that providers put in URLs never reach logs.</summary>
        public static string SafeUrl(string url)
        {
            var q = url.IndexOf('?');
            return q < 0 ? url : url.Substring(0, q);
        }

        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
