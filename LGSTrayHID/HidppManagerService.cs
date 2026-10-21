using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using MessagePipe;
using Microsoft.Extensions.Hosting;

namespace LGSTrayHID
{
    public class HidppManagerService : IHostedService
    {
        private readonly IDistributedPublisher<IPCMessageType, IPCMessage> _publisher;

        public HidppManagerService(IDistributedPublisher<IPCMessageType, IPCMessage> publisher)
        {
            _publisher = publisher;

            HidppManagerContext.Instance.HidppDeviceEvent += async (type, message) =>
            {
#if DEBUG
                if (message is InitMessage initMessage)
                {
                    Console.WriteLine(initMessage.deviceName);
                }
#endif

                // Runs as async void, so an exception here would take down the whole daemon
                try
                {
                    await _publisher.PublishAsync(type, message);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Failed to send {type} for {message.deviceId} to the UI", ex);
                }
            };
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            HidppManagerContext.Instance.Start(cancellationToken);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
