using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Host;
using Halyard.Core.Llm;
using Halyard.Core.Planning;
using Halyard.Core.Serialization;

namespace Halyard.Core.Intelligence
{
    /// <summary>One command the crew ran (or was refused) while answering.</summary>
    public sealed class CrewCall
    {
        public string Command { get; set; }
        public string Method { get; set; }
        public string Path { get; set; }
        public int Status { get; set; }
        /// <summary>ran | refused | failed</summary>
        public string Outcome { get; set; }
        public string Note { get; set; }

        public CrewCall()
        {
            Command = string.Empty;
            Method = string.Empty;
            Path = string.Empty;
            Outcome = string.Empty;
            Note = string.Empty;
        }
    }

    /// <summary>A turn in the conversation the page keeps and sends back (the engine keeps no chat history).</summary>
    public sealed class CrewMessage
    {
        /// <summary>user | assistant</summary>
        public string Role { get; set; }
        public string Text { get; set; }

        public CrewMessage()
        {
            Role = "user";
            Text = string.Empty;
        }
    }

    public sealed class CrewTurn
    {
        public string Answer { get; set; }
        public List<CrewCall> Calls { get; set; }
        /// <summary>Proposal batches the crew made while answering; a person reviews and applies them on the Proposals page.</summary>
        public List<string> Batches { get; set; }
        /// <summary>fairlead when a model answered; brief when the deterministic scrum brief did.</summary>
        public string Source { get; set; }

        public CrewTurn()
        {
            Answer = string.Empty;
            Calls = new List<CrewCall>();
            Batches = new List<string>();
            Source = "brief";
        }
    }

    public sealed class CrewCallResult
    {
        public int Status { get; set; }
        public string Body { get; set; }
    }

    /// <summary>Runs one Halyard command in process, as the browser would (same router, same checks).</summary>
    public delegate Task<CrewCallResult> CrewDispatch(string method, string path, string query, string body);

    /// <summary>
    /// The crew: an agentic product owner and scrum master. Given a question or an instruction, it plans with the model through
    /// Fairlead, calls Halyard's own commands (the command registry is its tool list), reads the results and answers. It may read
    /// anything and may make proposals, but it can never write: commands marked as writing, approving, applying, rolling back,
    /// promoting, seeding and changing settings are refused before they reach the router, so every change still waits for a person
    /// on the Proposals page. With no model configured it answers from the scrum brief instead.
    /// </summary>
    public sealed class CrewAgent
    {
        public const int MaxSteps = 6;
        public const int MaxResultChars = 6000;
        public const int MaxHistory = 10;
        public const string NoModelNote = "(No model is configured, so this comes from the scrum brief. Install a Fairlead key for Halyard to let me plan and propose.)";

        /// <summary>POST commands that only read or only make proposal batches (nothing reaches the sandbox).</summary>
        public static readonly HashSet<string> ProposingPosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "halyard goal ready", "halyard metadata propose", "halyard parents propose", "halyard plan preview", "halyard plan propose",
            "halyard intake preview", "halyard handoff preview", "halyard promote preview",
        };

        /// <summary>Commands the crew never runs even though they do not write to a repository.</summary>
        public static readonly HashSet<string> Never = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "halyard session stop", "halyard heartbeat send", "halyard config set", "halyard proposal add", "halyard proposal decide",
            "halyard proposal delete", "halyard job cancel", "halyard llm test", "halyard agent ask", "halyard agent watch",
            "halyard voice speak", "halyard export semaphore", "halyard leadership report", "halyard export table",
        };

        private static readonly Regex PathParam = new Regex(@"\{([A-Za-z]+)\}");
        private readonly ILlmClient _llm;
        private readonly CrewDispatch _dispatch;
        private readonly List<CommandInfo> _tools;

        public CrewAgent(ILlmClient llm, CrewDispatch dispatch, IEnumerable<CommandInfo> commands)
        {
            if (dispatch == null) throw new ArgumentNullException("dispatch");
            _llm = llm;
            _dispatch = dispatch;
            _tools = Tools(commands ?? CommandRegistry.All).ToList();
        }

        public IReadOnlyList<CommandInfo> AvailableTools { get { return _tools; } }

        /// <summary>The commands the crew may call: reads, plus the POSTs that only propose. Never a writing or async command.</summary>
        public static IEnumerable<CommandInfo> Tools(IEnumerable<CommandInfo> all)
        {
            return all.Where(c => !c.Writes && !c.Async && !Never.Contains(c.Name) && (c.Method == "GET" || ProposingPosts.Contains(c.Name)));
        }

        public static string SystemPrompt(IEnumerable<CommandInfo> tools, ScrumBrief brief, string repository)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are the Halyard crew: the product owner and scrum master for the backlog in " + (string.IsNullOrEmpty(repository) ? "the current snapshot" : repository) + ".");
            sb.AppendLine("As product owner you keep the backlog ready (Theme > Epic > Feature > Story > Task), sized, prioritised and planned, and you report upward to leadership.");
            sb.AppendLine("As scrum master you run the cadence: standup (blockers, stale work, unowned work), refinement, planning within capacity, review and retro, and you remove impediments by naming who must act.");
            sb.AppendLine();
            sb.AppendLine("Rules:");
            sb.AppendLine("- You may read anything and you may create proposals. You can never approve, apply, roll back, promote, seed or change settings: a person does that on the Proposals page. When you made proposals, say which batch to review.");
            sb.AppendLine("- Use the commands below to look before you answer. Do not guess numbers; read them.");
            sb.AppendLine("- Answers are short, plain and spoken aloud: no tables, no markdown headings, at most six sentences unless asked for more.");
            sb.AppendLine();
            sb.AppendLine("Reply with exactly one JSON object and nothing else, either");
            sb.AppendLine("  {\"call\": {\"command\": \"halyard review show\", \"params\": {}, \"query\": {}, \"body\": {}}}");
            sb.AppendLine("to run a command (params fill {placeholders} in the route, query is for GET filters, body is the JSON for a POST), or");
            sb.AppendLine("  {\"answer\": \"...\"}");
            sb.AppendLine("when you are done. You get at most " + MaxSteps + " calls.");
            sb.AppendLine();
            sb.AppendLine("Commands:");
            foreach (var t in tools) sb.AppendLine("- " + t.Name + " (" + t.Method + " " + t.Route + "): " + t.Summary);
            if (brief != null)
            {
                sb.AppendLine();
                sb.AppendLine("Scrum brief right now: " + brief.Spoken);
                if (brief.Risks.Count > 0) sb.AppendLine("Risks: " + string.Join(" ", brief.Risks));
                if (brief.Actions.Count > 0) sb.AppendLine("Suggested next actions: " + string.Join("; ", brief.Actions.Select(a => a.Title + " (" + a.Command + ")")));
            }
            return sb.ToString();
        }

        public async Task<CrewTurn> AskAsync(string message, IList<CrewMessage> history, ScrumBrief brief, string repository, CancellationToken cancellation)
        {
            var text = (message ?? string.Empty).Trim();
            if (text.Length == 0) throw new ArgumentException("Say or type what you need.", "message");
            if (_llm == null) return Fallback(text, brief);

            var turn = new CrewTurn { Source = "fairlead" };
            var messages = new List<LlmMessage> { LlmMessage.System(SystemPrompt(_tools, brief, repository)) };
            if (history != null)
            {
                foreach (var h in history.Skip(Math.Max(0, history.Count - MaxHistory)))
                {
                    if (string.IsNullOrWhiteSpace(h.Text)) continue;
                    messages.Add(new LlmMessage(h.Role == "assistant" ? "assistant" : "user", h.Text));
                }
            }
            messages.Add(LlmMessage.User(text));

            for (var step = 0; step <= MaxSteps; step++)
            {
                if (step == MaxSteps) messages.Add(LlmMessage.User("You have used every call. Answer now with {\"answer\": \"...\"}."));
                var reply = await _llm.ChatAsync(messages, cancellation).ConfigureAwait(false);
                var parsed = Parse(reply.Text);
                if (parsed.Answer != null || parsed.Call == null || step == MaxSteps)
                {
                    turn.Answer = parsed.Answer ?? (parsed.Call == null ? reply.Text.Trim() : "I ran out of steps before I could finish; ask me again with a narrower question.");
                    return turn;
                }
                messages.Add(new LlmMessage("assistant", reply.Text));
                var result = await RunAsync(parsed.Call, turn).ConfigureAwait(false);
                messages.Add(LlmMessage.User(result));
            }
            return turn;
        }

        /// <summary>Runs one requested call if it is allowed; returns the text the model reads next.</summary>
        public async Task<string> RunAsync(JObject call, CrewTurn turn)
        {
            var name = ((string)call["command"] ?? string.Empty).Trim();
            var record = new CrewCall { Command = name };
            turn.Calls.Add(record);
            var info = CommandRegistry.All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (info == null) { record.Outcome = "refused"; record.Note = "No such command."; return "REFUSED: there is no command named '" + name + "'. Use one from the list."; }
            record.Method = info.Method;
            if (!_tools.Any(t => t.Name == info.Name))
            {
                record.Outcome = "refused";
                record.Note = info.Writes ? "Writes need a person." : "Not available to the crew.";
                return "REFUSED: '" + info.Name + "' " + (info.Writes ? "writes, and only a person may run it. Tell the user what to approve or apply instead." : "is not available to you.");
            }
            var parameters = call["params"] as JObject;
            string missing = null;
            var path = PathParam.Replace(info.Route, m =>
            {
                var v = parameters == null ? null : (string)parameters[m.Groups[1].Value];
                if (string.IsNullOrWhiteSpace(v)) { missing = m.Groups[1].Value; return m.Value; }
                return Uri.EscapeDataString(v.Trim());
            });
            record.Path = path;
            if (missing != null) { record.Outcome = "refused"; record.Note = "Missing " + missing + "."; return "REFUSED: give params." + missing + " for " + info.Name + "."; }
            var query = string.Empty;
            var q = call["query"] as JObject;
            if (q != null && info.Method == "GET")
                query = string.Join("&", q.Properties().Where(p => p.Value != null && p.Value.Type != JTokenType.Null).Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString((string)p.Value ?? string.Empty)));
            var body = info.Method == "POST" ? ((call["body"] as JObject) ?? new JObject()).ToString(Formatting.None) : null;

            CrewCallResult result;
            try { result = await _dispatch(info.Method, path, query, body).ConfigureAwait(false); }
            catch (Exception ex)
            {
                record.Outcome = "failed"; record.Note = ex.GetType().Name;
                return "FAILED: " + info.Name + " threw " + ex.GetType().Name + ".";
            }
            record.Status = result.Status;
            record.Outcome = result.Status >= 200 && result.Status < 300 ? "ran" : "failed";
            foreach (var b in BatchIds(result.Body)) if (!turn.Batches.Contains(b)) turn.Batches.Add(b);
            var bodyText = result.Body ?? string.Empty;
            if (bodyText.Length > MaxResultChars) bodyText = bodyText.Substring(0, MaxResultChars) + " ...(cut)";
            record.Note = record.Outcome == "ran" ? (bodyText.Length + " characters") : Short(bodyText);
            return "RESULT of " + info.Name + " (HTTP " + result.Status + "):\n" + bodyText;
        }

        public sealed class Parsed
        {
            public string Answer { get; set; }
            public JObject Call { get; set; }
        }

        /// <summary>Reads the model's reply: the first JSON object in it, with "answer" or "call". Anything else is an answer.</summary>
        public static Parsed Parse(string text)
        {
            var result = new Parsed();
            if (string.IsNullOrWhiteSpace(text)) { result.Answer = string.Empty; return result; }
            var t = text.Trim();
            var start = t.IndexOf('{');
            var end = t.LastIndexOf('}');
            if (start < 0 || end <= start) { result.Answer = t; return result; }
            JObject o;
            try { o = JObject.Parse(t.Substring(start, end - start + 1)); }
            catch (JsonException) { result.Answer = t; return result; }
            var call = o["call"] as JObject;
            if (call != null && !string.IsNullOrWhiteSpace((string)call["command"])) { result.Call = call; return result; }
            var answer = o["answer"];
            result.Answer = answer != null && answer.Type == JTokenType.String ? ((string)answer).Trim() : t;
            return result;
        }

        /// <summary>Proposal batch ids in a command's reply: a batch object's id, or any batchId field.</summary>
        public static IEnumerable<string> BatchIds(string body)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(body)) return found;
            foreach (Match m in Regex.Matches(body, "\"batchId\"\\s*:\\s*\"([^\"]+)\"")) found.Add(m.Groups[1].Value);
            try
            {
                var o = JToken.Parse(body) as JObject;
                if (o != null && o["proposals"] is JArray && ((JArray)o["proposals"]).Count > 0 && o["id"] != null && o["id"].Type == JTokenType.String) found.Add((string)o["id"]);
            }
            catch (JsonException) { }
            return found.Distinct().ToList();
        }

        /// <summary>No model: answer from the scrum brief, picking the part the question is about.</summary>
        public static CrewTurn Fallback(string message, ScrumBrief brief)
        {
            var turn = new CrewTurn { Source = "brief" };
            if (brief == null) { turn.Answer = "Synchronize a repository first, then ask me again."; return turn; }
            var m = message.ToLowerInvariant();
            var parts = new List<string>();
            if (m.Contains("block") || m.Contains("impediment"))
                parts.Add(brief.Blockers.Count == 0 ? "There are no open blockers." : "Blockers: " + string.Join("; ", brief.Blockers.Take(5).Select(b => b.Title + ", " + b.AgeDays + " days" + (string.IsNullOrEmpty(b.Owner) ? string.Empty : ", " + b.Owner))) + ".");
            else if (m.Contains("retro"))
                parts.Add(brief.RetroPrompts.Count == 0 ? "No finished sprints to look back on yet." : string.Join(" ", brief.RetroPrompts));
            else if (m.Contains("risk"))
                parts.Add(brief.Risks.Count == 0 ? "No risks stand out." : string.Join(" ", brief.Risks));
            else if (m.Contains("refine") || m.Contains("ready"))
                parts.Add(brief.Refinement.Count == 0 ? "Nothing is waiting for refinement." : "To refine: " + string.Join("; ", brief.Refinement.Take(5).Select(r => "#" + r.Number + " " + r.Title)) + ".");
            else if (m.Contains("ceremon") || m.Contains("meeting") || m.Contains("calendar"))
                parts.Add(string.Join(" ", brief.Ceremonies.Select(c => c.Name + " " + c.At.ToString("ddd d MMM") + ": " + c.Agenda)));
            else if (m.Contains("next") || m.Contains("should") || m.Contains("do "))
                parts.Add(string.Join(" ", brief.Actions.Take(3).Select(a => a.Title + ": " + a.Why)));
            else parts.Add(brief.Spoken);
            parts.Add(NoModelNote);
            turn.Answer = string.Join(" ", parts);
            return turn;
        }

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var t = LlmClient.Redact(s.Trim());
            return t.Length > 200 ? t.Substring(0, 200) : t;
        }
    }
}
