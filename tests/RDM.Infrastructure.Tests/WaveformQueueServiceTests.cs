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

public sealed class WaveformQueueServiceTests
{
    private static WaveformQueueService Build(
        WaveformQueue              queue,
        Mock<IWaveformGenerator>   mockGen,
        Mock<IWaveformRepository>  mockWaveformRepo,
        Mock<IEventBus>            mockBus) =>
        new(queue, mockGen.Object, mockWaveformRepo.Object, mockBus.Object,
            new Mock<ILogger<WaveformQueueService>>().Object);

    [Fact]
    public async Task ProcessTask_WhenGenerationSucceeds_SavesAndPublishesEvent()
    {
        var queue    = new WaveformQueue();
        var mockGen  = new Mock<IWaveformGenerator>();
        var mockRepo = new Mock<IWaveformRepository>();
        var mockBus  = new Mock<IEventBus>();

        var fakeBytes = new byte[] { 1, 2, 3 };
        await queue.Writer.WriteAsync(new WaveformTask("asset-1", "/audio.mp3"));
        queue.Writer.Complete();

        mockGen.Setup(g => g.GenerateAsync("/audio.mp3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeBytes);
        mockRepo.Setup(r => r.SaveAsync("asset-1", fakeBytes, WaveformConstants.NPoints, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockBus.Setup(b => b.PublishAsync(It.IsAny<WaveformReadyEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = Build(queue, mockGen, mockRepo, mockBus);
        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await svc.StopAsync(CancellationToken.None);

        mockRepo.Verify(r => r.SaveAsync(
            "asset-1", fakeBytes, WaveformConstants.NPoints, It.IsAny<CancellationToken>()), Times.Once);
        mockBus.Verify(b => b.PublishAsync(
            It.Is<WaveformReadyEvent>(e => e.AssetId == "asset-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessTask_WhenGeneratorThrows_QueueContinuesWithNextTask()
    {
        var queue    = new WaveformQueue();
        var mockGen  = new Mock<IWaveformGenerator>();
        var mockRepo = new Mock<IWaveformRepository>();
        var mockBus  = new Mock<IEventBus>();

        await queue.Writer.WriteAsync(new WaveformTask("fail-asset", "/bad.mp3"));
        await queue.Writer.WriteAsync(new WaveformTask("good-asset", "/good.mp3"));
        queue.Writer.Complete();

        mockGen.Setup(g => g.GenerateAsync("/bad.mp3", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BASS decode error"));
        mockGen.Setup(g => g.GenerateAsync("/good.mp3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 1, 2, 3 });
        mockRepo.Setup(r => r.SaveAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockBus.Setup(b => b.PublishAsync(It.IsAny<WaveformReadyEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = Build(queue, mockGen, mockRepo, mockBus);
        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await svc.StopAsync(CancellationToken.None);

        mockGen.Verify(g => g.GenerateAsync("/good.mp3", It.IsAny<CancellationToken>()), Times.Once);
        mockBus.Verify(b => b.PublishAsync(
            It.Is<WaveformReadyEvent>(e => e.AssetId == "good-asset"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessTask_WhenSaveThrows_QueueContinuesWithNextTask()
    {
        var queue    = new WaveformQueue();
        var mockGen  = new Mock<IWaveformGenerator>();
        var mockRepo = new Mock<IWaveformRepository>();
        var mockBus  = new Mock<IEventBus>();

        var bytes = new byte[] { 1, 2, 3 };
        await queue.Writer.WriteAsync(new WaveformTask("fail-save", "/audio1.mp3"));
        await queue.Writer.WriteAsync(new WaveformTask("good-save", "/audio2.mp3"));
        queue.Writer.Complete();

        mockGen.Setup(g => g.GenerateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(bytes);
        mockRepo.Setup(r => r.SaveAsync("fail-save", It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("FK constraint violated"));
        mockRepo.Setup(r => r.SaveAsync("good-save", It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockBus.Setup(b => b.PublishAsync(It.IsAny<WaveformReadyEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = Build(queue, mockGen, mockRepo, mockBus);
        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await svc.StopAsync(CancellationToken.None);

        mockBus.Verify(b => b.PublishAsync(
            It.Is<WaveformReadyEvent>(e => e.AssetId == "good-save"),
            It.IsAny<CancellationToken>()), Times.Once);
        mockBus.Verify(b => b.PublishAsync(
            It.Is<WaveformReadyEvent>(e => e.AssetId == "fail-save"),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
