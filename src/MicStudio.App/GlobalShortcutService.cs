using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MicStudio.App;

/// <summary>UI-thread global shortcut registration. No capture or keyboard-text storage.</summary>
public sealed class GlobalShortcutService : IDisposable
{
    public const int HotkeyMessage = 0x0312;
    public const int PttLeaseMilliseconds = 300;
    private const uint NoRepeat = 0x4000;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer timer;
    private readonly IGlobalShortcutNative native;
    private readonly Func<long> clock;
    private readonly bool polling;
    private readonly Dictionary<int, ShortcutBinding> registered = [];
    private readonly PushToTalkLatch latch = new();
    private nint window;
    private int nextId = 0x5000;
    private bool disposed, suspended, pttPublished;
    private long lastPoll, lastLease;
    private ShortcutBinding? pttBinding;
    private ShortcutPreferences preferences = new();
    public event Action<ShortcutAction>? ActionTriggered;
    public event Action<bool>? PttChanged;
    public event Action? PttLeaseRenewed;
    public event Action<ShortcutStatus>? StatusChanged;
    public bool IsPttDown => latch.IsDown;
    public bool PttReady => Status.PttReady && !suspended && !disposed;
    public ShortcutStatus Status { get; private set; } = new(false, 0, false, []);

    public GlobalShortcutService(Dispatcher dispatcher) : this(dispatcher, new Win32ShortcutNative(), () => Environment.TickCount64, true) { }
    internal GlobalShortcutService(Dispatcher dispatcher, IGlobalShortcutNative native, Func<long> clock, bool polling)
    {
        this.dispatcher = dispatcher; this.native = native; this.clock = clock; this.polling = polling;
        timer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => PollKeyState();
    }

    public void Configure(nint hwnd, ShortcutPreferences settings)
    {
        dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(settings); settings.Normalize();
        ClearRegistrations(); preferences = settings.Clone(); window = hwnd;
        var issues = new List<ShortcutIssue>();
        if (preferences.PushToTalkEnabled && !preferences.Enabled) issues.Add(new(ShortcutAction.PushToTalk, "ptt_requires_shortcuts"));
        if (!preferences.Enabled || suspended) { PublishStatus(false, issues); return; }
        if (window == 0) { issues.Add(new(null, "window_unavailable")); PublishStatus(true, issues); return; }
        var candidates = preferences.Bindings.Where(b => b.Enabled && (b.Action != ShortcutAction.PushToTalk || preferences.PushToTalkEnabled)).ToArray();
        foreach (var binding in candidates)
        {
            if (!binding.TryValidate(out var error)) { issues.Add(new(binding.Action, error)); continue; }
            if (candidates.Count(b => b.Action == binding.Action) > 1) { issues.Add(new(binding.Action, "duplicate_action")); continue; }
            if (candidates.Count(b => b.VirtualKey == binding.VirtualKey && b.Modifiers == binding.Modifiers) > 1) { issues.Add(new(binding.Action, "duplicate_binding")); continue; }
            if (nextId > 0xBFFF) { issues.Add(new(binding.Action, "id_exhausted")); continue; }
            var id = nextId++;
            if (!native.Register(window, id, (uint)binding.Modifiers | NoRepeat, (uint)binding.VirtualKey, out var nativeError))
            { issues.Add(new(binding.Action, "registration_failed", nativeError)); continue; }
            registered.Add(id, binding);
            if (binding.Action == ShortcutAction.PushToTalk) pttBinding = binding;
        }
        if (preferences.PushToTalkEnabled && pttBinding == null && !issues.Any(i => i.Action == ShortcutAction.PushToTalk))
            issues.Add(new(ShortcutAction.PushToTalk, "ptt_unassigned"));
        ResetPushToTalk();
        PublishStatus(true, issues);
        if (pttBinding != null && polling) timer.Start();
    }

    public bool ProcessWindowMessage(int message, nint wParam, nint lParam)
    {
        dispatcher.VerifyAccess();
        if (disposed || message != HotkeyMessage || !registered.TryGetValue((int)wParam, out var binding)) return false;
        if (suspended) return true;
        var packed = unchecked((ulong)lParam.ToInt64());
        if ((int)((packed >> 16) & 0xFFFF) != binding.VirtualKey || (ShortcutModifiers)(packed & 15) != binding.Modifiers) return true;
        if (binding.Action == ShortcutAction.PushToTalk)
        {
            var now = clock();
            if (HasStaleTiming(now)) ResetPushToTalk();
            latch.Press(native.IsDown(binding.VirtualKey), ModifiersMatch(binding.Modifiers));
            PublishPtt();
            if (latch.IsDown) { lastLease = now; PttLeaseRenewed?.Invoke(); }
        }
        else ActionTriggered?.Invoke(binding.Action);
        return true;
    }

    /// <summary>Closes PTT and waits for trigger release before accepting another press.</summary>
    public void ResetPushToTalk()
    {
        dispatcher.VerifyAccess();
        latch.Reset(); lastPoll = lastLease = clock();
        if (pttBinding != null) latch.Observe(native.IsDown(pttBinding.VirtualKey), ModifiersMatch(pttBinding.Modifiers));
        PublishPtt();
    }
    public void Suspend()
    {
        dispatcher.VerifyAccess(); if (disposed) return;
        suspended = true; ClearRegistrations(); PublishStatus(preferences.Enabled, []);
    }
    public void Resume()
    {
        dispatcher.VerifyAccess(); if (disposed) return;
        suspended = false; Configure(window, preferences);
    }
    internal void PollKeyState()
    {
        dispatcher.VerifyAccess();
        if (disposed || suspended || pttBinding == null) return;
        var now = clock();
        if (HasStaleTiming(now)) latch.Reset();
        lastPoll = now;
        latch.Observe(native.IsDown(pttBinding.VirtualKey), ModifiersMatch(pttBinding.Modifiers));
        PublishPtt();
        if (latch.IsDown && now - lastLease >= 100) { lastLease = now; PttLeaseRenewed?.Invoke(); }
    }
    private bool ModifiersMatch(ShortcutModifiers expected)
    {
        var actual = ShortcutModifiers.None;
        if (native.IsDown(0x10)) actual |= ShortcutModifiers.Shift;
        if (native.IsDown(0x11)) actual |= ShortcutModifiers.Control;
        if (native.IsDown(0x12)) actual |= ShortcutModifiers.Alt;
        if (native.IsDown(0x5B) || native.IsDown(0x5C)) actual |= ShortcutModifiers.Windows;
        // Required modifiers must remain held. Additional game controls such as
        // Shift must not interrupt an already-held, otherwise valid PTT chord.
        return (actual & expected) == expected;
    }
    // The audio lease can expire before a 300ms poll gap if the previous
    // heartbeat preceded the last poll. Stop early; never reopen an expired
    // lease just because the trigger is still physically held after a UI stall.
    private bool HasStaleTiming(long now) => now < lastPoll || now - lastPoll > PttLeaseMilliseconds
        || (latch.IsDown && (now < lastLease || now - lastLease >= PttLeaseMilliseconds - 50));
    private void PublishPtt()
    {
        if (pttPublished == latch.IsDown) return;
        pttPublished = latch.IsDown; PttChanged?.Invoke(pttPublished);
    }
    private void PublishStatus(bool enabled, IReadOnlyList<ShortcutIssue> issues)
    {
        Status = new(enabled, registered.Count, pttBinding != null && !suspended, issues.ToArray());
        StatusChanged?.Invoke(Status);
    }
    private void ClearRegistrations()
    {
        timer.Stop(); latch.Reset(); PublishPtt();
        foreach (var id in registered.Keys) native.Unregister(window, id);
        registered.Clear(); pttBinding = null;
    }
    public void Dispose()
    {
        dispatcher.VerifyAccess(); if (disposed) return;
        ClearRegistrations(); disposed = true;
        Status = new(false, 0, false, []);
        ActionTriggered = null; PttChanged = null; PttLeaseRenewed = null; StatusChanged = null;
    }
}

internal interface IGlobalShortcutNative
{
    bool Register(nint hwnd, int id, uint modifiers, uint virtualKey, out int error);
    void Unregister(nint hwnd, int id);
    bool IsDown(int virtualKey);
}
internal sealed class Win32ShortcutNative : IGlobalShortcutNative
{
    public bool Register(nint hwnd, int id, uint modifiers, uint virtualKey, out int error)
    { var success = RegisterHotKey(hwnd, id, modifiers, virtualKey); error = success ? 0 : Marshal.GetLastWin32Error(); return success; }
    public void Unregister(nint hwnd, int id) => UnregisterHotKey(hwnd, id);
    public bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
}
