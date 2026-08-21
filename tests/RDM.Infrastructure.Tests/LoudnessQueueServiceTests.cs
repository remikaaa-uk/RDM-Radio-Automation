using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Core.Queues;
using RDM.Infrastructure;
using Xunit;

namespace RDM.Infrastructure.Tests;

public sealed class LoudnessQueueServiceTests
{
    private static LoudnessQueueService Build(
        LoudnessQueue             queue,
        Mock<ILoudnessAnalyzer>   mockAnalyzer,
        Mock<IAssetRepository>    mockRepo,
        Mock<IEventBus>           mockBus) =>
        new(queue, mockAnalyzer.Object, mockRepo.Object, mockBus.Object,
            new Mock<ILogger<LoudnessQueueService>>().Object);

    [Fact]
    public async Task ProcessTask_WhenUpdateReturns0_SilentNoOp()
    {
        var queue        = new LoudnessQueue();
        var mockAnalyzer = new Mock<ILoudnessAnalyzer>();
        var mockRepo     = new Mock<IAssetRepository>();
        var mockBus      = new Mock<IEventBus>();

        await queue.Writer.WriteAsync(new LoudnessTask("asset-1", "/audio.mp3"));
        queue.Writer.Complete();

        mockAnalyzer.Setup(a => a.AnalyzeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoudnessResult(-23.0m, -1.0m));
        mockRepo.Setup(r => r.UpdateLoudnessAsync("asset-1", It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0); // asset no longer exists

        var svc = Build(queue, mockAnalyzer, mockRepo, mockBus);
        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await svc.StopAsync(CancellationToken.None);

        // No event, no exception, no disk artefact to clean up
        mockBus.Verify(b => b.PublishAsync(It.IsAny<LoudnessAnalyzedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessTask_WhenAnalyzerThrows_QueueContinuesWithNextTask()
    {
        var queue        = new LoudnessQueue();
        var mockAnalyzer = new Mock<ILoudnessAnalyzer>();
        var mockRepo     = new Mock<IAssetRepository>();
        var mockBus      = new Mock<IEventBus>();

        await queue.Writer.WriteAsync(new LoudnessTask("fail-asset", "/bad.mp3"));
        await queue.Writer.WriteAsync(new LoudnessTask("good-asset", "/good.mp3"));
        queue.Writer.Complete();

        mockAnalyzer.Setup(a => a.AnalyzeAsync("/bad.mp3", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BASSloud error"));
        mockAnalyzer.Setup(a => a.AnalyzeAsync("/good.mp3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoudnessResult(-18.5m, -0.3m));
        mockRepo.Setup(r => r.UpdateLoudnessAsync("good-asset", It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        mockBus.Setup(b => b.PublishAsync(It.IsAny<LoudnessAnalyzedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = Build(queue, mockAnalyzer, mockRepo, mockBus);
        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await svc.StopAsync(CancellationToken.None);

        mockAnalyzer.Verify(a => a.AnalyzeAsync("/good.mp3", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessTask_WhenUpdateReturns1_PublishesLoudnessAnalyzedEvent()
    {
        var queue        = new LoudnessQueue();
        var mockAnalyzer = new Mock<ILoudnessAnalyzer>();
        var mockRepo     = new Mock<IAssetRepository>();
        var mockBus      = new Mock<IEventBus>();

        await queue.Writer.WriteAsync(new LoudnessTask("asset-1", "/audio.mp3"));
        queue.Writer.Complete();

        mockAnalyzer.Setup(a => a.AnalyzeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoudnessResult(-23.0m, -1.2m));
        mockRepo.Setup(r => r.UpdateLoudnessAsync("asset-1", -23.0m, -1.2m, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        mockBus.Setup(b => b.PublishAsync(It.IsAny<LoudnessAnalyzedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = Build(queue, mockAnalyzer, mockRepo, mockBus);
        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await svc.StopAsync(CancellationToken.None);

        mockBus.Verify(b => b.PublishAsync(
            It.Is<LoudnessAnalyzedEvent>(e => e.AssetId == "asset-1" && e.LufsIntegrated == -23.0m && e.TruePeak == -1.2m),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
