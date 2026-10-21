using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using MessagePack.Resolvers;
using System.Collections.Concurrent;
using MessagePipe;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace LGSTrayCore.Managers
{
    public class LGSTrayHIDManager : IDeviceManager, IHostedService, IDisposable
    {
        #region IDisposable
        private Func<Task>? _diposeSubs;
        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _ = _diposeSubs?.Invoke();
                    _diposeSubs = null;
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~LGSTrayHIDDaemon()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
        #endregion

        private static readonly TimeSpan FastFailWindow = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan FastFailBackoff = TimeSpan.FromMinutes(1);

        private readonly CancellationTokenSource _cts = new();
        private CancellationTokenSource? _daemonCts;

        // Devices reported by the daemon, so they can be shown as disconnected while it restarts
        private readonly ConcurrentDictionary<string, byte> _nativeDeviceIds = new();

        private readonly IDistributedSubscriber<IPCMessageType, IPCMessage> _subscriber;
        private readonly IPublisher<IPCMessage> _deviceEventBus;

        public LGSTrayHIDManager(
            IDistributedSubscriber<IPCMessageType, IPCMessage> subscriber,
            IPublisher<IPCMessage> deviceEventBus
        )
        {
            _subscriber = subscriber;
            _deviceEventBus = deviceEventBus;
        }

        private async Task<int> DaemonLoop()
        {
            _daemonCts = new();

            using Process proc = new();
            proc.StartInfo = new()
            {
                RedirectStandardError = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                FileName = Path.Combine(AppContext.BaseDirectory, "LGSTrayHID.exe"),
                Arguments = Environment.ProcessId.ToString(),
                UseShellExecute = true,
                CreateNoWindow = true
            };
            proc.Start();

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _daemonCts.Token);
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (Exception)
            {
                if (!proc.HasExited)
                {
                    proc.Kill();
                }
            }
            finally
            {
                _daemonCts.Dispose();
                _daemonCts = null;
            }

            // Its devices can't report anything until a new daemon finds them again
            foreach (var deviceId in _nativeDeviceIds.Keys)
            {
                _deviceEventBus.Publish(UpdateMessage.Offline(deviceId));
            }

            await Task.Delay(1000);
            return proc.ExitCode;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var sub1 = await _subscriber.SubscribeAsync(
                IPCMessageType.INIT,
                x =>
                {
                    var initMessage = (InitMessage)x;
                    _nativeDeviceIds[initMessage.deviceId] = 0;
                    //_logiDeviceCollection.OnInitMessage(initMessage);
                    _deviceEventBus.Publish(initMessage);
                },
                cancellationToken
            );

            var sub2 = await _subscriber.SubscribeAsync(
                IPCMessageType.UPDATE,
                x =>
                {
                    var updateMessage = (UpdateMessage)x;
                    //_logiDeviceCollection.OnUpdateMessage(updateMessage);
                    _deviceEventBus.Publish(updateMessage);
                },
                cancellationToken
            );

            _diposeSubs = async () =>
            {
                await sub1.DisposeAsync();
                await sub2.DisposeAsync();
            };

            _ = Task.Run(async () =>
            {
                int fastFailCount = 0;

                while (!_cts.Token.IsCancellationRequested)
                {
                    DateTime then = DateTime.Now;
                    int ret = await DaemonLoop();
                    TimeSpan uptime = DateTime.Now - then;

                    if (_cts.Token.IsCancellationRequested) { break; }

                    // Daemon returns -1 on .Kill(), i.e. the user asked to rediscover devices
                    if ((ret != -1) && (uptime < FastFailWindow))
                    {
                        fastFailCount++;
                    }
                    else
                    {
                        fastFailCount = 0;
                    }

                    AppLog.Warn($"HID daemon exited with code {ret} after {uptime:hh\\:mm\\:ss}, restarting");

                    if (fastFailCount > 3)
                    {
                        AppLog.Error($"HID daemon keeps exiting right after starting, waiting {FastFailBackoff.TotalSeconds:f0}s before trying again");
                        try
                        {
                            await Task.Delay(FastFailBackoff, _cts.Token);
                        }
                        catch (OperationCanceledException) { break; }
                        fastFailCount = 0;
                    }
                }
            }, CancellationToken.None);

            return;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cts.Cancel();

            return Task.CompletedTask;
        }

        public void RediscoverDevices()
        {
            _daemonCts?.Cancel();
        }
    }
}
