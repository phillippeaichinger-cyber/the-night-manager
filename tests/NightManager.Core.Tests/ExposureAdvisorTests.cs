using NightManager.Core.Contracts;
using NightManager.Core.Exposure;

namespace NightManager.Core.Tests;

public class ExposureAdvisorTests {
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 23, 0, 0, TimeSpan.Zero);
    private const string Setting = "100|50|1|";

    /// <summary>A frame with Gaussian noise of σ on a gradient (and a nebula-like blob and a few stars).</summary>
    private static ushort[] Frame(int w, int h, double sigma, int seed = 1) {
        var random = new Random(seed);
        var pixels = new ushort[w * h];
        for (var y = 0; y < h; y++) {
            for (var x = 0; x < w; x++) {
                var u1 = 1 - random.NextDouble();
                var u2 = random.NextDouble();
                var gauss = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                var gradient = 800 + 400.0 * x / w + 200.0 * y / h;
                var dx = x - w / 2.0; var dy = y - h / 2.0;
                var nebula = 600 * Math.Exp(-(dx * dx + dy * dy) / (2 * 300.0 * 300.0));
                pixels[y * w + x] = (ushort)Math.Clamp(gradient + nebula + gauss * sigma, 0, 65535);
            }
        }
        for (var s = 0; s < 60; s++) {
            int sx = random.Next(5, w - 5), sy = random.Next(5, h - 5);
            for (var y = -2; y <= 2; y++) for (var x = -2; x <= 2; x++) pixels[(sy + y) * w + sx + x] = 60000;
        }
        return pixels;
    }

    [Test]
    public void Measures_Pixel_Noise_Ignoring_Gradients_Nebulosity_And_Stars() {
        var measured = BackgroundNoise.Measure(Frame(1600, 1200, 12), 1600, 1200, bayered: false);
        Assert.That(measured, Is.Not.Null);
        Assert.That(measured!.Value.Noise, Is.EqualTo(12).Within(0.8));
    }

    private static ExposureHistory History(double readAdu, string filter, double seconds, double noiseAdu, int count = 10, double? rms = 0.6, int rejected = 0) {
        var history = new ExposureHistory();
        history.AddReadNoise(Setting, readAdu);
        for (var i = 0; i < count; i++) {
            history.AddSub(new SubSample { At = Now.AddMinutes(-i * 5), Filter = filter, Seconds = seconds, Setting = Setting, Gain = 100,
                NoiseAdu = noiseAdu, GuidingRms = rms, Rejected = i < rejected });
        }
        return history;
    }

    private static FilterAdvice Advise(ExposureHistory history, double? scale = 1.2, Dictionary<string, double>? bandwidths = null) =>
        ExposureAdvisor.Advise(history, bandwidths ?? [], scale, Now, () => "id").Filters[0];

    [Test]
    public void The_C_Factor_Matches_Dr_Glovers_Five_Percent() {
        Assert.That(ExposureAdvisor.CFactor(5), Is.EqualTo(9.76).Within(0.01));
        Assert.That(ExposureAdvisor.CFactor(10), Is.EqualTo(4.76).Within(0.01));
    }

    [Test]
    public void Short_Subs_Where_Read_Noise_Still_Shows_Are_Told_To_Go_Longer() {
        // σ_read 10, σ_sub 20: sky variance 300 = 3 × read variance → read noise adds 15.5%.
        var advice = Advise(History(10, "Ha", 60, 20));
        Assert.Multiple(() => {
            Assert.That(advice.Status, Is.EqualTo(SubLengthStatus.TooShort));
            Assert.That(advice.ReadNoisePercent, Is.EqualTo(15.5).Within(0.1));
            Assert.That(advice.MinimumSeconds, Is.EqualTo(195).Within(1));
            Assert.That(advice.SuggestedSeconds, Is.EqualTo(240));
        });
    }

    [Test]
    public void Long_Subs_Well_Past_The_Point_Are_Offered_Something_Shorter() {
        var advice = Advise(History(10, "L", 300, 60));
        Assert.Multiple(() => {
            Assert.That(advice.Status, Is.EqualTo(SubLengthStatus.LongerThanNeeded));
            Assert.That(advice.SuggestedSeconds, Is.EqualTo(90));
        });
    }

    [Test]
    public void A_Good_Length_Stays() {
        var advice = Advise(History(10, "R", 60, 40));
        Assert.That(advice.Status, Is.EqualTo(SubLengthStatus.Good));
        Assert.That(advice.SuggestedSeconds, Is.EqualTo(60));
    }

    [Test]
    public void Poor_Guiding_Or_Many_Rejects_Hold_Back_Longer_Subs() {
        Assert.That(Advise(History(10, "Ha", 60, 20, rms: 1.5), scale: 1.2).Status, Is.EqualTo(SubLengthStatus.LimitedByGuiding));
        Assert.That(Advise(History(10, "Ha", 60, 20, rejected: 3)).Status, Is.EqualTo(SubLengthStatus.LimitedByGuiding));
    }

    [Test]
    public void Without_A_Bias_Or_Dark_Flat_It_Asks_For_One() {
        var history = History(10, "Ha", 60, 20);
        history.ReadNoise.Clear();
        Assert.That(Advise(history).Status, Is.EqualTo(SubLengthStatus.NeedsCalibration));
    }

    [Test]
    public void A_Filter_Not_Imaged_Yet_Is_Predicted_From_The_Sky_Through_Another_By_Bandwidth() {
        var result = ExposureAdvisor.Advise(History(10, "L", 60, 40), new Dictionary<string, double> { ["L"] = 300, ["Ha"] = 7 }, 1.2, Now, () => "id");
        var ha = result.Filters.Single(f => f.Filter == "Ha");
        var l = result.Filters.Single(f => f.Filter == "L");
        Assert.Multiple(() => {
            Assert.That(ha.Predicted, Is.True);
            Assert.That(ha.MinimumSeconds, Is.EqualTo(l.MinimumSeconds!.Value * 300 / 7).Within(2));
        });
    }

    private static byte[] Fits(params (string Key, string Value)[] cards) {
        var text = new System.Text.StringBuilder();
        text.Append("SIMPLE  =                    T".PadRight(80));
        foreach (var (k, v) in cards) text.Append($"{k,-8}= {v}".PadRight(80));
        text.Append("END".PadRight(80));
        return System.Text.Encoding.ASCII.GetBytes(text.ToString().PadRight(2880));
    }

    [Test]
    public void Reads_Which_Calibration_Frame_A_Fits_File_Is() {
        var header = CalibrationHeader.Parse(Fits(("IMAGETYP", "'DARKFLAT'"), ("EXPTIME", "2.5"), ("GAIN", "100"), ("OFFSET", "50"), ("XBINNING", "1"), ("READOUTM", "'High Gain'")));
        Assert.Multiple(() => {
            Assert.That(header!.IsReadNoiseFrame(), Is.True);
            Assert.That(header.SettingKey, Is.EqualTo(ExposureHistory.SettingKey(100, 50, 1, "High Gain")));
        });
        Assert.That(CalibrationHeader.Parse(Fits(("IMAGETYP", "'DARK'"), ("EXPTIME", "300")))!.IsReadNoiseFrame(), Is.False);
        Assert.That(CalibrationHeader.Parse(Fits(("IMAGETYP", "'Master Bias'")))!.IsReadNoiseFrame(), Is.False);
        Assert.That(CalibrationHeader.Parse(Fits(("IMAGETYP", "'LIGHT'")))!.IsReadNoiseFrame(), Is.False);
    }

    [Test]
    public void Reads_Which_Calibration_Frame_An_Xisf_File_Is() {
        var xml = """<?xml version="1.0" encoding="UTF-8"?><xisf version="1.0"><Image geometry="4656:3520:1"><FITSKeyword name="IMAGETYP" value="'BIAS'" comment=""/><FITSKeyword name="GAIN" value="200" comment=""/><FITSKeyword name="OFFSET" value="56" comment=""/><FITSKeyword name="BAYERPAT" value="'RGGB'" comment=""/></Image></xisf>""";
        var bytes = System.Text.Encoding.ASCII.GetBytes("XISF0100").Concat(new byte[8]).Concat(System.Text.Encoding.UTF8.GetBytes(xml)).ToArray();
        var header = CalibrationHeader.Parse(bytes);
        Assert.Multiple(() => {
            Assert.That(header!.ImageType, Is.EqualTo("BIAS"));
            Assert.That(header.Gain, Is.EqualTo(200));
            Assert.That(header.Bayered, Is.True);
            Assert.That(header.IsReadNoiseFrame(), Is.True);
        });
    }

    /// <summary>A season of 300 s subs over 20 nights: median 0.6″, and a share of them hit by guiding spikes.</summary>
    private static List<GuidingSample> Season(double spoiledShare, int subs = 600) {
        var list = new List<GuidingSample>();
        var start = Now.AddDays(-60).ToUnixTimeSeconds();
        for (var i = 0; i < subs; i++) {
            var spoiled = i % 100 < spoiledShare * 100;
            list.Add(new GuidingSample(start + i / 30 * 86400 + i % 30 * 330, 300, spoiled ? 2.5 : 0.55 + i % 7 * 0.02));
        }
        return list;
    }

    [Test]
    public void The_Mounts_Record_Gives_The_Chance_A_Sub_Catches_A_Guiding_Spike() {
        var record = GuidingCapability.Assess(Season(0.05), imageScale: 1.7, Now)!;
        Assert.Multiple(() => {
            Assert.That(record.Period, Is.EqualTo("year"));
            Assert.That(record.MedianRms, Is.EqualTo(0.61).Within(0.03));
            Assert.That(record.LossAt(300), Is.EqualTo(0.05).Within(0.005));
            // 5% of 5-minute subs spoiled → about 10% for 10-minute subs, and 10 % is reached near 10 min.
            Assert.That(record.LossAt(600), Is.EqualTo(0.0975).Within(0.005));
            Assert.That(record.MaxSeconds(0.10), Is.EqualTo(616).Within(15));
        });
        Assert.That(GuidingCapability.Assess(Season(0.05, subs: 20), 1.7, Now), Is.Null);
    }

    [Test]
    public void A_Longer_Suggestion_Is_Held_To_What_The_Mount_Sustains() {
        // Read noise wants ~16 min (σ_read 10, σ_sub 12 at 300 s); a mount losing 5% of 5-minute subs holds about 10 min.
        var history = History(10, "Ha", 300, 12);
        var advice = ExposureAdvisor.Advise(history, new Dictionary<string, double>(), 1.7, Now, () => "id", guiding: GuidingCapability.Assess(Season(0.05), 1.7, Now)).Filters[0];
        Assert.Multiple(() => {
            Assert.That(advice.Status, Is.EqualTo(SubLengthStatus.TooShort));
            Assert.That(advice.SuggestedSeconds, Is.EqualTo(600));
            Assert.That(advice.GuidingLoss, Is.LessThanOrEqualTo(0.10));
            Assert.That(advice.Summary, Does.Contain("holds about 10 min"));
        });
        // A mount spoiling 20% of 5-minute subs can't go longer at all.
        var held = ExposureAdvisor.Advise(History(10, "Ha", 300, 12), new Dictionary<string, double>(), 1.7, Now, () => "id", guiding: GuidingCapability.Assess(Season(0.2), 1.7, Now)).Filters[0];
        Assert.That(held.Status, Is.EqualTo(SubLengthStatus.LimitedByGuiding));
    }
}
