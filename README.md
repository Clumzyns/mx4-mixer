# Mixer

Per-app volume mixer for Windows. Five columns (Game, Chat, Media, Aux, and System for anything unassigned), each with its own fader, mute, peak meter, and playback device. Assignments are remembered by program name.

The window title and tray name come from `AppInfo.Name` in `src/Mixer/AppInfo.cs`, which is `Mixer`. The published file is `AudioMixer.exe`.

## Setup

1. Install [Virtual Audio Cable](https://vac.muzychenko.net/en/download.htm) by Eugene Muzychenko.
2. Open VAC Control Panel and set the number of cables to 4. Restart Mixer after Windows shows Line 1 through Line 4.
3. In Mixer settings, check the Game, Chat, Media, and Aux cable dropdowns. They default to Line 1 through Line 4.
4. Optional: Rename to Mixer - Game, Chat, Media, and Aux. Windows asks for administrator approval.
5. Set OUTPUT to the IEM DAC, for example Fosi Audio K5 Pro. Leave each channel on Master mix.
6. Quit SteelSeries GG if it is open so the Nova Pro dial stays with Mixer.

Apps play into a cable's playback device. Mixer records that cable and sends the mix to the master output.

## Run

Windows 10 version 1803 or later, or Windows 11.

Build on Windows with the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0):

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

That writes `dist\AudioMixer.exe`. It is a self-contained 64-bit app, so the machine you copy it to does not need the .NET runtime installed. Double-click it. Closing the window leaves it in the tray. Exit from the tray menu.

## What it does

- The top bar picks the Windows default speakers and microphone for the console, multimedia, and communications roles.
- The mic button mutes that microphone. The bar next to it is the live input level.
- Virtual Audio Cable lines are the channel inputs. Mixer records each cable, applies the faders, and plays the mix to the master output.
- The ChatMix slider at the bottom of the window sets Game and Chat. Center keeps both at full volume, the Game end is game only, and the Chat end is chat only. It works with no base station attached. The position is stored as `chatMixBalance` in `%APPDATA%\Mixer\config.json`.
- ChatMix on the Nova Pro is USB report `07 45` (game %, chat %) on interface 4, after `06 49 01`. Mixer reads both MI_04 collections at once and only sends the init writes to the collection that accepts them (usage page `0xFFC0`). A `07 45` report moves the slider. Quit SteelSeries GG first.
- The wired base-station volume knob reports `07 25` plus an attenuation position. Position 0 is full master volume and 56 is silent (`(1 - position / 56) * 100`). A higher position raises and saves that maximum. The first report after connect is applied. Settings has an "Invert knob" toggle. ChatMix `07 45` moves the slider and the Game and Chat gains as each report arrives.
- Vertical faders put 0 at the bottom and 100 at the top. Dragging up is louder. The master fader is the vertical strip beside the channels.
- **Master mix** keeps a channel in that mix. Any other device on the channel plays that channel there instead.
- Drag an app chip onto a column, or use the menu on the chip. Discord, TeamSpeak, Spotify, Chrome, Firefox, Edge, and VLC start on Chat or Media. Everything else starts on System.
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
- The dial is not mixed inside the DAC. Mixer applies it while mixing Sonar's channels into the master output.
- The published exe is large because it includes the .NET 8 runtime.
- Inter is included under the SIL Open Font License (`src/Mixer/Assets/Fonts/OFL.txt`).
