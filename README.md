# StandUp

A Windows tray app that makes you stop working, stand up and walk. It needs nothing installed beyond Windows itself.

Looking for the Mac version? See [mac/README.md](mac/README.md).

## Install

Run `release\StandUp-Setup.exe` (built by `package.cmd`). It installs to `%LOCALAPPDATA%\Programs\StandUp` for the current
user, adds a Start menu shortcut and an Installed apps entry, and starts StandUp, which asks for a passcode the first time.

Versions 1.0 and 1.1 could add a timer to the lock screen. Version 1.2 removed it. The installer offers to clean it up, or you
can run `StandUp.exe --uninstall-lock-timer` as administrator.

## Presenting

No break appears during a PowerPoint slide show or Windows presentation mode. The work timer stops, and a full work period
starts about 10 seconds after you finish.

Zoom and Teams screen sharing are not detected, so breaks still appear during a share. To add them later, `tests\ShareRecorder.cs` logs
what those apps do. In a recorded Zoom desktop share, visible windows of class `ZPFloatToolbarClass` and `sharing frame` appeared
only while sharing. Zoom's `CptHost.exe` process keeps running afterwards, so it is not a reliable sign.

## Your data

Everything is in `%LOCALAPPDATA%\StandUp`:

- `settings.ini`: settings and the salted passcode hash
- `history.csv`: one line per break or snooze
- `photos\`: add your own JPG or PNG files here to join the picture pool

Forgot the passcode? End StandUp in Task Manager, delete the `salt=` and `hash=` lines from `settings.ini`, and start it again.

## Build and test

`build.cmd` compiles `dist\StandUp.exe` with the C# compiler included in Windows, and `package.cmd` wraps it into the installer.
`art\generate.html` draws the built-in pictures in each language (`art\render.ps1` saves them as PNGs), and `i18n\strings.json` holds the text for both apps.
`tests\EngineTests.cs` covers the timing rules. `tests\LiveProbe.cs` drives the real break screen and covers your display for about 30 seconds.
