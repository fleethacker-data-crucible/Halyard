using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Halyard.Core.Providers
{
    /// <summary>Shared plumbing for REST providers: auth headers, JSON, error mapping, write pacing.</summary>
    public abstract class RestProviderBase
    {
        protected RestProviderBase(IHttpTransport http, string baseUrl, string? token, int writeDelayMs)
        {
            Http = http ?? throw new ArgumentNullException("http");
            BaseUrl = baseUrl.TrimEnd('/');
            Token = token;
            WriteDelayMs = writeDelayMs;
        }

        protected IHttpTransport Http { get; private set; }
        protected string BaseUrl { get; private set; }
        protected string? Token { get; private set; }
        protected int WriteDelayMs { get; private set; }

        protected abstract void Authenticate(HttpCall call);

        protected HttpCall NewCall(string method, string url)
        {
            var call = new HttpCall(method, url);
            call.Headers["Accept"] = "application/json";
            call.Headers["User-Agent"] = "Halyard/1.0 (+delivery-intelligence)";
            Authenticate(call);
            return call;
        }

        protected async Task<HttpReply> SendAsync(HttpCall call, CancellationToken cancellation)
        {
            var reply = await Http.SendAsync(call, cancellation).ConfigureAwait(false);
            if (!reply.IsSuccess)
            {
                throw new ProviderHttpException(reply.Status, DescribeFailure(call, reply), reply.Body);
            }
            return reply;
        }

        protected virtual string DescribeFailure(HttpCall call, HttpReply reply)
        {
            var detail = ExtractMessage(reply.Body);
            return call.Method + " " + HttpClientTransport.SafeUrl(call.Url) + " failed with HTTP " + reply.Status + (string.IsNullOrEmpty(detail) ? string.Empty : ": " + detail);
        }

        protected static string ExtractMessage(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;
            try
            {
                var tok = JToken.Parse(body!);
                if (tok is JObject o)
                {
                    var msg = (string?)o["message"] ?? (string?)o["error"] ?? (string?)o["error_description"];
                    if (msg == null && o["errors"] != null) msg = o["errors"]!.ToString(Formatting.None);
                    if (msg != null) return msg.Length > 300 ? msg.Substring(0, 300) : msg;
                }
            }
            catch (JsonException) { }
            var text = body!.Trim();
            return text.Length > 200 ? text.Substring(0, 200) : text;
        }

        protected async Task<JToken> GetJsonAsync(string url, CancellationToken cancellation)
        {
            var reply = await SendAsync(NewCall("GET", url), cancellation).ConfigureAwait(false);
            return ParseJson(reply.Body);
        }

        protected async Task<HttpReply> SendJsonAsync(string method, string url, JToken? payload, CancellationToken cancellation)
        {
            var call = NewCall(method, url);
            if (payload != null) call.JsonBody = payload.ToString(Formatting.None);
            return await SendAsync(call, cancellation).ConfigureAwait(false);
        }

        protected static JToken ParseJson(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return new JObject();
            return JToken.Parse(body);
        }

        /// <summary>Space out content-creating calls to stay clear of secondary rate limits.</summary>
        protected async Task PaceWriteAsync(CancellationToken cancellation)
        {
            var delay = WriteDelayMs >= 0 ? WriteDelayMs : DefaultWriteDelayMs;
            if (delay > 0) await Task.Delay(delay, cancellation).ConfigureAwait(false);
        }

        protected abstract int DefaultWriteDelayMs { get; }

        protected static string Esc(string value)
        {
            return Uri.EscapeDataString(value);
        }

        protected static DateTimeOffset? ParseDate(JToken? token)
        {
            var s = (string?)token;
            DateTimeOffset d;
            return !string.IsNullOrEmpty(s) && DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal, out d) ? d : (DateTimeOffset?)null;
        }

        protected static List<string> ReadLabels(JToken? labels)
        {
            var list = new List<string>();
            if (labels == null) return list;
            foreach (var l in labels)
            {
                var name = l.Type == JTokenType.String ? (string?)l : (string?)l["name"];
                if (!string.IsNullOrWhiteSpace(name)) list.Add(name!.Trim());
            }
            return list;
        }

        public static WorkItemState StateFromText(string? state)
        {
            return string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase) ? WorkItemState.Closed : WorkItemState.Open;
        }
    }
}
