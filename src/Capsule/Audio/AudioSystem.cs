using System.Runtime.InteropServices;
using Capsule.Core;
using Capsule.Interop;

namespace Capsule.Audio;

/// <summary>Reads Windows audio endpoints and changes the default device. Use from the UI thread.</summary>
internal sealed class AudioSystem : IDisposable
{
    private static readonly Guid AudioEndpointVolumeId = typeof(IAudioEndpointVolume).GUID;
    private readonly IMMDeviceEnumerator _enumerator;
    private bool _disposed;

    public AudioSystem()
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
    }

    public IReadOnlyList<AudioEndpoint> GetEndpoints(EDataFlow flow, DeviceState states = DeviceState.Active)
    {
        var list = new List<AudioEndpoint>();
        if (_enumerator.EnumAudioEndpoints(flow, states, out var collection) != 0 || collection is null)
        {
            return list;
        }

        try
        {
            collection.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0 || device is null) continue;
                try
                {
                    var endpoint = Describe(device, flow);
                    if (endpoint is not null) list.Add(endpoint);
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(collection);
        }

        return list;
    }

    public AudioEndpoint? GetEndpoint(string id)
    {
        if (string.IsNullOrEmpty(id) || _enumerator.GetDevice(id, out var device) != 0 || device is null)
        {
            return null;
        }

        try
        {
            var flow = EDataFlow.Render;
            if (device is IMMEndpoint endpoint && endpoint.GetDataFlow(out var reported) == 0) flow = reported;
            return Describe(device, flow);
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    public string? GetDefaultId(EDataFlow flow, ERole role)
    {
        if (_enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0 || device is null)
        {
            return null;
        }

        try
        {
            return device.GetId(out var id) == 0 ? id : null;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    /// <summary>Makes the endpoint the default for sound (and optionally calls).</summary>
    public bool SetDefault(string id, bool includeCommunications)
    {
        IPolicyConfig? policy = null;
        try
        {
            policy = (IPolicyConfig)new PolicyConfigComObject();
            var hr = policy.SetDefaultEndpoint(id, ERole.Console);
            hr |= policy.SetDefaultEndpoint(id, ERole.Multimedia);
            if (includeCommunications) hr |= policy.SetDefaultEndpoint(id, ERole.Communications);
            if (hr != 0) Log.Error($"Windows refused to change the default device (code {hr})");
            return hr == 0;
        }
        catch (Exception ex)
        {
            Log.Error("Changing the default device failed", ex);
            return false;
        }
        finally
        {
            if (policy is not null) Marshal.ReleaseComObject(policy);
        }
    }

    public IAudioEndpointVolume? ActivateVolume(string id)
    {
        if (_enumerator.GetDevice(id, out var device) != 0 || device is null) return null;
        try
        {
            var iid = AudioEndpointVolumeId;
            return device.Activate(ref iid, NativeAudio.ClsCtxAll, IntPtr.Zero, out var instance) == 0
                ? instance as IAudioEndpointVolume
                : null;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    public void Register(IMMNotificationClient client) =>
        _enumerator.RegisterEndpointNotificationCallback(client);

    public void Unregister(IMMNotificationClient client) =>
        _enumerator.UnregisterEndpointNotificationCallback(client);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Marshal.ReleaseComObject(_enumerator);
    }

    private static AudioEndpoint? Describe(IMMDevice device, EDataFlow flow)
    {
        if (device.GetId(out var id) != 0 || id is null) return null;
        device.GetState(out var state);

        string name = "", deviceName = "";
        var formFactor = FormFactor.Unknown;
        var container = Guid.Empty;

        if (device.OpenPropertyStore(NativeAudio.StgmRead, out var store) == 0 && store is not null)
        {
            try
            {
                // Names come from devices and drivers: keep them to one clean line.
                name = DisplayText.Clean(ReadString(store, PropertyKeys.FriendlyName), 256);
                deviceName = DisplayText.Clean(ReadString(store, PropertyKeys.InterfaceFriendlyName), 256);
                formFactor = (FormFactor)ReadUInt(store, PropertyKeys.FormFactor, (uint)FormFactor.Unknown);
                container = ReadGuid(store, PropertyKeys.ContainerId);
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        return new AudioEndpoint(id, name, deviceName, flow, state, formFactor, container);
    }

    private static string ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0) return "";
        try
        {
            return value.VarType == NativeAudio.VtLpwstr && value.Value != IntPtr.Zero
                ? Marshal.PtrToStringUni(value.Value) ?? ""
                : "";
        }
        finally
        {
            NativeAudio.PropVariantClear(ref value);
        }
    }

    private static uint ReadUInt(IPropertyStore store, PropertyKey key, uint fallback)
    {
        if (store.GetValue(ref key, out var value) != 0) return fallback;
        try
        {
            return value.VarType == NativeAudio.VtUInt32 ? unchecked((uint)(value.Value.ToInt64() & uint.MaxValue)) : fallback;
        }
        finally
        {
            NativeAudio.PropVariantClear(ref value);
        }
    }

    private static Guid ReadGuid(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0) return Guid.Empty;
        try
        {
            return value.VarType == NativeAudio.VtClsid && value.Value != IntPtr.Zero
                ? Marshal.PtrToStructure<Guid>(value.Value)
                : Guid.Empty;
        }
        finally
        {
            NativeAudio.PropVariantClear(ref value);
        }
    }
}
