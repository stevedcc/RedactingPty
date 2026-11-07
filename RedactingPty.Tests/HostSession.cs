using System.Diagnostics;
using System.Text;
using Xunit;

namespace RedactingPty.Tests;

/// <summary>
/// A running RedactingPty.TestHost whose stdin stays open: tests wait for concrete conditions on the
/// output so far (<see cref="WaitForOutput"/>), send input, and collect the result. A deadline only
/// fails the test; nothing here waits for time to pass.
/// </summary>
internal sealed class HostSession : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    // A monitor object, not System.Threading.Lock: WaitForOutput needs Monitor.Wait/PulseAll on it.
    private readonly object _gate = new();
    private readonly MemoryStream _stdout = new();
    private readonly Task _stdoutTask;
    private readonly Task<string> _stderrTask;

    private HostSession(Process process)
    {
        _process = process;
        _stdoutTask = Task.Run(CopyStdout);
        _stderrTask = process.StandardError.ReadToEndAsync();
    }

    public static HostSession Start(string hostPath, string runner, string[] hostArgs, string[] childArgv)
    {
        Assert.True(File.Exists(hostPath), $"test host not found at {hostPath}");
        var psi = new ProcessStartInfo(hostPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(runner);
        foreach (var a in hostArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--");
        foreach (var a in childArgv) psi.ArgumentList.Add(a);
        return new HostSession(Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null"));
    }

    public int HostPid => _process.Id;

    /// <summary>The raw output so far, decoded as UTF-8.</summary>
    public string Output
    {
        get { lock (_gate) return Encoding.UTF8.GetString(_stdout.ToArray()); }
    }

    private void CopyStdout()
    {
        var buf = new byte[4096];
        var stream = _process.StandardOutput.BaseStream;
        int n;
        while ((n = stream.Read(buf, 0, buf.Length)) > 0)
        {
            lock (_gate)
            {
                _stdout.Write(buf, 0, n);
                Monitor.PulseAll(_gate);
            }
        }
        lock (_gate) Monitor.PulseAll(_gate);
    }

    /// <summary>Blocks until <paramref name="condition"/> holds for the output so far; fails at the deadline.</summary>
    public void WaitForOutput(Func<string, bool> condition, string what)
    {
        var until = DateTime.UtcNow + Deadline;
        lock (_gate)
        {
            while (!condition(Encoding.UTF8.GetString(_stdout.ToArray())))
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || (_stdoutTask.IsCompleted && !condition(Encoding.UTF8.GetString(_stdout.ToArray()))))
                    Assert.Fail($"waiting for {what}: not seen (output so far: {Encoding.UTF8.GetString(_stdout.ToArray())})");
                Monitor.Wait(_gate, left);
            }
        }
    }

    /// <summary>Polls <paramref name="condition"/> on a concrete external state (a file, /proc) until it holds.</summary>
    public static void WaitFor(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + Deadline;
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                Assert.Fail($"waiting for {what}: not seen within {Deadline.TotalSeconds} s");
            tick.WaitForNextTickAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public void Send(string input)
    {
        _process.StandardInput.Write(input);
        _process.StandardInput.Flush();
    }

    public void CloseInput() => _process.StandardInput.Close();

    /// <summary>Waits for the host to exit; returns its exit code, all output and stderr.</summary>
    public (int ExitCode, string Output, string Stderr) WaitForExit()
    {
        if (!_process.WaitForExit(Deadline))
            Assert.Fail($"test host did not exit within {Deadline.TotalSeconds} s; output so far: {Output}");
        _stdoutTask.Wait(Deadline);
        return (_process.ExitCode, Output, _stderrTask.Result);
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }
        _process.Dispose();
    }
}
