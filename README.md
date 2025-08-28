# Keyboard Backlight for Lenovo

This is a collection of software utilities for controlling the keyboard backlight on Lenovo laptops under Windows.

Basically:

- A Windows system tray application for controlling which state needs to be preserved: Off, Low, or High. This will also instruct the companion service which is the set value.
- A CLI application that may be used to test the functionality or use with other automation tools, such as Task Scheduler.
- A Windows service for responding to events (boot, screen on) to reset the keyboard backlight status to a value set by the tray or the CLI application.

The tray application has an Auto mode: a set mode for day and a set mode for night. This is either time based (on set time intervals) or Night light based where it follows Night light state.

The auto mode suspends persistence: the keyboard backlight is set to the day or night level on transition, and user changes are ignored until the next transition. Manual override is still possible, but the backlight status shall be reset to the value set for day or night mode upon encountering a power event.

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
[@Maclay74](https://github.com/Maclay74) for [figuring out NightLight](https://github.com/Maclay74/tiny-screen/blob/main/TinyScreen/Src/Services/NightLight.cs).
