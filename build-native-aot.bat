@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "PROJECT=%SCRIPT_DIR%KeySwitcher\KeySwitcher.csproj"
set "CONFIG=Release"
set "RID=%~1"

if "%RID%"=="" set "RID=win-x64"

echo.
echo [KeySwitcher] Native AOT build started
echo Project: %PROJECT%
echo Config : %CONFIG%
echo RID    : %RID%
echo.

dotnet publish "%PROJECT%" ^
  -c %CONFIG% ^
  -r %RID% ^
  --self-contained true ^
  -p:PublishAot=true ^
  -p:PublishTrimmed=true ^
  -p:TrimMode=partial ^
  -p:InvariantGlobalization=false ^
  -p:DebugType=None ^
  -p:DebugSymbols=false

if errorlevel 1 (
  echo.
  echo [KeySwitcher] Native AOT build failed.
  exit /b 1
)

echo.
echo [KeySwitcher] Native AOT build completed successfully.
echo Output: %SCRIPT_DIR%KeySwitcher\bin\%CONFIG%\net10.0-windows\%RID%\publish
echo.

endlocal
