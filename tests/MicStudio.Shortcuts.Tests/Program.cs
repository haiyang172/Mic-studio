using MicStudio.App;
using System.Windows.Threading;

internal static class Program
{
    private static int passed;
    private static void Assert(bool condition, string message = "assertion failed") { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static ShortcutPreferences Enabled() => new() { Enabled = true, PushToTalkEnabled = true };
    private static nint Packed(ShortcutBinding binding) => (nint)(((long)binding.VirtualKey << 16) | (uint)binding.Modifiers);
    private static void Press(GlobalShortcutService service, FakeNative native, ShortcutAction action)
    {
        var registration = native.Active.Single(r => r.Value.Binding.Action == action);
        service.ProcessWindowMessage(GlobalShortcutService.HotkeyMessage, registration.Key, Packed(registration.Value.Binding));
    }
    [STAThread]
    private static int Main()
    {
        try
        {
            Test("Disabled defaults register nothing and do not enable PTT", () =>
            {
                var native = new FakeNative(); using var service = new GlobalShortcutService(Dispatcher.CurrentDispatcher, native, () => 0, false);
                service.Configure(1, new()); Assert(native.RegisterCalls == 0 && !service.PttReady && !service.IsPttDown);
                Assert(ShortcutPreferences.CreateDefaults().All(b => b.TryValidate(out _)));
            });
            Test("OS reserved keys and unmodified typing shortcuts are rejected", () =>
            {
                foreach (var binding in new[] {
                    new ShortcutBinding { VirtualKey = 0x7B }, new ShortcutBinding { VirtualKey = 0x2C },
                    new ShortcutBinding { VirtualKey = 0x41, Modifiers = ShortcutModifiers.Windows },
                    new ShortcutBinding { VirtualKey = 0x73, Modifiers = ShortcutModifiers.Alt },
                    new ShortcutBinding { VirtualKey = 0x2E, Modifiers = ShortcutModifiers.Control | ShortcutModifiers.Alt },
                    new ShortcutBinding { VirtualKey = 0x41, Modifiers = ShortcutModifiers.None },
                    new ShortcutBinding { VirtualKey = 0x11 }, new ShortcutBinding { Action = (ShortcutAction)999, VirtualKey = 0x41 } })
                    Assert(!binding.TryValidate(out _));
                Assert(new ShortcutBinding { Action = ShortcutAction.PushToTalk, VirtualKey = 0x56, Modifiers = ShortcutModifiers.None }.TryValidate(out _));
            });
            Test("Every active registration requests no auto-repeat", () =>
            {
                var native = new FakeNative(); using var service = new GlobalShortcutService(Dispatcher.CurrentDispatcher, native, () => 0, false);
                service.Configure(1, Enabled()); Assert(native.Active.Count == 7 && service.PttReady);
                Assert(native.Active.Values.All(v => (v.NativeModifiers & 0x4000) != 0));
            });
            Test("Duplicate actions or chords are reported and never registered", () =>
            {
                var prefs = Enabled(); prefs.Bindings[1].VirtualKey = prefs.Bindings[0].VirtualKey;
                prefs.Bindings.Add(prefs.Bindings[2].Clone());
                var native = new FakeNative(); using var service = new GlobalShortcutService(Dispatcher.CurrentDispatcher, native, () => 0, false);
                service.Configure(1, prefs);
                Assert(service.Status.Issues.Count(i => i.Code == "duplicate_binding") == 2);
                Assert(service.Status.Issues.Count(i => i.Code == "duplicate_action") == 2);
                Assert(native.Active.Values.All(v => v.Binding.Action is not (ShortcutAction.ToggleTransmitMute or ShortcutAction.ToggleEffects or ShortcutAction.NextPreset)));
            });
            Test("PTT registration conflicts fail closed while other actions remain usable", () =>
            {
                var native = new FakeNative { ConflictKey = 0x20 }; using var service = new GlobalShortcutService(Dispatcher.CurrentDispatcher, native, () => 0, false);
                var triggered = 0; service.ActionTriggered += _ => triggered++;
                service.Configure(1, Enabled()); Press(service, native, ShortcutAction.ToggleEffects);
                Assert(native.Active.Count == 6 && !service.PttReady && !service.IsPttDown && triggered == 1);
                Assert(service.Status.Issues.Any(i => i.Action == ShortcutAction.PushToTalk && i.Code == "registration_failed" && i.NativeError == 1409));
            });
            Test("A held key at configure cannot open PTT until a release and fresh press", () =>
            {
                var native = new FakeNative(); native.SetChord(true); long now = 0;
                using var service = new GlobalShortcutService(Dispatcher.CurrentDispatcher, native, () => now, false);
                service.Configure(1, Enabled()); Press(service, native, ShortcutAction.PushToTalk); Assert(!service.IsPttDown);
                native.SetChord(false); now = 20; service.PollKeyState();
                native.SetChord(true); now = 40; Press(service, native, ShortcutAction.PushToTalk); Assert(service.IsPttDown);
                service.ResetPushToTalk(); Assert(!service.IsPttDown);
                Press(service, native, ShortcutAction.PushToTalk); Assert(!service.IsPttDown);
            });
            Test("Modifier release closes PTT and cannot reopen it with a still-held trigger", () =>
            {
                var native = new FakeNative(); using var service = new GlobalShortcutService(Dispatcher.CurrentDispatcher, native, () => 0, false);
                service.Configure(1, Enabled()); native.SetChord(true); Press(service, native, ShortcutAction.PushToTalk); Assert(service.IsPttDown);
                native.Keys.Remove(0x11); service.PollKeyState(); Assert(!service.IsPttDown);
                native.Keys.Add(0x11); Press(service, native, ShortcutAction.PushToTalk); Assert(!service.IsPttDown);
                native.Keys.Remove(0x20); service.PollKeyState(); native.Keys.Add(0x20); Press(service, native, ShortcutAction.PushToTalk); Assert(service.IsPttDown);
            });
            Test("PTT emits bounded lease renewals and a long UI gap requires a fresh press", () =>
            {
                var native = new FakeNative(); long now = 0; var leases = 0;
                using var service = new GlobalShortcutService(Dispatcher.CurrentDispatcher, native, () => now, false);
                service.PttLeaseRenewed += () => leases++;
                service.Configure(1, Enabled()); native.SetChord(true); Press(service, native, ShortcutAction.PushToTalk); Assert(leases == 1);
                now = 20; service.PollKeyState(); Assert(leases == 1);
                now = 100; service.PollKeyState(); Assert(leases == 2);
                now = 500; service.PollKeyState(); Assert(!service.IsPttDown && leases == 2);
                Press(service, native, ShortcutAction.PushToTalk); Assert(!service.IsPttDown && leases == 2);
            });
            Test("Additional game modifiers do not interrupt a valid held PTT chord", () =>
            {
                var native = new FakeNative(); using var service = new GlobalShortcutService(Dispatcher.CurrentDispatcher, native, () => 0, false);
                service.Configure(1, Enabled()); native.SetChord(true); Press(service, native, ShortcutAction.PushToTalk);
                native.Keys.Add(0x10); service.PollKeyState(); Assert(service.IsPttDown);
                native.Keys.Remove(0x12); service.PollKeyState(); Assert(!service.IsPttDown);
            });
            Test("Stale registration messages and mismatched chords cannot dispatch", () =>
            {
                var native = new FakeNative(); using var service = new GlobalShortcutService(Dispatcher.CurrentDispatcher, native, () => 0, false);
                var calls = 0; service.ActionTriggered += _ => calls++;
                var prefs = Enabled(); service.Configure(1, prefs);
                var stale = native.Active.First(); service.Configure(1, prefs);
                Assert(!service.ProcessWindowMessage(GlobalShortcutService.HotkeyMessage, stale.Key, Packed(stale.Value.Binding)));
                var current = native.Active.First(); service.ProcessWindowMessage(GlobalShortcutService.HotkeyMessage, current.Key, 0);
                Assert(calls == 0); Press(service, native, ShortcutAction.ToggleEffects); Assert(calls == 1);
            });
            Test("A heartbeat older than the send lease cannot reopen PTT after a shorter poll stall", () =>
            {
                var native = new FakeNative(); long now = 0; var leases = 0;
                using var service = new GlobalShortcutService(Dispatcher.CurrentDispatcher, native, () => now, false);
                service.PttLeaseRenewed += () => leases++;
                service.Configure(1, Enabled()); native.SetChord(true); Press(service, native, ShortcutAction.PushToTalk);
                now = 80; service.PollKeyState();
                now = 310; service.PollKeyState(); Assert(!service.IsPttDown && leases == 1);
                Press(service, native, ShortcutAction.PushToTalk); Assert(!service.IsPttDown && leases == 1);
            });
            Test("Configured bindings are a snapshot and never follow later editor mutations", () =>
            {
                var native = new FakeNative(); using var service = new GlobalShortcutService(Dispatcher.CurrentDispatcher, native, () => 0, false);
                var prefs = Enabled(); var calls = 0; service.ActionTriggered += _ => calls++;
                service.Configure(1, prefs); prefs.Bindings[0].VirtualKey = 0x5A;
                Press(service, native, ShortcutAction.ToggleTransmitMute); Assert(calls == 1);
            });
            Test("Suspend and dispose close PTT and unregister every owned shortcut", () =>
            {
                var native = new FakeNative(); var service = new GlobalShortcutService(Dispatcher.CurrentDispatcher, native, () => 0, false);
                service.Configure(1, Enabled()); native.SetChord(true); Press(service, native, ShortcutAction.PushToTalk); Assert(service.IsPttDown);
                service.Suspend(); Assert(!service.IsPttDown && native.Active.Count == 0 && !service.PttReady);
                service.Resume(); Press(service, native, ShortcutAction.PushToTalk); Assert(!service.IsPttDown);
                service.Dispose(); Assert(native.Active.Count == 0 && !service.IsPttDown && !service.PttReady);
            });
            Test("Missing window or PTT without global enable remains unavailable", () =>
            {
                var native = new FakeNative(); using var service = new GlobalShortcutService(Dispatcher.CurrentDispatcher, native, () => 0, false);
                service.Configure(0, Enabled()); Assert(native.RegisterCalls == 0 && service.Status.Issues.Any(i => i.Code == "window_unavailable"));
                service.Configure(1, new() { PushToTalkEnabled = true }); Assert(native.RegisterCalls == 0 && !service.PttReady && service.Status.Issues.Any(i => i.Code == "ptt_requires_shortcuts"));
            });
            Console.WriteLine($"Shortcut synthetic verification: {passed} passed. No OS shortcuts registered, keyboard captured or audio opened."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private sealed class FakeNative : IGlobalShortcutNative
    {
        public readonly Dictionary<int, (ShortcutBinding Binding, uint NativeModifiers)> Active = [];
        public readonly HashSet<int> Keys = [];
        public int RegisterCalls, ConflictKey = -1;
        private static ShortcutAction ActionFor(uint key) => key switch { 0x4D => ShortcutAction.ToggleTransmitMute, 0x45 => ShortcutAction.ToggleEffects, 0x50 => ShortcutAction.NextPreset, 0x26 => ShortcutAction.GainUp, 0x28 => ShortcutAction.GainDown, 0x53 => ShortcutAction.StopSending, _ => ShortcutAction.PushToTalk };
        public bool Register(nint hwnd, int id, uint modifiers, uint virtualKey, out int error)
        {
            RegisterCalls++; error = virtualKey == ConflictKey ? 1409 : 0;
            if (error != 0) return false;
            Active.Add(id, (new() { Action = ActionFor(virtualKey), VirtualKey = (int)virtualKey, Modifiers = (ShortcutModifiers)(modifiers & 15) }, modifiers)); return true;
        }
        public void Unregister(nint hwnd, int id) => Active.Remove(id);
        public bool IsDown(int virtualKey) => Keys.Contains(virtualKey);
        public void SetChord(bool down) { if (down) { Keys.Add(0x11); Keys.Add(0x12); Keys.Add(0x20); } else { Keys.Clear(); } }
    }
}
