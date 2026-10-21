using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayHID.Features;
using System.Text;

using static LGSTrayHID.HidppDevices;

#if DEBUG
using Log = System.Console;
#else
using Log = System.Diagnostics.Debug;
#endif

namespace LGSTrayHID
{
    public class HidppDevice
    {
        private readonly SemaphoreSlim _initSemaphore = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private Func<HidppDevice, Task<BatteryUpdateReturn?>>? _getBatteryAsync;
        private Func<Hidpp20, BatteryUpdateReturn>? _parseBattery;
        private byte _batteryFeatureIdx;
        private volatile bool _isOnline;
        private bool _pollFailing;
        private readonly object _batteryLock = new();

        public string DeviceName { get; private set; } = string.Empty;
        public int DeviceType { get; private set; } = 3;
        public string Identifier { get; private set; } = string.Empty;

        private BatteryUpdateReturn lastBatteryReturn;
        private DateTimeOffset lastUpdate = DateTimeOffset.MinValue;

        private readonly HidppDevices _parent;
        public HidppDevices Parent => _parent;

        private readonly byte _deviceIdx;
        public byte DeviceIdx => _deviceIdx;

        private readonly Dictionary<ushort, byte> _featureMap = [];
        public Dictionary<ushort, byte> FeatureMap => _featureMap;

        public HidppDevice(HidppDevices parent, byte deviceIdx, string identifier = "")
        {
            _parent = parent;
            _deviceIdx = deviceIdx;
            Identifier = identifier;
        }

        public void Stop(bool signalOffline)
        {
            _cts.Cancel();
            _isOnline = false;
            HidppManagerContext.Instance.MarkOffline(this, signalOffline);
        }

        public bool TryHandleEvent(Hidpp20 message)
        {
            if (!_isOnline || _parseBattery == null || message.Length < 7)
            {
                return false;
            }

            // Battery features broadcast their status as event 0 with a software ID of 0
            if ((message.GetFeatureIndex() != _batteryFeatureIdx) || (message.GetSoftwareId() != 0) || (message.GetFunctionId() != 0))
            {
                return false;
            }

#if DEBUG
            Log.WriteLine($"{DeviceName} battery event");
#endif
            ReportBattery(_parseBattery(message));
            return true;
        }

        // A device that just woke up can take a while before it answers, so keep trying for about a minute
        private static readonly int[] InitRetryDelaysMs = [1000, 2000, 5000, 10000, 30000];

        public async Task InitWithRetriesAsync()
        {
            for (int attempt = 1; attempt <= InitRetryDelaysMs.Length; attempt++)
            {
                try
                {
                    await Task.Delay(InitRetryDelaysMs[attempt - 1], _cts.Token);
                    if (await InitAsync()) { return; }
                    AppLog.Warn($"Receiver {_parent.Label}: device {_deviceIdx} didn't answer (attempt {attempt})");
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    // Replaced by a newer connection, or the receiver went away
                    return;
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Receiver {_parent.Label}: setting up device {_deviceIdx} failed (attempt {attempt}): {ex.GetType().Name}: {ex.Message}");
                }

                if (_cts.IsCancellationRequested || _parent.Disposed) { return; }
            }

            AppLog.Error($"Receiver {_parent.Label}: gave up setting up device {_deviceIdx} until it reconnects");
        }

        // Returns false if the device didn't answer and is worth retrying
        public async Task<bool> InitAsync()
        {
            await _initSemaphore.WaitAsync();
            try
            {
                Hidpp20 ret;

                // Sync Ping
                int successCount = 0;
                int successThresh = 3;
                for (int i = 0; i < 10; i++)
                {
                    var ping = await _parent.Ping20(_deviceIdx, 100);
                    if (ping)
                    {
                        successCount++;
                    }
                    else
                    {
                        successCount = 0;
                    }

                    if (successCount >= successThresh) { break; }
                }

                if (successCount < successThresh) { return false; }

                // Find 0x0001 IFeatureSet
                ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, 0x00, 0x00 | SW_ID, 0x00, 0x01, 0x00 });
                _featureMap[0x0001] = ret.GetParam(0);

                // Get Feature Count
                ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, _featureMap[0x0001], 0x00 | SW_ID, 0x00, 0x00, 0x00 });
                int featureCount = ret.GetParam(0);

                // Enumerate Features
                for (byte i = 0; i <= featureCount; i++)
                {
                    ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, _featureMap[0x0001], 0x10 | SW_ID, i, 0x00, 0x00 });
                    ushort featureId = (ushort)((ret.GetParam(0) << 8) + ret.GetParam(1));

                    _featureMap[featureId] = i;
                }

                await InitPopulateAsync();
                return true;
            }
            finally
            {
                _initSemaphore.Release();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0018:Inline variable declaration")]
        private async Task InitPopulateAsync()
        {
            Hidpp20 ret;
            byte featureId;

            // Device name
            if (_featureMap.TryGetValue(0x0005, out featureId))
            {
                ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, featureId, 0x00 | SW_ID, 0x00, 0x00, 0x00 });
                int nameLength = ret.GetParam(0);

                string name = "";

                while (name.Length < nameLength)
                {
                    ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, featureId, 0x10 | SW_ID, (byte)name.Length, 0x00, 0x00 });
                    name += Encoding.UTF8.GetString(ret.GetParams());
                }

                DeviceName = name.TrimEnd('\0');

                foreach (var tag in GlobalSettings.settings.DisabledDevices)
                {
                    if (DeviceName.Contains(tag))
                    {
                        AppLog.Info($"{DeviceName} is marked as disabled");
                        return;
                    }
                };

                ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, featureId, 0x20 | SW_ID, 0x00, 0x00, 0x00 });
                DeviceType = ret.GetParam(0);
            }
            else
            {
                // Device does not have a name/Hidpp error ignore it
                AppLog.Info($"Receiver {_parent.Label}: device {_deviceIdx} has no name feature, ignoring it");
                return;
            }

            if (_featureMap.TryGetValue(0x0003, out featureId))
            {
                ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, featureId, 0x00 | SW_ID, 0x00, 0x00, 0x00 });

                string unitId = BitConverter.ToString(ret.GetParams().ToArray(), 1, 4).Replace("-", string.Empty);
                string modelId = BitConverter.ToString(ret.GetParams().ToArray(), 7, 5).Replace("-", string.Empty);

                bool serialNumberSupported = (ret.GetParam(14) & 0x1) == 0x1;
                string? serialNumber = null;
                if (serialNumberSupported)
                {
                    ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, featureId, 0x20 | SW_ID, 0x00, 0x00, 0x00 });
                    serialNumber = BitConverter.ToString(ret.GetParams().ToArray(), 0, 11).Replace("-", string.Empty);
                }

                Identifier = serialNumber ?? $"{unitId}-{modelId}";
            }
            else
            {
                // Device does not have a serial identifier, use a stable hash of the device name
                var hashBytes = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(DeviceName));
                Identifier = Convert.ToHexString(hashBytes, 0, 4);
            }

#if DEBUG
            Log.WriteLine("---");
            Log.WriteLine(DeviceName + " Ready");
            Log.WriteLine(Identifier);
            foreach ((ushort featureIdItr, string featureDesc) in new (ushort, string)[]
            {
                (0x1000, "Battery Unified Level"),
                (0x1001, "Battery Voltage"),
                (0x1004, "Unified Battery"),
            })
            {
                if (_featureMap.ContainsKey(featureIdItr))
                {
                    Log.WriteLine($"0x{featureIdItr:X} - {featureDesc} Found");
                }
            }
            Log.WriteLine("---");
#endif

            if (_featureMap.TryGetValue(0x1000, out featureId))
            {
                (_getBatteryAsync, _parseBattery, _batteryFeatureIdx) = (Battery1000.GetBatteryAsync, Battery1000.Parse, featureId);
            }
            else if (_featureMap.TryGetValue(0x1001, out featureId))
            {
                (_getBatteryAsync, _parseBattery, _batteryFeatureIdx) = (Battery1001.GetBatteryAsync, Battery1001.Parse, featureId);
            }
            else if (_featureMap.TryGetValue(0x1004, out featureId))
            {
                (_getBatteryAsync, _parseBattery, _batteryFeatureIdx) = (Battery1004.GetBatteryAsync, Battery1004.Parse, featureId);
            }

            if (_cts.IsCancellationRequested) { return; }

            HidppManagerContext.Instance.MarkOnline(this);
            if (_cts.IsCancellationRequested)
            {
                HidppManagerContext.Instance.MarkOffline(this, signal: false);
                return;
            }

            HidppManagerContext.Instance.SignalDeviceEvent(
                IPCMessageType.INIT,
                new InitMessage(Identifier, DeviceName, _getBatteryAsync != null, (DeviceType)DeviceType)
            );
            _isOnline = true;
            AppLog.Info($"Receiver {_parent.Label}: {DeviceName} ready as device {_deviceIdx} ({Identifier}){(_getBatteryAsync == null ? ", no battery feature" : "")}");

            await Task.Delay(1000);

            _ = Task.Run(async () =>
            {
                if (_getBatteryAsync == null) { return; }

                try
                {
                    while (!_cts.IsCancellationRequested && !Parent.Disposed)
                    {
                        var now = DateTimeOffset.Now;
#if DEBUG
                        var expectedUpdateTime = lastUpdate.AddSeconds(1);
#else
                        var expectedUpdateTime = lastUpdate.AddSeconds(GlobalSettings.settings.PollPeriod);
#endif
                        if (now < expectedUpdateTime)
                        {
                            await Task.Delay((int)(expectedUpdateTime - now).TotalMilliseconds, _cts.Token);
                        }

                        await UpdateBattery();
                        await Task.Delay(GlobalSettings.settings.RetryTime * 1000, _cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            });
        }

        public async Task UpdateBattery(bool forceIpcUpdate = false)
        {
            if (Parent.Disposed) { return; }
            if (_getBatteryAsync == null) { return; }

            var ret = await _getBatteryAsync.Invoke(this);

            if (ret == null)
            {
                if (!_pollFailing)
                {
                    _pollFailing = true;
                    AppLog.Warn($"{DeviceName}: battery poll got no answer, retrying every {GlobalSettings.settings.RetryTime}s");
                }
                return;
            }

            if (_pollFailing)
            {
                _pollFailing = false;
                AppLog.Info($"{DeviceName}: battery poll answering again");
            }

            ReportBattery(ret.Value, forceIpcUpdate);
        }

        private void ReportBattery(BatteryUpdateReturn batStatus, bool forceIpcUpdate = false)
        {
            if (_cts.IsCancellationRequested) { return; }

            lock (_batteryLock)
            {
                lastUpdate = DateTimeOffset.Now;

                if (!forceIpcUpdate && (batStatus == lastBatteryReturn))
                {
                    // Don't report if no change
                    return;
                }

                lastBatteryReturn = batStatus;
            }

            AppLog.Info($"{DeviceName}: {batStatus.batteryPercentage}% {batStatus.status}");

            HidppManagerContext.Instance.SignalDeviceEvent(
                IPCMessageType.UPDATE,
                new UpdateMessage(Identifier, batStatus.batteryPercentage, batStatus.status, batStatus.batteryMVolt, lastUpdate)
            );
        }
    }
}
