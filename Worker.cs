using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace OpenCodeWebService;

public class OpencodeWorker : BackgroundService
{
    private readonly ILogger<OpencodeWorker> _logger;
    private readonly IConfiguration _config;
    private readonly string _logPath;
    private readonly object _logLock = new();
    private StreamWriter? _logWriter;
    private bool _stopLogged = false;
    private readonly object _stopLock = new();

    private static readonly string[] NoisyPrefixes = new[]
    {
        "PowerShell discovery failed",
        "Config updated:",
        "stopping",
        "stopped",
        "restarting",
    };

    public OpencodeWorker(ILogger<OpencodeWorker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;

        var configuredLogPath = config["Opencode:LogPath"] ?? "opencode.log";
        _logPath = Path.IsPathRooted(configuredLogPath)
            ? configuredLogPath
            : Path.Combine(AppContext.BaseDirectory, configuredLogPath);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var executeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _executeCts = executeCts;
        var token = executeCts.Token;

        var isManual = Environment.UserInteractive;
        if (!int.TryParse(_config["Opencode:RestartDetectionSeconds"] ?? "10", out var restartDetectionSeconds))
            restartDetectionSeconds = 10;

        DateTimeOffset? lastStop = null;
        var isServiceRestart = TryGetLastServiceStop(_logPath, 65536, out lastStop)
            && lastStop.HasValue
            && (DateTimeOffset.Now - lastStop.Value).TotalSeconds <= restartDetectionSeconds;

        EnsureLogWriter();

        if (isServiceRestart)
        {
            var elapsed = DateTimeOffset.Now - lastStop!.Value;
            WriteLogLine(isManual
                ? $"Service restarted (manually, {elapsed.TotalSeconds:F1}s after stop)"
                : $"Service restarted via service manager ({elapsed.TotalSeconds:F1}s after stop)");
        }
        else
        {
            WriteLogLine(isManual ? "Service started (manually)" : "Service started");
        }

        var configuredPath = _config["Opencode:Path"];
        var opencodePath = !string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathRooted(configuredPath)
            ? configuredPath
            : ResolveOpenCodeFromPath();
        var proxyPortText = _config["Opencode:Port"] ?? "80";
        var proxyHost = _config["Opencode:Hostname"] ?? "127.0.0.2";
        var username = _config["Opencode:Username"] ?? "opencode";
        var password = _config["Opencode:Password"] ?? "";
        var experimentalWebsockets = (_config["Opencode:ExperimentalWebsockets"] ?? "true").ToLower();

        if (!int.TryParse(proxyPortText, out var proxyPort))
        {
            _logger.LogWarning("Invalid Opencode:Port '{PortText}', falling back to 80", proxyPortText);
            proxyPort = 80;
        }

        while (!token.IsCancellationRequested)
        {
            RemoveProxy(proxyHost, proxyPort);

            try
            {
                await Task.Delay(1000, token);
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
                await Task.Delay(5000, token);
                continue;
            }

            var psi = new ProcessStartInfo
            {
                FileName = opencodePath,
                Arguments = $"web --port {actualPort} --hostname {proxyHost}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
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

            WriteLogLine($"Opencode web: \"{opencodePath}\" {psi.Arguments}");
            WriteLogLine("--- opencode stdout/stderr begin ---");

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _opencodeProcess = process;
            var started = false;

            _logger.LogInformation("Starting opencode web on a random port behind proxy {ProxyHost}:{ProxyPort}",
                proxyHost, proxyPort);

            try
            {
                process.Start();
                started = true;

                _ = Task.Run(() => ReadStream(process.StandardOutput, false), token);
                _ = Task.Run(() => ReadStream(process.StandardError, true), token);

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

                if (await WaitForPort(proxyHost, actualPort, token))
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

                await Task.WhenAny(exitedTcs.Task, Task.Delay(Timeout.Infinite, token));

                StopOpencodeProcess(process);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start opencode serve");
            }
            finally
            {
                WriteLogLine("--- opencode stdout/stderr end ---");
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

                if (_opencodeProcess == process)
                    _opencodeProcess = null;

                RemoveProxy(proxyHost, proxyPort);
            }

            if (token.IsCancellationRequested) break;

            WriteLogLine("Restarting opencode web after child process exit");
            _logger.LogInformation("Restarting opencode web in 5 seconds...");
            try
            {
                await Task.Delay(5000, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _executeCts = null;
    }

    private TcpListener? _proxyListener;
    private CancellationTokenSource? _proxyCts;
    private Process? _opencodeProcess;
    private CancellationTokenSource? _executeCts;

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

    private void StopOpencodeProcess(Process process)
    {
        if (process.HasExited)
            return;

        try
        {
            _logger.LogInformation("Killing opencode process tree (PID {Pid})...", process.Id);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
        catch (InvalidOperationException)
        {
            // Already exited or handle closed
        }
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

                WriteLogLine(line);

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

    private void WriteLogLine(string line)
    {
        try
        {
            var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
            var entry = $"[{timestamp}] {line}";
            lock (_logLock)
            {
                _logWriter?.WriteLine(entry);
                _logWriter?.Flush();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write opencode log line to disk");
        }
    }

    private void EnsureLogWriter()
    {
        try
        {
            lock (_logLock)
            {
                var directory = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                if (_logWriter is null)
                {
                    _logWriter = new StreamWriter(_logPath, append: true, System.Text.Encoding.UTF8)
                    {
                        AutoFlush = true,
                    };
                }
            }
            _logger.LogInformation("OpenCode stdout/stderr log: {LogPath}", _logPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open opencode log file {LogPath}", _logPath);
        }
    }

    private void CloseLogWriter()
    {
        try
        {
            lock (_logLock)
            {
                _logWriter?.Flush();
                _logWriter?.Dispose();
                _logWriter = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to close opencode log file");
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        var isManual = Environment.UserInteractive;

        // Make sure the file logger is open before writing lifecycle entries.
        EnsureLogWriter();

        var stoppingMessage = isManual ? "Service stopping (manual)" : "Service stopping";
        _logger.LogInformation(stoppingMessage);
        WriteLogLine(stoppingMessage);

        try
        {
            var process = _opencodeProcess;
            if (process is not null && !process.HasExited)
            {
                _logger.LogInformation("Service stopping: killing opencode process...");
                StopOpencodeProcess(process);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error while stopping opencode process during service shutdown");
        }

        // Cancel ExecuteAsync immediately so base.StopAsync doesn't wait on everything else.
        try
        {
            _executeCts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        lock (_stopLock)
        {
            if (!_stopLogged)
            {
                var stoppedMessage = isManual ? "Service stopped (manually)" : "Service stopped";
                _logger.LogInformation(stoppedMessage);
                WriteLogLine(stoppedMessage);
                _stopLogged = true;
            }
        }

        CloseLogWriter();
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        lock (_stopLock)
        {
            if (!_stopLogged)
            {
                WriteLogLine("Service stopped");
                _stopLogged = true;
            }
        }
        CloseLogWriter();
        base.Dispose();
    }

    private static bool TryParseLogTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var open = line.IndexOf('[');
        var close = line.IndexOf(']');
        if (open < 0 || close <= open) return false;

        var timestampText = line.Substring(open + 1, close - open - 1);
        return DateTimeOffset.TryParse(timestampText, out timestamp);
    }

    private static bool TryGetLastServiceStop(string logPath, int tailBytes, out DateTimeOffset? stoppedAt)
    {
        stoppedAt = null;
        if (!File.Exists(logPath)) return false;

        try
        {
            var fileInfo = new FileInfo(logPath);
            if (fileInfo.Length == 0) return false;

            var bufferSize = (int)Math.Min(tailBytes, fileInfo.Length);
            var buffer = new byte[bufferSize];
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(-bufferSize, SeekOrigin.End);
            fs.ReadExactly(buffer, 0, bufferSize);

            var tail = System.Text.Encoding.UTF8.GetString(buffer);
            var lines = tail.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].Contains("Service stopped", StringComparison.OrdinalIgnoreCase)
                    && TryParseLogTimestamp(lines[i], out var timestamp))
                {
                    stoppedAt = timestamp;
                    return true;
                }
            }
        }
        catch { }

        return false;
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
