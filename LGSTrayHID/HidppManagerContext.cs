using static LGSTrayHID.HidApi.HidApi;
using static LGSTrayHID.HidApi.HidApiWinApi;
using static LGSTrayHID.HidApi.HidApiHotPlug;
using LGSTrayHID.HidApi;
using LGSTrayPrimitives;
using System.Collections.Concurrent;
using LGSTrayPrimitives.MessageStructs;

namespace LGSTrayHID
{
    public sealed class HidppManagerContext
    {
        public static readonly HidppManagerContext _instance = new();
        public static HidppManagerContext Instance => _instance;

        // The UI restarts the daemon whenever it exits; this code marks a deliberate restart in its log
        public const int RestartExitCode = 3;

        // How long a receiver that stopped answering has to be reported as removed before the
        // daemon restarts itself to recover it
        private const int ReadFailureGraceMs = 5000;

        // Hotplug callbacks and the init thread both touch these, so access goes through _mapLock
        private readonly object _mapLock = new();
        private readonly Dictionary<string, Guid> _containerMap = [];
        private readonly Dictionary<Guid, HidppDevices> _deviceMap = [];
        private readonly BlockingCollection<(string Path, HidppMessageType Type)> _deviceQueue = [];

        // The same physical device can be reachable through more than one path at once,
        // e.g. via its receiver and over a USB cable while charging.
        private readonly Dictionary<string, HashSet<HidppDevice>> _onlineSources = [];

        public delegate void HidppDeviceEventHandler(IPCMessageType messageType, IPCMessage message);

        public event HidppDeviceEventHandler? HidppDeviceEvent;

        private HidppManagerContext()
        {

        }

        static HidppManagerContext()
        {
            _ = HidInit();
        }

        public void SignalDeviceEvent(IPCMessageType messageType, IPCMessage message)
        {
            HidppDeviceEvent?.Invoke(messageType, message);
        }

        public void MarkOnline(HidppDevice device)
        {
            lock (_onlineSources)
            {
                if (!_onlineSources.TryGetValue(device.Identifier, out var sources))
                {
                    sources = [];
                    _onlineSources[device.Identifier] = sources;
                }
                sources.Add(device);
            }
        }

        public void MarkOffline(HidppDevice device, bool signal)
        {
            if (string.IsNullOrEmpty(device.Identifier)) { return; }

            lock (_onlineSources)
            {
                if (_onlineSources.TryGetValue(device.Identifier, out var sources))
                {
                    sources.Remove(device);
                    if (sources.Count > 0)
                    {
                        return;
                    }

                    _onlineSources.Remove(device.Identifier);
                }
            }

            if (signal)
            {
                SignalDeviceEvent(IPCMessageType.UPDATE, UpdateMessage.Offline(device.Identifier));
            }
        }

        private unsafe int EnqueueDevice(HidHotPlugCallbackHandle _, HidDeviceInfo* device, HidApiHotPlugEvent hidApiHotPlugEvent, nint __)
        {
            // An exception escaping a native callback kills the process
            try
            {
                if (hidApiHotPlugEvent == HidApiHotPlugEvent.HID_API_HOTPLUG_EVENT_DEVICE_ARRIVED)
                {
                    // hidapi owns the device info, so copy what's needed before it can be freed
                    var messageType = (*device).GetHidppMessageType();
                    if (messageType is HidppMessageType.SHORT or HidppMessageType.LONG)
                    {
                        _deviceQueue.Add(((*device).GetPath(), messageType));
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to queue arriving HID device", ex);
            }

            return 0;
        }

        private async Task<int> InitDevice(string devPath, HidppMessageType messageType)
        {
            HidDevicePtr dev = HidOpenPath(devPath);
            if (dev == IntPtr.Zero)
            {
                AppLog.Warn($"Failed to open {messageType} interface {devPath}");
                return 0;
            }

            _ = HidWinApiGetContainerId(dev, out Guid containerId);

#if DEBUG
            Console.WriteLine(devPath);
            Console.WriteLine(containerId.ToString());
            Console.WriteLine();
#endif

            HidppDevices receiver;
            lock (_mapLock)
            {
                if (!_deviceMap.TryGetValue(containerId, out receiver!))
                {
                    receiver = new(containerId.ToString());
                    receiver.ReadFailed += OnReadFailed;
                    _deviceMap[containerId] = receiver;
                }
                _containerMap[devPath] = containerId;
            }

            if (receiver.HasHandle(messageType))
            {
                // Windows re-announced an interface without reporting its removal, so the old handle is dead
                HidClose(dev);
                RequestRestart($"Receiver {containerId} re-announced its {messageType} interface without being removed");
                return 0;
            }

            AppLog.Info($"Opened {messageType} interface of receiver {containerId}");

            switch (messageType)
            {
                case HidppMessageType.SHORT:
                    await receiver.SetDevShort(dev);
                    break;
                case HidppMessageType.LONG:
                    await receiver.SetDevLong(dev);
                    break;
            }

            return 0;
        }

        private unsafe int DeviceLeft(HidHotPlugCallbackHandle callbackHandle, HidDeviceInfo* deviceInfo, HidApiHotPlugEvent hidApiHotPlugEvent, nint userData)
        {
            try
            {
                string devPath = (*deviceInfo).GetPath();
                HidppDevices? removed = null;
                Guid containerId = Guid.Empty;

                lock (_mapLock)
                {
                    if (_containerMap.TryGetValue(devPath, out containerId) && _deviceMap.Remove(containerId, out removed))
                    {
                        foreach (var path in _containerMap.Where(x => x.Value == containerId).Select(x => x.Key).ToList())
                        {
                            _containerMap.Remove(path);
                        }
                    }
                }

                if (removed != null)
                {
                    AppLog.Info($"Receiver {containerId} removed");
                    removed.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to handle HID device removal", ex);
            }

            return 0;
        }

        private void OnReadFailed(HidppDevices receiver)
        {
            // A real unplug also fails the read, shortly before Windows reports the removal
            _ = Task.Run(async () =>
            {
                await Task.Delay(ReadFailureGraceMs);

                bool stillListed;
                lock (_mapLock)
                {
                    stillListed = _deviceMap.ContainsValue(receiver);
                }

                if (stillListed && !receiver.Disposed)
                {
                    RequestRestart($"Receiver {receiver.Label} stopped responding but is still connected");
                }
            });
        }

        private static void RequestRestart(string reason)
        {
            AppLog.Warn($"{reason}; restarting the daemon to recover");
            Environment.Exit(RestartExitCode);
        }

        public void Start(CancellationToken cancellationToken)
        {
            new Thread(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var (path, type) = _deviceQueue.Take();
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        await InitDevice(path, type);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error($"Failed to initialise HID device {path}", ex);
                    }
                }
            }).Start();

            unsafe
            {
                HidHotplugRegisterCallback(0x046D, 0x00, HidApiHotPlugEvent.HID_API_HOTPLUG_EVENT_DEVICE_ARRIVED, HidApiHotPlugFlag.HID_API_HOTPLUG_ENUMERATE, EnqueueDevice, IntPtr.Zero, (int*)IntPtr.Zero);
                HidHotplugRegisterCallback(0x046D, 0x00, HidApiHotPlugEvent.HID_API_HOTPLUG_EVENT_DEVICE_LEFT, HidApiHotPlugFlag.NONE, DeviceLeft, IntPtr.Zero, (int*)IntPtr.Zero);
            }
        }

        public async Task ForceBatteryUpdates()
        {
            List<HidppDevices> receivers;
            lock (_mapLock)
            {
                receivers = [.. _deviceMap.Values];
            }

            foreach (var receiver in receivers)
            {
                var tasks = receiver.DeviceCollection
                    .Select(x => x.Value)
                    .Select(x => x.UpdateBattery(true));

                await Task.WhenAll(tasks);
            }
        }
    }
}
