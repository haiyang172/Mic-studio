using MicStudio.WindowsAudio;

internal static class MicrophoneVolumeLockChecks
{
    public static int Run()
    {
        var count = 0;
        static void Assert(bool condition) { if (!condition) throw new Exception("Microphone volume coordination check failed."); }
        void Test(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }
        Test("Volume coordination is inert until an explicit endpoint is enabled", () =>
        {
            var reads = 0; var writes = 0;
            using var service = new MicrophoneVolumeLock(_ => { reads++; return .5; }, (_, _) => writes++, automaticPolling: false);
            service.PollNow(); Assert(!service.State.Enabled && service.State.EndpointId == null && reads == 0 && writes == 0);
            Assert(!service.SetTarget(.6) && !service.AdoptCurrentTarget());
        });
        Test("Enabling adopts the exact selected endpoint volume without writing", () =>
        {
            var ids = new List<string>(); var writes = 0;
            using var service = new MicrophoneVolumeLock(id => { ids.Add(id); return .63; }, (_, _) => writes++, () => 0, false);
            var status = service.Enable("selected-capture"); service.PollNow();
            Assert(status.IsActive && status.Target == .63 && ids.All(id => id == "selected-capture") && writes == 0);
        });
        Test("Small level jitter is tolerated and actual changes restore only the selected endpoint", () =>
        {
            var actual = .5; var now = 0L; var writes = new List<(string, double)>();
            using var service = new MicrophoneVolumeLock(_ => actual, (id, level) => { writes.Add((id, level)); actual = level; }, () => now, false);
            service.Enable("selected-capture"); actual += .003; service.PollNow(); Assert(writes.Count == 0);
            actual = .2; service.PollNow(); Assert(writes.SequenceEqual(new[] { ("selected-capture", .5) }) && actual == .5);
        });
        Test("Volume restoration is throttled and repeated competing writes pause coordination", () =>
        {
            var actual = .5; var now = 0L; var writes = 0;
            using var service = new MicrophoneVolumeLock(_ => actual, (_, level) => { writes++; actual = level; }, () => now, false);
            service.Enable("capture"); actual = .1; service.PollNow(); actual = .1; now = 400; service.PollNow(); Assert(writes == 1);
            foreach (var time in new long[] { 800, 1600, 2400, 3200 }) { now = time; actual = .1; service.PollNow(); }
            Assert(writes == 4 && service.State.Paused && !service.State.IsActive && service.State.Code == "conflict");
            now = 20000; service.PollNow(); Assert(writes == 4);
            service.SetTarget(.7); Assert(service.State.Paused); service.AdoptCurrentTarget(); Assert(service.State.Paused);
        });
        Test("Explicit retry captures current volume rather than reviving an obsolete target", () =>
        {
            var actual = .5; var now = 0L; var writes = 0;
            using var service = new MicrophoneVolumeLock(_ => actual, (_, level) => { writes++; actual = level; }, () => now, false);
            service.Enable("capture");
            foreach (var time in new long[] { 0, 800, 1600, 2400, 3200 }) { now = time; actual = .2; service.PollNow(); }
            Assert(service.State.Paused); service.Enable("capture"); service.PollNow();
            Assert(service.State.IsActive && service.State.Target == .2 && writes == 4);
        });
        Test("Own slider intent and quantized read-back update the desired volume without an extra write", () =>
        {
            var actual = .5; var writes = 0;
            using var service = new MicrophoneVolumeLock(_ => actual, (_, level) => { writes++; actual = level; }, () => 0, false);
            service.Enable("capture"); Assert(service.SetTarget(.37, "capture")); actual = .36;
            Assert(service.AdoptCurrentTarget("capture") && service.State.Target == .36); service.PollNow(); Assert(writes == 0);
            Assert(!service.SetTarget(.7, "other-capture") && !service.AdoptCurrentTarget("other-capture") && service.State.Target == .36);
        });
        Test("Disable stops every future intervention and forgets the endpoint", () =>
        {
            var actual = .5; var writes = 0;
            using var service = new MicrophoneVolumeLock(_ => actual, (_, _) => writes++, () => 0, false);
            var enabled = service.Enable("capture"); service.Disable(); actual = .1; service.PollNow();
            Assert(!service.State.Enabled && service.State.EndpointId == null && service.State.Generation > enabled.Generation && writes == 0);
        });
        Test("Unavailable selected devices pause without probing or substituting another microphone", () =>
        {
            var ids = new List<string>(); var unavailable = false; var writes = 0;
            using var service = new MicrophoneVolumeLock(id => { ids.Add(id); if (unavailable) throw new InvalidOperationException(); return .5; }, (_, _) => writes++, () => 0, false);
            service.Enable("capture"); unavailable = true; service.PollNow(); service.PollNow();
            Assert(service.State.Paused && service.State.Code == "endpoint_unavailable" && writes == 0 && ids.Count == 2 && ids.All(id => id == "capture"));
        });
        Test("Failed restores pause rather than repeatedly writing an unresponsive endpoint", () =>
        {
            var actual = .5; var writes = 0;
            using var service = new MicrophoneVolumeLock(_ => actual, (_, _) => { writes++; throw new IOException(); }, () => 0, false);
            service.Enable("capture"); actual = .2; service.PollNow(); service.PollNow();
            Assert(service.State.Paused && service.State.Code == "io_error" && writes == 1);
        });
        Test("An in-flight observation cannot write after Disable returns", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var block = false; var writes = 0;
            using var service = new MicrophoneVolumeLock(_ => { if (block) { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException(); return .1; } return .5; }, (_, _) => writes++, () => 0, false);
            service.Enable("capture"); block = true; var pending = Task.Run(service.PollNow);
            try { Assert(entered.Wait(5000)); service.Disable(); } finally { release.Set(); }
            Assert(pending.Wait(5000) && writes == 0 && !service.State.Enabled);
        });
        Test("An in-flight Enable cannot reactivate a disposed coordinator", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var writes = 0;
            var service = new MicrophoneVolumeLock(_ => { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException(); return .5; }, (_, _) => writes++, () => 0, false);
            var pending = Task.Run(() => service.Enable("capture"));
            try { Assert(entered.Wait(5000)); service.Dispose(); } finally { release.Set(); }
            Assert(pending.Wait(5000) && !service.State.Enabled && writes == 0); service.PollNow(); service.Dispose();
        });
        Test("Sparse external changes do not accumulate into a false conflict", () =>
        {
            var policy = new MicrophoneVolumeLockPolicy(); policy.Enable(.5, 0);
            foreach (var time in new long[] { 0, 11000, 22000, 33000, 44000, 55000 }) Assert(policy.Observe(.1, time) == VolumeLockDecision.Restore);
            Assert(!policy.Paused && policy.RestoreAttempts == 6);
        });
        Test("Clock discontinuities and invalid device levels fail without restoring", () =>
        {
            var policy = new MicrophoneVolumeLockPolicy(); policy.Enable(.5, 100);
            Assert(policy.Observe(.1, 99) == VolumeLockDecision.Pause && policy.Code == "clock_changed");
            policy.Enable(.5, 100); Assert(policy.Observe(double.NaN, 100) == VolumeLockDecision.Pause && policy.Code == "invalid_volume");
            var writes = 0; using var service = new MicrophoneVolumeLock(_ => double.NaN, (_, _) => writes++, () => 0, false);
            Assert(service.Enable("capture").Code == "invalid_volume"); service.PollNow(); Assert(writes == 0);
        });
        Test("Subscriber failures do not escape a timer-controlled volume operation", () =>
        {
            var actual = .5; var writes = 0;
            using var service = new MicrophoneVolumeLock(_ => actual, (_, level) => { writes++; actual = level; }, () => 0, false);
            service.StatusChanged += (_, _) => throw new InvalidOperationException("consumer fixture");
            service.Enable("capture"); actual = .1; service.PollNow(); Assert(writes == 1 && service.State.IsActive);
        });
        return count;
    }
}
