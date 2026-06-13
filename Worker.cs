using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace OpenCodeWebService;

public class OpencodeWorker : BackgroundService
{
    private readonly ILogger<OpencodeWorker> _logger;
    private readonly IConfiguration _config;

    private static readonly string[] NoisyPrefixes = new[]
    {
        "PowerShell discovery failed",
        "Config updated:",
    };

    public OpencodeWorker(ILogger<OpencodeWorker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configuredPath = _config["Opencode:Path"];
        var opencodePath = !string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathRooted(configuredPath)
            ? configuredPath
            : ResolveOpenCodeFromPath();
        var proxyPortText = _config["Opencode:Port"] ?? "80";
        var proxyHost = _config["Opencode:Hostname"] ?? "127.0.0.2";
        var username = _config["Opencode:Username"] ?? "opencode";
        var password = _config["Opencode:Password"] ?? "";
        var experimentalWebsockets = _config["Opencode:ExperimentalWebsockets"] ?? "TRUE";

        if (!int.TryParse(proxyPortText, out var proxyPort))
        {
            _logger.LogWarning("Invalid Opencode:Port '{PortText}', falling back to 80", proxyPortText);
            proxyPort = 80;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            RemoveProxy(proxyHost, proxyPort);

            try
            {
                await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            int actualPort;
            try
            {
                actualPort = GetFreePort();
                _logger.LogInformation("Allocated free port {ActualPort} on {ProxyHost} for opencode", actualPort, proxyHost);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to allocate a free port");
                await Task.Delay(5000, stoppingToken);
                continue;
            }

            var psi = new ProcessStartInfo
            {
                FileName = opencodePath,
                Arguments = $"web --port {actualPort} --hostname {proxyHost}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            foreach (var key in psi.EnvironmentVariables.Keys.Cast<string>().ToList())
            {
                if (key.StartsWith("OPENCODE_", StringComparison.OrdinalIgnoreCase))
                    psi.EnvironmentVariables.Remove(key);
            }

            if (!string.IsNullOrEmpty(password))
            {
                psi.EnvironmentVariables["OPENCODE_SERVER_PASSWORD"] = password;
                if (!string.IsNullOrEmpty(username))
                    psi.EnvironmentVariables["OPENCODE_SERVER_USERNAME"] = username;
            }
            if (!string.IsNullOrEmpty(experimentalWebsockets))
                psi.EnvironmentVariables["OPENCODE_EXPERIMENTAL_WEBSOCKETS"] = experimentalWebsockets;

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var started = false;

            _logger.LogInformation("Starting opencode web on a random port behind proxy {ProxyHost}:{ProxyPort}",
                proxyHost, proxyPort);

            try
            {
                process.Start();
                started = true;

                _ = Task.Run(() => ReadStream(process.StandardOutput, false), stoppingToken);
                _ = Task.Run(() => ReadStream(process.StandardError, true), stoppingToken);

                var exitedTcs = new TaskCompletionSource<bool>();
                process.Exited += (_, _) =>
                {
                    try
                    {
                        _logger.LogWarning("Opencode process exited with code {ExitCode}", process.ExitCode);
                    }
                    catch (InvalidOperationException)
                    {
                        _logger.LogWarning("Opencode process exited but exit code could not be read");
                    }
                    exitedTcs.TrySetResult(true);
                };

                if (await WaitForPort(proxyHost, actualPort, stoppingToken))
                {
                    _logger.LogInformation("Opencode listening on {ProxyHost}:{ActualPort}, creating proxy {ProxyHost}:{ProxyPort} -> {ProxyHost}:{ActualPort}",
                        proxyHost, actualPort, proxyHost, proxyPort, proxyHost, actualPort);
                    CreateProxy(proxyHost, proxyPort, proxyHost, actualPort);
                }
                else
                {
                    _logger.LogWarning("Opencode did not start listening on {ProxyHost}:{ActualPort}; proxy will not be created",
                        proxyHost, actualPort);
                }

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
                _logger.LogError(ex, "Failed to start opencode serve");
            }
            finally
            {
                if (started)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) { }
                    process.Dispose();
                }
                RemoveProxy(proxyHost, proxyPort);
            }

            if (stoppingToken.IsCancellationRequested) break;

            _logger.LogInformation("Restarting opencode web in 5 seconds...");
            try
            {
                await Task.Delay(5000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private TcpListener? _proxyListener;
    private CancellationTokenSource? _proxyCts;

    private void CreateProxy(string listenHost, int listenPort, string targetHost, int targetPort)
    {
        try
        {
            RemoveProxy();

            var ip = IPAddress.Parse(listenHost);
            _proxyListener = new TcpListener(ip, listenPort);
            _proxyListener.Start();
            _proxyCts = new CancellationTokenSource();
            var token = _proxyCts.Token;

            _logger.LogInformation("TCP proxy listening on {Host}:{Port} -> {TargetHost}:{TargetPort}",
                listenHost, listenPort, targetHost, targetPort);

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient? client = null;
                    try
                    {
                        client = await _proxyListener.AcceptTcpClientAsync(token);
                        _ = Task.Run(() => ForwardConnection(client, targetHost, targetPort, token), token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Proxy accept failed");
                        client?.Dispose();
                    }
                }
            }, token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create TCP proxy for {Host}:{Port} -> {TargetHost}:{TargetPort}",
                listenHost, listenPort, targetHost, targetPort);
        }
    }

    private async Task ForwardConnection(TcpClient client, string targetHost, int targetPort, CancellationToken token)
    {
        try
        {
            using var target = new TcpClient();
            await target.ConnectAsync(targetHost, targetPort);
            using var clientStream = client.GetStream();
            using var targetStream = target.GetStream();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var c2t = clientStream.CopyToAsync(targetStream, cts.Token);
            var t2c = targetStream.CopyToAsync(clientStream, cts.Token);
            await Task.WhenAny(c2t, t2c);
            cts.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Proxy connection forward ended");
        }
        finally
        {
            try { client.Close(); } catch { }
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<bool> WaitForPort(string host, int port, CancellationToken stoppingToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                await Task.Delay(500, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port);
                return true;
            }
            catch
            {
            }
        }
        return false;
    }

    private void RemoveProxy(string listenHost, int listenPort)
    {
        RemoveProxy();
    }

    private void RemoveProxy()
    {
        try
        {
            _proxyCts?.Cancel();
            _proxyListener?.Stop();
            _proxyListener = null;
            _proxyCts?.Dispose();
            _proxyCts = null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No TCP proxy to remove (expected on first run)");
        }
    }

    private static string ResolveOpenCodeFromPath()
    {
        var extensions = new[] { ".cmd", ".bat", ".ps1", ".exe" };

        var searchDirs = new List<string>();

        var path = Environment.GetEnvironmentVariable("PATH")
                   ?? Environment.GetEnvironmentVariable("Path")
                   ?? string.Empty;
        searchDirs.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appData))
            searchDirs.Add(Path.Combine(appData, "npm"));

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(localAppData))
        {
            searchDirs.Add(Path.Combine(localAppData, "npm"));
            searchDirs.Add(Path.Combine(localAppData, "Microsoft", "WindowsApps"));
        }

        searchDirs.Add(@"C:\Program Files\nodejs");
        searchDirs.Add(@"C:\Program Files (x86)\nodejs");

        foreach (var dir in searchDirs)
        {
            foreach (var ext in extensions)
            {
                var fullPath = Path.Combine(dir.Trim(), "opencode" + ext);
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "opencode",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    if (File.Exists(firstLine))
                        return firstLine;
                }
            }
        }
        catch { }

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

                if (IsNoisy(line))
                {
                    _logger.LogDebug("[opencode] {Msg}", line);
                    continue;
                }

                if (isError)
                    _logger.LogWarning("[stderr] {Msg}", line);
                else
                    _logger.LogInformation("[stdout] {Msg}", line);
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private static bool IsNoisy(string line)
    {
        foreach (var prefix in NoisyPrefixes)
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
