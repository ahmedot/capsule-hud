using Capsule.Interop;

namespace Capsule.Audio;

/// <summary>Windows endpoint form factors (EndpointFormFactor).</summary>
public enum FormFactor : uint
{
    RemoteNetworkDevice = 0,
    Speakers = 1,
    LineLevel = 2,
    Headphones = 3,
    Microphone = 4,
    Headset = 5,
    Handset = 6,
    UnknownDigitalPassthrough = 7,
    Spdif = 8,
    DigitalAudioDisplayDevice = 9,
    Unknown = 10,
}

/// <summary>A snapshot of one Windows audio endpoint.</summary>
public sealed record AudioEndpoint(
    string Id,
    string Name,
    string DeviceName,
    EDataFlow Flow,
    DeviceState State,
    FormFactor FormFactor,
    Guid ContainerId)
{
    public bool IsActive => State == DeviceState.Active;

    /// <summary>Short label for the HUD, e.g. "Studio Headphones" instead of "Headphones (Studio Headphones)".</summary>
    public string ShortName => string.IsNullOrWhiteSpace(DeviceName) ? Name : DeviceName;
}
