using System.Collections.Generic;
using System.Collections.Specialized;
using ParityProof.App.ViewModels;
using Xunit;

namespace ParityProof.Engine.Tests;

public sealed class ObservableRangeCollectionTests
{
    [Fact]
    public void ReplaceAll_FiresSingleResetEvent_InsteadOfMultipleAddEvents()
    {
        ObservableRangeCollection<string> collection = new();
        int eventCount = 0;
        NotifyCollectionChangedAction? lastAction = null;

        collection.CollectionChanged += (s, e) =>
        {
            eventCount++;
            lastAction = e.Action;
        };

        List<string> newItems = new() { "A", "B", "C", "D", "E" };
        collection.ReplaceAll(newItems);

        Assert.Equal(1, eventCount);
        Assert.Equal(NotifyCollectionChangedAction.Reset, lastAction);
        Assert.Equal(5, collection.Count);
        Assert.Equal("A", collection[0]);
        Assert.Equal("E", collection[4]);
    }

    [Fact]
    public void AddRange_FiresSingleResetEvent()
    {
        ObservableRangeCollection<int> collection = new() { 1, 2 };
        int eventCount = 0;
        NotifyCollectionChangedAction? lastAction = null;

        collection.CollectionChanged += (s, e) =>
        {
            eventCount++;
            lastAction = e.Action;
        };

        List<int> additionalItems = new() { 3, 4, 5 };
        collection.AddRange(additionalItems);

        Assert.Equal(1, eventCount);
        Assert.Equal(NotifyCollectionChangedAction.Reset, lastAction);
        Assert.Equal(5, collection.Count);
    }
}
