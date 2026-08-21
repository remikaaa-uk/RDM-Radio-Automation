using System.Threading.Channels;
using RDM.Core.Models;

namespace RDM.Core.Queues;

public sealed class CueAnalysisQueue
{
    private readonly Channel<CueAnalysisTask> _channel = Channel.CreateBounded<CueAnalysisTask>(
        new BoundedChannelOptions(2048)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    public ChannelWriter<CueAnalysisTask> Writer => _channel.Writer;
    public ChannelReader<CueAnalysisTask> Reader => _channel.Reader;
}
