using System.IO.Compression;
using MicStudio.Components;

int passed = 0;
void Check(string name, Action body) { try { body(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex); Environment.ExitCode = 1; } }
void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
void Rejected(Action action) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException) { return; } throw new Exception("Unsafe input accepted."); }
const string Official45 = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
Check("official current+old links choose newest", () => Assert(DriverManager.ParseOfficialDownload("<a HREF=\"" + Official45 + "\">current</a><a href='https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip'>old</a>").Pack == 45));
Check("no guessed download URLs", () => Rejected(() => DriverManager.ParseOfficialDownload("<html>new package coming</html>")));
foreach (var bad in new[] { "http://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip", "https://evil.example/VBCABLE_Driver_Pack45.zip", "https://download.vb-audio.com.evil.example/Download_CABLE/VBCABLE_Driver_Pack45.zip", Official45 + "?redirect=evil", "https://download.vb-audio.com/Download_CABLE/../malware.exe", "https://user@download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip" })
    Check("untrusted download/redirect rejected " + bad, () => Rejected(() => DriverManager.ValidateOfficialUri(new Uri(bad), true)));
Check("official page endpoint accepted", () => DriverManager.ValidateOfficialUri(new Uri(DriverManager.OfficialPage), false));
Check("unrelated official-host page rejected", () => Rejected(() => DriverManager.ValidateOfficialUri(new Uri("https://vb-audio.com/Services/other.exe"), false)));
Check("malicious parser domain spoof rejected", () => Rejected(() => DriverManager.ParseOfficialDownload("<a href='https://download.vb-audio.com.evil.example/Download_CABLE/VBCABLE_Driver_Pack99.zip'>fake</a>")));
Check("paid extra cable package excluded", () => Rejected(() => DriverManager.ParseOfficialDownload("<a href='https://download.vb-audio.com/Download_CABLE/VBCABLE_A_B_Driver_Pack99.zip'>paid</a>")));
Check("HTTP-only link never upgraded by guess", () => Rejected(() => DriverManager.ParseOfficialDownload("<a href='http://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip'>plain</a>")));
Check("implausible package number rejected", () => Rejected(() => DriverManager.ParseOfficialDownload("<a href='https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack999999.zip'>bad</a>")));

var fixture = Path.Combine(Path.GetTempPath(), "MicStudio-ComponentTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
using var manager = new DriverManager(fixture);
string MakeZip(string name, Action<ZipArchive> write)
{
    string directory = Path.Combine(fixture, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, name); using var file = File.Create(path); using var zip = new ZipArchive(file, ZipArchiveMode.Create); write(zip); return path;
}
void Entry(ZipArchive zip, string name, string content = "test") { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(content); }
try
{
    Check("unsafe ZIP traversal rejected", () => { var path = MakeZip("VBCABLE_Driver_Pack45.zip", z => { Entry(z, "VBCABLE_Setup_x64.exe"); Entry(z, "../outside.txt"); }); Rejected(() => manager.InspectPackage(path)); });
    Check("ZIP case duplicate rejected", () => { var path = MakeZip("VBCABLE_Driver_Pack45.zip", z => { Entry(z, "VBCABLE_Setup_x64.exe"); Entry(z, "FILE.txt"); Entry(z, "file.txt"); }); Rejected(() => manager.InspectPackage(path)); });
    Check("ZIP symlink rejected", () => { var path = MakeZip("VBCABLE_Driver_Pack45.zip", z => { Entry(z, "VBCABLE_Setup_x64.exe"); z.CreateEntry("link").ExternalAttributes = unchecked((int)0xA1FF0000); }); Rejected(() => manager.InspectPackage(path)); });
    Check("ZIP alternate installer rejected", () => { var path = MakeZip("VBCABLE_Driver_Pack45.zip", z => Entry(z, "runme.exe")); Rejected(() => manager.InspectPackage(path)); });
    Check("ZIP Windows device name rejected", () => { var path = MakeZip("VBCABLE_Driver_Pack45.zip", z => { Entry(z, "VBCABLE_Setup_x64.exe"); Entry(z, "NUL.txt"); }); Rejected(() => manager.InspectPackage(path)); });
    Check("valid package inspected with SHA256", () => { var path = MakeZip("VBCABLE_Driver_Pack45.zip", z => Entry(z, "VBCABLE_Setup_x64.exe", "MZsynthetic")); var package = manager.InspectPackage(path); Assert(package.PackNumber == 45 && package.Sha256.Length == 64); });
    Check("changed package rejected before extraction", () => { var path = MakeZip("VBCABLE_Driver_Pack45.zip", z => Entry(z, "VBCABLE_Setup_x64.exe")); var package = manager.InspectPackage(path); File.AppendAllText(path, "external modification"); Rejected(() => manager.PrepareInstaller(package)); });
    if (args.Length >= 1 && args[0] != "--online")
    {
        Check("real official original package hash and installer signature (no launch)", () =>
        {
            var package = manager.InspectPackage(Path.GetFullPath(args[0]));
            Assert(package.Sha256 == "b950e39f01af1d04ea623c8f6d8eb9b6ea5c477c637295fabf20631c85116bfb");
            var installer = manager.PrepareInstaller(package);
            Assert(installer.SignatureValid);
            Console.WriteLine("  Actual signer: " + installer.Signer);
        });
    }
    if (args.Contains("--online"))
    {
        Check("real official update page and forced download (no launch)", () =>
        {
            var update = manager.CheckForUpdatesAsync().GetAwaiter().GetResult();
            Assert(update.AvailablePackNumber >= 45 && update.DownloadUrl.StartsWith("https://download.vb-audio.com/Download_CABLE/"));
            var package = manager.DownloadLatestAsync(true).GetAwaiter().GetResult();
            Assert(package.PackNumber == update.AvailablePackNumber);
            Console.WriteLine("  Downloaded official Pack" + package.PackNumber + " SHA256 " + package.Sha256);
            var installer = manager.PrepareInstaller(package);
            Assert(installer.SignatureValid);
            Console.WriteLine("  Downloaded installer signature verified: " + installer.Signer + "; not launched.");
        });
    }
    Check("cancelled download exits without lock disposal race", () =>
    {
        using var local = new DriverManager(fixture);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { local.DownloadLatestAsync(true, cancellation.Token).GetAwaiter().GetResult(); throw new Exception("Cancelled operation completed."); }
        catch (OperationCanceledException) { }
    });
    Check("dispose during active or queued download cannot release disposed semaphore", () =>
    {
        var local = new DriverManager(fixture);
        var first = local.DownloadLatestAsync(true);
        var queued = local.DownloadLatestAsync(true);
        local.Dispose();
        foreach (var operation in new[] { first, queued })
        {
            try { operation.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { }
            catch (ObjectDisposedException ex) when (!ex.ObjectName?.Contains("SemaphoreSlim", StringComparison.OrdinalIgnoreCase) == true) { }
        }
    });
    var installed = manager.Detect(false, false);
    Console.WriteLine("Read-only installed driver detection: service=" + installed.ServicePresent + ", version=" + installed.DriverVersion + ", trust=" + installed.SignatureStatus + ". Endpoint flags were not probed by this test.");
    Console.WriteLine($"Component checks passed: {passed}. No driver installation or UAC launch was performed.");
}
finally
{
    var resolved = Path.GetFullPath(fixture);
    if (Path.GetDirectoryName(resolved) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(resolved).StartsWith("MicStudio-ComponentTests-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
}
