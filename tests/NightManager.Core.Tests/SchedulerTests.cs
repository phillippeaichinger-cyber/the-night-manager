using NightManager.Core.Contracts;
using NightManager.Core.Json;
using NightManager.Core.Scheduler;

namespace NightManager.Core.Tests;

public class SchedulerTests {
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 19, 54, 0, TimeSpan.Zero);

    /// <summary>Headers exactly as TS 5.9.6 published them on the observatory (9 Oct 2026, 21:54 local).</summary>
    private static SchedulerMessage NewTargetStart() => new() {
        Topic = SchedulerTracker.NewTargetStart,
        Content = "Sh2 119",
        Expiration = new DateTimeOffset(2026, 10, 10, 4, 29, 46, TimeSpan.FromHours(2)),
        Headers = new Dictionary<string, object?> {
            ["ProjectName"] = "SHO Targets",
            ["TargetName"] = "Sh2 119",
            ["Rotation"] = 0.0,
            ["ExposureFilterName"] = "O",
            ["ExposureLength"] = 300.0,
            ["ExposureGain"] = "(camera)",
            ["ExposureOffset"] = "(camera)",
            ["ExposureBinning"] = "1x1",
        },
        RightAscension = 21.3072,
        Declination = 43.9378,
    };

    [Test]
    public void New_target_start_sets_live_imaging_state() {
        var tracker = new SchedulerTracker();
        var timeline = tracker.Apply(NewTargetStart(), Now);

        Assert.That(timeline, Is.Null, "the sequencer target change already makes the timeline entry");
        var s = tracker.Current!;
        Assert.Multiple(() => {
            Assert.That(s.Status, Is.EqualTo(SchedulerStatus.Imaging));
            Assert.That(s.ProjectName, Is.EqualTo("SHO Targets"));
            Assert.That(s.TargetName, Is.EqualTo("Sh2 119"));
            Assert.That(s.Filter, Is.EqualTo("O"));
            Assert.That(s.ExposureSeconds, Is.EqualTo(300));
            Assert.That(s.Binning, Is.EqualTo("1x1"));
            Assert.That(s.TargetRightAscension, Is.EqualTo(21.3072));
            Assert.That(s.PlannedEnd, Is.EqualTo(new DateTimeOffset(2026, 10, 10, 2, 29, 46, TimeSpan.Zero)));
        });
    }

    [Test]
    public void Target_start_for_the_same_target_keeps_the_exposure_details() {
        var tracker = new SchedulerTracker();
        tracker.Apply(NewTargetStart(), Now);
        tracker.Apply(new SchedulerMessage {
            Topic = SchedulerTracker.TargetStart,
            Content = "Sh2 119",
            Expiration = new DateTimeOffset(2026, 10, 10, 4, 29, 46, TimeSpan.FromHours(2)),
            Headers = new Dictionary<string, object?> { ["ProjectName"] = "SHO Targets", ["TargetName"] = "Sh2 119" },
        }, Now.AddMinutes(12));

        Assert.That(tracker.Current!.Filter, Is.EqualTo("O"));
        Assert.That(tracker.Current.ExposureSeconds, Is.EqualTo(300));
    }

    [Test]
    public void Wait_start_reports_the_next_target_once() {
        var tracker = new SchedulerTracker();
        var until = new DateTimeOffset(2026, 10, 9, 21, 10, 0, TimeSpan.Zero);
        var wait = new SchedulerMessage {
            Topic = SchedulerTracker.WaitStart,
            Content = until.LocalDateTime,
            Expiration = until,
            Headers = new Dictionary<string, object?> { ["ProjectName"] = "SHO Targets", ["TargetName"] = "Sh2 132", ["SecondsUntilNextTarget"] = 4560 },
        };

        var first = tracker.Apply(wait, Now);
        var repeat = tracker.Apply(wait, Now.AddSeconds(30));

        Assert.That(first!.Kind, Is.EqualTo(EventKinds.SchedulerWaiting));
        Assert.That(first.Title, Is.EqualTo("Waiting for Sh2 132"));
        Assert.That(repeat, Is.Null);
        Assert.That(tracker.Current!.Status, Is.EqualTo(SchedulerStatus.Waiting));
        Assert.That(tracker.Current.NextTargetName, Is.EqualTo("Sh2 132"));
        Assert.That(tracker.Current.WaitUntil, Is.EqualTo(until));
        Assert.That(tracker.Current.TargetName, Is.Null, "nothing is being imaged while waiting");
    }

    [Test]
    public void Container_stopped_after_imaging_is_on_the_timeline_but_not_twice() {
        var tracker = new SchedulerTracker();
        Assert.That(tracker.Apply(new SchedulerMessage { Topic = SchedulerTracker.ContainerStopped, Content = "Container Stopped" }, Now), Is.Null,
            "TS also publishes this when a sequence is merely loaded/stopped without running");

        tracker.Apply(NewTargetStart(), Now);
        var stopped = tracker.Apply(new SchedulerMessage { Topic = SchedulerTracker.ContainerStopped }, Now.AddHours(6));
        var again = tracker.Apply(new SchedulerMessage { Topic = SchedulerTracker.ContainerStopped }, Now.AddHours(6));

        Assert.That(stopped!.Kind, Is.EqualTo(EventKinds.SchedulerStopped));
        Assert.That(again, Is.Null);
        Assert.That(tracker.Current!.Status, Is.EqualTo(SchedulerStatus.Stopped));
        Assert.That(tracker.Current.ProjectName, Is.EqualTo("SHO Targets"));
    }

    [Test]
    public void Target_complete_is_a_timeline_entry() {
        var tracker = new SchedulerTracker();
        var done = tracker.Apply(new SchedulerMessage {
            Topic = SchedulerTracker.TargetComplete,
            Content = "Sh2 119",
            Headers = new Dictionary<string, object?> { ["ProjectName"] = "SHO Targets" },
        }, Now);
        Assert.That(done!.Title, Is.EqualTo("Sh2 119 complete"));
    }

    private const string PreviewJson = """
        [
          {"Id":null,"Name":null,"WaitPeriod":true,"StartTime":"2026-10-12T19:30:00+01:00","EndTime":"2026-10-12T20:05:00+01:00","ExposurePlan":[]},
          {"Id":"a1","Name":"Sh2 119","WaitPeriod":false,"StartTime":"2026-10-12T20:05:00+01:00","EndTime":"2026-10-13T01:40:00+01:00",
           "ExposurePlan":[{"FilterName":"O","Exposure":300,"Count":40},{"FilterName":"S","Exposure":300,"Count":12},{"FilterName":"O","Exposure":300,"Count":2}]},
          {"Id":"b2","Name":"Sh2 132","WaitPeriod":false,"StartTime":"2026-10-13T01:40:00","EndTime":"2026-10-13T04:30:00",
           "ExposurePlan":[{"FilterName":"O","Exposure":300,"Count":34},{"FilterName":"H","Exposure":0,"Count":0}]}
        ]
        """;

    [Test]
    public void Preview_becomes_tonights_plan_with_projects_and_merged_filters() {
        var plan = SchedulerPreview.Parse(PreviewJson,
            new Dictionary<string, string> { ["a1"] = "SHO Targets", ["b2"] = "SHO Targets" },
            local: dt => new DateTimeOffset(dt, TimeSpan.FromHours(1)));

        Assert.That(plan, Has.Count.EqualTo(3));
        Assert.Multiple(() => {
            Assert.That(plan[0].Wait, Is.True);
            Assert.That(plan[0].TargetName, Is.Null);
            Assert.That(plan[1].TargetName, Is.EqualTo("Sh2 119"));
            Assert.That(plan[1].ProjectName, Is.EqualTo("SHO Targets"));
            Assert.That(plan[1].Exposures.Select(e => $"{e.Filter}x{e.Count}"), Is.EqualTo(new[] { "Ox42", "Sx12" }));
            Assert.That(plan[2].Start, Is.EqualTo(new DateTimeOffset(2026, 10, 13, 0, 40, 0, TimeSpan.Zero)), "local times without an offset use the PC's zone");
            Assert.That(plan[2].Exposures.Select(e => e.Filter), Is.EqualTo(new[] { "O" }), "empty exposure blocks are dropped");
        });
    }

    [Test]
    public void Preview_rejects_unexpected_shapes() {
        Assert.Throws<FormatException>(() => SchedulerPreview.Parse("{\"error\":1}", new Dictionary<string, string>()));
    }

    [Test]
    public void Signature_ignores_ids_and_generation_time_but_not_content() {
        SchedulerPlanItem Item(string id, DateTimeOffset generated, int acquired) => new() {
            Id = id,
            Timestamp = generated,
            PreviewAvailable = true,
            PreviewGeneratedAt = generated,
            Projects = [new SchedulerProject {
                Id = "p", Name = "SHO Targets", State = "active", Priority = "high",
                Targets = [new SchedulerTarget { Id = "a1", Name = "Sh2 119", Exposures = [new SchedulerExposurePlan { Filter = "O", Desired = 200, Acquired = acquired }] }],
            }],
        };

        Assert.That(SchedulerPreview.Signature(Item("x", Now, 137)), Is.EqualTo(SchedulerPreview.Signature(Item("y", Now.AddMinutes(15), 137))));
        Assert.That(SchedulerPreview.Signature(Item("x", Now, 137)), Is.Not.EqualTo(SchedulerPreview.Signature(Item("x", Now, 138))));
    }

    [Test]
    public void Plan_item_serialises_with_its_type() {
        var json = NmJson.Serialize<IngestItem>(new SchedulerPlanItem { Id = "abc", Timestamp = Now, PreviewAvailable = false, PreviewUnavailableReason = "API off" });
        Assert.That(json, Does.Contain("\"type\":\"schedulerPlan\""));
        Assert.That(json, Does.Contain("\"previewUnavailableReason\":\"API off\""));
    }
}
