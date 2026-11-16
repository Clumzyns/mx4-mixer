# Mixer

Per-app volume mixer for Windows. Five columns (Game, Chat, Media, Aux, and System for anything unassigned), each with its own fader, mute, peak meter, and playback device. Assignments are remembered by program name.

The window title and tray name come from `AppInfo.Name` in `src/Mixer/AppInfo.cs`, which is `Mixer`. The published file is `AudioMixer.exe`.

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
- ChatMix reads the Arctis Nova Pro base-station dial over USB. Game and Chat faders follow the two numbers it sends (0 to 100 each). Sound still plays on whatever device each channel is set to, including an IEM amp. Quit SteelSeries GG, or the dial stays locked. The ChatMix switch turns this off.
- Each channel fader scales the volume of the apps on that channel and keeps their relative levels.
- **Default** on a channel follows the master output. Any other choice sends that channel's apps to that playback device.
- Drag an app chip onto a column, or use the menu on the chip. Discord, TeamSpeak, Spotify, Chrome, Firefox, Edge, and VLC start on Chat or Media. Everything else starts on System.
- Settings are stored in `%APPDATA%\Mixer\config.json`.

## Limits

- This does not install a virtual audio device. A channel plays on a real playback device you already have, such as an IEM amp. It is not a single mix bus the way SteelSeries Sonar is.
- Per-app device routing needs Windows 10 1803 (build 17134) or later. Older Windows can still use the faders when changing the default device works.
- Exclusive-mode WASAPI, ASIO, and apps that pick their own device ignore the per-app route.
- A stream that is already playing may stay on the old device until the app opens a new one.
- System Sounds have no process id, so they can be turned up or down but not sent to a chosen device.
- Some elevated or protected processes hide their program name. Those show up as a process id until the path is readable.
- Channel volume changes the app's session volume, not the hardware knob on the amp.
- ChatMix supports the Nova Pro wired GameDAC (USB product ids 0x12CB and 0x12CD) and the Nova Pro Wireless base station (0x12E0, 0x12E5, and 0x225D). Other SteelSeries dials are not read.
- SteelSeries GG must not be running. It takes the same USB connection, and then the dial never reaches Mixer.
- The dial is not a hardware crossfade into the IEM amp. Mixer copies its position onto the Game and Chat session volumes.
- The published exe is large because it includes the .NET 8 runtime.
- Inter is included under the SIL Open Font License (`src/Mixer/Assets/Fonts/OFL.txt`).
