using System.Threading.Channels;
using RDM.Core.Models;

namespace RDM.Core.Queues;

public sealed class WaveformQueue
{
    private readonly Channel<WaveformTask> _channel = Channel.CreateBounded<WaveformTask>(
        new BoundedChannelOptions(2048)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    public ChannelWriter<WaveformTask> Writer => _channel.Writer;
    public ChannelReader<WaveformTask> Reader => _channel.Reader;
}
