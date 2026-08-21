using System.Threading.Channels;
using RDM.Core.Models;

namespace RDM.Core.Queues;

public sealed class LoudnessQueue
{
    private readonly Channel<LoudnessTask> _channel = Channel.CreateBounded<LoudnessTask>(
        new BoundedChannelOptions(2048)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    public ChannelWriter<LoudnessTask> Writer => _channel.Writer;
    public ChannelReader<LoudnessTask> Reader => _channel.Reader;
}
