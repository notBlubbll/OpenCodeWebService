using OpenCodeWebService;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "OpencodeWeb";
});
builder.Services.AddHostedService<OpencodeWorker>();

var host = builder.Build();
host.Run();
