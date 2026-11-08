namespace MicStudio.WindowsAudio;

public sealed record BackupMicrophone(string Id, string Name, int SampleRate, int Channels);

/// <summary>Local preferences for explicitly chosen capture endpoints. No device access occurs here.</summary>
public sealed class AutomaticInputPreferences
{
    public const int MaximumBackups = 8;
    public const int MaximumExcludedInputs = 64;
    public const int MaximumEndpointIdLength = 2048;
    public const int MaximumDeviceNameLength = 256;

    public bool UseBackups { get; set; }
    public List<BackupMicrophone> Backups { get; set; } = [];
    public List<string> ExcludedInputIds { get; set; } = [];

    public bool IsExcluded(string? endpointId) => endpointId != null &&
        ExcludedInputIds.Contains(endpointId, StringComparer.Ordinal);

    public void Normalize()
    {
        if (Backups == null || Backups.Count > MaximumBackups || ExcludedInputIds == null ||
            ExcludedInputIds.Count > MaximumExcludedInputs)
            throw new ArgumentException("Invalid automatic microphone preferences.");

        var backupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var backup in Backups)
        {
            ValidateBackup(backup);
            if (!backupIds.Add(backup.Id)) throw new ArgumentException("A backup microphone is duplicated.");
        }

        var excludedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpointId in ExcludedInputIds)
        {
            ValidateEndpointId(endpointId);
            if (!excludedIds.Add(endpointId)) throw new ArgumentException("An excluded microphone is duplicated.");
        }
    }

    public AutomaticInputPreferences Clone()
    {
        Normalize();
        return new()
        {
            UseBackups = UseBackups,
            Backups = [.. Backups],
            ExcludedInputIds = [.. ExcludedInputIds]
        };
    }

    public static void ValidateBackup(BackupMicrophone backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ValidateEndpointId(backup.Id);
        if (string.IsNullOrWhiteSpace(backup.Name) || backup.Name.Length > MaximumDeviceNameLength ||
            backup.Name.Any(char.IsControl))
            throw new ArgumentException("A backup microphone name is invalid.");
        if (backup.SampleRate is < 8000 or > 384000 || backup.Channels is < 1 or > 8)
            throw new ArgumentException("A backup microphone format is invalid.");
    }

    public static void ValidateEndpointId(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId) || endpointId.Length > MaximumEndpointIdLength ||
            endpointId.Any(char.IsControl))
            throw new ArgumentException("An explicit microphone endpoint ID is required.");
    }
}
