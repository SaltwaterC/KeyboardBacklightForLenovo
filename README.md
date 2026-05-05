# Keyboard Backlight for Lenovo

This is a collection of software utilities for controlling the keyboard backlight on Lenovo laptops under Windows.

Basically:

- A Windows system tray application for controlling which state needs to be preserved: Off, Low, or High. This will also instruct the companion service which is the set value.
- A CLI application that may be used to test the functionality or use with other automation tools, such as Task Scheduler.
- A Windows service for responding to events (boot, screen on) to reset the keyboard backlight status to a value set by the tray or the CLI application.

The tray application has an Auto mode: a set mode for day and a set mode for night. This is either time based (on set time intervals) or Night light based where it follows Night light state.

The auto mode suspends persistence: the keyboard backlight is set to the day or night level on transition, and user changes are ignored until the next transition. Manual override is still possible, but the backlight status shall be reset to the value set for day or night mode upon encountering a power event.

## Installing

Fetch a release bundle and run. The bundle has 3 components:

- .NET desktop runtime installer needed to run this software. This only runs when the configured runtime channel is not installed.
- [ScreenStateService](https://github.com/SaltwaterC/ScreenStateService) to detect power events when laptop screen turns on.
- The collection of software from this project.

## Uninstalling

- Run the uninstaller from Settings » Apps.
- Optional: Uninstall ScreenStateService from Settings » Apps (if you don't plan to reinstall and you don't use it for something else).
- Optional: Uninstall .NET desktop runtime if not needed anymore. Note that other software may be using this.

If .NET does not show up in Settings » Apps, run this from command prompt (Win+R » cmd.exe):

```sh
winget uninstall --id Microsoft.DotNet.DesktopRuntime.10
```

## Under the bonnet

This uses ACPI (Advanced Configuration and Power Interface) principals exposed by Lenovo's drivers to get and set backlight level:

- IBMPmDrv for ThinkPads
- EnergyDrv for ThinkBooks or IdeaPads

Does not depend on Lenovo's software suite (such as Lenovo Commercial Vantage).

That being said, the driver config is entirely undocumented so it may not work on all machines.

Tested on the following laptops:

- ThinkPad P14s Gen 2 AMD
- ThinkBook 14 G2 ARE

## Special thanks

[@gonwan](https://github.com/gonwan) for [figuring out EnergyDrv](https://www.gonwan.com/2025/04/11/keyboard-backlight-control-on-lenovo-ideapad-xiaoxin-models/).\
[@Maclay74](https://github.com/Maclay74) for [figuring out NightLight](https://github.com/Maclay74/tiny-screen/blob/main/TinyScreen/Src/Services/NightLight.cs).\
[@cmspam](https://github.com/cmspam) for [figuring out EnergyDrv alternate values](https://github.com/SaltwaterC/KeyboardBacklightForLenovo/issues/9#issuecomment-4229780300).
