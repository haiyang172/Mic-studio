using System.Text.Json;
using MicStudio.WindowsAudio;

internal static class AutomaticInputPreferencesChecks
{
    public static int Run()
    {
        var count = 0;
        static void Assert(bool condition)
        {
            if (!condition) throw new Exception("Automatic microphone preference check failed.");
        }
        static void Reject(AutomaticInputPreferences preferences)
        {
            try { preferences.Normalize(); }
            catch (ArgumentException) { return; }
            throw new Exception("Invalid automatic microphone preferences were accepted.");
        }
        void Test(string name, Action action)
        {
            action();
            count++;
            Console.WriteLine("PASS " + name);
        }
        static BackupMicrophone Backup(string id) => new(id, "Test microphone", 48000, 2);

        Test("Automatic input preferences default to an empty disabled selection", () =>
        {
            var preferences = new AutomaticInputPreferences();
            preferences.Normalize();
            Assert(!preferences.UseBackups && preferences.Backups.Count == 0 &&
                preferences.ExcludedInputIds.Count == 0 && !preferences.IsExcluded(null));
            var older = JsonSerializer.Deserialize<AutomaticInputPreferences>("{}")!;
            older.Normalize();
            Assert(!older.UseBackups && older.Backups.Count == 0 && older.ExcludedInputIds.Count == 0);
        });
        Test("Preference JSON retains explicit backup order formats and exclusions", () =>
        {
            var preferences = new AutomaticInputPreferences
            {
                UseBackups = true,
                Backups = [Backup("first"), new("second", "第二个麦克风", 16000, 1)],
                ExcludedInputIds = ["excluded", "first"]
            };
            var restored = JsonSerializer.Deserialize<AutomaticInputPreferences>(JsonSerializer.Serialize(preferences))!;
            restored.Normalize();
            Assert(restored.UseBackups && restored.Backups.Select(x => x.Id).SequenceEqual(new[] { "first", "second" }) &&
                restored.Backups[1].SampleRate == 16000 && restored.Backups[1].Channels == 1 && restored.IsExcluded("first"));
        });
        Test("Cloned preferences can be edited without changing the running snapshot", () =>
        {
            var original = new AutomaticInputPreferences { UseBackups = true, Backups = [Backup("first")], ExcludedInputIds = ["hidden"] };
            var clone = original.Clone();
            clone.UseBackups = false;
            clone.Backups[0] = clone.Backups[0] with { SampleRate = 44100 };
            clone.Backups.Add(Backup("second"));
            clone.ExcludedInputIds.Clear();
            Assert(original.UseBackups && original.Backups.Count == 1 && original.Backups[0].SampleRate == 48000 && original.IsExcluded("hidden"));
        });
        Test("Exclusion matching uses exact endpoint identity", () =>
        {
            var preferences = new AutomaticInputPreferences { ExcludedInputIds = ["Mic-A"] };
            preferences.Normalize();
            Assert(preferences.IsExcluded("Mic-A") && !preferences.IsExcluded("mic-a") &&
                !preferences.IsExcluded("Mic-A-copy") && !preferences.IsExcluded(""));
        });
        Test("Malformed endpoint identities and names are rejected without dropping valid entries", () =>
        {
            foreach (var id in new[] { "", " ", "mic\nother", new string('a', 2049) })
            {
                Reject(new() { Backups = [Backup(id)] });
                Reject(new() { ExcludedInputIds = [id] });
            }
            foreach (var name in new[] { "", " ", "name\0suffix", new string('a', 257) })
                Reject(new() { Backups = [Backup("valid") with { Name = name }] });
            var duplicate = new AutomaticInputPreferences { Backups = [Backup("same"), Backup("same")] };
            Reject(duplicate);
            Assert(duplicate.Backups.Count == 2);
        });
        Test("Duplicate exclusions null collections and excessive counts are rejected", () =>
        {
            Reject(new() { ExcludedInputIds = ["same", "same"] });
            Reject(new() { Backups = null! });
            Reject(new() { ExcludedInputIds = null! });
            Reject(new() { Backups = [null!] });
            Reject(new() { Backups = Enumerable.Range(0, 9).Select(i => Backup("mic-" + i)).ToList() });
            Reject(new() { ExcludedInputIds = Enumerable.Range(0, 65).Select(i => "mic-" + i).ToList() });
        });
        Test("Only supported complete capture formats can be stored as backups", () =>
        {
            foreach (var rate in new[] { 0, 7999, 384001 }) Reject(new() { Backups = [Backup("mic") with { SampleRate = rate }] });
            foreach (var channels in new[] { 0, 9 }) Reject(new() { Backups = [Backup("mic") with { Channels = channels }] });
            var boundaries = new AutomaticInputPreferences { Backups = [new("low", "Low", 8000, 1), new("high", "High", 384000, 8)] };
            boundaries.Normalize();
            Assert(boundaries.Backups.Count == 2);
        });
        return count;
    }
}
