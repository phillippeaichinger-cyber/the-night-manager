using NightManager.Core.Contracts;
using NightManager.Core.Json;

namespace NightManager.Core.Tests;

public class SequenceTreeTests {
    // What the phone sends for a typical night: cool and unpark; one target with center, autofocus and two
    // smart exposures plus a meridian-flip trigger; park and warm at the end.
    private const string Night = """
        {"v":1,"name":"Galaxy night","kind":"sequence",
         "start":[{"t":"NINA.Sequencer.SequenceItem.Camera.CoolCamera","p":{"Temperature":-10,"Duration":10}},
                  {"t":"NINA.Sequencer.SequenceItem.Telescope.UnparkScope"}],
         "targets":[{"t":"NINA.Sequencer.Container.DeepSkyObjectContainer",
                     "p":{"Target":{"name":"M31","ra":0.712314,"dec":41.26875,"pa":35}},
                     "i":[{"t":"NINA.Sequencer.SequenceItem.Platesolving.Center"},
                          {"t":"NINA.Sequencer.SequenceItem.Autofocus.RunAutofocus"},
                          {"t":"NINA.Sequencer.SequenceItem.Imaging.SmartExposure",
                           "i":[{"t":"NINA.Sequencer.SequenceItem.FilterWheel.SwitchFilter","p":{"Filter":"L"}},
                                {"t":"NINA.Sequencer.SequenceItem.Imaging.TakeExposure","p":{"ExposureTime":120,"Gain":139}}],
                           "c":[{"t":"NINA.Sequencer.Conditions.LoopCondition","p":{"Iterations":60}}],
                           "g":[{"t":"NINA.Sequencer.Trigger.Guider.DitherAfterExposures","p":{"AfterExposures":3}}]}],
                     "c":[{"t":"NINA.Sequencer.Conditions.AltitudeCondition","p":{"Altitude":25}}],
                     "g":[{"t":"NINA.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger"},
                          {"t":"NINA.Sequencer.Trigger.Autofocus.AutofocusAfterFilterChange"}]}],
         "end":[{"t":"NINA.Sequencer.SequenceItem.Telescope.ParkScope"},
                {"t":"NINA.Sequencer.SequenceItem.Camera.WarmCamera","off":true}]}
        """;

    [Test]
    public void Reads_a_whole_sequence_from_the_phone_and_keeps_it_small() {
        var command = NmJson.Deserialize<RemoteCommand>($$"""
            {"id":"c","type":"applyTemplate","issuedAt":"2026-10-12T22:00:00Z","expiresAt":"2026-10-12T22:05:00Z","args":{"mode":"load"},"payload":{{Night}}}
            """)!;
        var template = SeqTemplate.FromPayload(command.Payload)!;
        Assert.Multiple(() => {
            Assert.That(template.Kind, Is.EqualTo(SeqTemplateKinds.Sequence));
            Assert.That(template.Start, Has.Count.EqualTo(2));
            var target = template.Targets!.Single();
            Assert.That(target.P!["Target"].GetProperty("name").GetString(), Is.EqualTo("M31"));
            Assert.That(target.I![2].C!.Single().P!["Iterations"].GetInt32(), Is.EqualTo(60));
            Assert.That(target.G!.Select(g => g.T[(g.T.LastIndexOf('.') + 1)..]), Is.EqualTo(new[] { "MeridianFlipTrigger", "AutofocusAfterFilterChange" }));
            Assert.That(template.End![1].Off, Is.True);
            Assert.That(template.Problems(), Is.Empty);
            Assert.That(NmJson.Serialize(template).Length, Is.LessThan(2500), "a whole night fits in a couple of KB");
        });
    }

    [Test]
    public void Says_which_types_this_NINA_is_missing() {
        var template = NmJson.Deserialize<SeqTemplate>(Night)!;
        var known = new HashSet<string>(template.Roots().SelectMany(Flatten).Select(n => n.T));
        known.Remove("NINA.Sequencer.SequenceItem.Autofocus.RunAutofocus");
        Assert.That(template.Problems(known).Single(), Is.EqualTo("This NINA doesn't have: RunAutofocus (a plugin may be missing)."));
    }

    [Test]
    public void Refuses_trees_that_are_too_deep_or_too_big() {
        SeqNode Nest(int depth) => depth == 0 ? new SeqNode { T = "X" } : new SeqNode { T = "C", I = [Nest(depth - 1)] };
        Assert.That(new SeqTemplate { Name = "deep", Block = Nest(20) }.Problems(), Has.Some.Contains("Nested deeper"));
        var wide = new SeqTemplate { Name = "wide", Block = new SeqNode { T = "C", I = Enumerable.Range(0, 700).Select(_ => new SeqNode { T = "X" }).ToList() } };
        Assert.That(wide.Problems(), Has.Some.Contains("Too many items"));
        Assert.That(new SeqTemplate { Name = "empty" }.Problems(), Has.Some.EqualTo("The template is empty."));
    }

    private static IEnumerable<SeqNode> Flatten(SeqNode n) => new[] { n }.Concat(n.Children().SelectMany(Flatten));
}
