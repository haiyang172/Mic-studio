using System.IO.MemoryMappedFiles;
using System.Buffers.Binary;
using MicStudio.Core;

namespace MicStudio.NativeApo;

/// <summary>Writes an explicitly provisioned per-endpoint mapping; never creates a system file or changes ACLs.</summary>
public sealed class NativeApoParameterWriter : IDisposable
{
    private readonly FileStream stream;
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly object gate = new();
    private bool disposed;
    public string FilePath { get; }
    private NativeApoParameterWriter(string path)
    {
        FilePath = path;
        stream = NativeApoPaths.OpenParameterFile(path);
        try
        {
            if (stream.Length != NativeApoParameterCodec.Size) throw new InvalidDataException("APO parameter file must contain exactly 224 bytes.");
            mapping = MemoryMappedFile.CreateFromFile(stream, null, NativeApoParameterCodec.Size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
            try { view = mapping.CreateViewAccessor(0, NativeApoParameterCodec.Size, MemoryMappedFileAccess.ReadWrite); }
            catch { mapping.Dispose(); throw; }
        }
        catch { stream.Dispose(); throw; }
    }

    public static NativeApoParameterWriter OpenExisting(Guid endpoint, string? parameterRoot = null) => new(NativeApoPaths.ParameterFile(endpoint, parameterRoot));

    public void Write(AudioSettings settings, int sampleRate, int channels, bool enabled = true, bool testMinus6Db = false)
    {
        var bytes = NativeApoParameterCodec.Encode(settings, sampleRate, channels, enabled, testMinus6Db);
        // SFX negotiates a format per client stream. Device mix format only
        // validates the editor snapshot; the native processor adapts per stream.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 0);
        WriteSnapshot(bytes);
    }

    public void Disable()
    {
        var bytes = NativeApoParameterCodec.Encode(new AudioSettings(), 48000, 2, enabled: false);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 0);
        WriteSnapshot(bytes);
    }

    public bool RefreshLease()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            stream.Lock(0, NativeApoParameterCodec.Size);
            try
            {
                var bytes = new byte[NativeApoParameterCodec.Size]; view.ReadArray(0, bytes, 0, bytes.Length);
                if (!NativeApoParameterCodec.TryDecode(bytes, out var snapshot) || snapshot is not { Enabled: true } || snapshot.OwnerProcessId != Environment.ProcessId) return false;
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(208, 8), (ulong)Math.Max(1, Environment.TickCount64));
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(216, 4), (uint)Environment.ProcessId);
                WriteSnapshotLocked(bytes); return true;
            }
            finally { stream.Unlock(0, NativeApoParameterCodec.Size); }
        }
    }

    private void WriteSnapshot(byte[] bytes)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            stream.Lock(0, NativeApoParameterCodec.Size);
            try
            {
                var current = new byte[NativeApoParameterCodec.Size]; view.ReadArray(0, current, 0, current.Length);
                if (NativeApoParameterCodec.TryDecode(current, out var previous) && previous is { Enabled: true }
                    && previous.OwnerProcessId != Environment.ProcessId && previous.IsLeaseFresh((ulong)Math.Max(1, Environment.TickCount64)))
                    throw new IOException("Another Mic Studio process currently owns this APO endpoint. Stop it or wait for its lease to expire before taking control.");
                WriteSnapshotLocked(bytes);
            }
            finally { stream.Unlock(0, NativeApoParameterCodec.Size); }
        }
    }

    private void WriteSnapshotLocked(byte[] bytes)
    {
        var sequence = view.ReadUInt32(NativeApoParameterCodec.SequenceOffset);
        var odd = unchecked((sequence & ~1u) + 1u);
        view.Write(NativeApoParameterCodec.SequenceOffset, odd); Thread.MemoryBarrier();
        view.WriteArray(0, bytes, 0, NativeApoParameterCodec.SequenceOffset);
        view.WriteArray(12, bytes, 12, bytes.Length - 12);
        Thread.MemoryBarrier(); view.Write(NativeApoParameterCodec.SequenceOffset, unchecked(odd + 1u));
        // Shared mapped pages are coherent without a synchronous disk Flush.
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            view.Dispose(); mapping.Dispose(); stream.Dispose();
        }
    }
}
