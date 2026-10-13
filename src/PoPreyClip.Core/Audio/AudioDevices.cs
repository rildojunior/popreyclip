using System.Runtime.InteropServices;
using PoPreyClip.Audio.Interop;
using static PoPreyClip.Audio.Interop.CoreAudioNative;

namespace PoPreyClip.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>Creates WASAPI clients for microphones and for per-process loopback capture.</summary>
internal static class AudioDevices
{
    public static List<AudioDeviceInfo> ListMicrophones()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Console, out var defaultDevice) >= 0)
            {
                defaultDevice.GetId(out defaultId);
                Marshal.ReleaseComObject(defaultDevice);
            }

            Check(enumerator.EnumAudioEndpoints(EDataFlow.Capture, DeviceStateActive, out var collection), "EnumAudioEndpoints");
            var result = new List<AudioDeviceInfo>();
            Check(collection.GetCount(out var count), "GetCount");
            for (uint i = 0; i < count; i++)
            {
                Check(collection.Item(i, out var device), "Item");
                try
                {
                    device.GetId(out var id);
                    result.Add(new AudioDeviceInfo(id, GetFriendlyName(device) ?? id, id == defaultId));
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }

            Marshal.ReleaseComObject(collection);
            return result;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>"Auto" or empty picks the default microphone; otherwise the first device whose name contains the text.</summary>
    public static AudioDeviceInfo? ResolveMicrophone(string? wanted)
    {
        var devices = ListMicrophones();
        if (string.IsNullOrWhiteSpace(wanted) || wanted.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
        }

        return devices.FirstOrDefault(d => d.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase) || d.Id == wanted);
    }

    public static WasapiCapture CreateMicrophoneCapture(string deviceId, PcmFifo fifo, string description)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            Check(enumerator.GetDevice(deviceId, out var device), "GetDevice");
            try
            {
                var iid = IidAudioClient;
                Check(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var client), "IMMDevice.Activate");
                return new WasapiCapture((IAudioClient)client, processLoopback: false, fifo, description);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Captures only the audio rendered by one process and its children, using the Windows
    /// process loopback virtual device (the same API Medal's native recorder imports).
    /// </summary>
    public static WasapiCapture CreateProcessCapture(int processId, PcmFifo fifo, string description)
    {
        var activation = new AudioClientActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = ProcessLoopbackModeIncludeTargetProcessTree,
        };

        var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        var paramsPtr = Marshal.AllocHGlobal(paramsSize);
        var variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        try
        {
            Marshal.StructureToPtr(activation, paramsPtr, false);
            var variant = new PropVariant { VarType = VtBlob, BlobSize = (uint)paramsSize, BlobData = paramsPtr };
            Marshal.StructureToPtr(variant, variantPtr, false);

            var handler = new ActivationHandler();
            var iid = IidAudioClient;
            Check(ActivateAudioInterfaceAsync(VirtualAudioDeviceProcessLoopback, ref iid, variantPtr, handler, out var operation),
                "ActivateAudioInterfaceAsync");
            try
            {
                if (!handler.Completion.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("A ativação da captura por processo não respondeu em 5 segundos.");
                }

                return new WasapiCapture(handler.Completion.Result, processLoopback: true, fifo, description);
            }
            finally
            {
                Marshal.ReleaseComObject(operation);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(variantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private static string? GetFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StgmRead, out var store) < 0)
        {
            return null;
        }

        try
        {
            var key = DeviceFriendlyName;
            if (store.GetValue(ref key, out var value) < 0)
            {
                return null;
            }

            try
            {
                return value.VarType == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Completion => _completion.Task;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                var hr = activateOperation.GetActivateResult(out var activateResult, out var instance);
                if (hr < 0 || activateResult < 0)
                {
                    var code = hr < 0 ? hr : activateResult;
                    _completion.TrySetException(new COMException($"Ativação da captura por processo falhou (0x{code:X8})", code));
                }
                else
                {
                    _completion.TrySetResult((IAudioClient)instance);
                }
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }

            return 0;
        }
    }
}
