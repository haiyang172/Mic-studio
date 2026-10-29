using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MicStudio.NativeApo;

public sealed record NativeApoStatus(NativeApoFileIdentity Component, bool Registered, bool Bound,
    bool ParametersReady, bool EndpointAvailable, string Message)
{
    public string AudioPathVerification => "Not verified";
}
public sealed record NativeApoOperationResult(bool Success, bool NeedsElevation, string Message, string? BackupFile = null);
public sealed record NativeApoOperationReport(Guid RequestId, string WriterSid, DateTimeOffset CreatedAt,
    NativeApoOperationResult Result, string? BackupSha256, string? BackupInspectionError = null);

public sealed class NativeApoInstallPlan
{
    internal NativeApoInstallPlan() { }
    public bool CanApply { get; internal init; }
    public string Message { get; internal init; } = "";
    public string PreviewText { get; internal init; } = "";
    public string Fingerprint { get; internal init; } = "";
    public string SourceDll { get; internal init; } = "";
    public string SourceSha256 { get; internal init; } = "";
    public Guid Endpoint { get; internal init; }
    public string WriterSid { get; internal init; } = "";
    internal IReadOnlyList<NativeRegistryEdit> Edits { get; init; } = [];
    internal string Destination { get; init; } = "";
    internal string ParameterHash { get; init; } = "";
}

public sealed record NativeRegistryData(RegistryValueKind Kind, string? Text = null, int? Number = null, string[]? Strings = null)
{
    internal object Value => Kind switch { RegistryValueKind.String or RegistryValueKind.ExpandString => Text!, RegistryValueKind.DWord => Number!.Value, RegistryValueKind.MultiString => Strings!, _ => throw new InvalidDataException("Unsupported registry value kind.") };
    internal static NativeRegistryData? Read(RegistryKey? key, string name)
    {
        if (key == null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return key.GetValueKind(name) switch
        {
            RegistryValueKind.String => new(RegistryValueKind.String, (string?)value),
            RegistryValueKind.ExpandString => new(RegistryValueKind.ExpandString, (string?)value),
            RegistryValueKind.DWord => new(RegistryValueKind.DWord, Number: (int?)value),
            RegistryValueKind.MultiString => new(RegistryValueKind.MultiString, Strings: (string[]?)value),
            _ => throw new InvalidDataException("An existing APO registry value has an unsupported type; refusing to replace it.")
        };
    }
}
internal sealed record NativeRegistryEdit(string Key, string Name, NativeRegistryData? Before, NativeRegistryData After);
internal sealed record NativeApoBackup(int Schema, Guid Endpoint, string Destination, string Sha256, bool DllCreated,
    string ParameterFile, bool ParameterCreated, List<NativeRegistryEdit> Edits);

/// <summary>Explicit signed-component deployment. Merely constructing or inspecting this service performs no writes.</summary>
public sealed class NativeApoDeploymentService
{
    public static readonly Guid ClassId = new("6843D3F0-EA67-4B0C-AB63-90A28CF45578");
    public const string StreamEffectProperty = "{D04E05A6-594B-4fb6-A80D-01AF5EED7D1D},5";
    public const string ProcessingModesProperty = "{D3993A3F-99C2-4402-B5EC-A92A0367664B},5";
    private const string DefaultMode = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
    private const string CommunicationsMode = "{98951333-B9CD-48B1-A0A3-FF40682D73F7}";
    private static string ComKey => @"SOFTWARE\Classes\CLSID\" + ClassId.ToString("B");
    private static string AudioKey => @"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\" + ClassId.ToString("B");
    private static string EndpointKey(Guid endpoint) => @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\" + endpoint.ToString("B");
    private static string BackupRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MicStudio", "APO", "Backups");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = false };

    /// <summary>Writes one privileged result at a fixed GUID-derived path. No caller-supplied output path is accepted.</summary>
    public string WriteOperationReport(Guid requestId, string writerSid, NativeApoOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var path = NativeApoPaths.OperationReport(requestId);
        var writer = new SecurityIdentifier(writerSid);
        if (!writer.IsAccountSid()) throw new ArgumentException("The operation report reader must be a specific Windows user.", nameof(writerSid));
        if (!IsElevated()) throw new UnauthorizedAccessException("Only the explicitly elevated APO helper can provision an operation report.");
        string? backupHash = null, backupError = null;
        if (!string.IsNullOrEmpty(result.BackupFile))
        {
            var backup = Path.GetFullPath(result.BackupFile); NativeApoPaths.RejectReparseParents(backup);
            if (!backup.StartsWith(BackupRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(backup) != ".json")
                throw new ArgumentException("Only an owned native APO backup can be included in an operation report.", nameof(result));
            try
            {
                using var file = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length > 65536) throw new InvalidDataException("The owned backup exceeds its size limit.");
                backupHash = Convert.ToHexString(SHA256.HashData(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { backupError = "The operation backup could not be inspected: " + ex.Message; }
        }
        ProtectDirectory(Path.GetDirectoryName(Path.GetDirectoryName(NativeApoPaths.DefaultReportRoot)!)!, null);
        ProtectDirectory(Path.GetDirectoryName(NativeApoPaths.DefaultReportRoot)!, null);
        ProtectDirectory(NativeApoPaths.DefaultReportRoot, writer);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new NativeApoOperationReport(requestId, writerSid, DateTimeOffset.UtcNow, result, backupHash, backupError), Json);
        if (bytes.Length > 65536) throw new InvalidDataException("The operation report exceeds its size limit.");
        // CREATE_NEW refuses an existing file, including a final-component link.
        // The parent is admin-owned and permits no unprivileged child creation.
        // Apply the restrictive descriptor atomically when creating the file;
        // no inherited-readable interval exposes its result to other users.
        using (var file = FileSystemAclExtensions.Create(new FileInfo(path), FileMode.CreateNew, FileSystemRights.Write,
                   FileShare.None, 4096, FileOptions.None, OperationReportSecurity(writer)))
        {
            file.Write(bytes); file.Flush(true);
        }
        return path;
    }

    /// <summary>Reads a result only after proving that an unprivileged writer cannot forge or replace it.</summary>
    public NativeApoOperationReport ReadOperationReport(Guid requestId, string writerSid)
    {
        var path = NativeApoPaths.OperationReport(requestId);
        var reader = new SecurityIdentifier(writerSid);
        if (!reader.IsAccountSid()) throw new ArgumentException("A specific report reader is required.", nameof(writerSid));
        var root = NativeApoPaths.DefaultReportRoot;
        foreach (var directory in new[] { Path.GetDirectoryName(Path.GetDirectoryName(root)!)!, Path.GetDirectoryName(root)!, root })
        {
            NativeApoPaths.RejectReparseParents(directory);
            if (!Directory.Exists(directory) || !PrivilegedFileSecurity(new DirectoryInfo(directory).GetAccessControl()))
                throw new IOException("The operation report directory is not protected by an administrator.");
        }
        using var file = NativeApoPaths.OpenExistingFileNoReparse(path, writable: false);
        var security = new FileInfo(path).GetAccessControl();
        if (!ReportSecurityAllowsRead(security, reader)) throw new IOException("The operation report does not have a protected administrator-owned read-only descriptor.");
        if (file.Length is <= 0 or > 65536) throw new InvalidDataException("The operation report size is invalid.");
        var bytes = new byte[file.Length]; file.ReadExactly(bytes);
        return ParseOperationReport(bytes, requestId, writerSid);
    }

    internal static NativeApoOperationReport ParseOperationReport(ReadOnlySpan<byte> bytes, Guid requestId, string writerSid)
    {
        if (bytes.Length is <= 0 or > 65536) throw new InvalidDataException("The operation report size is invalid.");
        var options = new JsonSerializerOptions(Json) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 12 };
        var report = JsonSerializer.Deserialize<NativeApoOperationReport>(bytes, options) ?? throw new InvalidDataException("The operation report is empty.");
        if (requestId == Guid.Empty || report.RequestId != requestId || report.WriterSid != writerSid || report.CreatedAt == default || report.Result == null
            || string.IsNullOrWhiteSpace(report.Result.Message) || report.Result.Message.Length > 32768
            || (report.BackupSha256 != null && (report.BackupSha256.Length != 64 || report.BackupSha256.Any(c => !Uri.IsHexDigit(c)))))
            throw new InvalidDataException("The operation report does not match the requested operation/user or is malformed.");
        return report;
    }

    internal static bool PrivilegedFileSecurity(FileSystemSecurity security)
    {
        if (!security.AreAccessRulesProtected || security.GetOwner(typeof(SecurityIdentifier))?.Value is not ("S-1-5-18" or "S-1-5-32-544")) return false;
        const FileSystemRights mutation = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544") && (rule.FileSystemRights & mutation) != 0) return false;
        return true;
    }

    internal static bool ReportSecurityAllowsRead(FileSecurity security, SecurityIdentifier reader)
    {
        if (!PrivilegedFileSecurity(security)) return false;
        var granted = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference.Value != reader.Value || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            if (rule.AccessControlType == AccessControlType.Deny && (rule.FileSystemRights & FileSystemRights.ReadData) != 0) return false;
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & FileSystemRights.ReadData) != 0) granted = true;
        }
        return granted;
    }

    public NativeApoStatus Inspect(string sourceDll, Guid endpoint)
    {
        var identity = NativeApoFileTrust.Inspect(sourceDll);
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var com = machine.OpenSubKey(ComKey + @"\InprocServer32");
            using var owner = machine.OpenSubKey(ComKey);
            using var audio = machine.OpenSubKey(AudioKey);
            using var device = machine.OpenSubKey(EndpointKey(endpoint));
            using var fx = machine.OpenSubKey(EndpointKey(endpoint) + @"\FxProperties");
            var registeredPath = com?.GetValue("") as string;
            var registered = owner?.GetValue("MicStudioOwner") as string == "NativeAPO-v2" && audio != null && registeredPath != null
                && Path.GetFullPath(registeredPath).StartsWith(NativeApoPaths.DefaultInstallRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                && File.Exists(registeredPath) && NativeApoFileTrust.Inspect(registeredPath) is { Trusted: true, NativeX64: true };
            var bound = IsOwnEffect(NativeRegistryData.Read(fx, StreamEffectProperty));
            var parameter = NativeApoPaths.ParameterFile(endpoint);
            var ready = File.Exists(parameter) && new FileInfo(parameter).Length == NativeApoParameterCodec.Size;
            var available = device?.GetValue("DeviceState") is int state && state == 1;
            var message = !identity.Present ? identity.Message : !identity.Trusted ? identity.Message : !available ? "The selected capture endpoint is unavailable." : !registered ? "Native APO is not registered." : !bound ? "Native APO is not bound to this endpoint." : !ready ? "The per-endpoint parameter file is missing." : "Native APO is registered and bound; Windows loading and actual audio response remain unverified.";
            return new(identity, registered, bound, ready, available, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException)
        { return new(identity, false, false, false, false, "Native APO status is unavailable: " + ex.Message); }
    }

    public NativeApoInstallPlan PrepareInstallation(string sourceDll, Guid endpoint, string writerSid)
    {
        try
        {
            if (endpoint == Guid.Empty) return Rejected("Select an explicit capture endpoint.");
            var sid = new SecurityIdentifier(writerSid);
            if (!sid.IsAccountSid()) return Rejected("The parameter writer must be a specific Windows user.");
            var source = Path.GetFullPath(sourceDll);
            var identity = NativeApoFileTrust.Inspect(source);
            if (!identity.Present || !identity.NativeX64 || !identity.Trusted || !identity.ContractExport) return Rejected(identity.Message);
            using var sourceLock = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!NativeApoFileTrust.VerifyContract(source, identity.Sha256, out var contractMessage)) return Rejected(contractMessage);
            NativeApoPaths.RejectReparseParents(NativeApoPaths.DefaultInstallRoot);
            NativeApoPaths.RejectReparseParents(BackupRoot);
            var parameterFile = NativeApoPaths.ParameterFile(endpoint);
            var parameterHash = "";
            if (File.Exists(parameterFile))
            {
                var parameterBytes = ReadParameterBytes(parameterFile);
                if (parameterBytes.Length != NativeApoParameterCodec.Size || !NativeApoParameterCodec.TryDecode(parameterBytes, out var previous) || previous is not { Enabled: false })
                    return Rejected("An active or invalid parameter file already exists. Disable and review the previous deployment before reinstalling.");
                parameterHash = Convert.ToHexString(SHA256.HashData(parameterBytes));
            }
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var device = machine.OpenSubKey(EndpointKey(endpoint));
            using var fx = machine.OpenSubKey(EndpointKey(endpoint) + @"\FxProperties");
            var effect = NativeRegistryData.Read(fx, StreamEffectProperty);
            var modes = NativeRegistryData.Read(fx, ProcessingModesProperty);
            using var ownCom = machine.OpenSubKey(ComKey);
            using var ownAudio = machine.OpenSubKey(AudioKey);
            var rejection = NativeApoDeploymentPolicy.Rejection(identity, device?.GetValue("DeviceState") is int state && state == 1, effect, modes, ownCom != null, ownAudio != null);
            if (rejection != null) return Rejected(rejection);
            var destination = Path.Combine(NativeApoPaths.DefaultInstallRoot, "MicStudioApo-" + identity.Sha256[..16].ToLowerInvariant() + ".dll");
            var edits = BuildEdits(endpoint, destination);
            foreach (var edit in edits)
            {
                using var key = machine.OpenSubKey(edit.Key);
                if (NativeRegistryData.Read(key, edit.Name) != null) return Rejected("A registration changed while preparing the plan. Refresh and review again.");
            }
            var fingerprint = Fingerprint(source, identity.Sha256, endpoint, writerSid, destination, parameterHash, edits);
            return new() { CanApply = true, SourceDll = source, SourceSha256 = identity.Sha256, Endpoint = endpoint, WriterSid = writerSid,
                Destination = destination, ParameterHash = parameterHash, Edits = edits, Fingerprint = fingerprint, Message = "Signed native APO is ready for explicit administrator installation; actual loading is not yet verified.",
                PreviewText = $"Native DLL: {source}\nSHA-256: {identity.Sha256}\nInstall DLL: {destination}\nCapture endpoint: {endpoint:D}\nCLSID: {ClassId:B}\nParameter writer SID: {writerSid}\nParameter file: {parameterFile}\nRegister one SFX for Default/Communications. Existing endpoint effects are never replaced.\nCreate a protected backup and disabled parameter file. No audio service restart, capture, system defaults change, or protected-audio policy change.\nReview fingerprint: {fingerprint}" };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Rejected("Cannot prepare native APO installation: " + ex.Message); }
    }

    public NativeApoOperationResult ApplyPreparedPlan(NativeApoInstallPlan plan, string expectedFingerprint)
    {
        if (!plan.CanApply || string.IsNullOrEmpty(expectedFingerprint) || plan.Fingerprint != expectedFingerprint) return new(false, false, "The reviewed installation plan is invalid.");
        if (!IsElevated()) return new(false, true, "Native APO installation requires explicit Windows administrator approval.");
        var fresh = PrepareInstallation(plan.SourceDll, plan.Endpoint, plan.WriterSid);
        if (!fresh.CanApply || fresh.Fingerprint != expectedFingerprint) return new(false, false, "The component or endpoint changed after preview. Nothing was installed. " + fresh.Message);
        var parameter = NativeApoPaths.ParameterFile(plan.Endpoint);
        string? backupFile = null; bool copied = false, createdParameter = false;
        var applied = new List<NativeRegistryEdit>();
        try
        {
            ProtectDirectory(Path.GetDirectoryName(NativeApoPaths.DefaultInstallRoot)!, null);
            ProtectDirectory(Path.GetDirectoryName(Path.GetDirectoryName(NativeApoPaths.DefaultParameterRoot)!)!, null);
            ProtectDirectory(Path.GetDirectoryName(NativeApoPaths.DefaultParameterRoot)!, null);
            ProtectDirectory(NativeApoPaths.DefaultInstallRoot, null);
            ProtectDirectory(NativeApoPaths.DefaultParameterRoot, new SecurityIdentifier(plan.WriterSid));
            ProtectDirectory(BackupRoot, null);
            if (File.Exists(plan.Destination))
            { if (NativeApoFileTrust.Inspect(plan.Destination).Sha256 != plan.SourceSha256) return new(false, false, "An existing component file differs; it was preserved."); }
            else { File.Copy(plan.SourceDll, plan.Destination, false); copied = true; }
            new FileInfo(plan.Destination).SetAccessControl(ReadOnlySecurity(executable: true));
            if (NativeApoFileTrust.Inspect(plan.Destination) is not { Trusted: true, ContractExport: true } installed || installed.Sha256 != plan.SourceSha256
                || !NativeApoFileTrust.VerifyContract(plan.Destination, plan.SourceSha256, out _)) throw new IOException("Copied native component failed integrity/signature/contract validation.");
            var initial = NativeApoParameterCodec.Encode(new(), 48000, 2, enabled: false);
            if (string.IsNullOrEmpty(plan.ParameterHash))
            { using (var file = new FileStream(parameter, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Write(initial); file.Flush(true); } createdParameter = true; }
            else if (Convert.ToHexString(SHA256.HashData(ReadParameterBytes(parameter))) != plan.ParameterHash) throw new IOException("The parameter file changed after review.");
            ProtectParameterFile(parameter, new SecurityIdentifier(plan.WriterSid));
            backupFile = Path.Combine(BackupRoot, plan.Endpoint.ToString("D") + "-" + Guid.NewGuid().ToString("N") + ".json");
            var backup = new NativeApoBackup(1, plan.Endpoint, plan.Destination, plan.SourceSha256, copied, parameter, createdParameter, plan.Edits.ToList());
            using (var file = new FileStream(backupFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { JsonSerializer.Serialize(file, backup, Json); file.Flush(true); }
            new FileInfo(backupFile).SetAccessControl(ReadOnlySecurity(executable: false));
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var edit in plan.Edits)
            {
                using var key = machine.CreateSubKey(edit.Key, true);
                if (NativeRegistryData.Read(key, edit.Name) != null) throw new IOException("A registration changed during installation; rolling back owned values.");
                key.SetValue(edit.Name, edit.After.Value, edit.After.Kind); applied.Add(edit);
            }
            return new(true, false, "Native APO was registered and bound. Reopen the audio stream explicitly and verify actual loading/response; no sound verification has been claimed.", backupFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            var rollback = RestoreEdits(applied);
            if (createdParameter && File.Exists(parameter)) TryDeleteUnchanged(parameter, SHA256.HashData(NativeApoParameterCodec.Encode(new(), 48000, 2, false)));
            if (copied && File.Exists(plan.Destination)) TryDeleteUnchanged(plan.Destination, Convert.FromHexString(plan.SourceSha256));
            return new(false, false, "Installation failed; owned changes were rolled back where unchanged. " + ex.Message + (rollback ? "" : " Some changed values were preserved for review."), backupFile);
        }
    }

    public NativeApoOperationResult Restore(string backupFile, string expectedBackupSha256)
    {
        if (!IsElevated()) return new(false, true, "Restoring native APO registration requires Windows administrator approval.");
        try
        {
            backupFile = Path.GetFullPath(backupFile); NativeApoPaths.RejectReparseParents(backupFile);
            if (!backupFile.StartsWith(BackupRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(backupFile) != ".json") return new(false, false, "Only an owned APO backup can be restored.");
            using var backupStream = new FileStream(backupFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (backupStream.Length > 65536) return new(false, false, "The backup exceeds the size limit.");
            var bytes = new byte[backupStream.Length]; backupStream.ReadExactly(bytes);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedBackupSha256, StringComparison.OrdinalIgnoreCase)) return new(false, false, "The reviewed backup changed.");
            var backup = JsonSerializer.Deserialize<NativeApoBackup>(bytes, Json) ?? throw new InvalidDataException("Invalid backup.");
            if (backup.Schema != 1 || backup.Endpoint == Guid.Empty || backup.Edits == null || backup.Edits.Count > 32 || !ValidateOwnedEdits(backup)) return new(false, false, "Backup ownership validation failed.");
            using (var writer = NativeApoParameterWriter.OpenExisting(backup.Endpoint)) writer.Disable();
            var restored = RestoreEdits(backup.Edits);
            return new(restored, false, restored ? "Owned native APO binding/registration restored. Component files and personal settings were retained. Reopen the stream explicitly." : "Some registry values changed externally and were preserved; review the backup before continuing.", backupFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or JsonException)
        { return new(false, false, "Cannot restore native APO: " + ex.Message); }
    }

    internal static List<NativeRegistryEdit> BuildEdits(Guid endpoint, string destination)
    {
        var values = new List<NativeRegistryEdit>();
        void S(string key, string name, string text) => values.Add(new(key, name, null, new(RegistryValueKind.String, text)));
        void D(string name, int number) => values.Add(new(AudioKey, name, null, new(RegistryValueKind.DWord, Number: number)));
        S(ComKey, "", "Mic Studio Native Microphone APO"); S(ComKey, "MicStudioOwner", "NativeAPO-v2");
        S(ComKey + @"\InprocServer32", "", destination); S(ComKey + @"\InprocServer32", "ThreadingModel", "Both");
        S(AudioKey, "FriendlyName", "Mic Studio Native Microphone APO"); S(AudioKey, "Copyright", "Mic Studio");
        D("MajorVersion", 1); D("MinorVersion", 0); D("Flags", 0x0e); D("MinInputConnections", 1); D("MaxInputConnections", 1);
        D("MinOutputConnections", 1); D("MaxOutputConnections", 1); D("MaxInstances", -1); D("NumAPOInterfaces", 1);
        S(AudioKey, "APOInterface0", "{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}");
        S(EndpointKey(endpoint) + @"\FxProperties", StreamEffectProperty, ClassId.ToString("B"));
        values.Add(new(EndpointKey(endpoint) + @"\FxProperties", ProcessingModesProperty, null, new(RegistryValueKind.MultiString, Strings: [DefaultMode, CommunicationsMode])));
        return values;
    }

    internal static bool ValidateOwnedEdits(NativeApoBackup backup)
    {
        if (backup.Sha256 is not { Length: 64 } || backup.Sha256.Any(c => !Uri.IsHexDigit(c))) return false;
        var destination = Path.GetFullPath(backup.Destination);
        if (!destination.StartsWith(NativeApoPaths.DefaultInstallRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(destination).Equals("MicStudioApo-" + backup.Sha256[..16].ToLowerInvariant() + ".dll", StringComparison.Ordinal)) return false;
        var expected = BuildEdits(backup.Endpoint, destination);
        return backup.Edits.Count == expected.Count && JsonSerializer.Serialize(backup.Edits, Json) == JsonSerializer.Serialize(expected, Json) && backup.ParameterFile == NativeApoPaths.ParameterFile(backup.Endpoint);
    }
    private static bool RestoreEdits(IEnumerable<NativeRegistryEdit> edits)
    {
        var success = true;
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var edit in edits.Reverse())
        {
            try
            {
                using (var key = machine.OpenSubKey(edit.Key, true))
                {
                    var current = NativeRegistryData.Read(key, edit.Name);
                    if (current == null) continue;
                    if (JsonSerializer.Serialize(current, Json) != JsonSerializer.Serialize(edit.After, Json)) { success = false; continue; }
                    if (edit.Before == null) key!.DeleteValue(edit.Name, false); else key!.SetValue(edit.Name, edit.Before.Value, edit.Before.Kind);
                }
                if (edit.Key.StartsWith(ComKey, StringComparison.OrdinalIgnoreCase) || edit.Key == AudioKey)
                { using var remaining = machine.OpenSubKey(edit.Key); if (remaining is { ValueCount: 0, SubKeyCount: 0 }) { remaining.Dispose(); machine.DeleteSubKey(edit.Key, false); } }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { success = false; }
        }
        return success;
    }
    private static void ProtectDirectory(string path, SecurityIdentifier? writer)
    {
        NativeApoPaths.RejectReparseParents(path);
        const string ownerText = "MicStudio Native APO v2";
        var marker = Path.Combine(path, ".micstudio-native-owner");
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            var owner = new DirectoryInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value;
            NativeApoPaths.RejectReparseParents(marker);
            if (!File.Exists(marker) || new FileInfo(marker).Length > 128 || File.ReadAllText(marker) != ownerText || owner is not ("S-1-5-18" or "S-1-5-32-544"))
                throw new IOException("An existing unowned directory was preserved: " + path);
        }
        var directory = Directory.CreateDirectory(path); NativeApoPaths.RejectReparseParents(path);
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" }) security.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var sid in new[] { "S-1-5-32-545", "S-1-5-19" }) security.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (writer != null) security.AddAccessRule(new(writer, FileSystemRights.ReadAndExecute, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
        if (!File.Exists(marker)) using (var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { var bytes = Encoding.UTF8.GetBytes(ownerText); file.Write(bytes); file.Flush(true); }
        new FileInfo(marker).SetAccessControl(ReadOnlySecurity(executable: false));
    }
    private static void ProtectParameterFile(string path, SecurityIdentifier writer)
    {
        new FileInfo(path).SetAccessControl(ParameterSecurity(writer));
    }
    internal static FileSecurity ParameterSecurity(SecurityIdentifier writer)
    {
        var security = new FileSecurity(); security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" }) security.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier("S-1-5-19"), FileSystemRights.Read, AccessControlType.Allow));
        security.AddAccessRule(new(writer, FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Synchronize, AccessControlType.Allow));
        return security;
    }
    internal static FileSecurity ReadOnlySecurity(bool executable)
    {
        var security = new FileSecurity(); security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" }) security.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
        foreach (var sid in new[] { "S-1-5-32-545", "S-1-5-19" }) security.AddAccessRule(new(new SecurityIdentifier(sid), executable ? FileSystemRights.ReadAndExecute : FileSystemRights.Read, AccessControlType.Allow));
        return security;
    }
    internal static FileSecurity OperationReportSecurity(SecurityIdentifier reader)
    {
        var security = new FileSecurity(); security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" }) security.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(reader, FileSystemRights.Read, AccessControlType.Allow));
        return security;
    }
    public static bool IsElevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private static bool IsOwnEffect(NativeRegistryData? data) => data?.Text != null && Guid.TryParse(data.Text, out var value) && value == ClassId;
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string Fingerprint(string source, string sha256, Guid endpoint, string writerSid, string destination, string parameterHash, IReadOnlyList<NativeRegistryEdit> edits) =>
        Hash(JsonSerializer.Serialize(new { Source = source, Sha256 = sha256, Endpoint = endpoint, WriterSid = writerSid, Destination = destination, ParameterHash = parameterHash, Edits = edits }, Json));
    private static NativeApoInstallPlan Rejected(string message) => new() { Message = message, PreviewText = message };
    private static void TryDeleteUnchanged(string path, byte[] hash)
    { try { NativeApoPaths.RejectReparseParents(path); if (SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(hash)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    private static byte[] ReadParameterBytes(string path)
    {
        NativeApoPaths.RejectReparseParents(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != NativeApoParameterCodec.Size) throw new InvalidDataException("The parameter file size is invalid.");
        var bytes = new byte[NativeApoParameterCodec.Size]; stream.ReadExactly(bytes); return bytes;
    }
}
