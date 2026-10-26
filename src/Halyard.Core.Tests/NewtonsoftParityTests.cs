using System;
using System.Collections.Generic;
using System.Linq;
using Halyard.Core.Host;
using Halyard.Core.Intelligence;
using Halyard.Core.Synthetic;
using Xunit;
using NJ = Newtonsoft.Json;

namespace Halyard.Core.Tests
{
    /// <summary>
    /// The engine replaced Newtonsoft.Json (1.0) with Halyard.Core.Serialization. Number and date text depends on the
    /// runtime (the "R" format differs between .NET Framework 4.8 and .NET 8), so the golden files alone cannot prove
    /// the workstation output is unchanged. These tests run on every target framework (net48 on the Windows runner,
    /// i.e. the Windows PowerShell 5.1 runtime) and compare with Newtonsoft on that same runtime, using the settings 1.0
    /// used. Newtonsoft is a test-only package here.
    /// </summary>
    public class NewtonsoftParityTests
    {
        private static readonly NJ.JsonSerializerSettings ApiSettings = new NJ.JsonSerializerSettings
        {
            ContractResolver = new NJ.Serialization.DefaultContractResolver { NamingStrategy = new NJ.Serialization.CamelCaseNamingStrategy { ProcessDictionaryKeys = false, OverrideSpecifiedNames = true } },
            NullValueHandling = NJ.NullValueHandling.Include,
            Converters = { new NJ.Converters.StringEnumConverter { NamingStrategy = new NJ.Serialization.CamelCaseNamingStrategy() } },
            DateFormatHandling = NJ.DateFormatHandling.IsoDateFormat,
            Formatting = NJ.Formatting.None,
        };

        private static IEnumerable<double> Doubles()
        {
            var fixedValues = new[] { 0.0, -0.0, 1.0, -1.0, 0.1, 0.2, 0.3, 1.0 / 3.0, 2.0 / 3.0, 0.25, 1.5, 2.5, 99.99, 123456789.0, 1e14, 1e15, 1e16, 1e17, 1e21, 1e22, 1.5e300, 5e-324, 1e-5, 1e-7, 0.000123, 12.345678901234567, double.MaxValue, double.MinValue, double.Epsilon, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 3.0, 13.0, 0.5 };
            var rng = new Random(20261026);
            var random = Enumerable.Range(0, 400).Select(i => (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-12, 22)));
            var ratios = Enumerable.Range(1, 60).Select(i => 100.0 * i / 7.0);
            return fixedValues.Concat(random).Concat(ratios);
        }

        [Fact]
        public void NumbersMatchNewtonsoftOnThisRuntime()
        {
            foreach (var d in Doubles())
            {
                Assert.Equal(NJ.JsonConvert.SerializeObject(d, ApiSettings), Json.Serialize(d));
                Assert.Equal(NJ.JsonConvert.SerializeObject((float)d, ApiSettings), Json.Serialize((float)d));
                Assert.Equal(NJ.JsonConvert.SerializeObject(new { v = d }, NJ.Formatting.Indented), Serialization.JsonConvert.SerializeObject(new { v = d }, Serialization.Formatting.Indented));
            }
            foreach (var m in new[] { 0m, 1m, 1.0m, 1.50m, -2.125m, 12.345m, 79228162514264337593543950335m, 0.0000001m })
                Assert.Equal(NJ.JsonConvert.SerializeObject(m, ApiSettings), Json.Serialize(m));
            foreach (var l in new object[] { int.MinValue, int.MaxValue, long.MinValue, long.MaxValue, (short)-3, (byte)7, ulong.MaxValue })
                Assert.Equal(NJ.JsonConvert.SerializeObject(l, ApiSettings), Json.Serialize(l));
        }

        [Fact]
        public void DomNumbersMatchNewtonsoftOnThisRuntime()
        {
            foreach (var d in Doubles())
            {
                var mine = new Serialization.JObject { { "d", d } };
                var theirs = new Newtonsoft.Json.Linq.JObject { ["d"] = d };
                Assert.Equal(theirs.ToString(NJ.Formatting.None), mine.ToString(Serialization.Formatting.None));
                Assert.Equal(theirs["d"]!.ToString(), mine["d"].ToString());
                Assert.Equal((string?)theirs["d"], (string)mine["d"]);
            }
        }

        [Fact]
        public void DatesMatchNewtonsoftOnThisRuntime()
        {
            var values = new object[]
            {
                new DateTimeOffset(2026, 10, 26, 9, 5, 7, 120, TimeSpan.FromHours(-5)), new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
                new DateTimeOffset(new DateTime(2026, 1, 2, 3, 4, 5).AddTicks(1234567), TimeSpan.FromMinutes(330)), DateTimeOffset.MinValue, DateTimeOffset.MaxValue,
                new DateTime(2026, 10, 26, 14, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 26, 14, 0, 0, 500, DateTimeKind.Unspecified), new DateTime(2026, 10, 26, 14, 0, 0, DateTimeKind.Local),
                DateTime.MinValue, TimeSpan.FromSeconds(3725.5), new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
            };
            foreach (var v in values) Assert.Equal(NJ.JsonConvert.SerializeObject(v, ApiSettings), Json.Serialize(v));
        }

        [Fact]
        public void DtosMatchNewtonsoftOnThisRuntime()
        {
            var at = new DateTimeOffset(2026, 10, 26, 8, 30, 0, 250, TimeSpan.Zero);
            var dto = new WireDto { Raw = null }; // Raw holds an engine JObject, which Newtonsoft cannot know
            var batch = new ProposalBatch { Id = "b", Kind = ProposalKind.Refinement, CreatedAt = at, Considered = 3 };
            batch.Proposals.Add(new Proposal { Id = "p", Kind = ProposalKind.StoryPoints, CreatedAt = at, Decision = ProposalDecision.Approved, ProposedMeta = new Dictionary<string, string> { { "points", "3" }, { "effort", null } } });
            var manifest = new SeedManifest { RunId = "r", StartedAt = at, Requested = 1 };
            manifest.Items.Add(new SeededItem { Id = "x", Defects = { "d" } });
            var job = new JobInfo { Id = "j", Kind = "sync", State = JobState.Failed, CreatedAt = at, ProgressDone = 1, ProgressTotal = 3, Result = new { count = 2, ratio = 0.1 } };
            foreach (var v in new object[] { dto, batch, manifest, job, SyntheticOptions.ForTotal(30) })
            {
                Assert.Equal(NJ.JsonConvert.SerializeObject(v, ApiSettings), Json.Serialize(v));
                Assert.Equal(NJ.JsonConvert.SerializeObject(v, NJ.Formatting.Indented), Serialization.JsonConvert.SerializeObject(v, Serialization.Formatting.Indented));
            }
        }
    }
}
