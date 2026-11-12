# StandUp for Mac

The Mac version of StandUp, built with [Tauri](https://tauri.app). The Windows app in `src/` is separate and unchanged.

## Get the app

GitHub builds it. Open this repository's **Actions** tab, choose the latest **Build Mac app** run, and download **StandUp-mac** under Artifacts. Unzip it, open the `.dmg`, and drag StandUp to Applications.

The app isn't notarized by Apple, so macOS blocks the first launch. Try to open it, then go to **System Settings > Privacy & Security** and click **Open Anyway**.

## What it does

The same as the Windows app: a 2-minute break every hour and a 10-minute break every 3 hours, chimes at the start and end, the work clock pausing while you're away (5 minutes by default), and English, Español and Tiếng Việt (Advanced settings). The text for both apps lives in `i18n/strings.json`.

## Differences from Windows

| Feature | Mac |
|---|---|
| Timer on the lock screen | Not available. macOS doesn't let normal apps draw there; the break still keeps counting |
| Lock during a break | Control+Command+Q instead of Win+L |
| PowerPoint pause | Asks once for permission to check PowerPoint |
| Force Quit | Left working on purpose, so a bug can't trap you |
| Forgot passcode | Quit with Force Quit, delete `~/Library/Application Support/io.github.toantruongam.standup/settings.json`, and reopen |

## Develop

Needs a Mac with Rust. In `mac/src-tauri`, run `cargo test`, or `cargo install tauri-cli --version "^2" --locked` and then `cargo tauri dev`.
