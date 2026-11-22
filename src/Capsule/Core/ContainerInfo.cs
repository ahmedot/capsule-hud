using Windows.Devices.Enumeration;

namespace Capsule.Core;

/// <summary>Looks up facts about a physical device (its "container"): is it Bluetooth, and its battery level.</summary>
internal static class ContainerInfo
{
    // Battery level that Windows shows under Settings > Bluetooth & devices.
    private const string BatteryProperty = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    public sealed record Facts(bool IsBluetooth, int? BatteryPercent);

    public static async Task<Facts> ReadAsync(Guid containerId)
    {
        if (containerId == Guid.Empty) return new Facts(false, null);

        try
        {
            var query = $"System.Devices.ContainerId:=\"{{{containerId}}}\"";
            var devices = await DeviceInformation.FindAllAsync(query, [BatteryProperty], DeviceInformationKind.Device);

            var bluetooth = false;
            int? battery = null;
            foreach (var device in devices)
            {
                if (device.Id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) bluetooth = true;
                if (battery is null
                    && device.Properties.TryGetValue(BatteryProperty, out var value)
                    && ToPercent(value) is { } percent)
                {
                    battery = percent;
                }
            }

            return new Facts(bluetooth, battery);
        }
        catch (Exception ex)
        {
            Log.Error("Could not read device details", ex);
            return new Facts(false, null);
        }
    }

    private static int? ToPercent(object? value)
    {
        int? number = value switch
        {
            byte b => b,
            ushort s => s,
            uint u when u <= 100 => (int)u,
            int i => i,
            _ => null,
        };
        return number is >= 0 and <= 100 ? number : null;
    }
}
