using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using MicStudio.Core;
using MicStudio.NativeApo;

var temporary = Path.Combine(Path.GetTempPath(), "MicStudio-NativeBridge-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var endpoint = new Guid("01234567-89ab-cdef-0123-456789abcdef");
var count = 0;
void Assert(bool ok, string message = "assertion failed") { if (!ok) throw new Exception(message); }
void Test(string name, Action test) { test(); count++; Console.WriteLine("PASS " + name); }
void Throws<T>(Action test) where T : Exception { try { test(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
byte[] Snapshot() => NativeApoParameterCodec.Encode(new AudioSettings(), 48000, 2);

try
{
    Test("Native ABI version size and exact field offsets", () =>
    {
        var settings = new AudioSettings { InputGainDb = 6, OutputGainDb = -3, EqEnabled = false }; settings.Limiter.Enabled = false;
        var data = NativeApoParameterCodec.Encode(settings, 48000, 2, sequence: 42);
        Assert(data.Length == 224 && U(data, 0) == 2 && U(data, 4) == 224 && U(data, 8) == 42 && U(data, 12) == 1);
        Assert(U(data, 16) == 48000 && U(data, 20) == 2 && U(data, 24) == 0 && U(data, 28) == 0);
        Assert(F(data, 32) == 6 && F(data, 36) == -3 && F(data, 40) == -1 && U(data, 44) == 0);
        Assert(U(data, 48) == 1 && U(data, 52) == 0 && F(data, 56) == 20 && U(data, 48 + 7 * 20 + 4) == 4);
        Assert(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(208, 8)) > 0 && U(data, 216) == Environment.ProcessId && U(data, 220) == 0);
        Assert(NativeApoParameterCodec.TryDecode(data, out var decoded) && decoded!.Sequence == 42 && !decoded.EqEnabled && !decoded.LimiterEnabled);
    });
    Test("Bypass and disabled control produce exact disabled metadata", () =>
    {
        var settings = new AudioSettings { Bypass = true, InputGainDb = 24 };
        var data = NativeApoParameterCodec.Encode(settings, 48000, 2);
        Assert(NativeApoParameterCodec.TryDecode(data, out var decoded) && !decoded!.Enabled && decoded.OwnerProcessId == 0 && decoded.HeartbeatTickMs == 0);
        settings.Bypass = false; Assert(U(NativeApoParameterCodec.Encode(settings, 48000, 2, enabled: false), 12) == 0);
    });
    Test("Processing snapshot adapts low sample rates without changing saved values", () =>
    {
        var settings = new AudioSettings(); var original = settings.Bands[7].FrequencyHz;
        settings.Bands[6].Enabled = false; settings.Bands[6].GainDb = 12;
        var data = NativeApoParameterCodec.Encode(settings, 8000, 1);
        Assert(NativeApoParameterCodec.TryDecode(data, out var decoded));
        Assert(decoded!.Bands[7].FrequencyHz == 3840 && settings.Bands[7].FrequencyHz == original);
        Assert(!decoded.Bands[6].Enabled && decoded.Bands[6].GainDb == 12);
    });
    Test("Unsupported local DSP modules are explicitly enumerated", () =>
    {
        var settings = new AudioSettings(); settings.Denoise.Enabled = true; settings.Creative.PitchSemitones = 7; settings.Creative.PhoneEnabled = true;
        var unsupported = NativeApoParameterCodec.GetUnsupportedEffects(settings);
        Assert(unsupported.SequenceEqual(new[] { "Noise reduction", "Phone", "Pitch shift" }));
    });
    Test("Codec rejects malformed and non-finite parameters before writing", () =>
    {
        Throws<ArgumentOutOfRangeException>(() => NativeApoParameterCodec.Encode(new(), 48000, 0));
        Throws<ArgumentException>(() => NativeApoParameterCodec.Encode(new(), 48000, 2, sequence: 1));
        Throws<ArgumentException>(() => NativeApoParameterCodec.Encode(new() { InputGainDb = double.NaN }, 48000, 2));
        foreach (var offset in new[] { 0, 4, 8, 12, 16, 20, 24, 28, 44, 48, 52, 216, 220 })
        {
            var data = Snapshot(); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), offset == 216 ? 0u : uint.MaxValue);
            Assert(!NativeApoParameterCodec.TryDecode(data, out _), "Accepted malformed offset " + offset);
        }
        foreach (var offset in new[] { 32, 36, 40, 56, 60, 64 })
        {
            var data = Snapshot(); BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset, 4), float.NaN);
            Assert(!NativeApoParameterCodec.TryDecode(data, out _));
        }
        Assert(!NativeApoParameterCodec.TryDecode(Snapshot().AsSpan(0, 223), out _));
    });
    Test("Lease expires after three seconds and rejects future heartbeats", () =>
    {
        Assert(NativeApoParameterCodec.TryDecode(Snapshot(), out var decoded));
        Assert(decoded!.IsLeaseFresh(decoded.HeartbeatTickMs + 3000));
        Assert(!decoded.IsLeaseFresh(decoded.HeartbeatTickMs + 3001) && !decoded.IsLeaseFresh(decoded.HeartbeatTickMs - 1));
        Assert(NativeApoParameterCodec.TryDecode(NativeApoParameterCodec.Encode(new(), 48000, 2, false), out var disabled) && disabled!.IsLeaseFresh(ulong.MaxValue));
    });
    Test("Writer never creates an unprovisioned file", () =>
    {
        Throws<IOException>(() => NativeApoParameterWriter.OpenExisting(endpoint, temporary));
        Assert(!File.Exists(NativeApoPaths.ParameterFile(endpoint, temporary)));
    });
    Test("Mapped writer updates an existing view with even sequence and lease", () =>
    {
        var path = NativeApoPaths.ParameterFile(endpoint, temporary); File.WriteAllBytes(path, new byte[224]);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var mapping = MemoryMappedFile.CreateFromFile(reader, null, 224, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
        using var view = mapping.CreateViewAccessor(0, 224, MemoryMappedFileAccess.Read);
        using var writer = NativeApoParameterWriter.OpenExisting(endpoint, temporary);
        writer.Write(new() { InputGainDb = -6 }, 48000, 2, testMinus6Db: true);
        var first = Read(view); Assert(NativeApoParameterCodec.TryDecode(first, out var decoded) && decoded!.InputGainDb == -6 && decoded.TestMinus6Db && decoded.Sequence == 2 && decoded.SampleRate == 0 && decoded.Channels == 0);
        Assert(writer.RefreshLease()); var refreshed = Read(view); Assert(U(refreshed, 8) == 4 && F(refreshed, 32) == -6);
        writer.Disable(); var disabled = Read(view); Assert(U(disabled, 8) == 6 && U(disabled, 12) == 0 && U(disabled, 16) == 0 && U(disabled, 20) == 0 && !writer.RefreshLease());
        Assert(NativeApoParameterCodec.TryDecode(disabled, out _));
    });
    Test("Invalid provisioned size is never silently resized", () =>
    {
        var id = Guid.NewGuid(); var path = NativeApoPaths.ParameterFile(id, temporary); File.WriteAllBytes(path, new byte[208]);
        Throws<InvalidDataException>(() => NativeApoParameterWriter.OpenExisting(id, temporary)); Assert(new FileInfo(path).Length == 208);
    });
    Test("Fresh controller ownership cannot be silently stolen or disabled", () =>
    {
        var id = Guid.NewGuid(); var path = NativeApoPaths.ParameterFile(id, temporary);
        var bytes = Snapshot(); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(216, 4), (uint)Environment.ProcessId + 100000);
        File.WriteAllBytes(path, bytes);
        using var writer = NativeApoParameterWriter.OpenExisting(id, temporary);
        Throws<IOException>(() => writer.Write(new(), 48000, 2)); Throws<IOException>(writer.Disable); Assert(!writer.RefreshLease());
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var mapping = MemoryMappedFile.CreateFromFile(stream, null, 224, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
        using var view = mapping.CreateViewAccessor(0, 224, MemoryMappedFileAccess.Read);
        Assert(Read(view).SequenceEqual(bytes));
    });
    Test("Explicit local endpoint paths exclude empty GUID and UNC roots", () =>
    {
        Throws<ArgumentException>(() => NativeApoPaths.ParameterFile(Guid.Empty, temporary));
        Throws<ArgumentException>(() => NativeApoPaths.ParameterFile(endpoint, @"\\server\share"));
        Assert(Path.GetFileName(NativeApoPaths.ParameterFile(endpoint, temporary)) == endpoint.ToString("D") + ".bin");
    });
    Test("PE inspection rejects managed DLL x86 and malformed headers", () =>
    {
        var pe = new byte[512]; pe[0] = (byte)'M'; pe[1] = (byte)'Z'; BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(60, 4), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(64, 4), 0x4550); Set16(pe, 68, 0x8664); Set16(pe, 84, 240); Set16(pe, 86, 0x2000); Set16(pe, 88, 0x20b);
        Assert(NativeApoFileTrust.IsNativeX64Dll(pe));
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(64 + 24 + 112 + 14 * 8, 4), 0x1234); Assert(!NativeApoFileTrust.IsNativeX64Dll(pe));
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(64 + 24 + 112 + 14 * 8, 4), 0); Set16(pe, 68, 0x14c); Assert(!NativeApoFileTrust.IsNativeX64Dll(pe));
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(60, 4), int.MaxValue); Assert(!NativeApoFileTrust.IsNativeX64Dll(pe)); Assert(!NativeApoFileTrust.IsNativeX64Dll([]));
    });
    Test("Missing unsigned and foreign registrations cannot pass deployment review", () =>
    {
        var trusted = new NativeApoFileIdentity(true, true, true, new string('A', 64), "trusted", ContractExport: true);
        Assert(NativeApoDeploymentPolicy.Rejection(trusted, true, null, null, false, false) == null);
        Assert(NativeApoDeploymentPolicy.Rejection(trusted with { Trusted = false }, true, null, null, false, false) != null);
        Assert(NativeApoDeploymentPolicy.Rejection(trusted, false, null, null, false, false) != null);
        Assert(NativeApoDeploymentPolicy.Rejection(trusted, true, new(RegistryValueKind.String, Guid.NewGuid().ToString("B")), null, false, false)!.Contains("preserved"));
        Assert(NativeApoDeploymentPolicy.Rejection(trusted, true, null, new(RegistryValueKind.MultiString, Strings: ["external"]), false, false) != null);
        Assert(NativeApoDeploymentPolicy.Rejection(trusted, true, null, null, true, false) != null);
        Assert(NativeApoDeploymentPolicy.Rejection(trusted, true, null, null, false, true) != null);
    });
    Test("Component export identity rejects unrelated and malformed PE exports", () =>
    {
        var bytes = ContractImage();
        Assert(NativeApoFileTrust.IsNativeX64Dll(bytes) && NativeApoFileTrust.HasContractExports(bytes));
        var changed = bytes.ToArray(); changed[1280] = (byte)'X'; Assert(!NativeApoFileTrust.HasContractExports(changed));
        changed = bytes.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(1024 + 24, 4), uint.MaxValue); Assert(!NativeApoFileTrust.HasContractExports(changed));
        changed = bytes.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(1088, 4), uint.MaxValue); Assert(!NativeApoFileTrust.HasContractExports(changed));
        Assert(!NativeApoFileTrust.HasContractExports([]));
    });
    Test("Generic trusted Windows DLL and unsigned spoof are never loaded as APO", () =>
    {
        var generic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        var identity = NativeApoFileTrust.Inspect(generic);
        Assert(identity.Present && identity.NativeX64 && !identity.ContractExport && !identity.Trusted);
        Assert(!NativeApoFileTrust.VerifyContract(generic, identity.Sha256, out _));
        var plan = new NativeApoDeploymentService().PrepareInstallation(generic, endpoint, "S-1-5-21-1-2-3-1001");
        Assert(!plan.CanApply && plan.Message.Contains("contract"));
        var spoof = Path.Combine(temporary, "unsigned-contract.dll"); File.WriteAllBytes(spoof, ContractImage());
        identity = NativeApoFileTrust.Inspect(spoof); Assert(identity.NativeX64 && identity.ContractExport && !identity.Trusted);
        Assert(!NativeApoFileTrust.VerifyContract(spoof, identity.Sha256, out _));
    });
    Test("Plan fingerprint covers endpoint user component parameter and registry content", () =>
    {
        var sha = new string('A', 64); var destination = Path.Combine(NativeApoPaths.DefaultInstallRoot, "MicStudioApo-" + sha[..16].ToLowerInvariant() + ".dll");
        var edits = NativeApoDeploymentService.BuildEdits(endpoint, destination);
        string Finger(Guid id, string sid, string parameter, IReadOnlyList<NativeRegistryEdit> changes) => NativeApoDeploymentService.Fingerprint("source.dll", sha, id, sid, destination, parameter, changes);
        var first = Finger(endpoint, "user", "", edits);
        Assert(first == Finger(endpoint, "user", "", edits) && first != Finger(Guid.NewGuid(), "user", "", edits));
        Assert(first != Finger(endpoint, "other-user", "", edits) && first != Finger(endpoint, "user", "changed", edits));
        var changed = edits.ToList(); changed[0] = changed[0] with { After = new(RegistryValueKind.String, "modified") };
        Assert(first != Finger(endpoint, "user", "", changed));
    });
    Test("Restore manifest permits only exact owned keys and selected endpoint", () =>
    {
        var sha = new string('A', 64); var destination = Path.Combine(NativeApoPaths.DefaultInstallRoot, "MicStudioApo-" + sha[..16].ToLowerInvariant() + ".dll");
        var edits = NativeApoDeploymentService.BuildEdits(endpoint, destination);
        var backup = new NativeApoBackup(1, endpoint, destination, sha, true, NativeApoPaths.ParameterFile(endpoint), true, edits);
        Assert(NativeApoDeploymentService.ValidateOwnedEdits(backup));
        Assert(!NativeApoDeploymentService.ValidateOwnedEdits(backup with { Endpoint = Guid.NewGuid() }));
        Assert(!NativeApoDeploymentService.ValidateOwnedEdits(backup with { Destination = Path.Combine(temporary, "foreign.dll") }));
        Assert(!NativeApoDeploymentService.ValidateOwnedEdits(backup with { Sha256 = "short" }));
        var changed = edits.ToList(); changed.Add(new(@"SOFTWARE\OtherProduct", "external", null, new(RegistryValueKind.String, "bad")));
        Assert(!NativeApoDeploymentService.ValidateOwnedEdits(backup with { Edits = changed }));
        Assert(edits.Count(e => e.Key.Contains(endpoint.ToString("B"), StringComparison.OrdinalIgnoreCase)) == 2);
        Assert(!edits.Any(e => e.Key.Contains("DisableProtectedAudioDG") || e.Key.Contains("Policy")));
    });
    Test("Rejected plans perform no installation or elevation", () =>
    {
        var service = new NativeApoDeploymentService();
        var missing = service.PrepareInstallation(Path.Combine(temporary, "missing.dll"), endpoint, "S-1-5-21-1-2-3-1001");
        Assert(!missing.CanApply && missing.Message.Contains("missing"));
        var result = service.ApplyPreparedPlan(missing, "wrong"); Assert(!result.Success && !result.NeedsElevation);
        Assert(!File.Exists(Path.Combine(temporary, "missing.dll")));
    });
    Test("Parameter ACL grants one user data access without ownership or delete", () =>
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var security = NativeApoDeploymentService.ParameterSecurity(user);
        Assert(security.AreAccessRulesProtected && security.GetOwner(typeof(SecurityIdentifier))!.Value == "S-1-5-32-544");
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        var writer = rules.Single(r => r.IdentityReference.Value == user.Value);
        Assert((writer.FileSystemRights & FileSystemRights.WriteData) != 0 && (writer.FileSystemRights & (FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) == 0);
        var service = rules.Single(r => r.IdentityReference.Value == "S-1-5-19");
        Assert((service.FileSystemRights & FileSystemRights.ReadData) != 0 && (service.FileSystemRights & FileSystemRights.WriteData) == 0);
    });
    Test("Installed DLL and backup descriptors are administrator-owned and user-read-only", () =>
    {
        foreach (var executable in new[] { false, true })
        {
            var security = NativeApoDeploymentService.ReadOnlySecurity(executable);
            Assert(security.AreAccessRulesProtected && security.GetOwner(typeof(SecurityIdentifier))!.Value == "S-1-5-32-544");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                if (rule.IdentityReference.Value is "S-1-5-32-545" or "S-1-5-19")
                    Assert((rule.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.Delete)) == 0);
        }
    });
    Test("Operation reports use only fixed request GUID paths", () =>
    {
        Throws<ArgumentException>(() => NativeApoPaths.OperationReport(Guid.Empty));
        var request = Guid.NewGuid(); var path = NativeApoPaths.OperationReport(request);
        Assert(Path.GetDirectoryName(path) == Path.GetFullPath(NativeApoPaths.DefaultReportRoot) && Path.GetFileName(path) == request.ToString("D") + ".json");
        var service = new NativeApoDeploymentService();
        Throws<ArgumentException>(() => service.WriteOperationReport(Guid.Empty, "S-1-5-21-1-2-3-1001", new(false, false, "fixture")));
        Throws<ArgumentException>(() => service.WriteOperationReport(request, "S-1-5-32-544", new(false, false, "fixture")));
    });
    Test("Operation report ACL grants only the chosen user read access", () =>
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001"); var security = NativeApoDeploymentService.OperationReportSecurity(user);
        Assert(security.AreAccessRulesProtected && security.GetOwner(typeof(SecurityIdentifier))!.Value == "S-1-5-32-544");
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert(rules.Count == 3 && rules.All(r => r.IdentityReference.Value != "S-1-5-32-545"));
        var reader = rules.Single(r => r.IdentityReference.Value == user.Value);
        Assert((reader.FileSystemRights & FileSystemRights.ReadData) != 0 && (reader.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.ChangePermissions | FileSystemRights.Delete | FileSystemRights.TakeOwnership)) == 0);
    });
    Test("Operation reports roundtrip the reviewed result and backup hash", () =>
    {
        var report = new NativeApoOperationReport(Guid.NewGuid(), "S-1-5-21-1-2-3-1001", DateTimeOffset.UtcNow,
            new(true, false, "fixture successful", "owned-backup.json"), new string('A', 64));
        var json = System.Text.Json.JsonSerializer.Serialize(report);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<NativeApoOperationReport>(json);
        Assert(parsed == report && parsed!.BackupSha256 == new string('A', 64) && parsed.Result.Success);
    });
    Test("Report reader rejects user ownership inheritance and any outside write ACL", () =>
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var security = NativeApoDeploymentService.OperationReportSecurity(user);
        Assert(NativeApoDeploymentService.ReportSecurityAllowsRead(security, user));
        security.SetOwner(user); Assert(!NativeApoDeploymentService.ReportSecurityAllowsRead(security, user));
        security = NativeApoDeploymentService.OperationReportSecurity(user); security.SetAccessRuleProtection(false, true);
        Assert(!NativeApoDeploymentService.ReportSecurityAllowsRead(security, user));
        security = NativeApoDeploymentService.OperationReportSecurity(user);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.WriteData, AccessControlType.Allow));
        Assert(!NativeApoDeploymentService.ReportSecurityAllowsRead(security, user));
        security = NativeApoDeploymentService.OperationReportSecurity(user);
        Assert(!NativeApoDeploymentService.ReportSecurityAllowsRead(security, new SecurityIdentifier("S-1-5-21-1-2-3-1002")));
    });
    Test("Report reader validates operation user payload bounds and strict schema", () =>
    {
        var id = Guid.NewGuid(); var sid = "S-1-5-21-1-2-3-1001";
        var report = new NativeApoOperationReport(id, sid, DateTimeOffset.UtcNow, new(true, false, "fixture"), new string('A', 64));
        byte[] Bytes(NativeApoOperationReport value) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value);
        Assert(NativeApoDeploymentService.ParseOperationReport(Bytes(report), id, sid) == report);
        Throws<InvalidDataException>(() => NativeApoDeploymentService.ParseOperationReport(Bytes(report), Guid.NewGuid(), sid));
        Throws<InvalidDataException>(() => NativeApoDeploymentService.ParseOperationReport(Bytes(report), id, "other-user"));
        Throws<InvalidDataException>(() => NativeApoDeploymentService.ParseOperationReport(Bytes(report with { BackupSha256 = "not-a-hash" }), id, sid));
        Throws<InvalidDataException>(() => NativeApoDeploymentService.ParseOperationReport(Bytes(report with { Result = null! }), id, sid));
        Throws<InvalidDataException>(() => NativeApoDeploymentService.ParseOperationReport(new byte[65537], id, sid));
        var extra = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(Bytes(report)).TrimEnd('}') + ",\"Unexpected\":true}");
        Throws<System.Text.Json.JsonException>(() => NativeApoDeploymentService.ParseOperationReport(extra, id, sid));
    });
    Console.WriteLine($"Native APO managed bridge verification: {count} passed. No system registration, ACL provisioning, UAC, capture or playback was performed.");
}
finally
{
    foreach (var file in Directory.EnumerateFiles(temporary)) File.Delete(file);
    Directory.Delete(temporary, false);
}

static uint U(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
static float F(byte[] bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset, 4));
static byte[] Read(MemoryMappedViewAccessor view) { var bytes = new byte[224]; view.ReadArray(0, bytes, 0, bytes.Length); return bytes; }
static void Set16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
static byte[] ContractImage()
{
    var bytes = new byte[2048]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
    void U(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    U(60, 128); U(128, 0x4550); Set16(bytes, 132, 0x8664); Set16(bytes, 134, 1); Set16(bytes, 148, 240); Set16(bytes, 150, 0x2000); Set16(bytes, 152, 0x20b);
    U(128 + 24 + 112, 0x1000); U(392 + 12, 0x1000); U(392 + 16, 1024); U(392 + 20, 1024);
    U(1024 + 24, 3); U(1024 + 32, 0x1040);
    U(1088, 0x1100); U(1092, 0x1140); U(1096, 0x1180);
    System.Text.Encoding.ASCII.GetBytes("DllGetClassObject\0").CopyTo(bytes, 1280);
    System.Text.Encoding.ASCII.GetBytes("DllCanUnloadNow\0").CopyTo(bytes, 1344);
    System.Text.Encoding.ASCII.GetBytes("MicStudioApoGetContract\0").CopyTo(bytes, 1408);
    return bytes;
}
