using System.Diagnostics;
using System.Text;
using Engine.Hosting.Lock;
using Shouldly;

namespace Engine.Tests.Persistence;

/// <summary>
/// Two real engine processes - <c>dotnet engine.dll</c>, Program.cs and all - against one real
/// database: the second refuses to start, says why, and exits non-zero.
/// </summary>
/// <remarks>
/// The engine's agent service points at a port nothing listens on, so the first engine's cycles
/// fail fast and harmlessly; it only has to be running and holding the lock. HOME is a fresh
/// directory so a developer's user-secrets store cannot change what these processes read.
/// </remarks>
[Collection(TradingDatabaseCollection.Name)]
public sealed class EngineProcessLockTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private readonly TradingDatabaseFixture _database;
    private readonly string _home = Directory.CreateTempSubdirectory("engine-lock-home-").FullName;
    private readonly List<EngineProcess> _engines = [];

    public EngineProcessLockTests(TradingDatabaseFixture database) => _database = database;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(_database.ResetAsync());

    public async ValueTask DisposeAsync()
    {
        foreach (var engine in _engines)
            engine.Dispose();
        await _database.ResetAsync();
        Directory.Delete(_home, recursive: true);
    }

    [Fact]
    public async Task A_second_engine_process_refuses_to_start_with_a_clear_error_and_a_non_zero_exit()
    {
        var first = Start();
        await first.WaitForLineAsync("Took the engine lock", Patience);

        var second = Start();
        var exitCode = await second.WaitForExitAsync(Patience);

        exitCode.ShouldBe(EngineLockService.AnotherEngineExitCode);
        second.Output.ShouldContain("Another engine is already running against this database");
        second.Output.ShouldContain("tas-engine (instance lock)");
        second.Output.ShouldNotContain("Trading mode is"); // refused before the trading worker started
        first.HasExited.ShouldBeFalse();
    }

    [Fact]
    public async Task Once_the_first_engine_is_gone_the_next_one_starts()
    {
        var first = Start();
        await first.WaitForLineAsync("Took the engine lock", Patience);
        first.Kill(); // no clean shutdown: the session's end is what releases the lock

        var next = Start();
        await next.WaitForLineAsync("Took the engine lock", Patience);
        next.HasExited.ShouldBeFalse();
    }

    private EngineProcess Start()
    {
        var engine = new EngineProcess(new ProcessStartInfo("dotnet", Path.Combine(AppContext.BaseDirectory, "engine.dll"))
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment =
            {
                ["HOME"] = _home,
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["Database__ConnectionString"] = _database.ConnectionString,
                ["AgentService__BaseUrl"] = "http://127.0.0.1:9",
                ["AgentService__ApiKey"] = "engine-process-test-key",
                ["AgentService__OutcomesHmacSecret"] = "engine-process-test-hmac",
                ["Trading__Mode"] = "Shadow",
                ["OTEL_SDK_DISABLED"] = "true",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
                ["Health__HeartbeatFile"] = "",
            },
        });
        _engines.Add(engine);
        return engine;
    }

    private sealed class EngineProcess : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();
        private readonly object _gate = new();

        public EngineProcess(ProcessStartInfo start)
        {
            _process = new Process { StartInfo = start };
            _process.OutputDataReceived += (_, line) => Append(line.Data);
            _process.ErrorDataReceived += (_, line) => Append(line.Data);
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public string Output
        {
            get
            {
                lock (_gate)
                    return _output.ToString();
            }
        }

        public bool HasExited => _process.HasExited;

        public async Task WaitForLineAsync(string text, TimeSpan patience)
        {
            var deadline = DateTime.UtcNow + patience;
            while (!Output.Contains(text, StringComparison.Ordinal))
            {
                if (_process.HasExited)
                    throw new ShouldAssertException($"The engine exited ({_process.ExitCode}) before logging '{text}':\n{Output}");
                if (DateTime.UtcNow > deadline)
                    throw new ShouldAssertException($"The engine never logged '{text}':\n{Output}");
                await Task.Delay(100, Token);
            }
        }

        public async Task<int> WaitForExitAsync(TimeSpan patience)
        {
            try
            {
                await _process.WaitForExitAsync(Token).WaitAsync(patience, Token);
            }
            catch (TimeoutException)
            {
                throw new ShouldAssertException($"The engine was still running after {patience}:\n{Output}");
            }

            _process.WaitForExit(); // drains the redirected streams
            return _process.ExitCode;
        }

        public void Kill()
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }

        public void Dispose()
        {
            if (!_process.HasExited)
                Kill();
            _process.Dispose();
        }

        private void Append(string? line)
        {
            if (line is null)
                return;
            lock (_gate)
                _output.AppendLine(line);
        }
    }
}
