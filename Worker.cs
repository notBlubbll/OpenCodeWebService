using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

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
        var opencodePath = _config["Opencode:Path"] ?? ResolveOpenCodeFromPath();
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
            FreeProxy(proxyHost, proxyPort);
            RemoveProxy(proxyHost, proxyPort);

            try
            {
                await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var psi = new ProcessStartInfo
            {
                FileName = opencodePath,
                Arguments = "web --port 0 --hostname 127.0.0.1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            if (!string.IsNullOrEmpty(username))
                psi.EnvironmentVariables["OPENCODE_SERVER_USERNAME"] = username;
            if (!string.IsNullOrEmpty(password))
                psi.EnvironmentVariables["OPENCODE_SERVER_PASSWORD"] = password;
            if (!string.IsNullOrEmpty(experimentalWebsockets))
                psi.EnvironmentVariables["OPENCODE_EXPERIMENTAL_WEBSOCKETS"] = experimentalWebsockets;
            psi.EnvironmentVariables["OPENCODE_DISABLE_EMBEDDED_WEB_UI"] = "true";
            psi.EnvironmentVariables["BROWSER"] = "none";

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

                var actualPort = await FindOpencodePort(process.Id, stoppingToken);
                _logger.LogInformation("FindOpencodePort returned actualPort={ActualPort}", actualPort);

                if (actualPort > 0)
                {
                    _logger.LogInformation("Opencode listening on port {ActualPort}, creating proxy {ProxyHost}:{ProxyPort} -> 127.0.0.1:{ActualPort}",
                        actualPort, proxyHost, proxyPort, actualPort);
                    CreateProxy(proxyHost, proxyPort, actualPort);
                }
                else
                {
                    _logger.LogWarning("Did not detect a valid opencode port; proxy will not be created");
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

    private void CreateProxy(string listenHost, int listenPort, int targetPort)
    {
        try
        {
            RunNetsh($"interface portproxy add v4tov4 listenaddress={listenHost} listenport={listenPort} " +
                     $"connectaddress=127.0.0.1 connectport={targetPort}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create portproxy for {Host}:{Port} -> 127.0.0.1:{TargetPort}",
                listenHost, listenPort, targetPort);
        }
    }

    private void RemoveProxy(string listenHost, int listenPort)
    {
        try
        {
            RunNetsh($"interface portproxy delete v4tov4 listenaddress={listenHost} listenport={listenPort}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No portproxy to remove for {Host}:{Port} (expected on first run)",
                listenHost, listenPort);
        }
    }

    private void RunNetsh(string arguments)
    {
        var psi = new ProcessStartInfo("netsh", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi);
        if (process is null)
        {
            _logger.LogWarning("Failed to start netsh for '{Arguments}'", arguments);
            return;
        }
        process.WaitForExit(10000);
        if (process.ExitCode != 0)
        {
            var err = process.StandardError.ReadToEnd();
            _logger.LogWarning("netsh returned exit code {ExitCode}: {Error}", process.ExitCode, err.Trim());
        }
    }

    private void FreeProxy(string hostname, int port)
    {
        try
        {
            var pids = GetListenerPids(port, hostname);
            foreach (var pid in pids)
            {
                try
                {
                    var existing = Process.GetProcessById(pid);
                    _logger.LogWarning(
                        "Killing process {ProcessName} (PID {Pid}) holding proxy port {Port}",
                        existing.ProcessName, pid, port);
                    existing.Kill(entireProcessTree: true);
                    existing.WaitForExit(5000);
                }
                catch (ArgumentException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not kill process PID {Pid} holding port {Port}", pid, port);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Process kill scan failed for proxy port {Port}", port);
        }

        try
        {
            int cleared = PortNuker.DeleteTcpEntriesForLocalPort(port);
            if (cleared > 0)
                _logger.LogWarning("Nuked {Count} TCP entries for proxy port {Port}", cleared, port);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nuclear TCP table cleanup failed for proxy port {Port}", port);
        }
    }

    private static async Task<int> FindOpencodePort(int pid, CancellationToken stoppingToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                await Task.Delay(500, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return -1;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c netstat -ano | findstr \"{pid}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) continue;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            foreach (var line in output.Split('\n', '\r'))
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4)
                    continue;

                var localAddress = parts[1];
                var state = parts[3];

                if (!state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                    continue;

                var addressPort = localAddress.Split(':');
                if (addressPort.Length < 2)
                    continue;

                if (int.TryParse(addressPort[^1], out var port) && port > 0)
                    return port;
            }
        }

        return -1;
    }

    private static List<int> GetListenerPids(int port, string hostname)
    {
        var pids = new List<int>();
        var search = $"{hostname}:{port}";

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c netstat -ano | findstr \"{search}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
            return pids;

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        foreach (var line in output.Split('\n', '\r'))
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
                continue;

            var localAddress = parts[1];
            var state = parts[3];
            var pidText = parts[4];

            if (!localAddress.Equals(search, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                continue;

            if (int.TryParse(pidText, out var pid) && !pids.Contains(pid))
                pids.Add(pid);
        }

        return pids;
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

    private static class PortNuker
    {
        private const uint ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint MIB_TCP_STATE_DELETE_TCB = 12;

        public static int DeleteTcpEntriesForLocalPort(int port)
        {
            uint targetPort = Htons((uint)port);
            int cleared = 0;
            IntPtr table = IntPtr.Zero;

            try
            {
                uint size = 0;
                uint result = GetTcpTable(IntPtr.Zero, ref size, true);
                if (result != 0 && result != ERROR_INSUFFICIENT_BUFFER)
                    throw new InvalidOperationException($"GetTcpTable returned {result}");

                table = Marshal.AllocHGlobal((int)size);
                result = GetTcpTable(table, ref size, true);
                if (result != 0)
                    throw new InvalidOperationException($"GetTcpTable returned {result}");

                int rowCount = Marshal.ReadInt32(table);
                int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW));
                IntPtr rowPtr = IntPtr.Add(table, sizeof(int));

                for (int i = 0; i < rowCount; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW>(rowPtr);

                    if (row.dwLocalPort == targetPort)
                    {
                        row.dwState = MIB_TCP_STATE_DELETE_TCB;

                        IntPtr rowBuffer = Marshal.AllocHGlobal(rowSize);
                        try
                        {
                            Marshal.StructureToPtr(row, rowBuffer, false);
                            int setResult = SetTcpEntry(rowBuffer);
                            if (setResult == 0)
                                cleared++;
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(rowBuffer);
                        }
                    }

                    rowPtr += rowSize;
                }
            }
            finally
            {
                if (table != IntPtr.Zero)
                    Marshal.FreeHGlobal(table);
            }

            return cleared;
        }

        private static uint Htons(uint value)
        {
            return ((value & 0xFF) << 8) | ((value >> 8) & 0xFF);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW
        {
            public uint dwState;
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwRemoteAddr;
            public uint dwRemotePort;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetTcpTable(IntPtr pTcpTable, ref uint pdwSize, bool bOrder);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int SetTcpEntry(IntPtr pTcpRow);
    }
}
