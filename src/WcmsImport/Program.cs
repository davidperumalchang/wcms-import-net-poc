using WcmsImport.Api;
using WcmsImport.Importer;
using WcmsImport.Mock;
using WcmsImport.Notifier;

// One executable, one image. The first argument picks which service this process runs,
// so each docker-compose service starts the same image with a different role.
var role = args.FirstOrDefault();
var rest = args.Skip(1).ToArray();

Func<string[], Task>? run = role switch
{
    "api" => ImportApi.RunAsync,
    "importer" => ImporterHost.RunAsync,
    "notifier" => NotifierHost.RunAsync,
    "mock-cms" => MockCms.RunAsync,
    "mock-upstream" => MockUpstream.RunAsync,
    _ => null,
};

if (run is null)
{
    Console.Error.WriteLine("usage: WcmsImport <api | importer | notifier | mock-cms | mock-upstream>");
    return 1;
}

await run(rest);
return 0;
