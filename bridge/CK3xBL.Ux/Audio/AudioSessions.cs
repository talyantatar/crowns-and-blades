using System.Runtime.InteropServices;

namespace CK3xBL.Ux.Audio;

public enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

/// <summary>One WASAPI render session as listed by <see cref="AudioSessions.List"/> (a read-only snapshot).</summary>
public sealed record AudioSessionInfo(
    string EndpointId, int ProcessId, string ProcessName, string InstanceId, string DisplayName,
    AudioSessionState State, float Volume, bool Muted, bool IsSystemSounds);

/// <summary>
/// Per-process audio sessions on every active render endpoint (the user may switch devices, and FMOD may hold more than one
/// session per process). Sessions appear lazily, so they are re-enumerated for every operation instead of being cached.
/// </summary>
public static class AudioSessions
{
    /// <summary>Event context passed with our own volume changes, so they can be told apart from the user's.</summary>
    public static readonly Guid BridgeEventContext = new("6c8f3a9e-2b1d-4c7e-9f00-c3b01d9e0001");

    /// <summary>Read-only listing of all render sessions (no volume or mute is changed).</summary>
    public static IReadOnlyList<AudioSessionInfo> List()
    {
        var result = new List<AudioSessionInfo>();
        foreach (var (endpoint, control) in Enumerate())
        {
            try
            {
                control.GetProcessId(out uint pid);
                control.GetState(out int state);
                control.GetSessionInstanceIdentifier(out string? instance);
                control.GetDisplayName(out string? display);
                bool system = control.IsSystemSoundsSession() == 0;
                var volume = (ISimpleAudioVolume)control;
                volume.GetMasterVolume(out float level);
                volume.GetMute(out bool muted);
                result.Add(new AudioSessionInfo(endpoint, (int)pid, ProcessNameOf((int)pid), instance ?? "", display ?? "",
                    (AudioSessionState)state, level, muted, system));
            }
            finally { Marshal.ReleaseComObject(control); }
        }
        return result;
    }

    /// <summary>Opens every session of <paramref name="processId"/> for volume control. Dispose each handle.</summary>
    internal static List<ProcessSession> OpenForProcess(int processId)
    {
        var result = new List<ProcessSession>();
        foreach (var (endpoint, control) in Enumerate())
        {
            bool keep = false;
            try
            {
                if (control.GetProcessId(out uint pid) < 0 || pid != (uint)processId) continue;
                control.GetState(out int state);
                control.GetSessionInstanceIdentifier(out string? instance);
                result.Add(new ProcessSession(control, (ISimpleAudioVolume)control, endpoint, instance ?? endpoint, (AudioSessionState)state));
                keep = true;
            }
            finally { if (!keep) Marshal.ReleaseComObject(control); }
        }
        return result;
    }

    internal static string ProcessNameOf(int pid) => pid == 0 ? "(system)" : Interop.Native.TryProcessName(pid) ?? "(exited)";

    private static IEnumerable<(string endpoint, IAudioSessionControl2 control)> Enumerate()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            if (enumerator.EnumAudioEndpoints(CoreAudio.eRender, CoreAudio.DEVICE_STATE_ACTIVE, out var devices) < 0) yield break;
            try
            {
                devices.GetCount(out int deviceCount);
                var iid = typeof(IAudioSessionManager2).GUID;
                for (int i = 0; i < deviceCount; i++)
                {
                    if (devices.Item(i, out var device) < 0) continue;
                    IAudioSessionEnumerator? sessions = null;
                    object? managerObject = null;
                    string endpointId = "";
                    try
                    {
                        device.GetId(out string? id);
                        endpointId = id ?? "";
                        if (device.Activate(ref iid, CoreAudio.CLSCTX_ALL, 0, out managerObject) < 0) continue;
                        if (((IAudioSessionManager2)managerObject).GetSessionEnumerator(out sessions) < 0) continue;
                        sessions.GetCount(out int sessionCount);
                        for (int j = 0; j < sessionCount; j++)
                            if (sessions.GetSession(j, out var control) >= 0 && control != null)
                                yield return (endpointId, control);
                    }
                    finally
                    {
                        if (sessions != null) Marshal.ReleaseComObject(sessions);
                        if (managerObject != null) Marshal.ReleaseComObject(managerObject);
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally { Marshal.ReleaseComObject(devices); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }
}

/// <summary>An open session of one process (volume control). Releases its COM reference on dispose.</summary>
internal sealed class ProcessSession(IAudioSessionControl2 control, ISimpleAudioVolume volume, string endpointId, string instanceId, AudioSessionState state)
    : IDisposable
{
    public string EndpointId { get; } = endpointId;
    public string InstanceId { get; } = instanceId;
    public AudioSessionState State { get; } = state;

    public float? GetLevel() => volume.GetMasterVolume(out float level) >= 0 ? level : null;

    public bool SetLevel(float level)
    {
        var context = AudioSessions.BridgeEventContext;
        // Fails harmlessly with AUDCLNT_E_DEVICE_INVALIDATED if the device vanished mid-ramp.
        return volume.SetMasterVolume(Math.Clamp(level, 0f, 1f), ref context) >= 0;
    }

    public void Dispose() => Marshal.ReleaseComObject(control);
}
