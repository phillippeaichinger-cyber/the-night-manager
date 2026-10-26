using NightManager.Core.Commands;
using NightManager.Core.Contracts;

namespace NightManager.Core.Tests;

public class EquipmentActionTests {
    private static RemoteCommand Command(string type, string action, params (string Key, string Value)[] args) => new() {
        Id = Guid.NewGuid().ToString("N"), Type = type, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        Args = new Dictionary<string, string>(args.Select(a => new KeyValuePair<string, string>(a.Key, a.Value))) { ["action"] = action }
    };

    [Test]
    public void EveryActionNeedsItsOwnPermission() {
        Assert.That(EquipmentActions.Refusal(Command(CommandTypes.Equipment, "camera.shot"), false, true), Is.Null);
        // Moving the mount is the "move" tick, never "equipment".
        Assert.That(EquipmentActions.Refusal(Command(CommandTypes.Equipment, "mount.slew"), false, true), Does.Contain("wrong permission"));
        Assert.That(EquipmentActions.Refusal(Command(CommandTypes.Move, "mount.slew"), false, true), Is.Null);
        Assert.That(EquipmentActions.Refusal(Command(CommandTypes.Equipment, "nuke"), false, true), Does.Contain("Unknown"));
    }

    [Test]
    public void ARunningSequenceOnlyAllowsTheSafeOnes() {
        Assert.That(EquipmentActions.Refusal(Command(CommandTypes.Equipment, "camera.shot"), true, true), Does.Contain("sequence is running"));
        Assert.That(EquipmentActions.Refusal(Command(CommandTypes.Move, "mount.slew"), true, true), Does.Contain("sequence is running"));
        foreach (var (type, action) in new[] { (CommandTypes.Equipment, "camera.cool"), (CommandTypes.Equipment, "camera.stop"), (CommandTypes.Move, "mount.stop"), (CommandTypes.Move, "dome.close"), (CommandTypes.Equipment, "switch.set") }) {
            Assert.That(EquipmentActions.Refusal(Command(type, action), true, true), Is.Null, action);
        }
    }

    [Test]
    public void LinkedPathsLeaveTheMountToTheMainRig() {
        Assert.That(EquipmentActions.Refusal(Command(CommandTypes.Move, "mount.park"), false, controlsMount: false), Does.Contain("linked optical path"));
        Assert.That(EquipmentActions.Refusal(Command(CommandTypes.Equipment, "focuser.nudge"), false, controlsMount: false), Is.Null);
    }

    [Test]
    public void NumbersAndFlagsReadInvariantly() {
        var c = Command(CommandTypes.Equipment, "camera.cool", ("temperature", "-10.5"), ("on", "true"));
        Assert.That(EquipmentActions.Number(c, "temperature"), Is.EqualTo(-10.5));
        Assert.That(EquipmentActions.Flag(c, "on"), Is.True);
        Assert.That(EquipmentActions.Number(c, "missing"), Is.Null);
    }
}
