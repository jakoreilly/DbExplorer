using DbExplorer.Core.Models;
using DbExplorer.Options;
using DbExplorer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class SystemAnalyserStoreTests
{
    private static SystemAnalyserStore Create(int bufferSize = 5000, bool enabled = true) =>
        new(Microsoft.Extensions.Options.Options.Create(new AnalyserOptions { Enabled = enabled, BufferSize = bufferSize }),
            NullLogger<SystemAnalyserStore>.Instance);

    private static DbActionEvent Evt(bool success = true, DateTimeOffset? ts = null) =>
        new(ts ?? DateTimeOffset.UtcNow, "MySql", DbActionCategory.Metadata, "op",
            null, null, 10, 1, success, success ? null : "TestException",
            success ? null : "boom", "tester");

    [Fact]
    public void Record_EvictsOldest_WhenBufferFull()
    {
        var store = Create(bufferSize: 100);
        for (var i = 0; i < 150; i++) store.Record(Evt());
        Assert.Equal(100, store.GetEvents(TimeSpan.FromHours(1)).Count);
    }

    [Fact]
    public void GetEvents_FiltersByWindow_AndReturnsNewestFirst()
    {
        var store = Create(bufferSize: 100);
        store.Record(Evt(ts: DateTimeOffset.UtcNow.AddHours(-2)));
        var recent = Evt();
        store.Record(recent);
        var events = store.GetEvents(TimeSpan.FromMinutes(30));
        Assert.Single(events);
        Assert.Equal(recent.Timestamp, events[0].Timestamp);
    }

    [Fact]
    public void RecordError_CapturesTypeAndMessage()
    {
        var store = Create(bufferSize: 100);
        store.RecordError("MySql", DbActionCategory.AdHocQuery, "RunQuery",
            new InvalidOperationException("bad sql"));
        var e = Assert.Single(store.GetEvents(TimeSpan.FromMinutes(5)));
        Assert.False(e.Success);
        Assert.Equal("InvalidOperationException", e.ErrorType);
        Assert.Equal("bad sql", e.ErrorMessage);
    }

    [Fact]
    public void Record_IsNoOp_WhenDisabled()
    {
        var store = Create(bufferSize: 100, enabled: false);
        store.Record(Evt());
        Assert.Empty(store.GetEvents(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void OnEvent_Fires_AfterRecord()
    {
        var store = Create(bufferSize: 100);
        var fired = 0;
        store.OnEvent += () => fired++;
        store.Record(Evt());
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task Record_IsThreadSafe_UnderParallelWrites()
    {
        var store = Create(bufferSize: 10_000);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => { for (var i = 0; i < 500; i++) store.Record(Evt()); })));
        Assert.Equal(4000, store.GetEvents(TimeSpan.FromHours(1)).Count);
    }
}
