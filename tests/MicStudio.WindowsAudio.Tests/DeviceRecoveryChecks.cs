using MicStudio.WindowsAudio;

internal static class DeviceRecoveryChecks
{
    public static int Run()
    {
        var count = 0;
        static void Assert(bool valid) { if (!valid) throw new Exception("Device recovery authorization check failed."); }
        void Test(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }
        static RecoveryRoute Route() => new("selected-microphone", "selected-send", "selected-receive", null, 48000, 2);
        static RecoveryRoute AuthorizedRoute(string inputId = "selected-microphone") => Route() with
        {
            InputId = inputId,
            EndpointFormats = inputId + ":48000/2;send:48000/2;receive:48000/2",
            SharedEndpointFormats = "send:48000/2;receive:48000/2"
        };
        static RecoveryAttempt Ready(DeviceRecoveryPolicy policy, RecoveryRoute route, Action<long> clock)
        {
            Assert(policy.ObserveUnavailable(route));
            clock(100); Assert(!policy.TryTakeRecovery(route, true, out _));
            clock(1100); Assert(policy.TryTakeRecovery(route, true, out var attempt));
            return attempt!;
        }

        Test("Recovery defaults off and cannot remember a running device until opted in", () =>
        {
            var policy = new DeviceRecoveryPolicy(() => 0); var route = Route();
            Assert(!policy.State.Enabled && policy.State.Code == "off");
            Assert(!policy.RememberRunning(route) && !policy.ObserveUnavailable(route));
            Assert(!policy.TryTakeRecovery(route, true, out _) && policy.State.Route == null);
        });
        Test("Restoring opt-in alone never authorizes capture from a stopped startup", () =>
        {
            var now = 0L; var policy = new DeviceRecoveryPolicy(() => now); var route = Route(); policy.SetEnabled(true);
            Assert(!policy.State.HasRunningIntent && !policy.ObserveUnavailable(route));
            now = 100000; Assert(!policy.TryTakeRecovery(route, true, out _) && policy.State.Code == "idle");
        });
        Test("Remembering a successful session still requires an observed outage", () =>
        {
            var now = 0L; var policy = new DeviceRecoveryPolicy(() => now); var route = Route(); policy.SetEnabled(true);
            Assert(policy.RememberRunning(route)); now = 5000;
            Assert(!policy.TryTakeRecovery(route, true, out _) && !policy.State.Pending && policy.State.Code == "armed");
        });
        Test("Recovery waits a full continuous second of exact readiness before one reservation", () =>
        {
            var now = 0L; var policy = new DeviceRecoveryPolicy(() => now); var route = Route(); policy.SetEnabled(true); policy.RememberRunning(route);
            Assert(policy.ObserveUnavailable(route)); now = 100;
            Assert(!policy.TryTakeRecovery(route, true, out _) && policy.State.Code == "stabilizing");
            now = 1099; Assert(!policy.TryTakeRecovery(route, true, out _));
            now = 1100; Assert(policy.TryTakeRecovery(route, true, out var attempt) && attempt!.Route == route);
            Assert(policy.State.AttemptInProgress && !policy.TryTakeRecovery(route, true, out _) && !policy.ObserveUnavailable(route));
        });
        Test("Readiness interruptions and duplicate loss notifications cannot shorten debounce", () =>
        {
            var now = 0L; var policy = new DeviceRecoveryPolicy(() => now); var route = Route(); policy.SetEnabled(true); policy.RememberRunning(route);
            policy.ObserveUnavailable(route); policy.ObserveUnavailable(route);
            now = 100; Assert(!policy.TryTakeRecovery(route, true, out _));
            now = 900; Assert(!policy.TryTakeRecovery(route, false, out _));
            now = 1000; Assert(!policy.TryTakeRecovery(route, true, out _));
            now = 1500; Assert(policy.ObserveUnavailable(route));
            now = 1600; Assert(!policy.TryTakeRecovery(route, true, out _));
            now = 2599; Assert(!policy.TryTakeRecovery(route, true, out _));
            now = 2600; Assert(policy.TryTakeRecovery(route, true, out _));
        });
        Test("Changing selected microphone cancels instead of recovering another input", () =>
        {
            var policy = new DeviceRecoveryPolicy(() => 0); var route = Route(); policy.SetEnabled(true); policy.RememberRunning(route); policy.ObserveUnavailable(route);
            Assert(!policy.TryTakeRecovery(route with { InputId = "other-microphone" }, true, out _));
            Assert(policy.State.Code == "route_changed" && !policy.State.HasRunningIntent && policy.State.Route == null);
            Assert(!policy.ObserveUnavailable(route));
        });
        Test("Recreated send or receive IDs never silently transfer recovery to a replacement cable", () =>
        {
            foreach (var changed in new[] { Route() with { CableSendId = "new-send" }, Route() with { CableReceiveId = "new-receive" } })
            {
                var policy = new DeviceRecoveryPolicy(() => 0); var route = Route(); policy.SetEnabled(true); policy.RememberRunning(route); policy.ObserveUnavailable(route);
                Assert(!policy.TryTakeRecovery(changed, true, out _) && policy.State.Code == "route_changed");
            }
        });
        Test("Monitor or capture format changes require a new explicit successful start", () =>
        {
            foreach (var changed in new[] { Route() with { MonitorId = "new-monitor" }, Route() with { SampleRate = 44100 }, Route() with { Channels = 1 } })
            {
                var policy = new DeviceRecoveryPolicy(() => 0); var route = Route(); policy.SetEnabled(true); policy.RememberRunning(route); policy.ObserveUnavailable(route);
                Assert(!policy.TryTakeRecovery(changed, true, out _) && !policy.State.HasRunningIntent);
            }
        });
        Test("Cable and monitor format fingerprints are part of the exact recovery route", () =>
        {
            var route = Route() with { EndpointFormats = "send:48000/2/32;receive:48000/2/32;monitor:none" };
            var policy = new DeviceRecoveryPolicy(() => 0); policy.SetEnabled(true); policy.RememberRunning(route); policy.ObserveUnavailable(route);
            Assert(!policy.TryTakeRecovery(route with { EndpointFormats = "send:44100/2/32;receive:48000/2/32;monitor:none" }, true, out _));
            Assert(policy.State.Code == "route_changed" && !policy.State.HasRunningIntent);
        });
        Test("A failed recovery is paused and cannot enter an automatic retry loop", () =>
        {
            var now = 0L; var policy = new DeviceRecoveryPolicy(() => now); var route = Route(); policy.SetEnabled(true); policy.RememberRunning(route);
            var attempt = Ready(policy, route, value => now = value); Assert(policy.CompleteAttempt(attempt, false));
            Assert(policy.State.Paused && policy.State.Code == "recovery_failed" && !policy.State.HasRunningIntent);
            now = 100000; Assert(!policy.ObserveUnavailable(route) && !policy.TryTakeRecovery(route, true, out _));
            Assert(!policy.CompleteAttempt(attempt, true));
        });
        Test("A successful recovery re-arms only that running route and consumes its completion token", () =>
        {
            var now = 0L; var policy = new DeviceRecoveryPolicy(() => now); var route = Route(); policy.SetEnabled(true); policy.RememberRunning(route);
            var attempt = Ready(policy, route, value => now = value); Assert(policy.CompleteAttempt(attempt, true));
            Assert(policy.State.HasRunningIntent && policy.State.Code == "recovered" && !policy.State.Pending && !policy.State.AttemptInProgress);
            Assert(!policy.CompleteAttempt(attempt, false) && policy.State.Code == "recovered");
            Assert(!policy.TryTakeRecovery(route, true, out _));
        });
        Test("Explicit stop invalidates pending and in-flight recovery even if endpoints return", () =>
        {
            var now = 0L; var policy = new DeviceRecoveryPolicy(() => now); var route = Route(); policy.SetEnabled(true); policy.RememberRunning(route);
            var attempt = Ready(policy, route, value => now = value); policy.Cancel();
            Assert(policy.State.Code == "user_stopped" && policy.State.Route == null && !policy.IsAttemptCurrent(attempt) && !policy.CompleteAttempt(attempt, true));
            now = 10000; Assert(!policy.ObserveUnavailable(route) && !policy.TryTakeRecovery(route, true, out _));
        });
        Test("Disable and re-enable forget all prior runtime authorization", () =>
        {
            var now = 0L; var policy = new DeviceRecoveryPolicy(() => now); var route = Route(); policy.SetEnabled(true); policy.RememberRunning(route);
            var attempt = Ready(policy, route, value => now = value); policy.SetEnabled(false); policy.SetEnabled(true);
            Assert(policy.State.Code == "idle" && !policy.State.HasRunningIntent && !policy.CompleteAttempt(attempt, true));
            Assert(!policy.ObserveUnavailable(route) && !policy.TryTakeRecovery(route, true, out _));
        });
        Test("A newer explicit session is immune to old loss and completion callbacks", () =>
        {
            var now = 0L; var policy = new DeviceRecoveryPolicy(() => now); var oldRoute = Route(); policy.SetEnabled(true); policy.RememberRunning(oldRoute);
            var oldAttempt = Ready(policy, oldRoute, value => now = value);
            var newRoute = oldRoute with { InputId = "new-explicit-microphone" }; Assert(policy.RememberRunning(newRoute));
            Assert(!policy.ObserveUnavailable(oldRoute) && !policy.CompleteAttempt(oldAttempt, true));
            Assert(policy.State.Route == newRoute && policy.State.HasRunningIntent && policy.State.Code == "armed");
        });
        Test("Repeated successful reconnects are bounded and explicit restart clears the burst pause", () =>
        {
            var now = 0L; var policy = new DeviceRecoveryPolicy(() => now); var route = Route(); policy.SetEnabled(true); policy.RememberRunning(route);
            for (var i = 0; i < 3; i++)
            {
                Assert(policy.ObserveUnavailable(route)); now = i * 2000;
                Assert(!policy.TryTakeRecovery(route, true, out _)); now += 1000;
                Assert(policy.TryTakeRecovery(route, true, out var attempt) && policy.IsAttemptCurrent(attempt!));
                Assert(policy.CompleteAttempt(attempt!, true));
            }
            Assert(policy.ObserveUnavailable(route)); now = 6000; Assert(!policy.TryTakeRecovery(route, true, out _));
            now = 7000; Assert(!policy.TryTakeRecovery(route, true, out _) && policy.State.Paused && policy.State.Code == "flapping");
            Assert(policy.State.AutomaticAttemptsInWindow == 3 && !policy.ObserveUnavailable(route));
            Assert(policy.RememberRunning(route) && !policy.State.Paused && policy.State.AutomaticAttemptsInWindow == 0);
        });
        Test("Sparse successful reconnects do not accumulate into a permanent burst pause", () =>
        {
            var now = 0L; var policy = new DeviceRecoveryPolicy(() => now); var route = Route(); policy.SetEnabled(true); policy.RememberRunning(route);
            for (var i = 0; i < 5; i++)
            {
                Assert(policy.ObserveUnavailable(route)); now = i * 61000;
                Assert(!policy.TryTakeRecovery(route, true, out _)); now += 1000;
                Assert(policy.TryTakeRecovery(route, true, out var attempt)); Assert(policy.CompleteAttempt(attempt!, true));
            }
            Assert(!policy.State.Paused && policy.State.AutomaticAttemptsInWindow == 1);
        });
        Test("The attempt limit applies across the old minute boundary", () =>
        {
            var now = 0L;
            var policy = new DeviceRecoveryPolicy(() => now);
            var route = Route();
            policy.SetEnabled(true);
            Assert(policy.RememberRunning(route));
            foreach (var attemptAt in new long[] { 1000, 59000, 60000, 61000 })
            {
                now = attemptAt - DeviceRecoveryPolicy.StableReadyMilliseconds;
                Assert(policy.ObserveUnavailable(route));
                Assert(!policy.TryTakeRecovery(route, true, out _));
                now = attemptAt;
                Assert(policy.TryTakeRecovery(route, true, out var attempt));
                Assert(policy.CompleteAttempt(attempt!, true));
            }
            Assert(policy.ObserveUnavailable(route));
            Assert(!policy.TryTakeRecovery(route, true, out _));
            now = 62000;
            Assert(!policy.TryTakeRecovery(route, true, out _) && policy.State.Code == "flapping");
        });
        Test("Clock rollback fails closed during debounce and invalidates in-flight completion", () =>
        {
            var now = 100L; var policy = new DeviceRecoveryPolicy(() => now); var route = Route(); policy.SetEnabled(true); policy.RememberRunning(route); policy.ObserveUnavailable(route);
            now = 99; Assert(!policy.TryTakeRecovery(route, true, out _) && policy.State.Code == "clock_changed");
            now = 100; policy.RememberRunning(route); var attempt = Ready(policy, route, value => now = value);
            now = 1099; Assert(!policy.CompleteAttempt(attempt, true) && policy.State.Code == "clock_changed");
        });
        Test("Invalid or incomplete endpoint routes cannot be remembered", () =>
        {
            var policy = new DeviceRecoveryPolicy(() => 0); policy.SetEnabled(true);
            foreach (var route in new[] { Route() with { InputId = "" }, Route() with { CableReceiveId = null }, Route() with { SampleRate = 0 }, Route() with { Channels = 0 } })
            {
                var rejected = false;
                try { policy.RememberRunning(route); } catch (ArgumentException) { rejected = true; }
                Assert(rejected && !policy.State.HasRunningIntent);
            }
        });
        Test("Backup authorization snapshots preserve explicit order and resist caller mutation", () =>
        {
            var primary = AuthorizedRoute();
            var first = AuthorizedRoute("first-backup");
            var second = AuthorizedRoute("second-backup");
            var backups = new List<RecoveryRoute> { first, second };
            var policy = new DeviceRecoveryPolicy(() => 0);
            policy.SetEnabled(true);
            Assert(policy.RememberRunning(primary, backups));
            backups[0] = AuthorizedRoute("unapproved-input");
            backups.Clear();
            var snapshot = policy.State.AuthorizedRoutes;
            Assert(snapshot.SequenceEqual(new[] { primary, first, second }));
            var rejected = false;
            try { ((IList<RecoveryRoute>)snapshot)[1] = AuthorizedRoute("snapshot-replacement"); }
            catch (NotSupportedException) { rejected = true; }
            Assert(rejected && policy.State.AuthorizedRoutes.SequenceEqual(snapshot));
        });
        Test("An approved backup reservation names both its target and the interrupted source", () =>
        {
            var now = 0L;
            var primary = AuthorizedRoute();
            var backup = AuthorizedRoute("approved-backup");
            var policy = new DeviceRecoveryPolicy(() => now);
            policy.SetEnabled(true);
            Assert(policy.RememberRunning(primary, [backup]) && policy.ObserveUnavailable(primary));
            Assert(!policy.TryTakeRecovery(backup, true, out _));
            now = 1000;
            Assert(policy.TryTakeRecovery(backup, true, out var attempt));
            Assert(attempt!.Route == backup && attempt.SourceRoute == primary && policy.IsAttemptCurrent(attempt));
            var changedSource = attempt with { SourceRoute = backup };
            Assert(!policy.IsAttemptCurrent(changedSource) && !policy.CompleteAttempt(changedSource, true));
            Assert(policy.CompleteAttempt(attempt, true));
            Assert(policy.State.Route == backup && policy.State.AuthorizedRoutes.SequenceEqual(new[] { primary, backup }));
        });
        Test("Changing an approved candidate restarts its full stability interval", () =>
        {
            var now = 0L;
            var primary = AuthorizedRoute();
            var first = AuthorizedRoute("first-backup");
            var second = AuthorizedRoute("second-backup");
            var policy = new DeviceRecoveryPolicy(() => now);
            policy.SetEnabled(true);
            policy.RememberRunning(primary, [first, second]);
            policy.ObserveUnavailable(primary);
            Assert(!policy.TryTakeRecovery(first, true, out _));
            now = 900;
            Assert(!policy.TryTakeRecovery(second, true, out _));
            now = 1000;
            Assert(!policy.TryTakeRecovery(first, true, out _));
            now = 1999;
            Assert(!policy.TryTakeRecovery(first, true, out _));
            now = 2000;
            Assert(policy.TryTakeRecovery(first, true, out var attempt) && attempt!.SourceRoute == primary);
        });
        Test("A running backup stays selected until its own confirmed outage", () =>
        {
            var now = 0L;
            var primary = AuthorizedRoute();
            var backup = AuthorizedRoute("approved-backup");
            var policy = new DeviceRecoveryPolicy(() => now);
            policy.SetEnabled(true);
            policy.RememberRunning(primary, [backup]);
            policy.ObserveUnavailable(primary);
            policy.TryTakeRecovery(backup, true, out _);
            now = 1000;
            Assert(policy.TryTakeRecovery(backup, true, out var first) && policy.CompleteAttempt(first!, true));
            now = 10000;
            Assert(!policy.TryTakeRecovery(primary, true, out _) && policy.State.Route == backup);
            Assert(!policy.ObserveUnavailable(primary) && !policy.State.Pending);
            Assert(policy.ObserveUnavailable(backup));
            Assert(!policy.TryTakeRecovery(primary, true, out _));
            now = 11000;
            Assert(policy.TryTakeRecovery(primary, true, out var second) && second!.SourceRoute == backup);
            Assert(policy.CompleteAttempt(second!, true) && policy.State.Route == primary);
        });
        Test("A failed backup attempt pauses the interrupted route and denies another automatic target", () =>
        {
            var now = 0L;
            var primary = AuthorizedRoute();
            var first = AuthorizedRoute("first-backup");
            var second = AuthorizedRoute("second-backup");
            var policy = new DeviceRecoveryPolicy(() => now);
            policy.SetEnabled(true);
            policy.RememberRunning(primary, [first, second]);
            policy.ObserveUnavailable(primary);
            policy.TryTakeRecovery(first, true, out _);
            now = 1000;
            Assert(policy.TryTakeRecovery(first, true, out var attempt) && policy.CompleteAttempt(attempt!, false));
            now = 100000;
            Assert(policy.State.Route == primary && policy.State.Paused && !policy.State.HasRunningIntent);
            Assert(!policy.ObserveUnavailable(primary) && !policy.TryTakeRecovery(second, true, out _));
        });
        Test("An approved backup ID cannot authorize a different capture format", () =>
        {
            foreach (var changed in new[]
            {
                AuthorizedRoute("approved-backup") with { SampleRate = 44100 },
                AuthorizedRoute("approved-backup") with { Channels = 1 },
                AuthorizedRoute("approved-backup") with { EndpointFormats = "new-input-format" }
            })
            {
                var primary = AuthorizedRoute();
                var backup = AuthorizedRoute("approved-backup");
                var policy = new DeviceRecoveryPolicy(() => 0);
                policy.SetEnabled(true);
                policy.RememberRunning(primary, [backup]);
                policy.ObserveUnavailable(primary);
                Assert(!policy.TryTakeRecovery(changed, true, out _) && policy.State.Code == "route_changed");
            }
        });
        Test("A changed shared endpoint fingerprint revokes an otherwise approved backup", () =>
        {
            var primary = AuthorizedRoute();
            var backup = AuthorizedRoute("approved-backup");
            var policy = new DeviceRecoveryPolicy(() => 0);
            policy.SetEnabled(true);
            policy.RememberRunning(primary, [backup]);
            policy.ObserveUnavailable(primary);
            Assert(!policy.TryTakeRecovery(backup with { SharedEndpointFormats = "send:44100/2;receive:48000/2" }, true, out _));
            Assert(!policy.State.HasRunningIntent && policy.State.AuthorizedRoutes.Count == 0);
        });
        Test("Switching among approved microphones preserves the rolling automatic-attempt limit", () =>
        {
            var now = 0L;
            var primary = AuthorizedRoute();
            var backup = AuthorizedRoute("approved-backup");
            var policy = new DeviceRecoveryPolicy(() => now);
            policy.SetEnabled(true);
            policy.RememberRunning(primary, [backup]);
            for (var index = 0; index < 3; index++)
            {
                var source = policy.State.Route!;
                var target = source == primary ? backup : primary;
                Assert(policy.ObserveUnavailable(source));
                now = index * 2000;
                Assert(!policy.TryTakeRecovery(target, true, out _));
                now += 1000;
                Assert(policy.TryTakeRecovery(target, true, out var attempt) && attempt!.SourceRoute == source);
                Assert(policy.CompleteAttempt(attempt!, true));
            }
            Assert(policy.State.Route == backup && policy.ObserveUnavailable(backup));
            now = 6000;
            Assert(!policy.TryTakeRecovery(primary, true, out _));
            now = 7000;
            Assert(!policy.TryTakeRecovery(primary, true, out _) && policy.State.Code == "flapping");
            Assert(policy.State.AutomaticAttemptsInWindow == 3);
        });
        Test("Invalid backup authorization is rejected atomically without revoking a valid reservation", () =>
        {
            var now = 0L;
            var primary = AuthorizedRoute();
            var backup = AuthorizedRoute("approved-backup");
            var policy = new DeviceRecoveryPolicy(() => now);
            policy.SetEnabled(true);
            policy.RememberRunning(primary, [backup]);
            policy.ObserveUnavailable(primary);
            policy.TryTakeRecovery(backup, true, out _);
            now = 1000;
            Assert(policy.TryTakeRecovery(backup, true, out var attempt));
            var generation = policy.State.Generation;
            IReadOnlyList<RecoveryRoute>[] invalid =
            [
                [backup, backup], [primary], [null!],
                [backup with { InputId = primary.CableReceiveId! }],
                [backup with { CableSendId = "another-send" }],
                [backup with { CableReceiveId = "another-receive" }],
                [backup with { MonitorId = "another-monitor" }],
                [backup with { SampleRate = 0 }],
                [backup with { EndpointFormats = null }],
                [backup with { SharedEndpointFormats = "different-shared-format" }],
                [backup with { SharedEndpointFormats = "format\nwith-control" }],
                Enumerable.Range(0, DeviceRecoveryPolicy.MaximumBackupRoutes + 1).Select(index => AuthorizedRoute("backup-" + index)).ToArray()
            ];
            foreach (var routes in invalid)
            {
                var rejected = false;
                try { policy.RememberRunning(primary, routes); } catch (ArgumentException) { rejected = true; }
                Assert(rejected && policy.State.Generation == generation && policy.IsAttemptCurrent(attempt!));
                Assert(policy.State.AuthorizedRoutes.SequenceEqual(new[] { primary, backup }));
            }
            foreach (var invalidPrimary in new[] { primary with { SharedEndpointFormats = null }, primary with { EndpointFormats = null } })
            {
                var rejected = false;
                try { policy.RememberRunning(invalidPrimary, [backup]); } catch (ArgumentException) { rejected = true; }
                Assert(rejected && policy.State.Generation == generation && policy.IsAttemptCurrent(attempt!));
            }
        });
        Test("The maximum backup list can recover and cancellation erases all target permission", () =>
        {
            var now = 0L;
            var primary = AuthorizedRoute();
            var backups = Enumerable.Range(0, DeviceRecoveryPolicy.MaximumBackupRoutes).Select(index => AuthorizedRoute("backup-" + index)).ToArray();
            var policy = new DeviceRecoveryPolicy(() => now);
            policy.SetEnabled(true);
            Assert(policy.RememberRunning(primary, backups) && policy.State.AuthorizedRoutes.Count == backups.Length + 1);
            Assert(policy.ObserveUnavailable(primary));
            Assert(!policy.TryTakeRecovery(backups[^1], true, out _));
            now = 1000;
            Assert(policy.TryTakeRecovery(backups[^1], true, out var attempt));
            policy.Cancel();
            Assert(policy.State.AuthorizedRoutes.Count == 0 && !policy.IsAttemptCurrent(attempt!) && !policy.CompleteAttempt(attempt!, true));
        });
        return count;
    }
}
