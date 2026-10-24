using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cascade.Core.Providers;

namespace Cascade.Core.Tests
{
    /// <summary>Scripted HTTP transport: match on method + URL substring, record every call.</summary>
    public sealed class FakeTransport : IHttpTransport
    {
        public sealed class Rule
        {
            public string Method = "GET";
            public string UrlContains = string.Empty;
            public int Status = 200;
            public string Body = "{}";
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Func<HttpCall, HttpReply>? Dynamic;
        }

        public List<Rule> Rules = new List<Rule>();
        public List<HttpCall> Calls = new List<HttpCall>();

        public FakeTransport On(string method, string urlContains, int status, string body, Dictionary<string, string>? headers = null)
        {
            Rules.Add(new Rule { Method = method, UrlContains = urlContains, Status = status, Body = body, Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) });
            return this;
        }

        public FakeTransport On(string method, string urlContains, Func<HttpCall, HttpReply> dynamic)
        {
            Rules.Add(new Rule { Method = method, UrlContains = urlContains, Dynamic = dynamic });
            return this;
        }

        public Task<HttpReply> SendAsync(HttpCall call, CancellationToken cancellation)
        {
            Calls.Add(call);
            // First matching rule wins: tests list specific rules before general ones.
            var rule = Rules.FirstOrDefault(r => string.Equals(r.Method, call.Method, StringComparison.OrdinalIgnoreCase) && call.Url.IndexOf(r.UrlContains, StringComparison.OrdinalIgnoreCase) >= 0);
            if (rule == null) return Task.FromResult(new HttpReply(404, "{\"message\":\"no fake rule for " + call.Method + " " + call.Url + "\"}", new Dictionary<string, string>()));
            if (rule.Dynamic != null) return Task.FromResult(rule.Dynamic(call));
            return Task.FromResult(new HttpReply(rule.Status, rule.Body, rule.Headers));
        }

        public IEnumerable<HttpCall> Writes()
        {
            return Calls.Where(c => c.Method != "GET" && c.Method != "HEAD");
        }
    }
}
