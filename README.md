# StandUp

A Windows tray app that makes you stop working, stand up and walk. It needs nothing installed beyond Windows itself.

Looking for the Mac version? See [mac/README.md](mac/README.md).

## Start it

Put `dist\StandUp.exe` somewhere permanent and double-click it. It asks you to choose a passcode, then runs in the tray.
"Start with Windows" points at wherever the exe was last run from, so run it again if you move it.

## Lock-screen timer

Normal apps can't draw on the Windows lock screen. Tray icon > Settings > **Turn on lock-screen timer** asks for
administrator approval once. It copies the exe to `C:\Program Files\StandUp` and adds a scheduled task that runs as SYSTEM
each time the PC locks. That task exits at once unless a break is running. Once it's on, the button disappears from Settings.
To remove it, run `StandUp.exe --uninstall-lock-timer` as administrator. Problems are logged to `C:\ProgramData\StandUp\lock.log`.
To update the copy after rebuilding the exe, run that command, then turn the timer on again.

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

`build.cmd` compiles `dist\StandUp.exe` with the C# compiler included in Windows. `art\generate.html` draws the built-in pictures.
`tests\EngineTests.cs` covers the timing rules. `tests\LiveProbe.cs` drives the real break screen and covers your display for about 30 seconds.
