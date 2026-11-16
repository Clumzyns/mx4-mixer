# Mixer

Per-app volume mixer for Windows. Five columns (Game, Chat, Media, Aux, and System for anything unassigned), each with its own fader, mute, peak meter, and playback device. Assignments are remembered by program name.

The window title and tray name come from `AppInfo.Name` in `src/Mixer/AppInfo.cs`, which is `Mixer`. The published file is `AudioMixer.exe`.

## Setup

1. Install SteelSeries GG once and open Sonar so Windows creates `SteelSeries Sonar - Gaming`, `Chat`, `Media`, and `Aux`.
2. Quit SteelSeries GG. Mixer has a Close SteelSeries button because GG takes the dial and mixes a second time.
3. Set OUTPUT to the IEM DAC, for example Fosi Audio K5 Pro. Leave each channel on Master mix.
4. Point games at SteelSeries Sonar - Gaming and Discord at SteelSeries Sonar - Chat, or let Mixer route them there.

A custom `Mixer - Game` driver is not installed by this build. See `driver/README.md`.

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
- When SteelSeries Sonar devices are installed, Mixer loopback-captures Gaming, Chat, Media, and Aux, applies the faders, and plays the mix to the master output.
- ChatMix reads the Nova Pro dial on USB interface 4 (`0x06 0x49 0x01` and `0x06 0x8D 0x01`). Game and Chat follow the dial. Quit SteelSeries GG first.
- **Master mix** keeps a channel in that mix. Any other device on the channel plays that channel there instead.
- Drag an app chip onto a column, or use the menu on the chip. Discord, TeamSpeak, Spotify, Chrome, Firefox, Edge, and VLC start on Chat or Media. Everything else starts on System.
- Settings are stored in `%APPDATA%\Mixer\config.json`.

## Limits

- Mixer does not ship its own virtual audio driver. It uses Sonar's signed devices. Those devices have to exist before the mix can start.
- Per-app device routing needs Windows 10 1803 (build 17134) or later. Older Windows can still use the faders when changing the default device works.
- Exclusive-mode WASAPI, ASIO, and apps that pick their own device ignore the per-app route.
- A stream that is already playing may stay on the old device until the app opens a new one.
- System Sounds have no process id, so they can be turned up or down but not sent to a chosen device.
- Some elevated or protected processes hide their program name. Those show up as a process id until the path is readable.
- Channel volume changes the app's session volume, not the hardware knob on the amp.
- ChatMix supports the Nova Pro wired GameDAC (USB product ids 0x12CB and 0x12CD) and the Nova Pro Wireless base station (0x12E0, 0x12E5, and 0x225D). Other SteelSeries dials are not read.
- SteelSeries GG must not be running. It takes the same USB connection, and then the dial never reaches Mixer.
- The dial is not mixed inside the DAC. Mixer applies it while mixing Sonar's channels into the master output.
- The published exe is large because it includes the .NET 8 runtime.
- Inter is included under the SIL Open Font License (`src/Mixer/Assets/Fonts/OFL.txt`).
