using MicStudio.NativeApo;
using System.Security.Principal;

// No silent elevation. The UI previews a fingerprinted plan first and invokes
// this helper through Windows UAC only after the user's explicit action.
if (args.Length == 0 || args[0] == "--help")
{
    Console.WriteLine("Mic Studio native APO administration. --apply <dll> <endpoint-guid> <writer-sid> <review-fingerprint> <request-guid>; --restore <protected-backup> <backup-sha256> <writer-sid> <request-guid>.");
    return 0;
}
try
{
    var service = new NativeApoDeploymentService();
    NativeApoOperationResult result;
    string writerSid; Guid request;
    if (args.Length == 6 && args[0] == "--apply")
    {
        writerSid = args[3]; request = ParseRequest(args[5]); ValidateWriter(writerSid);
        var plan = service.PrepareInstallation(args[1], Guid.Parse(args[2]), args[3]);
        result = service.ApplyPreparedPlan(plan, args[4]);
    }
    else if (args.Length == 5 && args[0] == "--restore")
    {
        writerSid = args[3]; request = ParseRequest(args[4]); ValidateWriter(writerSid);
        result = service.Restore(args[1], args[2]);
    }
    else { Console.Error.WriteLine("Invalid operation arguments."); return 64; }
    service.WriteOperationReport(request, writerSid, result);
    return result.Success ? 0 : result.NeedsElevation ? 2 : 1;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message); return 1;
}
static Guid ParseRequest(string value) { var parsed = Guid.Parse(value); return parsed != Guid.Empty ? parsed : throw new ArgumentException("A nonempty request GUID is required."); }
static void ValidateWriter(string value) { if (!new SecurityIdentifier(value).IsAccountSid()) throw new ArgumentException("A specific Windows account SID is required."); }
