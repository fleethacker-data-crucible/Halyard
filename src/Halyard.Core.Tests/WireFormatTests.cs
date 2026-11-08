using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Halyard.Core.Config;
using Halyard.Core.Host;
using Halyard.Core.Intelligence;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Schema;
using Halyard.Core.Snapshots;
using Halyard.Core.Synthetic;
using Halyard.Core.Serialization;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>
    /// Golden files pinning the JSON Halyard writes: API responses (what the browser UI parses), persisted files
    /// (what the next version must still read) and provider/LLM request bodies. They were recorded from the
    /// Newtonsoft.Json build (1.0) before the JSON layer was replaced; the text must stay byte for byte the same.
    /// Set HALYARD_UPDATE_GOLDEN=1 to re-record after an intended change, and review the diff.
    /// </summary>
    internal static class Golden
    {
        public static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Halyard.sln"))) dir = dir.Parent;
            if (dir == null) throw new InvalidOperationException("Halyard.sln not found above " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        public static void Check(string name, string actual)
        {
            actual = actual.Replace("\r\n", "\n");
            var path = Path.Combine(RepoRoot(), "fixtures", "wire", name);
            if (Environment.GetEnvironmentVariable("HALYARD_UPDATE_GOLDEN") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, actual, new UTF8Encoding(false));
                return;
            }
            Assert.True(File.Exists(path), "Missing golden file " + path + " (record it with HALYARD_UPDATE_GOLDEN=1).");
            var expected = File.ReadAllText(path).Replace("\r\n", "\n");
            if (expected == actual) return;
            var lines = expected.Split('\n');
            var got = actual.Split('\n');
            for (var i = 0; i < Math.Max(lines.Length, got.Length); i++)
            {
                var e = i < lines.Length ? lines[i] : "<end>";
                var a = i < got.Length ? got[i] : "<end>";
                if (e != a) Assert.Fail(name + " differs at line " + (i + 1) + ":\nexpected: " + e + "\nactual:   " + a);
            }
        }

        /// <summary>Masks values that change per run while keeping their shape (date text format, digit counts that matter).</summary>
        public static string Normalize(string json, params string[] extraVolatileKeys)
        {
            // ISO dates: keep the format (fraction present or not, zone suffix), mask the digits.
            json = Regex.Replace(json, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?", m => "<date" + (m.Groups[1].Success ? ".f" : string.Empty) + (m.Groups[2].Success ? (m.Groups[2].Value == "Z" ? "Z" : "+zz:zz") : string.Empty) + ">");
            // Ids built from the clock and a random suffix (jobs, batches, runs, proposals).
            json = Regex.Replace(json, @"\b\d{8}-\d{6}(-[0-9a-f]{4})?\b", "<stamp-id>");
            json = Regex.Replace(json, @"\b\d{6}-[0-9a-f]{6}\b", "<job-id>");
            json = Regex.Replace(json, @"(?<![0-9a-f])[0-9a-f]{12}(?![0-9a-f])", "<hex12>");
            foreach (var key in new[] { "uptimeSeconds", "ageSeconds", "port", "pid" }.Concat(extraVolatileKeys))
                json = Regex.Replace(json, "(\"" + key + "\"\\s*:\\s*)(\"[^\"]*\"|-?[0-9.]+)", "$1<" + key + ">");
            json = json.Replace(Path.GetTempPath().Replace("\\", "\\\\"), "<tmp>/").Replace(Path.GetTempPath(), "<tmp>/");
            json = Regex.Replace(json, @"halyard-(host|wire)-[0-9a-f]{32}", "<root>");
            // Windows path separators (JSON-escaped) read the same as the recorded '/'.
            json = json.Replace("\\\\", "/");
            // The parser's own wording after this prefix is not part of the contract (status, code and prefix are).
            json = Regex.Replace(json, "Body is not valid JSON: [^\"]*", "Body is not valid JSON: <parser message>");
            return json;
        }
    }

    public enum WireSample { FirstValue = 0, SecondValue = 1, URLStyle = 2, already_lower = 3 }

    [Flags]
    public enum WireFlags { None = 0, Alpha = 1, Beta = 2 }

    public sealed class WireBase
    {
        public string BaseName { get; set; } = "base";
    }

    public sealed class WireDto
    {
        public string Id { get; set; } = "dto-1";
        public string? Missing { get; set; }
        public int Count { get; set; } = 3;
        public long Big { get; set; } = 9007199254740993L;
        public double Ratio { get; set; } = 2.0;
        public double? MaybeRatio { get; set; }
        public decimal Money { get; set; } = 12.50m;
        public float Single { get; set; } = 0.1f;
        public bool IsOK { get; set; } = true;
        public WireSample Sample { get; set; } = WireSample.URLStyle;
        public WireSample? MaybeSample { get; set; }
        public WireFlags Flags { get; set; } = WireFlags.Alpha | WireFlags.Beta;
        public DateTimeOffset At { get; set; } = new DateTimeOffset(2026, 10, 26, 9, 5, 7, 120, TimeSpan.FromHours(-5));
        public DateTimeOffset? MaybeAt { get; set; }
        public DateTime UtcTime { get; set; } = new DateTime(2026, 10, 26, 14, 0, 0, DateTimeKind.Utc);
        public DateTime PlainTime { get; set; } = new DateTime(2026, 10, 26, 14, 0, 0, 500, DateTimeKind.Unspecified);
        public TimeSpan Span { get; set; } = TimeSpan.FromSeconds(3725.5);
        public Guid Guid { get; set; } = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        public char Letter { get; set; } = 'x';
        public Uri Link { get; set; } = new Uri("https://example.test/a?b=c&d=e");
        public List<string> Tags { get; set; } = new List<string> { "a", "B" };
        public string[] Empty { get; set; } = new string[0];
        public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int> { { "UpperKey", 1 }, { "lower", 2 }, { "with space", 3 } };
        public Dictionary<string, string?> Meta { get; set; } = new Dictionary<string, string?> { { "points", "5" }, { "gone", null } };
        public Dictionary<WireSample, int> ByEnum { get; set; } = new Dictionary<WireSample, int> { { WireSample.SecondValue, 2 } };
        public IReadOnlyList<WireSample> Samples { get; set; } = new[] { WireSample.FirstValue, WireSample.already_lower };
        public WireBase Child { get; set; } = new WireBase();
        public object? Anything { get; set; } = new { Nested = 1, inner = new[] { 1.5, 0.0001, 1e21, 0.0 } };
        public string Text { get; set; } = "quote\" back\\ slash/ tab\t nl\n cr\r bell\u0007 nul\0 del\u007f é 漢 \u2028 \u2029 \u0085 <script>&'";
        public JObject Raw { get; set; } = new JObject { ["kept"] = "as is", ["n"] = 1 };
        public int this[int i] { get { return i; } }
        public static string StaticIgnored { get { return "never"; } }
        internal string InternalIgnored { get; set; } = "never";
        public string WriteOnly { set { } }
        public string PublicField = "field";
    }

    public class WireFormatTests
    {
        [Fact]
        public void ApiSerializerShapes()
        {
            var sb = new StringBuilder();
            void Case(string name, object? value) { sb.Append(name).Append(" => ").Append(Json.Serialize(value)).Append('\n'); }
            Case("dto", new WireDto());
            Case("null", null);
            Case("string", "plain");
            Case("int", 42);
            // Only values whose "R" text is the same on .NET Framework 4.8 and .NET 8 (1/3, 5e-324, -0.0, 1e15 and 1e16 differ there, with Newtonsoft too).
            Case("doubles", new[] { 0.0, 1.0, -1.0, 0.1, 0.25, 123456789.0, 1e14, 1e17, 1.5e300, 1e-7, 0.000123, double.MaxValue });
            Case("specials", new object[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, float.NaN });
            Case("floats", new[] { 0.1f, 1f, 2.5f, 1e-7f });
            Case("decimals", new[] { 0m, 1m, 1.0m, 1.50m, -2.125m, 79228162514264337593543950335m });
            Case("ints", new object[] { int.MinValue, long.MaxValue, (short)-3, (byte)7, (sbyte)-7, (ushort)9, 10u, 11ul, (long?)null });
            Case("anon", new { Id = "x", id2 = "y", ID3 = "z", URLValue = 1, ETag = 2, IsOK = 3, A = 4, a_b = 5, X1Y = 6, Ünïcode = 7, @protected = 8, _under = 9 });
            Case("anonNulls", new { a = (string?)null, b = (int?)null, c = (object?)null, d = (List<string>?)null });
            Case("enumValues", new object[] { WireSample.FirstValue, WireSample.URLStyle, WireSample.already_lower, (WireSample)42, WireFlags.None, WireFlags.Alpha | WireFlags.Beta, JobState.Running, DayOfWeek.Monday });
            Case("dates", new object[] { new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.FromMinutes(330)), new DateTimeOffset(new DateTime(2026, 1, 2, 3, 4, 5).AddTicks(1234567), TimeSpan.Zero), new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), DateTime.MinValue });
            Case("selectEnumerable", Enumerable.Range(1, 3).Select(i => new { i, sq = i * i }));
            Case("nestedLists", new List<List<int>> { new List<int> { 1 }, new List<int>() });
            Case("hashSet", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Only" });
            Case("dictOfObjects", new Dictionary<string, object?> { { "A", new { B = 1 } }, { "n", null }, { "list", new[] { "x" } } });
            Case("readOnlyDict", (IReadOnlyDictionary<string, int>)new Dictionary<string, int> { { "K", 1 } });
            Case("sortedDict", new SortedDictionary<string, int> { { "b", 2 }, { "a", 1 } });
            Case("jtoken", JToken.Parse("{\"x\":[1,2.5,\"s\",null,true,{}],\"y\":{\"Z\":1}}"));
            Case("jvalueDateString", JToken.Parse("{\"d\":\"2026-10-24T10:00:00Z\",\"o\":\"2026-10-24T10:00:00.123+02:00\",\"plain\":\"2026-10-24\"}"));
            Case("error", System.Text.Encoding.UTF8.GetString(Json.Error(400, "BAD_REQUEST", "nope", "req-1").Body));
            Case("keyValuePairs", new[] { new KeyValuePair<string, int>("k", 1) });
            Case("tupleLike", Tuple.Create("a", 1));
            Case("bytes", new byte[] { 1, 2, 3, 250 });
            Case("bools", new[] { true, false });
            Case("chars", new[] { 'a', '"', '\n' });
            Check("api-serializer.txt", sb.ToString());
        }

        [Fact]
        public void DomTextFormats()
        {
            var sb = new StringBuilder();
            var o = new JObject
            {
                ["s"] = "text \"q\" \u2028 é",
                ["i"] = 5,
                ["l"] = 5000000000L,
                ["d"] = 2.0,
                ["d2"] = 0.1,
                ["b"] = true,
                ["n"] = null,
                ["dto"] = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero).ToString("o"),
                ["a"] = new JArray(1, "two", new JObject(), new JArray()),
                ["empty"] = new JObject(),
                ["nested"] = new JObject { ["deep"] = new JArray(new JObject { ["k"] = "v" }) },
            };
            sb.Append("indented:\n").Append(o.ToString()).Append('\n');
            sb.Append("none:\n").Append(o.ToString(Formatting.None)).Append('\n');
            sb.Append("emptyArrayIndented:\n").Append(new JArray().ToString()).Append('\n');
            sb.Append("emptyObjectIndented:\n").Append(new JObject().ToString()).Append('\n');
            sb.Append("valueToString:\n");
            foreach (var t in new JToken[] { "str", 5, 2.5, 2.0, true, JValue.CreateNull(), 1e21, 0.000001 })
                sb.Append(t.Type).Append(": ").Append(t.ToString()).Append(" | ").Append(t.ToString(Formatting.None)).Append('\n');
            Check("dom-text.txt", sb.ToString());
        }

        [Fact]
        public void DomReadingConversions()
        {
            var sb = new StringBuilder();
            var j = JObject.Parse("{\"s\":\"5\",\"i\":7,\"f\":2.5,\"f0\":3.0,\"b\":true,\"bs\":\"true\",\"n\":null,\"d\":\"2026-10-24T10:00:00Z\",\"do\":\"2026-10-24T10:00:00.5+02:00\",\"date\":\"2026-09-21\",\"big\":12345678901,\"neg\":-0.5,\"exp\":1E3,\"o\":{\"k\":1},\"a\":[1,\"x\"]}");
            foreach (var p in j.Properties()) sb.Append(p.Name).Append(" type=").Append(p.Value.Type).Append('\n');
            void Try(string name, Func<object?> f)
            {
                string r;
                try { var v = f(); r = v == null ? "null" : v.GetType().Name + ":" + Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); }
                catch (Exception ex) { r = "throws " + (ex is JsonException ? "JsonException" : ex.GetType().Name); }
                sb.Append(name).Append(" => ").Append(r).Append('\n');
            }
            Try("(string)s", () => (string?)j["s"]);
            Try("(string)i", () => (string?)j["i"]);
            Try("(string)f", () => (string?)j["f"]);
            Try("(string)f0", () => (string?)j["f0"]);
            Try("(string)b", () => (string?)j["b"]);
            Try("(string)n", () => (string?)j["n"]);
            Try("(string)missing", () => (string?)j["missing"]);
            Try("(string)date", () => (string?)j["date"]);
            Try("(string)o", () => (string?)j["o"]);
            Try("(int?)s", () => (int?)j["s"]);
            Try("(int?)f", () => (int?)j["f"]);
            Try("(int?)n", () => (int?)j["n"]);
            Try("(int?)missing", () => (int?)j["missing"]);
            Try("(int)i", () => (int)j["i"]!);
            Try("(long?)big", () => (long?)j["big"]);
            Try("(int?)big", () => (int?)j["big"]);
            Try("(double?)i", () => (double?)j["i"]);
            Try("(double?)s", () => (double?)j["s"]);
            Try("(double?)neg", () => (double?)j["neg"]);
            Try("(double?)exp", () => (double?)j["exp"]);
            Try("(bool?)b", () => (bool?)j["b"]);
            Try("(bool?)bs", () => (bool?)j["bs"]);
            Try("(bool?)i", () => (bool?)j["i"]);
            Try("(bool)n", () => (bool)j["n"]!);
            Try("(string)a", () => (string?)j["a"]);
            Try("o.ToString(None)", () => j["o"]!.ToString(Formatting.None));
            Try("a[1]", () => (string?)j["a"]![1]);
            Try("s[\"x\"]", () => j["s"]!["x"]);
            Try("a[\"x\"]", () => j["a"]!["x"]);
            Try("o[0]", () => j["o"]![0]);
            Try("a[5]", () => j["a"]![5]);
            Try("exp.ToString()", () => j["exp"]!.ToString());
            Try("f0.ToString()", () => j["f0"]!.ToString());
            Try("parse trailing", () => JObject.Parse("{} x"));
            Try("parse array as object", () => JObject.Parse("[1]"));
            Try("parse bad", () => JToken.Parse("{\"a\":}"));
            Try("parse comment", () => JToken.Parse("{/*c*/\"a\":1}").ToString(Formatting.None));
            Try("parse single quotes", () => JToken.Parse("{'a':'b'}").ToString(Formatting.None));
            Try("parse unquoted name", () => JToken.Parse("{a:1}").ToString(Formatting.None));
            Try("parse trailing comma", () => JToken.Parse("[1,2,]").ToString(Formatting.None));
            Try("parse duplicate key", () => JToken.Parse("{\"a\":1,\"a\":2}").ToString(Formatting.None));
            Try("parse escapes", () => (string?)JToken.Parse("[\"\\u00e9\\n\\/\\\"\"]")[0]);
            Try("parse empty", () => JToken.Parse(""));
            Try("parse whitespace number", () => JToken.Parse("  12  ").ToString());
            Try("parse NaN", () => JToken.Parse("NaN").Type);
            Try("parse big integer", () => JToken.Parse("123456789012345678901234567890").Type);
            Check("dom-read.txt", sb.ToString());
        }

        [Fact]
        public void RequestBodyReads()
        {
            var sb = new StringBuilder();
            void Try<T>(string body, string field)
            {
                string r;
                try
                {
                    var j = Json.Body(new HttpRequest { Body = Encoding.UTF8.GetBytes(body) });
                    var v = Json.Read<T>(j, field);
                    r = v == null ? "null" : v is System.Collections.IEnumerable e && !(v is string) ? "[" + string.Join(",", e.Cast<object?>().Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture))) + "]" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!;
                }
                catch (BadRequestException ex) { r = "400 " + Regex.Replace(ex.Message, "Body is not valid JSON: .*", "Body is not valid JSON: <parser message>"); }
                catch (Exception ex) { r = "throws " + ex.GetType().Name; }
                sb.Append(typeof(T).Name).Append(' ').Append(body).Append(" => ").Append(r).Append('\n');
            }
            Try<int?>("{\"x\":5}", "x");
            Try<int?>("{\"x\":\"5\"}", "x");
            Try<int?>("{\"x\":5.5}", "x");
            Try<int?>("{\"x\":4.5}", "x");
            Try<int?>("{\"x\":\"abc\"}", "x");
            Try<int?>("{\"x\":true}", "x");
            Try<int?>("{\"x\":null}", "x");
            Try<int?>("{}", "x");
            Try<int?>("{\"x\":99999999999}", "x");
            Try<int?>("{\"x\":[1]}", "x");
            Try<double?>("{\"x\":2}", "x");
            Try<double?>("{\"x\":\"2.5\"}", "x");
            Try<bool?>("{\"x\":true}", "x");
            Try<bool?>("{\"x\":\"true\"}", "x");
            Try<bool?>("{\"x\":1}", "x");
            Try<bool?>("{\"x\":\"yes\"}", "x");
            Try<string>("{\"x\":\"s\"}", "x");
            Try<string>("{\"x\":5}", "x");
            Try<string>("{\"x\":true}", "x");
            Try<string>("{\"x\":2.50}", "x");
            Try<string>("{\"x\":{}}", "x");
            Try<string>("{\"x\":[]}", "x");
            Try<string>("{\"x\":\"2026-10-24T10:00:00Z\"}", "x");
            Try<List<string>>("{\"x\":[\"a\",1,true,null]}", "x");
            Try<List<string>>("{\"x\":\"a\"}", "x");
            Try<List<long>>("{\"x\":[1,\"2\"]}", "x");
            Try<List<long>>("{\"x\":[1.5]}", "x");
            Try<List<long>>("{\"x\":[\"z\"]}", "x");
            Try<string>("", "x");
            Try<string>("   ", "x");
            Try<string>("[1]", "x");
            Try<string>("{bad", "x");
            Try<string>("\"str\"", "x");
            Check("request-reads.txt", sb.ToString());
        }

        [Fact]
        public void PersistedFiles()
        {
            var root = Path.Combine(Path.GetTempPath(), "halyard-wire-" + Guid.NewGuid().ToString("N"));
            try
            {
                var sb = new StringBuilder();
                var at = new DateTimeOffset(2026, 10, 26, 8, 30, 0, TimeSpan.Zero);
                var manifest = new SeedManifest { RunId = "20261026-083000", Target = "github:o/r", StartedAt = at, FinishedAt = at.AddMinutes(2), Requested = 2 };
                manifest.Items.Add(new SeededItem { Id = "EPIC-a", Type = "EPIC", Number = 1, Key = "github:o/r:1", Url = "https://github.com/o/r/issues/1", NativeLinked = true });
                manifest.Items.Add(new SeededItem { Id = "STORY-b", Type = "STORY", ParentId = "EPIC-a", Number = 2, Key = "github:o/r:2", Defects = { "missing-points" }, ExpectedCodes = { "STORY_MISSING_POINTS" } });
                manifest.Errors.Add("one \"error\"");
                sb.Append("seed-manifest:\n").Append(manifest.ToJson()).Append('\n');
                var back = SeedManifest.FromJson(manifest.ToJson());
                sb.Append("seed-manifest-roundtrip:\n").Append(back.ToJson()).Append('\n');

                var store = new ProposalStore(Path.Combine(root, "proposals"));
                var batch = new ProposalBatch { Id = "20261026-083000-abcd", Kind = ProposalKind.Refinement, Target = "github:o/r", CreatedAt = at, Source = "llm:openai", Considered = 3, Skipped = 1, RawModelOutput = "raw\nout" };
                batch.SkipReasons.Add("no body");
                batch.Proposals.Add(new Proposal
                {
                    Id = "p00000000001", BatchId = batch.Id, Kind = ProposalKind.Refinement, ItemKey = "github:o/r:2", ItemNumber = 2, ItemCanonicalId = "STORY-b", ItemTitle = "STORY :: b",
                    CurrentValue = null, ProposedValue = "new body", Confidence = "high", Reason = "r", Source = "llm:openai", Decision = ProposalDecision.Approved, DecisionNote = "ok",
                    CreatedAt = at, DecidedAt = at.AddSeconds(1), ProposedMeta = new Dictionary<string, string?> { { "points", "3" }, { "effort", null } }, Addresses = { "A" }, Remaining = { },
                });
                store.Save(batch);
                var batchFile = Directory.GetFiles(Path.Combine(root, "proposals"), "proposals-*.json").Single();
                sb.Append("proposal-batch:\n").Append(File.ReadAllText(batchFile)).Append('\n');
                var loaded = store.Load(batch.Id)!;
                sb.Append("proposal-batch-api:\n").Append(Json.Serialize(loaded)).Append('\n');

                var visions = new VisionStore(Path.Combine(root, "vision"));
                var draft = new VisionDraft { Id = "20261026-083000", CreatedAt = at, Source = "deterministic", Repository = "github:o/r", Vision = "v", Markdown = "# m", ProposalBatchId = null };
                draft.Themes.Add(new VisionTheme { Id = "THEME-x", Title = "X", Purpose = "p", Epics = { "EPIC-a" }, IsNew = true });
                draft.Priorities.Add(new VisionPriority { Rank = 1, Epic = "EPIC-a", Title = "A", Why = "w" });
                draft.Risks.Add("risk");
                visions.Save(draft);
                sb.Append("vision:\n").Append(File.ReadAllText(Directory.GetFiles(Path.Combine(root, "vision"), "*.json").Single())).Append('\n');
                sb.Append("vision-api:\n").Append(Json.Serialize(visions.Load(draft.Id))).Append('\n');

                var snap = new Snapshot { Id = "20261026-083000", Repository = "o/r", Provider = ProviderKind.GitHub, TakenAt = at, LabelFilter = null };
                var repo = new RepositoryRef(ProviderKind.GitHub, "o/r");
                var w = new WorkItem(repo, 7) { ProviderId = "I_7", ProviderNumericId = 107, Title = "STORY :: t", Body = "body \"q\"\n~~~halyard-meta\ntype: STORY\n~~~", Url = "https://github.com/o/r/issues/7", CreatedAt = at, UpdatedAt = at.AddDays(1) };
                w.Labels.Add("type::story");
                snap.Items.Add(w);
                snap.Repositories.Add("o/r");
                var snapText = SnapshotStore.Serialize(snap);
                sb.Append("snapshot:\n").Append(snapText).Append('\n');
                var snapBack = SnapshotStore.Deserialize(snapText, true);
                sb.Append("snapshot-roundtrip:\n").Append(SnapshotStore.Serialize(snapBack)).Append('\n');
                sb.Append("snapshot-takenAt: ").Append(snapBack.TakenAt.ToUniversalTime().ToString("o")).Append('\n');

                sb.Append("taxonomy:\n").Append(Taxonomy.Default().ToJson()).Append('\n');
                var catalogOptions = SyntheticOptions.ForTotal(12);
                sb.Append("catalog:\n").Append(SyntheticCatalog.ToJson(SyntheticCatalog.Generate(catalogOptions).Take(4).ToList(), catalogOptions)).Append('\n');
                sb.Append("log-entry: ").Append(new LogEntry { Time = at, Level = "WARN", Event = "E", Message = "m \"q\"", Route = null, RequestId = "r1" }.ToJson().ToString(Formatting.None)).Append('\n');
                Check("persisted.txt", sb.ToString());
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void FilesWrittenByVersion1StillLoad()
        {
            // Written by Halyard 1.0 (Newtonsoft.Json, default settings): PascalCase names, enums as numbers, indented.
            var dir = Path.Combine(Golden.RepoRoot(), "fixtures", "wire", "v1-files");
            var store = new ProposalStore(Path.Combine(dir, "proposals"));
            var batch = store.Load("20261020-101500-1a2b")!;
            Assert.Equal(ProposalKind.StoryPoints, batch.Kind);
            Assert.Equal(2, batch.Proposals.Count);
            Assert.Equal(ProposalDecision.Applied, batch.Proposals[0].Decision);
            Assert.Null(batch.Proposals[1].CurrentValue);
            Assert.Equal(new DateTimeOffset(2026, 10, 20, 10, 15, 0, TimeSpan.Zero), batch.CreatedAt);
            Assert.Equal("5", batch.Proposals[0].ProposedValue);
            Assert.Null(batch.Proposals[1].ProposedMeta!["effort"]);
            Assert.Single(store.LoadAll());
            var manifest = SeedManifest.FromJson(File.ReadAllText(Path.Combine(dir, "seeds", "seed-20261020-101500.json")));
            Assert.Equal("20261020-101500", manifest.RunId);
            Assert.Equal(2, manifest.Items.Count);
            Assert.True(manifest.Completed);
            Assert.Equal(new[] { "missing-points" }, manifest.Items[1].Defects);
            var visions = new VisionStore(Path.Combine(dir, "vision"));
            var v = visions.Load("20261020-101500")!;
            Assert.Equal("THEME-a", v.Themes[0].Id);
            Assert.True(v.Themes[0].IsNew);
            Assert.Equal(new[] { "EPIC-1", "EPIC-2" }, v.Themes[0].Epics);
            var intakes = Intake.ReadManifests(Path.Combine(dir, "intakes"));
            Assert.Single(intakes);
            Assert.Equal("STORY-x", intakes[0].Items[0].CanonicalId);
            Assert.Equal(new DateTimeOffset(2026, 10, 20, 10, 16, 0, TimeSpan.FromHours(2)), intakes[0].FinishedAt);
        }

        private static void Check(string name, string text) { Golden.Check(name, text); }
    }

    /// <summary>Every API route the UI reads, recorded against a host serving a fixed synthetic snapshot.</summary>
    [Collection("CredentialReader")]
    public sealed class WireApiTests : IDisposable
    {
        // The recorded answers must not depend on whether this PC has a Fairlead key installed for Halyard.
        private readonly FakeCredentialStore _noKey = new FakeCredentialStore(null);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-wire-" + Guid.NewGuid().ToString("N"));
        private readonly HalyardHost _host;
        private readonly HostInfo _info;
        private readonly HttpClient _client = new HttpClient();

        public WireApiTests()
        {
            Directory.CreateDirectory(_root);
            var settings = new HalyardSettings { RootDirectory = _root };
            settings.Provider.Kind = ProviderKind.GitHub;
            settings.Provider.SandboxRepository = "fleethacker-data-crucible/Halyard-Ballast";
            settings.Provider.SourceRepository = "fleethacker-data-crucible/Halyard-Ballast";
            _host = new HalyardHost(new HostOptions { Settings = settings, PreferredPort = HostOptions.AnyPort, WebRoot = null });
            var catalog = SyntheticCatalog.Generate(SyntheticOptions.ForTotal(30));
            _host.Snapshots.Save(Items.Repo, SyntheticCatalog.ToWorkItems(catalog, Items.Repo), null);
            _info = _host.Start();
            _client.DefaultRequestHeaders.Add("X-Halyard-Session", _info.SessionToken);
        }

        private async Task<string> Call(string method, string path, string? body = null)
        {
            var req = new HttpRequestMessage(new HttpMethod(method), _info.Url + path);
            if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var r = await _client.SendAsync(req);
            var text = await r.Content.ReadAsStringAsync();
            return method + " " + path + (body == null ? string.Empty : " " + body) + " -> " + (int)r.StatusCode + " " + r.Content.Headers.ContentType + "\n" + text + "\n";
        }

        [Fact]
        public async Task ResponsesKeepTheirShape()
        {
            var sb = new StringBuilder();
            foreach (var path in new[]
            {
                "api/health", "api/config", "api/schema", "api/snapshot", "api/snapshots", "api/review", "api/hierarchy", "api/leadership", "api/team",
                "api/sandbox/preview?total=12", "api/sandbox/runs", "api/intake/sources", "api/intake/runs", "api/vision", "api/proposals", "api/writes",
                "api/items/999999", "api/nope",
            })
                sb.Append(await Call("GET", path));
            var snap = JObject.Parse(await _client.GetStringAsync(_info.Url + "api/snapshot"));
            var number = (long)snap["items"]!.First(i => (string)i["type"]! == "STORY")["number"]!;
            sb.Append(await Call("GET", "api/items/" + number));
            sb.Append(await Call("POST", "api/heartbeat", "{\"leaving\":false}"));
            sb.Append(await Call("POST", "api/config", "{\"labelFilter\":\"team::x\",\"writeDelayMs\":\"250\"}"));
            sb.Append(await Call("POST", "api/config", "{\"labelFilter\":\"\"}"));
            sb.Append(await Call("POST", "api/config", "{\"writeMode\":\"production\"}"));
            sb.Append(await Call("POST", "api/config", "{bad json"));
            sb.Append(await Call("POST", "api/config", "{\"writeDelayMs\":\"soon\"}"));
            var manual = await Call("POST", "api/proposals/manual", "{\"number\":" + number + ",\"kind\":\"points\",\"value\":\"5\",\"note\":\"n\"}");
            sb.Append(manual);
            sb.Append(await Call("POST", "api/intelligence/metadata", "{}"));
            sb.Append(await Call("POST", "api/intelligence/parents", "{}"));
            sb.Append(await Call("POST", "api/plan/preview", "{}"));
            sb.Append(await Call("POST", "api/intake/preview", "{}"));
            sb.Append(await Call("POST", "api/sandbox/seed", "{\"total\":10}"));
            sb.Append(await Call("GET", "api/proposals"));
            var batchId = (string)JObject.Parse(manual.Substring(manual.IndexOf('\n') + 1))["id"]!;
            sb.Append(await Call("GET", "api/proposals/" + batchId));
            sb.Append(await Call("POST", "api/proposals/" + batchId + "/decide", "{\"decision\":\"rejected\",\"all\":true}"));
            sb.Append(await Call("POST", "api/proposals/" + batchId + "/delete", "{}"));
            sb.Append(await Call("POST", "api/session/stop", ""));
            var status = await Call("GET", "api/status");
            sb.Append(status);
            Golden.Check("api-responses.txt", Golden.Normalize(sb.ToString(), "webRoot", "file", "envFile", "rootDirectory", "ageDays", "ageBusinessDays"));
        }

        [Fact]
        public async Task JobsKeepTheirShape()
        {
            var r = await _client.PostAsync(_info.Url + "api/plan/propose", new StringContent("{}"));
            var text = await r.Content.ReadAsStringAsync();
            var sb = new StringBuilder();
            sb.Append((int)r.StatusCode).Append('\n').Append(text).Append('\n');
            var jobs = JArray.Parse(await _client.GetStringAsync(_info.Url + "api/jobs"));
            foreach (var j in jobs)
            {
                var id = (string)j["id"]!;
                for (var i = 0; i < 100; i++)
                {
                    var state = (string)JObject.Parse(await _client.GetStringAsync(_info.Url + "api/jobs/" + id))["state"]!;
                    if (state != "queued" && state != "running") break;
                    await Task.Delay(50);
                }
                sb.Append(await _client.GetStringAsync(_info.Url + "api/jobs/" + id)).Append('\n');
            }
            Golden.Check("api-jobs.txt", Golden.Normalize(sb.ToString(), "webRoot", "file"));
        }

        public void Dispose()
        {
            _noKey.Dispose();
            _host.Dispose();
            _client.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }
}
