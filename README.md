# ScreenFlow

A native Windows app for automating repetitive tasks with mouse input, keyboard shortcuts, and OpenCV image matching.

[Download v0.1 for Windows x64](https://github.com/santa512/Screen-Flow/releases/download/v0.1/ScreenFlow-v0.1-win-x64.zip)

## Quick start

1. Extract the ZIP and run `ScreenFlow/ScreenFlow.exe`.
2. Add actions, then pick screen coordinates or capture a visual target.
3. Run the workflow and focus your target app during the countdown. Press **F8** to stop.

For Octo, open the desired profile first. ScreenFlow targets its Octium browser window.

## Features

- Mouse clicks, scrolling, text input, shortcuts, and delays.
- Browser focus, new tabs, URL copying, and variables.
- Visual target detection and workflow repetition.
- Saveable workflows with embedded target images.
- Native Fluent controls with System, Light, and Dark themes.

## Build

Requires Windows x64 and a .NET 10-compatible SDK.

```powershell
./build.ps1 -Test -Publish
```

Output: `dist/ScreenFlow-Production/ScreenFlow.exe`.

[Usage guide](docs/USAGE.md) · [Changelog](CHANGELOG.md)
