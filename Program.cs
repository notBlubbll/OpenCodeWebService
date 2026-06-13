using OpenCodeWebService;
using Microsoft.Extensions.Hosting.WindowsServices;

const string MutexName = "Global\\OpenCodeWebService_OpencodeWeb";

using var mutex = new Mutex(false, MutexName, out var createdNew);
if (!createdNew)
{
    Console.WriteLine("Another OpenCodeWebService instance is already running. Exiting.");
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "OpencodeWeb";
});
builder.Services.AddHostedService<OpencodeWorker>();

var host = builder.Build();
host.Run();
