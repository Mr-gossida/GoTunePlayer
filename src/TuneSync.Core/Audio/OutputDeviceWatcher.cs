using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace TuneSync.Core.Audio;

/// <summary>Tells when what the playback devices offer may have changed: one plugged in or
/// removed, enabled or disabled, a new Windows default, or a device's shared-mode format changed
/// in Windows' sound settings (소리 › 장치 속성 › 고급 › 기본 형식). <see cref="Changed"/> is raised
/// on a Windows audio thread, often several times for one change - debounce it.</summary>
public sealed class OutputDeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _registered;

    public event Action? Changed;

    public OutputDeviceWatcher()
    {
        _registered = _enumerator.RegisterEndpointNotificationCallback(this) == 0;
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Changed?.Invoke();

    public void OnDeviceAdded(string pwstrDeviceId) => Changed?.Invoke();

    public void OnDeviceRemoved(string deviceId) => Changed?.Invoke();

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) Changed?.Invoke();
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        if (key.formatId == OutputDevices.DeviceFormatKey.formatId && key.propertyId == OutputDevices.DeviceFormatKey.propertyId)
            Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_registered)
        {
            _registered = false;
            try { _enumerator.UnregisterEndpointNotificationCallback(this); }
            catch (System.Runtime.InteropServices.COMException) { /* audio service gone */ }
        }
        _enumerator.Dispose();
    }
}
