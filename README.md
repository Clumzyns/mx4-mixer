# Mixer

Per-app volume mixer for Windows, built for the SteelSeries Arctis Nova Pro base station and an external DAC. Four channels (Game, Chat, Media, and Aux), each with its own fader, mute, level meter, and playback device. Assignments are remembered by program name.

Mixer needs [Virtual Audio Cable](https://vac.muzychenko.net/en/), which is paid software and is not included. See [Virtual Audio Cable license](#virtual-audio-cable-license) below.

The window title and tray name come from `AppInfo.Name` in `src/Mixer/AppInfo.cs`, which is `Mixer`. The published file is `AudioMixer.exe`.

## Setup

1. Buy a [Virtual Audio Cable license](https://vac.muzychenko.net/en/purchase.htm) from its author, Eugene Muzychenko, and install the full version from the link in your purchase email. The [trial version](https://vac.muzychenko.net/en/download.htm) works for trying Mixer out, but it adds a spoken reminder to the audio.
2. Open VAC Control Panel and set the number of cables to 4. Restart Mixer after Windows shows Line 1 through Line 4.
3. In Mixer settings, check the Game, Chat, Media, and Aux cable dropdowns. They default to Line 1 through Line 4.
4. Optional: Rename to Mixer - Game, Chat, Media, and Aux. Windows asks for administrator approval.
5. Set OUTPUT to the IEM DAC, for example Fosi Audio K5 Pro. Leave each channel on Master mix.
6. Quit SteelSeries GG if it is open so the Nova Pro dial stays with Mixer.

Apps play into a cable's playback device. Mixer records that cable and sends the mix to the master output.

## Virtual Audio Cable license

Mixer uses Virtual Audio Cable (VAC) for its four channel inputs. VAC is a separate commercial product by Eugene Muzychenko. This repository does not contain VAC, its driver, or a license key, and Mixer's own license does not cover it.

- Everyone who runs Mixer needs their own VAC license. Buy one at [vac.muzychenko.net/en/purchase.htm](https://vac.muzychenko.net/en/purchase.htm).
- The terms are in the [VAC license agreement](https://vac.muzychenko.net/en/license.txt). The full version may not be shared, so do not pass your copy or key to anyone else.

## Run

Windows 10 version 1809 or later, or Windows 11.

Build on Windows with the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0):

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

That writes `dist\AudioMixer.exe`. It is a self-contained 64-bit app, so the machine you copy it to does not need the .NET runtime installed. Double-click it. Closing the window hides it in the tray and mixing continues. Quit from the tray menu exits. Start with Windows launches it minimized with `--tray`. Launching it again shows the window that is already running.

## What it does

- The top bar picks the Windows default speakers and microphone for the console, multimedia, and communications roles.
- The mic button mutes that microphone. The bar next to it is the live input level.
- Virtual Audio Cable lines are the channel inputs. Mixer records each cable, applies the faders, and plays the mix to the master output.
- The ChatMix dial on the left of the window sets Game and Chat. Center keeps both at full volume, the Game end is game only, and the Chat end is chat only. Drag it, scroll over it, or double-click to center. It works with no base station attached. The position is stored as `chatMixBalance` in `%APPDATA%\Mixer\config.json`.
- ChatMix on the Nova Pro is USB report `07 45` (game %, chat %) on interface 4, after `06 49 01`. Mixer reads both MI_04 collections at once and only sends the init writes to the collection that accepts them (usage page `0xFFC0`). A `07 45` report moves the dial. Quit SteelSeries GG first.
- The wired base-station volume knob reports `07 25` plus an attenuation position. Position 0 is full master volume and 56 is silent (`(1 - position / 56) * 100`). A higher position raises and saves that maximum. The first report after connect is applied. Settings has an "Invert knob" toggle. ChatMix `07 45` moves the dial and the Game and Chat gains as each report arrives.
- Faders put silence at the bottom and full volume at the top, on a 60 dB scale. The master knob sits under the ChatMix dial.
- The OUT strip shows the left and right peaks of the finished mix and how far the limiter is turning it down. The limiter only acts when the channels add up past full scale. Its button turns it off.
- The base station screen shows the mix while a dial or fader moves. After 5 idle seconds it shows the song Spotify is playing, with its progress. Both can be turned off in Settings.
- **Master mix** keeps a channel in that mix. Any other device on the channel plays that channel there instead.
- Drag an app chip onto a strip, or use the menu on the chip. Discord, TeamSpeak, Spotify, Chrome, Firefox, Edge, and VLC start on Chat or Media. Everything else starts on Game.
- Settings are stored in `%APPDATA%\Mixer\config.json`.

## Limits

- Mixer does not ship a virtual audio driver. Install Virtual Audio Cable and create 4 lines. The mix cannot start until those recording devices exist.
- Per-app device routing needs Windows 10 1803 (build 17134) or later. Older Windows can still use the faders when changing the default device works.
- Exclusive-mode WASAPI, ASIO, and apps that pick their own device ignore the per-app route.
- A stream that is already playing may stay on the old device until the app opens a new one.
- System Sounds have no process id, so they can be turned up or down but not sent to a chosen device.
- Some elevated or protected processes hide their program name. Those show up as a process id until the path is readable.
- Channel volume changes the app's session volume, not the hardware knob on the amp.
- ChatMix supports the Nova Pro wired GameDAC (USB product ids 0x12CB and 0x12CD) and the Nova Pro Wireless base station (0x12E0, 0x12E5, and 0x225D). Other SteelSeries dials are not read. Unrecognized report ids are written to `%APPDATA%\Mixer\mixer.log` and the latest bytes stay on screen.
- SteelSeries GG must not be running. It takes the same USB connection, and then the dial never reaches Mixer.
- The dial is not mixed inside the DAC. Mixer applies it while mixing the four channels into the master output.
- The published exe is large because it includes the .NET 8 runtime.

## License

Mixer is released under the [MIT License](LICENSE).

It builds on [NAudio](https://github.com/naudio/NAudio) (MIT) and [HidSharp](https://www.zer7.com/software/hidsharp) (Apache 2.0). The Inter font files in `src/Mixer/Assets/Fonts` are under the SIL Open Font License (`OFL.txt` in that folder).

Virtual Audio Cable is licensed separately by its author. See [Virtual Audio Cable license](#virtual-audio-cable-license).

Mixer is an independent project. It is not affiliated with or endorsed by SteelSeries, Spotify, or the author of Virtual Audio Cable. Product names belong to their owners.
