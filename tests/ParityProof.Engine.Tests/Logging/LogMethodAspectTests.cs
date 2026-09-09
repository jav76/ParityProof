using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ParityProof.Core.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace ParityProof.Engine.Tests.Logging;

public sealed class TestLogSink : ILogEventSink
{
    private readonly object _lock = new();
    private readonly List<LogEvent> _events = new();

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return _events.ToList();
            }
        }
    }

    public void Emit(LogEvent logEvent)
    {
        lock (_lock)
        {
            _events.Add(logEvent);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _events.Clear();
        }
    }
}

[LogMethod]
public class SampleDecoratedClass
{
    public int Multiply(int a, int b) => a * b;

    public void ExecuteAction(string text)
    {
    }

    public void FailSync() => throw new InvalidOperationException("Sync boom");

    public async Task<string> ComputeAsync(string prefix, int value)
    {
        await Task.Yield();
        return $"{prefix}_{value}";
    }

    public async Task AsyncVoidTask()
    {
        await Task.Yield();
    }

    public async Task AsyncFailTask()
    {
        await Task.Yield();
        throw new ApplicationException("Async boom");
    }

    public byte[] EchoBuffer(byte[] buffer) => buffer;
}

public class SampleMethodDecoratedClass
{
    [LogMethod(LogEventLevel.Information)]
    public string CustomLevelMethod(string item) => $"Processed {item}";

    public string UndecoratedMethod(string item) => $"Raw {item}";
}

[Collection("SerilogTestCollection")]
public sealed class LogMethodAspectTests
{
    private readonly TestLogSink _sink = new();

    public LogMethodAspectTests()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    private async Task WaitForLogCountAsync(int expectedCount, int timeoutMs = 1000)
    {
        int elapsed = 0;
        while (_sink.Events.Count < expectedCount && elapsed < timeoutMs)
        {
            await Task.Delay(20);
            elapsed += 20;
        }
    }

    [Fact]
    public void SynchronousMethod_LogsEntryAndExitWithArgumentsAndResult()
    {
        _sink.Clear();
        SampleDecoratedClass service = new();

        int result = service.Multiply(6, 7);

        Assert.Equal(42, result);
        IReadOnlyList<LogEvent> logs = _sink.Events;

        Assert.True(logs.Count >= 2, $"Expected at least 2 logs, got {logs.Count}");

        LogEvent entryLog = logs[0];
        string entryMessage = entryLog.RenderMessage();
        Assert.Contains("Entering with arguments", entryMessage);
        Assert.Contains("a: 6", entryMessage);
        Assert.Contains("b: 7", entryMessage);

        LogEvent exitLog = logs[1];
        string exitMessage = exitLog.RenderMessage();
        Assert.Contains("Exited in", exitMessage);
        Assert.Contains("with return value: 42", exitMessage);
    }

    [Fact]
    public void SynchronousVoidMethod_LogsEntryAndExitWithoutReturnValue()
    {
        _sink.Clear();
        SampleDecoratedClass service = new();

        service.ExecuteAction("ping");

        IReadOnlyList<LogEvent> logs = _sink.Events;
        Assert.True(logs.Count >= 2);

        string exitMessage = logs[1].RenderMessage();
        Assert.Contains("Exited in", exitMessage);
        Assert.DoesNotContain("with return value", exitMessage);
    }

    [Fact]
    public void SynchronousMethod_ThrowsException_LogsErrorAndRethrows()
    {
        _sink.Clear();
        SampleDecoratedClass service = new();

        Assert.Throws<InvalidOperationException>(() => service.FailSync());

        IReadOnlyList<LogEvent> logs = _sink.Events;
        Assert.True(logs.Count >= 2);

        LogEvent errorLog = logs.Last();
        Assert.Equal(LogEventLevel.Error, errorLog.Level);
        Assert.Contains("Failed after", errorLog.RenderMessage());
        Assert.Contains("Sync boom", errorLog.RenderMessage());
    }

    [Fact]
    public async Task AsyncMethod_GenericTask_LogsEntryAndAsyncCompletedWithResult()
    {
        _sink.Clear();
        SampleDecoratedClass service = new();

        string result = await service.ComputeAsync("item", 99);

        Assert.Equal("item_99", result);
        await WaitForLogCountAsync(2);

        IReadOnlyList<LogEvent> logs = _sink.Events;
        Assert.True(logs.Count >= 2);

        LogEvent entryLog = logs[0];
        Assert.Contains("Entering with arguments", entryLog.RenderMessage());
        Assert.Contains("prefix: \"item\"", entryLog.RenderMessage());
        Assert.Contains("value: 99", entryLog.RenderMessage());

        LogEvent exitLog = logs[1];
        Assert.Contains("Async completed in", exitLog.RenderMessage());
        Assert.Contains("with return value: \"item_99\"", exitLog.RenderMessage());
    }

    [Fact]
    public async Task AsyncMethod_NonGenericTask_LogsEntryAndAsyncCompleted()
    {
        _sink.Clear();
        SampleDecoratedClass service = new();

        await service.AsyncVoidTask();
        await WaitForLogCountAsync(2);

        IReadOnlyList<LogEvent> logs = _sink.Events;
        Assert.True(logs.Count >= 2);

        LogEvent exitLog = logs[1];
        Assert.Contains("Async completed in", exitLog.RenderMessage());
        Assert.DoesNotContain("with return value", exitLog.RenderMessage());
    }

    [Fact]
    public async Task AsyncMethod_FaultedTask_LogsError()
    {
        _sink.Clear();
        SampleDecoratedClass service = new();

        await Assert.ThrowsAsync<ApplicationException>(async () => await service.AsyncFailTask());
        await WaitForLogCountAsync(2);

        IReadOnlyList<LogEvent> logs = _sink.Events;
        Assert.True(logs.Count >= 2);

        LogEvent errorLog = logs.Last();
        Assert.Equal(LogEventLevel.Error, errorLog.Level);
        Assert.Contains("Async failed after", errorLog.RenderMessage());
        Assert.Contains("Async boom", errorLog.RenderMessage());
    }

    [Fact]
    public void LargeBuffer_IsTruncatedInLogOutput()
    {
        _sink.Clear();
        SampleDecoratedClass service = new();

        byte[] largeArray = new byte[1024];
        byte[] result = service.EchoBuffer(largeArray);

        Assert.NotNull(result);
        IReadOnlyList<LogEvent> logs = _sink.Events;
        Assert.True(logs.Count >= 2);

        string entryMessage = logs[0].RenderMessage();
        Assert.Contains("<byte[1024]>", entryMessage);

        string exitMessage = logs[1].RenderMessage();
        Assert.Contains("<byte[1024]>", exitMessage);
    }

    [Fact]
    public void CustomLogLevel_LogsAtSpecifiedLevel()
    {
        _sink.Clear();
        SampleMethodDecoratedClass service = new();

        string result = service.CustomLevelMethod("alpha");

        Assert.Equal("Processed alpha", result);
        IReadOnlyList<LogEvent> logs = _sink.Events;
        Assert.True(logs.Count >= 2);

        Assert.Equal(LogEventLevel.Information, logs[0].Level);
        Assert.Equal(LogEventLevel.Information, logs[1].Level);
    }

    [Fact]
    public void UndecoratedMethod_DoesNotEmitLogs()
    {
        _sink.Clear();
        SampleMethodDecoratedClass service = new();

        string result = service.UndecoratedMethod("beta");

        Assert.Equal("Raw beta", result);
        Assert.Empty(_sink.Events);
    }

    [Fact]
    public void FormatValue_HandlesVariousTypesCorrectly()
    {
        Assert.Equal("null", AppLogger.FormatValue(null));
        Assert.Equal("\"hello\"", AppLogger.FormatValue("hello"));

        string longString = new string('x', 300);
        string formattedLong = AppLogger.FormatValue(longString);
        Assert.Contains("... (length: 300)", formattedLong);

        byte[] bytes = new byte[16];
        Assert.Equal("<byte[16]>", AppLogger.FormatValue(bytes));

        int[] numbers = new int[] { 1, 2, 3 };
        Assert.Equal("[1, 2, 3]", AppLogger.FormatValue(numbers));

        int[] manyNumbers = Enumerable.Range(1, 20).ToArray();
        string formattedMany = AppLogger.FormatValue(manyNumbers);
        Assert.Contains("(truncated)", formattedMany);
    }
}
