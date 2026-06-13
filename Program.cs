using System.Diagnostics;
using OpenCodeWebService;
using Microsoft.Extensions.Hosting.WindowsServices;

KillExistingInstances();
KillProcesses("opencode");

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "OpencodeWeb";
});
builder.Services.AddHostedService<OpencodeWorker>();

var host = builder.Build();
host.Run();

static void KillExistingInstances()
{
    try
    {
        var current = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(current.ProcessName))
        {
            if (p.Id == current.Id) continue;
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
            catch { }
        }
    }
    catch { }
}

static void KillProcesses(string name)
{
    try
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
            catch { }
        }
    }
    catch { }
}
