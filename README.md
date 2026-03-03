# KeySwitcher

KeySwitcher is a lightweight Windows tray app for fast keyboard layout switching.

## Features

- Global hotkey switching (including modifier-only combos like `Ctrl+Shift`)
- Tray indicator with current language label
- One-click tray toggle
- Configurable layout pair (any two installed layouts)
- Input scope mode: per-window or global
- Optional Windows autostart
- Single-instance app

## Requirements

- Windows 11
- .NET 10 SDK (for local build/run)

## Run

```powershell
dotnet run --project .\KeySwitcher\KeySwitcher.csproj
```

## Build

```powershell
dotnet publish .\KeySwitcher\KeySwitcher.csproj -c Release
```

Native AOT:

```powershell
.\build-native-aot.bat
```

Optional RID:

```powershell
.\build-native-aot.bat win-x64
```

AOT output:

`KeySwitcher\bin\Release\net10.0-windows\<RID>\publish`

## Settings and Logs

- Settings: `%AppData%\KeySwitcher\settings.json`
- Logs: `%AppData%\KeySwitcher\logs\keyswitcher-YYYYMMDD.log`

## Notes

- Tray click always toggles globally.
- Hotkey toggle behavior follows selected input scope mode.
- If configured hotkey is unavailable, fallback is `Ctrl+Alt+Space`.
