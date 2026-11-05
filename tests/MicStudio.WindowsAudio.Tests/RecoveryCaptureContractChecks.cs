using MicStudio.WindowsAudio;
using NAudio.Wave;

internal static class RecoveryCaptureContractChecks
{
    public static int Run()
    {
        var count = 0;
        static void Assert(bool condition) { if (!condition) throw new Exception("Recovery capture contract failed."); }
        static void Throws<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }
        void Test(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }

        Test("Ordinary capture has no recovery format constraint", () =>
        {
            var options = new CaptureOptions { InputDeviceId = "unused" };
            Assert(options.ExpectedInputSampleRate == null && options.ExpectedInputChannels == null);
            AudioEngine.ValidateRecoveryFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), null, null);
            AudioEngine.ValidateRecoveryFormat(new WaveFormat(16000, 16, 1), null, null);
        });
        Test("Recovery accepts only the original sample rate and channel count", () =>
        {
            var original = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            AudioEngine.ValidateRecoveryFormat(original, 48000, 2);
            Throws<InvalidOperationException>(() => AudioEngine.ValidateRecoveryFormat(original, 44100, 2));
            Throws<InvalidOperationException>(() => AudioEngine.ValidateRecoveryFormat(original, 48000, 1));
        });
        Test("Partial and invalid recovery format expectations are rejected", () =>
        {
            var original = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            Throws<ArgumentException>(() => AudioEngine.ValidateRecoveryFormat(original, 48000, null));
            Throws<ArgumentException>(() => AudioEngine.ValidateRecoveryFormat(original, null, 2));
            foreach (var rate in new[] { 0, 7999, 384001 })
                Throws<ArgumentOutOfRangeException>(() => AudioEngine.ValidateRecoveryFormat(original, rate, 2));
            foreach (var channels in new[] { 0, 9 })
                Throws<ArgumentOutOfRangeException>(() => AudioEngine.ValidateRecoveryFormat(original, 48000, channels));
            Throws<ArgumentNullException>(() => AudioEngine.ValidateRecoveryFormat(null!, 48000, 2));
        });
        Test("Stop reasons remain explicit across compatible status construction and session copies", () =>
        {
            var ordinary = new AudioEngineStatus(false, "stopped", null, 7);
            Assert(ordinary.StopReason == AudioStopReason.None);
            var unavailable = new AudioEngineStatus(false, "device unavailable", "synthetic", 8) { StopReason = AudioStopReason.DeviceUnavailable };
            Assert((unavailable with { SessionId = 9 }).StopReason == AudioStopReason.DeviceUnavailable);
            var audioError = unavailable with { StopReason = AudioStopReason.AudioError };
            Assert(audioError.StopReason != AudioStopReason.DeviceUnavailable && audioError.SessionId == 8);
        });
        return count;
    }
}
