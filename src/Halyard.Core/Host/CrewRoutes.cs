using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Analysis;
using Halyard.Core.Config;
using Halyard.Core.Intelligence;
using Halyard.Core.Llm;
using Halyard.Core.Model;
using Halyard.Core.Planning;
using Halyard.Core.Serialization;

namespace Halyard.Core.Host
{
    /// <summary>The weekly watch: what the crew found and proposed in one unattended pass over the backlog.</summary>
    public sealed class WatchReport
    {
        public string Id { get; set; }
        public DateTimeOffset At { get; set; }
        public string Repository { get; set; }
        public GoalRun Goal { get; set; }
        public ScrumBrief Brief { get; set; }
        /// <summary>A short note from the product owner (the model), or the spoken brief when no model is used.</summary>
        public string Note { get; set; }
        public string Next { get; set; }
        public string MarkdownPath { get; set; }

        public WatchReport()
        {
            Id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            At = DateTimeOffset.UtcNow;
            Repository = string.Empty;
            Note = string.Empty;
            Next = string.Empty;
            MarkdownPath = string.Empty;
        }
    }

    /// <summary>
    /// The crew (agentic product owner and scrum master), its scrum brief, the weekly watch that a scheduled runner calls, and
    /// the voice: spoken answers through TalkForge (started when needed, as Lazarette does), and TalkForge's Whisper files for voice input in the page.
    /// </summary>
    public sealed partial class HalyardHost
    {
        private void RegisterCrewRoutes()
        {
            Router.Get("/api/scrum/brief", r => Task.FromResult(Json.Ok(BuildBrief(Query(r, "label")))));
            Router.Post("/api/agent/ask", AskCrew);
            Router.Post("/api/agent/watch", async r => Json.Ok(await RunWatchAsync(Json.Body(r)).ConfigureAwait(false)));
            Router.Get("/api/agent/watches", r => Task.FromResult(Json.Ok(ReadWatches())));
            Router.Get("/api/voice/status", async r => Json.Ok(await VoiceStatusAsync().ConfigureAwait(false)));
            Router.Post("/api/voice/start", async r => Json.Ok(await Voice.StartAsync(CancellationToken.None).ConfigureAwait(false)));
            Router.Get("/api/voice/voices", r => Task.FromResult(Json.Ok(Voice.ListVoices())));
            Router.Post("/api/voice/speak", Speak);
        }

        public ScrumBrief BuildBrief(string label)
        {
            if (Snapshots.Current == null) throw new BadRequestException("Synchronize a repository first; the scrum brief reads the snapshot.");
            List<WorkItem> all;
            var visible = VisibleItems(label ?? Settings.LabelFilter, out all);
            var review = Review.Run(visible, Taxonomy, label ?? Settings.LabelFilter, all);
            return ScrumMaster.Build(visible, review, Team, DateTimeOffset.UtcNow);
        }

        /// <summary>Runs an API route in process with this session's token, exactly as the page would.</summary>
        public async Task<CrewCallResult> DispatchAsync(string method, string path, string query, string body)
        {
            var request = new HttpRequest { Method = method, Path = path, Query = string.IsNullOrEmpty(query) ? string.Empty : "?" + query };
            request.Headers["Host"] = "127.0.0.1:" + Port;
            request.Headers[SessionHeader] = SessionToken;
            if (body != null) request.Body = Encoding.UTF8.GetBytes(body);
            var response = await HandleAsync(request).ConfigureAwait(false);
            return new CrewCallResult { Status = response.Status, Body = response.Body == null ? string.Empty : Encoding.UTF8.GetString(response.Body) };
        }

        private async Task<HttpResponse> AskCrew(HttpRequest r)
        {
            var body = Json.Body(r);
            var message = Json.Read<string>(body, "message") ?? string.Empty;
            if (message.Trim().Length == 0) throw new BadRequestException("Say or type what you need.");
            if (message.Length > 4000) throw new BadRequestException("Keep the message under 4000 characters.");
            var history = Json.Read<List<CrewMessage>>(body, "history") ?? new List<CrewMessage>();
            var label = Json.Read<string>(body, "label");
            ScrumBrief brief = null;
            try { brief = BuildBrief(label); }
            catch (BadRequestException) { brief = null; }   // no snapshot yet: the crew answers without the brief
            ILlmClient llm = Settings.Llm.IsConfigured() ? CreateLlm() : null;
            var crew = new CrewAgent(llm, DispatchAsync, CommandRegistry.All);
            var snap = Snapshots.Current;
            CrewTurn turn;
            try { turn = await crew.AskAsync(message, history, brief, snap == null ? null : snap.Repository, CancellationToken.None).ConfigureAwait(false); }
            catch (Providers.ProviderHttpException ex)
            {
                // Fairlead is down or refused: still answer, from the brief, and say why.
                Log.Warn("CREW_MODEL", ex.Code + " " + ex.Message);
                turn = CrewAgent.Fallback(message, brief);
                turn.Answer = turn.Answer.Replace(CrewAgent.NoModelNote, "(" + ex.Message + " This answer comes from the scrum brief.)");
            }
            Log.Info("CREW_TURN", turn.Source + ": " + turn.Calls.Count + " calls (" + string.Join(", ", turn.Calls.Select(c => c.Command + "=" + c.Outcome)) + "), " + turn.Batches.Count + " batches");
            return Json.Ok(turn);
        }

        /// <summary>
        /// One unattended pass: the backlog-ready goal (proposals only), the scrum brief and, with a model, a short note from the
        /// product owner. Writes output/watch/watch-&lt;id&gt;.json and .md. Nothing reaches the sandbox: applying stays a person's job.
        /// </summary>
        public async Task<WatchReport> RunWatchAsync(JObject body)
        {
            var snap = Snapshots.Current;
            if (snap == null) throw new BadRequestException("Synchronize a repository first.");
            var label = Json.Read<string>(body, "label");
            var useModel = (Json.Read<bool?>(body, "useModel") ?? false) && Settings.Llm.IsConfigured();
            var goalBody = new JObject { { "useModel", useModel } };
            if (!string.IsNullOrEmpty(label)) goalBody["label"] = label;
            var report = new WatchReport { Repository = snap.Repository ?? string.Empty };
            try { report.Goal = await RunReadyGoalAsync(goalBody).ConfigureAwait(false); }
            catch (BadRequestException ex)
            {
                // A source repository is read-only: proposals only target the sandbox. The watch still reports on the source.
                report.Goal = new GoalRun { Target = snap.Repository ?? string.Empty, Next = ex.Message };
                report.Goal.Steps.Add(new GoalStep { Id = "ready", Title = "Get the backlog ready", Status = "skipped", Note = ex.Message });
            }
            report.Brief = BuildBrief(label);
            report.Next = report.Goal.Next;
            report.Note = report.Brief.Spoken;
            if (useModel)
            {
                try
                {
                    var llm = CreateLlm();
                    var prompt = "Write the weekly note from the product owner and scrum master to the team and leadership, in at most six plain sentences: where the sprint stands, the main risk, what was proposed for review, and the one thing that most needs a decision.\n\nBrief: "
                        + report.Brief.Spoken + "\nRisks: " + string.Join(" ", report.Brief.Risks) + "\nGoal: " + string.Join("; ", report.Goal.Steps.Select(s => s.Title + " = " + s.Status + (s.Proposals > 0 ? " (" + s.Proposals + " proposals)" : string.Empty))) + "\nNext: " + report.Goal.Next;
                    var reply = await llm.ChatAsync(new[] { LlmMessage.System("You are a concise, practical product owner. No markdown."), LlmMessage.User(prompt) }, CancellationToken.None).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(reply.Text)) report.Note = reply.Text.Trim();
                }
                catch (Providers.ProviderHttpException ex) { Log.Warn("WATCH_NOTE", ex.Code + " " + ex.Message); }
                catch (InvalidOperationException ex) { Log.Warn("WATCH_NOTE", ex.Message); }
            }
            Directory.CreateDirectory(Settings.WatchDirectory);
            var stem = SafePath.Under(Settings.WatchDirectory, "watch-" + report.Id);
            report.MarkdownPath = stem + ".md";
            File.WriteAllText(report.MarkdownPath, WatchMarkdown(report), new UTF8Encoding(false));
            File.WriteAllText(stem + ".json", JsonConvert.SerializeObject(report, Formatting.Indented, JsonSettings.Api), new UTF8Encoding(false));
            Log.Info("WATCH_RUN", report.Id + " " + report.Repository + ": " + string.Join(", ", report.Goal.Steps.Select(s => s.Id + "=" + s.Status)));
            return report;
        }

        public static string WatchMarkdown(WatchReport w)
        {
            var b = w.Brief;
            var sb = new StringBuilder();
            sb.AppendLine("# Weekly watch: " + (string.IsNullOrEmpty(w.Repository) ? "backlog" : w.Repository));
            sb.AppendLine();
            sb.AppendLine(w.At.ToString("yyyy-MM-dd HH:mm") + " UTC, " + b.CurrentSprint + ", day " + b.DaysElapsed + " (" + b.DaysLeft + " left)");
            sb.AppendLine();
            sb.AppendLine(w.Note);
            sb.AppendLine();
            sb.AppendLine("## Sprint");
            sb.AppendLine();
            sb.AppendLine("- Points: " + b.PointsDone + " done of " + b.PointsPlanned + " planned (" + b.PointsExpected + " expected by today, " + (b.OnPace ? "on pace" : "behind") + ")");
            foreach (var r in b.Risks) sb.AppendLine("- Risk: " + r);
            sb.AppendLine();
            sb.AppendLine("## Standup");
            sb.AppendLine();
            if (b.Blockers.Count + b.Stale.Count + b.Unowned.Count == 0) sb.AppendLine("Nothing to raise.");
            foreach (var x in b.Blockers) sb.AppendLine("- Blocked #" + x.Number + " " + x.Title + " (" + x.AgeDays + " days" + (string.IsNullOrEmpty(x.Owner) ? string.Empty : ", " + x.Owner) + "): " + x.Reason);
            foreach (var x in b.Stale) sb.AppendLine("- Stale #" + x.Number + " " + x.Title + ": " + x.Reason);
            foreach (var x in b.Unowned) sb.AppendLine("- Unowned #" + x.Number + " " + x.Title);
            sb.AppendLine();
            sb.AppendLine("## Proposed for review");
            sb.AppendLine();
            foreach (var s in w.Goal.Steps) sb.AppendLine("- " + s.Title + ": " + s.Status + (string.IsNullOrEmpty(s.Note) ? string.Empty : ". " + s.Note));
            sb.AppendLine();
            sb.AppendLine("Next: " + w.Next);
            sb.AppendLine();
            sb.AppendLine("## Next best actions");
            sb.AppendLine();
            foreach (var a in b.Actions) sb.AppendLine("- " + a.Title + " (`" + a.Command + "`): " + a.Why);
            sb.AppendLine();
            sb.AppendLine("## Ceremonies");
            sb.AppendLine();
            foreach (var c in b.Ceremonies) sb.AppendLine("- " + c.Name + ", " + c.At.ToString("ddd d MMM") + ": " + c.Agenda);
            if (b.RetroPrompts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Retro prompts");
                sb.AppendLine();
                foreach (var p in b.RetroPrompts) sb.AppendLine("- " + p);
            }
            return sb.ToString();
        }

        private List<WatchReport> ReadWatches()
        {
            var list = new List<WatchReport>();
            if (!Directory.Exists(Settings.WatchDirectory)) return list;
            foreach (var file in Directory.GetFiles(Settings.WatchDirectory, "watch-*.json"))
            {
                try { var w = JsonConvert.DeserializeObject<WatchReport>(File.ReadAllText(file)); if (w != null) list.Add(w); }
                catch (JsonException ex) { Log.Warn("WATCH_UNREADABLE", Path.GetFileName(file) + " is skipped: " + ex.Message); }
            }
            return list.OrderByDescending(w => w.At).Take(12).ToList();
        }

        // ---- voice: TalkForge, the way Lazarette does it ----

        private TalkForge _talkForge;
        private readonly object _talkForgeGate = new object();

        /// <summary>The TalkForge this engine talks to (and starts when needed). Disposed, and its copy stopped, when the host stops.</summary>
        public TalkForge Voice
        {
            get
            {
                lock (_talkForgeGate)
                {
                    if (_talkForge == null)
                    {
                        _talkForge = new TalkForge(FindTalkForge(), Settings.TalkForgePort, Options.Transport) { Log = m => Log.Warn("TALKFORGE", m) };
                        _talkForge.ExternalToken = () => Environment.GetEnvironmentVariable("HALYARD_TALKFORGE_TOKEN");
                    }
                    return _talkForge;
                }
            }
        }

        private void StopVoice()
        {
            TalkForge t;
            lock (_talkForgeGate) { t = _talkForge; _talkForge = null; }
            if (t != null) t.Dispose();
        }

        private async Task<object> VoiceStatusAsync()
        {
            var state = await Voice.StateAsync(CancellationToken.None).ConfigureAwait(false);
            return new { speech = new { state = state.State, ready = state.State == "ready", message = state.Message, port = Voice.Port, voice = Settings.VoiceName }, dictation = SttStatus() };
        }

        private async Task<HttpResponse> Speak(HttpRequest r)
        {
            var body = Json.Body(r);
            var text = (Json.Read<string>(body, "text") ?? string.Empty).Trim();
            if (text.Length == 0 || text.Length > TalkForge.MaxChars) throw new BadRequestException("Send 1-" + TalkForge.MaxChars + " characters of text.");
            var voice = Json.Read<string>(body, "voice");
            if (string.IsNullOrWhiteSpace(voice)) voice = Settings.VoiceName;
            if (!string.IsNullOrWhiteSpace(voice) && !Voice.ListVoices().Voices.Any(v => string.Equals(v.Id, voice.Trim(), StringComparison.Ordinal)))
                throw new BadRequestException("The voice '" + voice + "' is not installed in TalkForge.");
            byte[] audio;
            try { audio = await Voice.SpeakAsync(text, voice, Settings.VoiceSpeed, CancellationToken.None).ConfigureAwait(false); }
            catch (TalkForgeOfflineException ex) { return Json.Error(409, "VOICE_OFFLINE", ex.Message, r.RequestId); }
            var response = HttpResponse.Bytes(200, audio, "audio/wav");
            response.Headers["Cache-Control"] = "no-store";
            return response;
        }

        /// <summary>Whisper and its runtime, read-only from TalkForge's assets, at fixed /stt/ paths. Nothing else under TalkForge is served.</summary>
        public static readonly IReadOnlyDictionary<string, string> SttFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "transformers.min.js", @"stt\transformers.min.js" },
            { "models/whisper-tiny.en/config.json", @"stt\models\whisper-tiny.en\config.json" },
            { "models/whisper-tiny.en/generation_config.json", @"stt\models\whisper-tiny.en\generation_config.json" },
            { "models/whisper-tiny.en/preprocessor_config.json", @"stt\models\whisper-tiny.en\preprocessor_config.json" },
            { "models/whisper-tiny.en/tokenizer.json", @"stt\models\whisper-tiny.en\tokenizer.json" },
            { "models/whisper-tiny.en/tokenizer_config.json", @"stt\models\whisper-tiny.en\tokenizer_config.json" },
            { "models/whisper-tiny.en/added_tokens.json", @"stt\models\whisper-tiny.en\added_tokens.json" },
            { "models/whisper-tiny.en/special_tokens_map.json", @"stt\models\whisper-tiny.en\special_tokens_map.json" },
            { "models/whisper-tiny.en/onnx/encoder_model_quantized.onnx", @"stt\models\whisper-tiny.en\onnx\encoder_model_quantized.onnx" },
            { "models/whisper-tiny.en/onnx/decoder_model_merged_quantized.onnx", @"stt\models\whisper-tiny.en\onnx\decoder_model_merged_quantized.onnx" },
            { "onnx/ort-wasm-simd-threaded.jsep.mjs", @"vendor\onnx\ort-wasm-simd-threaded.jsep.mjs" },
            { "onnx/ort-wasm-simd-threaded.jsep.wasm", @"vendor\onnx\ort-wasm-simd-threaded.jsep.wasm" },
        };

        private static readonly string[] SttRequired =
        {
            "transformers.min.js", "models/whisper-tiny.en/config.json", "models/whisper-tiny.en/preprocessor_config.json", "models/whisper-tiny.en/tokenizer.json",
            "models/whisper-tiny.en/onnx/encoder_model_quantized.onnx", "models/whisper-tiny.en/onnx/decoder_model_merged_quantized.onnx", "onnx/ort-wasm-simd-threaded.jsep.wasm",
        };

        /// <summary>The TalkForge folder: HALYARD_TALKFORGE_PATH, else a TalkForge folder next to Halyard, else %USERPROFILE%\Projects\TalkForge.</summary>
        public string FindTalkForge()
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(Settings.TalkForgePath)) candidates.Add(Settings.TalkForgePath.Trim());
            else
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(Settings.RootDirectory));
                if (!string.IsNullOrEmpty(parent)) candidates.Add(SafePath.Under(parent, "TalkForge"));
                var profile = Environment.GetEnvironmentVariable("USERPROFILE");
                if (!string.IsNullOrEmpty(profile)) candidates.Add(SafePath.Under(profile, "Projects", "TalkForge"));
            }
            return candidates.FirstOrDefault(c => Directory.Exists(SafePath.Under(c, "assets")));
        }

        private object SttStatus()
        {
            var dir = FindTalkForge();
            if (dir == null) return new { ready = false, missing = new string[0], message = "TalkForge was not found, so voice input uses the browser's on-device recogniser when it has one. Set HALYARD_TALKFORGE_PATH." };
            var missing = SttRequired.Where(k => !File.Exists(SafePath.Under(dir, "assets", SttFiles[k]))).ToArray();
            if (missing.Length > 0) return new { ready = false, missing = missing, message = "Voice input needs TalkForge's speech-to-text files (" + missing.Length + " missing). In the TalkForge folder run: populate-assets.ps1 -Only 'stt/*'" };
            return new { ready = true, missing = new string[0], message = "Voice input is ready (Whisper, in this browser tab)." };
        }

        /// <summary>Serves one /stt/ file, or null when the path is not one of them. These files hold no secrets, so a worker can fetch them without the session header.</summary>
        private HttpResponse ServeStt(HttpRequest request)
        {
            if (!request.Path.StartsWith("/stt/", StringComparison.Ordinal) || request.Method != "GET") return null;
            var name = request.Path.Substring(5);
            string rel;
            if (!SttFiles.TryGetValue(name, out rel)) return HttpResponse.Text(404, "Not found");
            var dir = FindTalkForge();
            var file = dir == null ? null : SafePath.Under(dir, "assets", rel);
            if (file == null || !File.Exists(file)) return HttpResponse.Text(404, "Voice input needs TalkForge's speech-to-text files. In the TalkForge folder run: populate-assets.ps1 -Only 'stt/*'");
            var ext = Path.GetExtension(name).ToLowerInvariant();
            var type = ext == ".js" || ext == ".mjs" ? "text/javascript; charset=utf-8" : ext == ".json" ? "application/json; charset=utf-8" : ext == ".wasm" ? "application/wasm" : "application/octet-stream";
            var response = HttpResponse.Bytes(200, File.ReadAllBytes(file), type);
            response.Headers["Cache-Control"] = "public, max-age=86400";
            return response;
        }
    }
}
