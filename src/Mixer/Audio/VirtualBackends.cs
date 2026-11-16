namespace Mixer.Audio;

/// <summary>
/// Picks the virtual playback device for a channel. Sonar's signed driver is first.
/// Names beginning with "Mixer - " are reserved for a later custom driver.
/// </summary>
internal static class VirtualBackends
{
    public static bool TryMap(string friendlyName, out ChannelId channel, out string backend)
    {
        var name = friendlyName.Trim();
        if (TrySonar(name, out channel))
        {
            backend = "Sonar";
            return true;
        }

        if (TryCustom(name, out channel))
        {
            backend = "Mixer";
            return true;
        }

        channel = ChannelId.System;
        backend = "";
        return false;
    }

    public static bool IsSonarMicrophone(string friendlyName)
    {
        var name = friendlyName.ToLowerInvariant();
        return name.Contains("sonar") && name.Contains("microphone");
    }

    private static bool TrySonar(string name, out ChannelId channel)
    {
        var text = name.ToLowerInvariant();
        if (!text.Contains("sonar"))
        {
            channel = ChannelId.System;
            return false;
        }

        if (text.Contains("gaming") || text.Contains("game"))
        {
            channel = ChannelId.Game;
            return true;
        }

        if (text.Contains("chat"))
        {
            channel = ChannelId.Chat;
            return true;
        }

        if (text.Contains("media"))
        {
            channel = ChannelId.Media;
            return true;
        }

        if (text.Contains("aux"))
        {
            channel = ChannelId.Aux;
            return true;
        }

        channel = ChannelId.System;
        return false;
    }

    private static bool TryCustom(string name, out ChannelId channel)
    {
        var text = name.ToLowerInvariant();
        if (!text.StartsWith("mixer - "))
        {
            channel = ChannelId.System;
            return false;
        }

        if (text.Contains("game")) { channel = ChannelId.Game; return true; }
        if (text.Contains("chat")) { channel = ChannelId.Chat; return true; }
        if (text.Contains("media")) { channel = ChannelId.Media; return true; }
        if (text.Contains("aux")) { channel = ChannelId.Aux; return true; }
        channel = ChannelId.System;
        return false;
    }
}
