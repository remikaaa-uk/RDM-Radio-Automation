using System.Threading.Channels;
using RDM.Core.Models;

namespace RDM.Core.Queues;

public sealed class BpmQueue
{
    private readonly Channel<BpmTask> _channel = Channel.CreateBounded<BpmTask>(
        new BoundedChannelOptions(2048)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    public ChannelWriter<BpmTask> Writer => _channel.Writer;
    public ChannelReader<BpmTask> Reader => _channel.Reader;
}
