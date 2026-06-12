using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

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
        var portText = _config["Opencode:Port"] ?? "4096";
        var hostname = _config["Opencode:Hostname"] ?? "127.0.0.1";
        var username = _config["Opencode:Username"] ?? "opencode";
        var password = _config["Opencode:Password"] ?? "";
        var experimentalWebsockets = _config["Opencode:ExperimentalWebsockets"] ?? "TRUE";

        if (!int.TryParse(portText, out var port))
        {
            _logger.LogWarning("Invalid Opencode:Port '{PortText}', falling back to 4096", portText);
            port = 4096;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            FreePort(port, hostname);
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
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{opencodePath}\" web --port {port} --hostname {hostname}\"",
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

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var started = false;

            _logger.LogInformation("Starting opencode web on {Hostname}:{Port}", hostname, port);

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
            finally
            {
                if (started)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Process was never associated or already gone.
                    }
                    process.Dispose();
                }
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

    private void FreePort(int port, string hostname)
    {
        try
        {
            var pids = GetListenerPids(port);
            if (pids.Count > 0)
            {
                foreach (var pid in pids)
                {
                    try
                    {
                        var existing = Process.GetProcessById(pid);
                        _logger.LogWarning(
                            "Killing process {ProcessName} (PID {Pid}) that is holding port {Port}",
                            existing.ProcessName, pid, port);
                        existing.Kill(entireProcessTree: true);
                        existing.WaitForExit(5000);
                    }
                    catch (ArgumentException)
                    {
                        // Process already gone.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not kill process PID {Pid} holding port {Port}", pid, port);
                    }
                }
            }
            else
            {
                _logger.LogDebug("No live process found listening on port {Port}", port);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Process kill scan failed for port {Port}", port);
        }

        // Nuclear option: forcibly delete the TCP control blocks from the kernel table.
        try
        {
            int cleared = PortNuker.DeleteTcpEntriesForLocalPort(port);
            if (cleared > 0)
            {
                _logger.LogWarning("Nuked {Count} TCP entries for port {Port} from the kernel table", cleared, port);
            }
            else
            {
                _logger.LogDebug("No TCP entries found to nuke for port {Port}", port);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nuclear TCP table cleanup failed for port {Port}", port);
        }
    }

    private static List<int> GetListenerPids(int port)
    {
        var pids = new List<int>();
        var portSuffix = $":{port}";

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c netstat -ano | findstr \"{portSuffix}\"",
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

            if (!localAddress.EndsWith(portSuffix, StringComparison.OrdinalIgnoreCase))
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

                if (isError)
                    _logger.LogWarning("[stderr] {Msg}", line);
                else
                    _logger.LogInformation("[stdout] {Msg}", line);
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
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
                {
                    throw new InvalidOperationException($"GetTcpTable returned {result}");
                }

                table = Marshal.AllocHGlobal((int)size);
                result = GetTcpTable(table, ref size, true);
                if (result != 0)
                {
                    throw new InvalidOperationException($"GetTcpTable returned {result}");
                }

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
                            {
                                cleared++;
                            }
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
