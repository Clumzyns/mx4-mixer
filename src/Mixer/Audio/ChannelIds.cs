namespace Mixer.Audio;

public enum ChannelId
{
    Game = 0,
    Chat = 1,
    Media = 2,
    Aux = 3,
    System = 4
}

public static class ChannelIds
{
    public static readonly ChannelId[] All =
    [
        ChannelId.Game,
        ChannelId.Chat,
        ChannelId.Media,
        ChannelId.Aux,
        ChannelId.System
    ];

    public static string Key(this ChannelId id) => id switch
    {
        ChannelId.Game => "game",
        ChannelId.Chat => "chat",
        ChannelId.Media => "media",
        ChannelId.Aux => "aux",
        _ => "system"
    };

    public static string Title(this ChannelId id) => id switch
    {
        ChannelId.Game => "Game",
        ChannelId.Chat => "Chat",
        ChannelId.Media => "Media",
        ChannelId.Aux => "Aux",
        _ => "System"
    };

    public static string Subtitle(this ChannelId id) => id switch
    {
        ChannelId.Game => "Games",
        ChannelId.Chat => "Voice",
        ChannelId.Media => "Music and browser",
        ChannelId.Aux => "Extra",
        _ => "Unassigned"
    };

    public static string Accent(this ChannelId id) => id switch
    {
        ChannelId.Game => "#B6F23A",
        ChannelId.Chat => "#7AD7FF",
        ChannelId.Media => "#D7A6FF",
        ChannelId.Aux => "#FFC56A",
        _ => "#8D96A5"
    };

    public static bool TryParse(string? key, out ChannelId id)
    {
        switch (key?.Trim().ToLowerInvariant())
        {
            case "game": id = ChannelId.Game; return true;
            case "chat": id = ChannelId.Chat; return true;
            case "media": id = ChannelId.Media; return true;
            case "aux": id = ChannelId.Aux; return true;
            case "system":
            case "unassigned": id = ChannelId.System; return true;
            default:
                id = ChannelId.System;
                return false;
        }
    }
}

public static class DefaultApps
{
    public static readonly Dictionary<string, ChannelId> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["discord.exe"] = ChannelId.Chat,
        ["discordptb.exe"] = ChannelId.Chat,
        ["discordcanary.exe"] = ChannelId.Chat,
        ["teamspeak.exe"] = ChannelId.Chat,
        ["teamspeak3.exe"] = ChannelId.Chat,
        ["ts3client_win64.exe"] = ChannelId.Chat,
        ["ts3client_win32.exe"] = ChannelId.Chat,
        ["spotify.exe"] = ChannelId.Media,
        ["chrome.exe"] = ChannelId.Media,
        ["firefox.exe"] = ChannelId.Media,
        ["msedge.exe"] = ChannelId.Media,
        ["vlc.exe"] = ChannelId.Media
    };

    public static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["discord.exe"] = "Discord",
        ["discordptb.exe"] = "Discord PTB",
        ["discordcanary.exe"] = "Discord Canary",
        ["teamspeak.exe"] = "TeamSpeak",
        ["teamspeak3.exe"] = "TeamSpeak",
        ["ts3client_win64.exe"] = "TeamSpeak",
        ["ts3client_win32.exe"] = "TeamSpeak",
        ["spotify.exe"] = "Spotify",
        ["chrome.exe"] = "Chrome",
        ["firefox.exe"] = "Firefox",
        ["msedge.exe"] = "Edge",
        ["vlc.exe"] = "VLC",
        ["system.sounds"] = "System Sounds"
    };
}
