# Monitor Control

A small Windows app for changing monitor settings from the desktop.

## What it can do

- Adjust brightness and contrast
- Switch input source (HDMI, DisplayPort, USB-C...)
- Change color presets, picture modes and red/green/blue levels
- Control multiple monitors

## Tech

- C# with Windows Forms (.NET Framework 4)
- DDC/CI through the Windows monitor API (`dxva2.dll`)
- Built with `build.bat` using the C# compiler included with Windows
