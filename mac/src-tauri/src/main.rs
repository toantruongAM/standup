// StandUp for Mac. The break rules are in engine.rs and the Mac system calls in platform.rs.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod engine;
mod platform;
mod storage;

use engine::{Engine, Event, Phase};
use serde::Serialize;
use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use storage::{DayStats, History, PhotoPool, Settings};
use tauri::menu::{Menu, MenuItem, PredefinedMenuItem};
use tauri::image::Image;
use tauri::tray::TrayIconBuilder;
use tauri::{AppHandle, Manager, RunEvent, State, WebviewUrl, WebviewWindow, WebviewWindowBuilder, WindowEvent, Wry};
use tauri_plugin_autostart::{MacosLauncher, ManagerExt};

const MIN_PASSCODE: usize = 4;
const PRESENTING_END_DELAY_MS: i64 = 10_000;

struct Core {
    engine: Engine,
    settings: Settings,
    settings_path: PathBuf,
    history: History,
    photos: PhotoPool,
    started: bool,
    presenting_seen: i64,
    overlays_up: bool,
    overlay_count: usize,
}

type Shared = Arc<Mutex<Core>>;

struct TrayItems {
    status: MenuItem<Wry>,
    today: MenuItem<Wry>,
    take_break: MenuItem<Wry>,
}

/// Latest answer from the slide-show watcher thread.
static SLIDE_SHOW: AtomicBool = AtomicBool::new(false);

fn now_ms() -> i64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_millis() as i64)
}

// ---- the background clock ----

fn spawn_clock(app: AppHandle) {
    thread::spawn(|| loop {
        SLIDE_SHOW.store(platform::powerpoint_slide_show_running(), Ordering::Relaxed);
        thread::sleep(Duration::from_secs(2));
    });

    thread::spawn(move || {
        let mut was_locked = false;
        let mut count: u64 = 0;
        let mut last_status = String::new();
        loop {
            thread::sleep(Duration::from_millis(250));
            count += 1;
            let shared = app.state::<Shared>();
            let now = now_ms();
            let mut core = shared.lock().expect("core lock");
            if !core.started {
                continue;
            }

            if count % 4 == 0 {
                let locked = platform::screen_is_locked();
                if locked && !was_locked {
                    core.engine.lock();
                } else if !locked && was_locked {
                    core.engine.unlock(now);
                }
                was_locked = locked;
            }

            if SLIDE_SHOW.load(Ordering::Relaxed) {
                core.presenting_seen = now;
                core.engine.set_presenting(true, now);
            } else if core.engine.is_presenting() && now - core.presenting_seen >= PRESENTING_END_DELAY_MS {
                core.engine.set_presenting(false, now);
            }

            core.engine.tick(now);
            for event in core.engine.take_events() {
                match event {
                    Event::BreakCompleted { at, seconds } => core.history.record(at, "break", seconds),
                    Event::Snoozed { at, seconds } => core.history.record(at, "snooze", seconds),
                }
            }

            let on_break = core.engine.phase == Phase::Break;
            let (overlays_up, overlay_count) = (core.overlays_up, core.overlay_count);
            let keep_active = on_break && count % 4 == 0;

            let status = status_text(&core, now);
            let breaks = core.history.breaks_today();
            let working = core.engine.phase == Phase::Working;
            drop(core);

            // Asking for the monitors waits on the main thread, so the lock must already be released.
            let needs_sync = on_break != overlays_up
                || (on_break && count % 8 == 0 && app.available_monitors().map_or(false, |m| m.len() != overlay_count));

            if needs_sync {
                let handle = app.clone();
                let _ = app.run_on_main_thread(move || sync_overlays(&handle));
            } else if keep_active {
                let _ = app.run_on_main_thread(platform::activate);
            }

            let line = format!("{status}|{breaks}|{working}");
            if line != last_status {
                last_status = line;
                let items = app.state::<TrayItems>();
                let _ = items.status.set_text(status);
                let _ = items.today.set_text(format!("Breaks today: {breaks}"));
                let _ = items.take_break.set_enabled(working);
            }
        }
    });
}

fn status_text(core: &Core, now: i64) -> String {
    match core.engine.phase {
        Phase::Break => "On a break".to_string(),
        Phase::Paused if core.engine.is_presenting() => "Paused while presenting".to_string(),
        Phase::Paused => "Paused while locked".to_string(),
        Phase::Working => {
            let minutes = ((core.engine.deadline - now) as f64 / 60_000.0).ceil().max(1.0) as i64;
            format!("Next break in {minutes} min{}", if core.engine.in_snooze { " (snoozed)" } else { "" })
        }
    }
}

// ---- break screens ----

fn overlay_windows(app: &AppHandle) -> Vec<WebviewWindow> {
    app.webview_windows().into_iter().filter(|(label, _)| label.starts_with("overlay-")).map(|(_, w)| w).collect()
}

fn sync_overlays(app: &AppHandle) {
    let shared = app.state::<Shared>();
    let monitors = app.available_monitors().unwrap_or_default();
    let (on_break, up, count) = {
        let core = shared.lock().expect("core lock");
        (core.engine.phase == Phase::Break, core.overlays_up, core.overlay_count)
    };
    // Requests can queue up while the main thread is busy; only the first one has work to do.
    if on_break == up && (!on_break || count == monitors.len()) {
        return;
    }

    for window in overlay_windows(app) {
        let _ = window.destroy();
    }

    if !on_break {
        platform::exit_kiosk();
        let mut core = shared.lock().expect("core lock");
        core.overlays_up = false;
        core.overlay_count = 0;
        return;
    }

    let primary = app.primary_monitor().ok().flatten();
    platform::enter_kiosk();

    for (index, monitor) in monitors.iter().enumerate() {
        let is_primary = primary.as_ref().map_or(index == 0, |p| p.position() == monitor.position());
        let scale = monitor.scale_factor();
        let (x, y) = (monitor.position().x as f64 / scale, monitor.position().y as f64 / scale);
        let (width, height) = (monitor.size().width as f64 / scale, monitor.size().height as f64 / scale);

        let built = WebviewWindowBuilder::new(app, format!("overlay-{index}"), WebviewUrl::App("overlay.html".into()))
            .title("StandUp break")
            .initialization_script(format!("window.STANDUP = {{ primary: {is_primary} }};").as_str())
            .decorations(false)
            .resizable(false)
            .always_on_top(true)
            .skip_taskbar(true)
            .visible_on_all_workspaces(true)
            .position(x, y)
            .inner_size(width, height)
            .focused(is_primary)
            .build();

        if let Ok(window) = built {
            #[cfg(target_os = "macos")]
            {
                if let Ok(ns_window) = window.ns_window() {
                    platform::raise_window(ns_window);
                }
            }
            if is_primary {
                let _ = window.set_focus();
            }
        }
    }

    let mut core = shared.lock().expect("core lock");
    core.overlays_up = true;
    core.overlay_count = monitors.len();
}

#[derive(Serialize)]
struct BreakState {
    left_ms: i64,
    can_snooze: bool,
    restarted: bool,
}

#[tauri::command]
fn break_state(shared: State<'_, Shared>) -> BreakState {
    let core = shared.lock().expect("core lock");
    let now = now_ms();
    BreakState {
        left_ms: (core.engine.deadline - now).max(0),
        can_snooze: core.engine.can_snooze(),
        restarted: now - core.engine.last_activity < 2000,
    }
}

#[tauri::command]
fn activity(shared: State<'_, Shared>) {
    shared.lock().expect("core lock").engine.activity(now_ms());
}

#[tauri::command]
fn snooze(shared: State<'_, Shared>) {
    shared.lock().expect("core lock").engine.snooze(now_ms());
}

#[tauri::command]
fn lock_screen() {
    platform::lock_screen();
}

#[tauri::command]
fn next_photo(shared: State<'_, Shared>) -> Option<String> {
    shared.lock().expect("core lock").photos.next()
}

// ---- dialogs ----

fn open_dialog(app: &AppHandle, label: &str, page: &str, title: &str, width: f64, height: f64, script: &str) {
    if let Some(existing) = app.get_webview_window(label) {
        let _ = existing.set_focus();
        return;
    }
    let built = WebviewWindowBuilder::new(app, label, WebviewUrl::App(page.into()))
        .title(title)
        .initialization_script(script)
        .inner_size(width, height)
        .resizable(false)
        .center()
        .build();
    if let Ok(window) = built {
        platform::activate();
        let _ = window.set_focus();
    }
}

fn open_passcode(app: &AppHandle, mode: &str) {
    open_dialog(app, "passcode", "passcode.html", "StandUp", 380.0, 300.0, &format!("window.STANDUP = {{ mode: '{mode}' }};"));
}

fn open_settings(app: &AppHandle) {
    open_dialog(app, "settings", "settings.html", "StandUp settings", 420.0, 470.0, "");
}

fn open_history(app: &AppHandle) {
    open_dialog(app, "history", "history.html", "StandUp history", 720.0, 520.0, "");
}

fn begin(app: &AppHandle) {
    let shared = app.state::<Shared>();
    let mut core = shared.lock().expect("core lock");
    if core.started {
        return;
    }
    core.started = true;
    core.engine.start(now_ms());
    let autostart = core.settings.start_with_mac;
    drop(core);
    apply_autostart(app, autostart);
}

fn apply_autostart(app: &AppHandle, enabled: bool) {
    let launcher = app.autolaunch();
    let _ = if enabled { launcher.enable() } else { launcher.disable() };
}

#[tauri::command]
fn close_self(window: WebviewWindow) {
    let _ = window.close();
}

/// Checks the passcode, then does what it was asked for. Returns false if it was wrong.
#[tauri::command]
fn submit_passcode(app: AppHandle, window: WebviewWindow, shared: State<'_, Shared>, mode: String, passcode: String) -> bool {
    if !shared.lock().expect("core lock").settings.check_passcode(&passcode) {
        return false;
    }
    let _ = window.destroy();
    match mode.as_str() {
        "exit" => app.exit(0),
        "settings" => open_settings(&app),
        _ => {}
    }
    true
}

#[tauri::command]
fn set_passcode(app: AppHandle, window: WebviewWindow, shared: State<'_, Shared>, current: String, passcode: String) -> Result<(), String> {
    if passcode.chars().count() < MIN_PASSCODE {
        return Err(format!("Use at least {MIN_PASSCODE} characters."));
    }
    let mut core = shared.lock().expect("core lock");
    let first_time = !core.settings.has_passcode();
    if !first_time && !core.settings.check_passcode(&current) {
        return Err("The current passcode is not right.".to_string());
    }
    core.settings.set_passcode(&passcode);
    let path = core.settings_path.clone();
    core.settings.save(&path).map_err(|e| e.to_string())?;
    drop(core);

    if first_time {
        let _ = window.destroy();
        begin(&app);
    }
    Ok(())
}

#[derive(Serialize)]
struct SettingsView {
    work_minutes: f64,
    break_minutes: f64,
    snooze_minutes: f64,
    start_with_mac: bool,
    built_in_pictures: usize,
    your_pictures: usize,
}

#[tauri::command]
fn get_settings(shared: State<'_, Shared>) -> SettingsView {
    let core = shared.lock().expect("core lock");
    SettingsView {
        work_minutes: core.settings.work_minutes,
        break_minutes: core.settings.break_minutes,
        snooze_minutes: core.settings.snooze_minutes,
        start_with_mac: core.settings.start_with_mac,
        built_in_pictures: core.photos.built_in_count(),
        your_pictures: core.photos.user_files().len(),
    }
}

#[tauri::command]
fn save_settings(app: AppHandle, shared: State<'_, Shared>, work: f64, rest: f64, snooze: f64, autostart: bool) -> Result<(), String> {
    let valid = |value: f64, min: f64, max: f64| value.is_finite() && value >= min && value <= max;
    if !valid(work, 1.0, 480.0) || !valid(rest, 0.5, 60.0) || !valid(snooze, 0.5, 60.0) {
        return Err("Work time must be 1 to 480 minutes. Break and snooze must be 0.5 to 60 minutes.".to_string());
    }
    let mut core = shared.lock().expect("core lock");
    core.settings.work_minutes = work;
    core.settings.break_minutes = rest;
    core.settings.snooze_minutes = snooze;
    core.settings.start_with_mac = autostart;
    let path = core.settings_path.clone();
    core.settings.save(&path).map_err(|e| e.to_string())?;
    let config = core.settings.engine_config();
    core.engine.apply_config(config, now_ms());
    drop(core);
    apply_autostart(&app, autostart);
    Ok(())
}

#[tauri::command]
fn add_picture(shared: State<'_, Shared>, name: String, data: String) -> Result<(), String> {
    shared.lock().expect("core lock").photos.add(&name, &data)
}

#[tauri::command]
fn open_pictures_folder(shared: State<'_, Shared>) {
    let dir = shared.lock().expect("core lock").photos.dir().to_path_buf();
    platform::open_folder(&dir);
}

#[tauri::command]
fn history_days(shared: State<'_, Shared>) -> Vec<DayStats> {
    shared.lock().expect("core lock").history.all()
}

// ---- start-up ----

fn build_core(app: &AppHandle) -> Result<Core, Box<dyn std::error::Error>> {
    let dir = app.path().app_data_dir()?;
    fs::create_dir_all(&dir)?;
    let settings_path = dir.join("settings.json");
    let settings = Settings::load(&settings_path);
    Ok(Core {
        engine: Engine::new(settings.engine_config()),
        history: History::load(dir.join("history.csv")),
        photos: PhotoPool::new(dir.join("photos")),
        settings,
        settings_path,
        started: false,
        presenting_seen: 0,
        overlays_up: false,
        overlay_count: 0,
    })
}

fn build_tray(app: &AppHandle) -> tauri::Result<()> {
    let status = MenuItem::with_id(app, "status", "Starting", false, None::<&str>)?;
    let today = MenuItem::with_id(app, "today", "Breaks today: 0", false, None::<&str>)?;
    let take_break = MenuItem::with_id(app, "break", "Take a break now", true, None::<&str>)?;
    let settings = MenuItem::with_id(app, "settings", "Settings...", true, None::<&str>)?;
    let history = MenuItem::with_id(app, "history", "History...", true, None::<&str>)?;
    let exit = MenuItem::with_id(app, "exit", "Exit...", true, None::<&str>)?;
    let separator_one = PredefinedMenuItem::separator(app)?;
    let separator_two = PredefinedMenuItem::separator(app)?;
    let menu = Menu::with_items(app, &[&status, &today, &separator_one, &take_break, &settings, &history, &separator_two, &exit])?;

    TrayIconBuilder::with_id("standup")
        .icon(Image::from_bytes(include_bytes!("../icons/tray.png"))?)
        .icon_as_template(true)
        .menu(&menu)
        .on_menu_event(|app, event| match event.id().as_ref() {
            "break" => {
                let shared = app.state::<Shared>();
                shared.lock().expect("core lock").engine.take_break_now(now_ms());
            }
            "settings" => open_passcode(app, "settings"),
            "history" => open_history(app),
            "exit" => open_passcode(app, "exit"),
            _ => {}
        })
        .build(app)?;

    app.manage(TrayItems { status, today, take_break });
    Ok(())
}

fn main() {
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_autostart::init(MacosLauncher::LaunchAgent, None))
        .menu(|handle| Menu::new(handle))
        .invoke_handler(tauri::generate_handler![
            break_state,
            activity,
            snooze,
            lock_screen,
            next_photo,
            close_self,
            submit_passcode,
            set_passcode,
            get_settings,
            save_settings,
            add_picture,
            open_pictures_folder,
            history_days
        ])
        .setup(|app| {
            #[cfg(target_os = "macos")]
            app.set_activation_policy(tauri::ActivationPolicy::Accessory);

            let handle = app.handle().clone();
            let core = build_core(&handle)?;
            let needs_passcode = !core.settings.has_passcode();
            app.manage::<Shared>(Arc::new(Mutex::new(core)));
            build_tray(&handle)?;
            spawn_clock(handle.clone());

            if needs_passcode {
                open_passcode(&handle, "setup");
            } else {
                begin(&handle);
            }
            Ok(())
        })
        .on_window_event(|window, event| {
            // Closing the first-run passcode window without choosing one quits, as Cancel does.
            if let WindowEvent::CloseRequested { .. } = event {
                let app = window.app_handle();
                let started = app.state::<Shared>().lock().expect("core lock").started;
                if window.label() == "passcode" && !started {
                    app.exit(0);
                }
            }
        })
        .build(tauri::generate_context!())
        .expect("error while starting StandUp");

    app.run(|_handle, event| {
        // A tray app must stay alive when its last window closes.
        if let RunEvent::ExitRequested { api, code, .. } = event {
            if code.is_none() {
                api.prevent_exit();
            }
        }
    });
}
