using System.Diagnostics;

namespace OpenCodeWebService;

public class OpencodeWorker : BackgroundService
{
    private readonly ILogger<OpencodeWorker> _logger;
    private readonly IConfiguration _config;

    public OpencodeWorker(ILogger<OpencodeWorker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opencodePath = ResolveOpenCodeFromPath();
        var port = _config["Opencode:Port"] ?? "4096";
        var hostname = _config["Opencode:Hostname"] ?? "127.0.0.1";
        var username = _config["Opencode:Username"] ?? "opencode";
        var password = _config["Opencode:Password"] ?? "";

        while (!stoppingToken.IsCancellationRequested)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{opencodePath}\" web --port {port} --hostname {hostname}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            psi.EnvironmentVariables["OPENCODE_SERVER_USERNAME"] = username;
            psi.EnvironmentVariables["OPENCODE_SERVER_PASSWORD"] = password;

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            _logger.LogInformation("Starting opencode web on {Hostname}:{Port}", hostname, port);

            try
            {
                process.Start();

                _ = Task.Run(() => ReadStream(process.StandardOutput, false), stoppingToken);
                _ = Task.Run(() => ReadStream(process.StandardError, true), stoppingToken);

                var exitedTcs = new TaskCompletionSource<bool>();
                process.Exited += (_, _) =>
                {
                    _logger.LogWarning("Opencode process exited with code {ExitCode}", process.ExitCode);
                    exitedTcs.TrySetResult(true);
                };

                await Task.WhenAny(exitedTcs.Task, Task.Delay(Timeout.Infinite, stoppingToken));

                if (!process.HasExited)
                {
                    _logger.LogInformation("Shutting down opencode process...");
                    process.Kill(entireProcessTree: true);
                    await Task.Run(() => process.WaitForExit(), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start opencode web");
            }

            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();

            if (stoppingToken.IsCancellationRequested) break;

            _logger.LogInformation("Restarting opencode web in 5 seconds...");
            await Task.Delay(5000, stoppingToken);
        }
    }

    private static string ResolveOpenCodeFromPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH")
                   ?? Environment.GetEnvironmentVariable("Path")
                   ?? string.Empty;

        var extensions = new[] { ".cmd", ".bat", ".exe" };

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var fullPath = Path.Combine(dir.Trim(), "opencode" + ext);
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        return "opencode";
    }

    private void ReadStream(StreamReader reader, bool isError)
    {
        try
        {
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (line is null) break;

                if (isError)
                    _logger.LogWarning("[stderr] {Msg}", line);
                else
                    _logger.LogInformation("[stdout] {Msg}", line);
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }
}
