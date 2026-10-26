using System.Text.Json;
using NightManager.Core.Contracts;
using NightManager.Core.Json;

namespace NightManager.Core.Tests;

public class SequencePlanTests {
    private const string FromPhone = """
        {"v":1,"name":"Andromeda + Heart","targetTemplate":"DSO – center, AF, guide",
         "autofocus":{"onFilterChange":true,"everyMinutes":60},
         "targets":[
           {"name":"M31","ra":0.7123,"dec":41.269,"rotation":35,
            "exposures":[{"filter":"L","seconds":120,"count":60,"gain":139,"ditherEvery":3},{"filter":"Ha","seconds":300,"count":24}]},
           {"name":"Heart Nebula","ra":2.545,"dec":61.45,"exposures":[{"filter":"Ha","seconds":300,"count":30}]}]}
        """;

    [Test]
    public void Reads_the_plan_delivered_with_the_command() {
        var command = NmJson.Deserialize<RemoteCommand>($$"""
            {"id":"c1","type":"loadSequence","issuedAt":"2026-10-12T22:00:00Z","expiresAt":"2026-10-12T22:05:00Z",
             "args":{"mode":"append","start":"false"},"payload":{{FromPhone}}}
            """)!;
        var plan = SequencePlan.FromPayload(command.Payload)!;
        Assert.Multiple(() => {
            Assert.That(plan.Name, Is.EqualTo("Andromeda + Heart"));
            Assert.That(plan.Targets.Select(t => t.Name), Is.EqualTo(new[] { "M31", "Heart Nebula" }));
            Assert.That(plan.Targets[0].Exposures[0], Is.EqualTo(new ExposureLine { Filter = "L", Seconds = 120, Count = 60, Gain = 139, DitherEvery = 3 }));
            Assert.That(plan.Autofocus!.EveryMinutes, Is.EqualTo(60));
            Assert.That(plan.TotalSeconds, Is.EqualTo(120 * 60 + 300 * 24 + 300 * 30));
            Assert.That(plan.Problems(["L", "R", "G", "B", "Ha", "OIII", "SII"]), Is.Empty);
        });
    }

    [Test]
    public void Explains_what_stops_a_plan_loading() {
        var plan = NmJson.Deserialize<SequencePlan>(FromPhone)! with { };
        var bad = plan with {
            Targets = [plan.Targets[0] with { Dec = 95 }, plan.Targets[1] with { Exposures = [new ExposureLine { Filter = "O3", Seconds = 300, Count = 10 }] }],
        };
        var problems = bad.Problems(["L", "Ha", "OIII"]);
        Assert.That(problems, Has.Some.EqualTo("M31 has impossible coordinates."));
        Assert.That(problems, Has.Some.Contains("filter \"O3\" isn't in this NINA's filter wheel (L, Ha, OIII)"));
        Assert.That((plan with { Targets = [] }).Problems(), Has.Some.EqualTo("The plan has no targets."));
    }

    [Test]
    public void An_export_round_trips_as_a_small_item() {
        var plan = NmJson.Deserialize<SequencePlan>(FromPhone)!;
        var item = new SequenceExportItem { Id = "e1", Timestamp = DateTimeOffset.UnixEpoch, Title = "Tonight", Running = true, Plan = plan, Templates = ["DSO – center, AF, guide"] };
        var json = NmJson.Serialize<IngestItem>(item);
        Assert.That(json, Does.StartWith("{\"type\":\"sequenceExport\""));
        Assert.That(json.Length, Is.LessThan(1500));
    }
}
