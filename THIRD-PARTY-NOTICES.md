# Third-party notices

HeadStart VR doesn't bundle or redistribute any third-party binaries. It works with tools you install yourself,
and downloads UEVR builds only when you ask it to, straight from each author's own GitHub releases.

## Code and formats

- **.NET runtime (MIT, © .NET Foundation and Contributors).** `UevrBuilds.ToBase32StringSuitableForDirName`
  reimplements the folder-name encoding .NET uses for user settings, so HeadStart VR can find where the
  UEVR injector keeps its OpenXR/OpenVR choice. https://github.com/dotnet/runtime
- **OpenXR (Khronos Group, Apache-2.0).** The headset check calls the OpenXR loader that ships with each UEVR
  build and uses the structure layouts and constants from the public `openxr.h` specification header.
  https://github.com/KhronosGroup/OpenXR-SDK

## Software HeadStart VR works with (not included, not affiliated)

- **UEVR** by praydog — https://github.com/praydog/UEVR (all rights reserved by its author).
  Forks: joeyhodge/UEVR, PureDark/UEVR (AFW).
- **OFXR Bridge** by tig3rmast3r — https://github.com/tig3rmast3r/OFXR-Bridge
- **Virtual Desktop** by Guy Godin — https://www.vrdesktop.net
- Game launchers: Steam (Valve), Epic Games Launcher (Epic Games), GOG Galaxy (GOG).

All product names and trademarks belong to their owners. HeadStart VR is an independent project and isn't
endorsed by or affiliated with any of them.
