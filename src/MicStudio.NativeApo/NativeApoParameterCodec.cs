using System.Buffers.Binary;
using MicStudio.Core;

namespace MicStudio.NativeApo;

public sealed record NativeApoSnapshot(uint Sequence, bool Enabled, int SampleRate, int Channels,
    bool EqEnabled, bool LimiterEnabled, float InputGainDb, float OutputGainDb,
    float OutputCeilingDb, bool TestMinus6Db, IReadOnlyList<EqBand> Bands, ulong HeartbeatTickMs, uint OwnerProcessId)
{
    public bool IsLeaseFresh(ulong nowTickMs) => !Enabled || (OwnerProcessId != 0 && HeartbeatTickMs != 0 && nowTickMs >= HeartbeatTickMs && nowTickMs - HeartbeatTickMs <= 3000);
}

/// <summary>ABI shared with MicStudioApoParams.h: version 2, Pack=4, exactly 224 bytes.</summary>
public static class NativeApoParameterCodec
{
    public const uint Version = 2;
    public const int Size = 224;
    public const int SequenceOffset = 8;
    public const int BandOffset = 48;
    public const int BandSize = 20;

    public static byte[] Encode(AudioSettings settings, int sampleRate, int channels,
        bool enabled = true, bool testMinus6Db = false, uint sequence = 0)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (channels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(channels));
        if ((sequence & 1) != 0) throw new ArgumentException("A complete snapshot needs an even sequence.", nameof(sequence));
        var copy = settings.CreateProcessingSnapshot(sampleRate, out _);
        var bytes = new byte[Size];
        U(0, Version); U(4, Size); U(8, sequence); U(12, enabled && !copy.Bypass ? 1u : 0u);
        U(16, (uint)sampleRate); U(20, (uint)channels); U(24, copy.EqEnabled ? 1u : 0u);
        U(28, copy.Limiter.Enabled ? 1u : 0u); F(32, copy.InputGainDb); F(36, copy.OutputGainDb);
        F(40, copy.Limiter.CeilingDb); U(44, testMinus6Db ? 1u : 0u);
        for (var index = 0; index < 8; index++)
        {
            var band = copy.Bands[index]; var offset = BandOffset + BandSize * index;
            U(offset, band.Enabled ? 1u : 0u); U(offset + 4, (uint)band.Type);
            F(offset + 8, band.FrequencyHz); F(offset + 12, band.GainDb); F(offset + 16, band.Q);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(208, 8), enabled && !copy.Bypass ? (ulong)Math.Max(1, Environment.TickCount64) : 0);
        U(216, enabled && !copy.Bypass ? (uint)Environment.ProcessId : 0); U(220, 0);
        return bytes;
        void U(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        void F(int offset, double value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), checked((float)value));
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out NativeApoSnapshot? snapshot)
    {
        snapshot = null;
        if (bytes.Length < Size) return false;
        var copy = bytes[..Size].ToArray();
        uint U(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(offset, 4));
        float F(int offset) => BinaryPrimitives.ReadSingleLittleEndian(copy.AsSpan(offset, 4));
        if (U(0) != Version || U(4) != Size || (U(8) & 1) != 0 || U(12) > 1 || U(24) > 1 || U(28) > 1 || U(44) > 1 || U(220) != 0) return false;
        var heartbeat = BinaryPrimitives.ReadUInt64LittleEndian(copy.AsSpan(208, 8));
        if (U(12) == 1 && (heartbeat == 0 || U(216) == 0)) return false;
        var rate = U(16); var channels = U(20);
        if ((rate != 0 && rate is < 8000 or > 384000) || channels > 8) return false;
        if (!Range(F(32), -24, 30) || !Range(F(36), -24, 0) || !Range(F(40), -12, -.1f)) return false;
        EqFilterType[] types = [EqFilterType.HighPass, EqFilterType.LowShelf, EqFilterType.Peak, EqFilterType.Peak, EqFilterType.Peak, EqFilterType.Peak, EqFilterType.HighShelf, EqFilterType.LowPass];
        var bands = new List<EqBand>(8);
        for (var index = 0; index < 8; index++)
        {
            var offset = BandOffset + BandSize * index;
            var frequency = F(offset + 8); var gain = F(offset + 12); var q = F(offset + 16);
            if (U(offset) > 1 || U(offset + 4) != (uint)types[index] || !Range(frequency, 20, index == 0 ? 250 : 20000)
                || !Range(gain, -12, 12) || !Range(q, .2f, 10) || (U(offset) == 1 && rate != 0 && frequency >= rate * .49)) return false;
            bands.Add(new() { Name = "Band " + (index + 1), Enabled = U(offset) == 1, Type = types[index], FrequencyHz = frequency, GainDb = gain, Q = q });
        }
        snapshot = new(U(8), U(12) == 1, (int)rate, (int)channels, U(24) == 1, U(28) == 1,
            F(32), F(36), F(40), U(44) == 1, bands, heartbeat, U(216));
        return true;
    }

    public static IReadOnlyList<string> GetUnsupportedEffects(AudioSettings settings)
    {
        var names = new List<string>();
        if (settings.Denoise.Enabled) names.Add("Noise reduction");
        if (settings.Compressor.Enabled) names.Add("Compressor");
        if (settings.Gate.Enabled) names.Add("Expander");
        if (settings.Creative.SaturationEnabled) names.Add("Saturation");
        if (settings.Creative.RobotEnabled) names.Add("Robot");
        if (settings.Creative.DelayEnabled) names.Add("Echo");
        if (settings.Creative.ReverbEnabled) names.Add("Reverb");
        if (settings.Creative.PhoneEnabled) names.Add("Phone");
        if (settings.Creative.PitchSemitones != 0) names.Add("Pitch shift");
        return names;
    }

    private static bool Range(float value, float min, float max) => float.IsFinite(value) && value >= min && value <= max;
}
