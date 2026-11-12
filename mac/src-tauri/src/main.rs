// StandUp for Mac. The break rules are in engine.rs, text in i18n.rs, and the Mac system calls in platform.rs.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod engine;
mod i18n;
mod platform;
mod storage;

use engine::{Engine, Event, Phase};
use serde::Serialize;
use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use storage::{DayStats, History, PhotoPool, Settings};
use tauri::image::Image;
use tauri::menu::{Menu, MenuItem, PredefinedMenuItem};
use tauri::tray::TrayIconBuilder;
use tauri::{AppHandle, Manager, RunEvent, State, WebviewUrl, WebviewWindow, WebviewWindowBuilder, WindowEvent, Wry};
use tauri_plugin_autostart::{MacosLauncher, ManagerExt};

const MIN_PASSCODE: usize = 4;
const PRESENTING_END_DELAY_MS: i64 = 10_000;
const START_SOUND: &[u8] = include_bytes!("../../../art/sounds/break-start.wav");
const END_SOUND: &[u8] = include_bytes!("../../../art/sounds/break-end.wav");

struct Core {
    engine: Engine,
    settings: Settings,
    settings_path: PathBuf,
    history: History,
    photos: PhotoPool,
    start_sound: PathBuf,
    end_sound: PathBuf,
    started: bool,
    presenting_seen: i64,
    overlays_up: bool,
    overlay_count: usize,
}

impl Core {
    fn lang(&self) -> &str {
        i18n::valid(&self.settings.language)
    }
}

type Shared = Arc<Mutex<Core>>;

struct TrayItems {
    status: MenuItem<Wry>,
    today: MenuItem<Wry>,
    take_break: MenuItem<Wry>,
    settings: MenuItem<Wry>,
    history: MenuItem<Wry>,
    exit: MenuItem<Wry>,
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
                    core.engine.lock(now);
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

            let away = platform::idle_seconds() >= core.settings.idle_minutes * 60.0;
            core.engine.set_idle(away, now);

            core.engine.tick(now);
            let mut sounds = Vec::new();
            for event in core.engine.take_events() {
                match event {
                    Event::BreakStarted { .. } => sounds.push(core.start_sound.clone()),
                    Event::BreakCompleted { at, seconds } => {
                        core.history.record(at, "break", seconds);
                        sounds.push(core.end_sound.clone());
                    }
                    Event::Snoozed { at, seconds } => core.history.record(at, "snooze", seconds),
                }
            }

            let on_break = core.engine.phase == Phase::Break;
            let (overlays_up, overlay_count) = (core.overlays_up, core.overlay_count);
            let keep_active = on_break && count % 4 == 0;

            let lang = core.lang().to_string();
            let status = status_text(&core, now);
            let breaks = core.history.breaks_today();
            let working = core.engine.phase == Phase::Working;
            drop(core);

            for sound in sounds {
                platform::play_sound(&sound);
            }

            // Asking for the monitors waits on the main thread, so the lock must already be released.
            let needs_sync = on_break != overlays_up
                || (on_break && count % 8 == 0 && app.available_monitors().map_or(false, |m| m.len() != overlay_count));

            if needs_sync {
                let handle = app.clone();
                let _ = app.run_on_main_thread(move || sync_overlays(&handle));
            } else if keep_active {
                let _ = app.run_on_main_thread(platform::activate);
            }

            let line = format!("{lang}|{status}|{breaks}|{working}");
            if line != last_status {
                last_status = line;
                let items = app.state::<TrayItems>();
                let _ = items.status.set_text(status);
                let _ = items.today.set_text(i18n::f(&lang, "tray.breaksToday", &[&breaks.to_string()]));
                let _ = items.take_break.set_enabled(working);
            }
        }
    });
}

fn status_text(core: &Core, now: i64) -> String {
    let lang = core.lang();
    let engine = &core.engine;
    match engine.phase {
        Phase::Break if engine.is_long_break => i18n::t(lang, "tray.onLongBreak"),
        Phase::Break => i18n::t(lang, "tray.onBreak"),
        Phase::Paused if engine.is_presenting() => i18n::t(lang, "tray.pausedPresenting"),
        Phase::Paused => i18n::t(lang, "tray.pausedLocked"),
        Phase::Working if engine.is_idle() => i18n::t(lang, "tray.pausedIdle"),
        Phase::Working => {
            let minutes = ((engine.deadline - now) as f64 / 60_000.0).ceil().max(1.0) as i64;
            let key = if engine.next_break_is_long(now) { "tray.nextLongBreak" } else { "tray.nextBreak" };
            let mut text = i18n::f(lang, key, &[&minutes.to_string()]);
            if engine.in_snooze {
                text = format!("{text} {}", i18n::t(lang, "tray.snoozed"));
            }
            text
        }
    }
}

fn apply_tray_language(app: &AppHandle, lang: &str) {
    let items = app.state::<TrayItems>();
    let _ = items.take_break.set_text(i18n::t(lang, "tray.takeBreak"));
    let _ = items.settings.set_text(i18n::t(lang, "tray.settings"));
    let _ = items.history.set_text(i18n::t(lang, "tray.history"));
    let _ = items.exit.set_text(i18n::t(lang, "tray.exit"));
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
            .title("StandUp")
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
    long: bool,
}

#[tauri::command]
fn break_state(shared: State<'_, Shared>) -> BreakState {
    let core = shared.lock().expect("core lock");
    let now = now_ms();
    BreakState {
        left_ms: (core.engine.deadline - now).max(0),
        can_snooze: core.engine.can_snooze(),
        restarted: now - core.engine.last_activity < 2000,
        long: core.engine.is_long_break,
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
    let mut core = shared.lock().expect("core lock");
    let lang = core.lang().to_string();
    core.photos.next(&lang)
}

#[derive(Serialize)]
struct UiStrings {
    lang: String,
    strings: HashMap<String, String>,
}

#[tauri::command]
fn ui_strings(shared: State<'_, Shared>) -> UiStrings {
    let lang = shared.lock().expect("core lock").lang().to_string();
    UiStrings { strings: i18n::all(&lang), lang }
}

// ---- dialogs ----

fn open_dialog(app: &AppHandle, label: &str, page: &str, title_key: &str, width: f64, height: f64, script: &str) {
    if let Some(existing) = app.get_webview_window(label) {
        let _ = existing.set_focus();
        return;
    }
    let lang = app.state::<Shared>().lock().expect("core lock").lang().to_string();
    let built = WebviewWindowBuilder::new(app, label, WebviewUrl::App(page.into()))
        .title(i18n::t(&lang, title_key))
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
    open_dialog(app, "passcode", "passcode.html", "passcode.title", 400.0, 320.0, &format!("window.STANDUP = {{ mode: '{mode}' }};"));
}

fn open_settings(app: &AppHandle) {
    open_dialog(app, "settings", "settings.html", "settings.title", 460.0, 540.0, "");
}

fn open_history(app: &AppHandle) {
    open_dialog(app, "history", "history.html", "history.title", 720.0, 520.0, "");
}

#[tauri::command]
fn open_advanced(app: AppHandle) {
    open_dialog(&app, "advanced", "advanced.html", "advanced.title", 460.0, 340.0, "");
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
    let mut core = shared.lock().expect("core lock");
    let lang = core.lang().to_string();
    if passcode.chars().count() < MIN_PASSCODE {
        return Err(i18n::f(&lang, "passcode.tooShort", &[&MIN_PASSCODE.to_string()]));
    }
    let first_time = !core.settings.has_passcode();
    if !first_time && !core.settings.check_passcode(&current) {
        return Err(i18n::t(&lang, "passcode.currentWrong"));
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
    long_every_minutes: f64,
    long_break_minutes: f64,
    snooze_minutes: f64,
    start_with_mac: bool,
}

#[tauri::command]
fn get_settings(shared: State<'_, Shared>) -> SettingsView {
    let core = shared.lock().expect("core lock");
    let s = &core.settings;
    SettingsView {
        work_minutes: s.work_minutes,
        break_minutes: s.break_minutes,
        long_every_minutes: s.long_every_minutes,
        long_break_minutes: s.long_break_minutes,
        snooze_minutes: s.snooze_minutes,
        start_with_mac: s.start_with_mac,
    }
}

#[tauri::command]
fn save_settings(
    app: AppHandle,
    shared: State<'_, Shared>,
    work: f64,
    rest: f64,
    long_every: f64,
    long_rest: f64,
    snooze: f64,
    autostart: bool,
) -> Result<(), String> {
    let valid = |value: f64, min: f64, max: f64| value.is_finite() && value >= min && value <= max;
    let mut core = shared.lock().expect("core lock");
    if !valid(work, 1.0, 480.0) || !valid(long_every, 1.0, 480.0) || !valid(rest, 0.5, 60.0) || !valid(long_rest, 0.5, 60.0) || !valid(snooze, 0.5, 60.0) {
        return Err(i18n::t(core.lang(), "settings.invalid"));
    }
    let s = &mut core.settings;
    s.work_minutes = work;
    s.break_minutes = rest;
    s.long_every_minutes = long_every;
    s.long_break_minutes = long_rest;
    s.snooze_minutes = snooze;
    s.start_with_mac = autostart;
    let path = core.settings_path.clone();
    core.settings.save(&path).map_err(|e| e.to_string())?;
    let config = core.settings.engine_config();
    core.engine.apply_config(config, now_ms());
    drop(core);
    apply_autostart(&app, autostart);
    Ok(())
}

#[derive(Serialize)]
struct LanguageOption {
    code: String,
    name: String,
}

#[derive(Serialize)]
struct AdvancedView {
    idle_minutes: f64,
    language: String,
    languages: Vec<LanguageOption>,
    built_in_pictures: usize,
    your_pictures: usize,
}

#[tauri::command]
fn get_advanced(shared: State<'_, Shared>) -> AdvancedView {
    let core = shared.lock().expect("core lock");
    AdvancedView {
        idle_minutes: core.settings.idle_minutes,
        language: core.lang().to_string(),
        languages: i18n::LANGUAGES.iter().map(|code| LanguageOption { code: code.to_string(), name: i18n::t(code, "lang.name") }).collect(),
        built_in_pictures: core.photos.built_in_count(),
        your_pictures: core.photos.user_files().len(),
    }
}

/// Saves the advanced settings. A new language is applied everywhere at once.
#[tauri::command]
fn save_advanced(app: AppHandle, shared: State<'_, Shared>, idle: f64, language: String) -> Result<(), String> {
    let mut core = shared.lock().expect("core lock");
    if !idle.is_finite() || !(1.0..=60.0).contains(&idle) {
        return Err(i18n::t(core.lang(), "advanced.invalidIdle"));
    }
    let language = i18n::valid(&language).to_string();
    let changed = language != core.lang();
    core.settings.idle_minutes = idle;
    core.settings.language = language.clone();
    let path = core.settings_path.clone();
    core.settings.save(&path).map_err(|e| e.to_string())?;
    drop(core);

    if changed {
        apply_tray_language(&app, &language);
        for (label, key) in [("settings", "settings.title"), ("history", "history.title")] {
            if let Some(window) = app.get_webview_window(label) {
                let _ = window.set_title(&i18n::t(&language, key));
                let _ = window.eval("location.reload()");
            }
        }
    }
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
    let start_sound = dir.join("break-start.wav");
    let end_sound = dir.join("break-end.wav");
    fs::write(&start_sound, START_SOUND)?;
    fs::write(&end_sound, END_SOUND)?;
    Ok(Core {
        engine: Engine::new(settings.engine_config()),
        history: History::load(dir.join("history.csv")),
        photos: PhotoPool::new(dir.join("photos")),
        settings,
        settings_path,
        start_sound,
        end_sound,
        started: false,
        presenting_seen: 0,
        overlays_up: false,
        overlay_count: 0,
    })
}

fn build_tray(app: &AppHandle, lang: &str) -> tauri::Result<()> {
    let status = MenuItem::with_id(app, "status", "StandUp", false, None::<&str>)?;
    let today = MenuItem::with_id(app, "today", i18n::f(lang, "tray.breaksToday", &["0"]), false, None::<&str>)?;
    let take_break = MenuItem::with_id(app, "break", i18n::t(lang, "tray.takeBreak"), true, None::<&str>)?;
    let settings = MenuItem::with_id(app, "settings", i18n::t(lang, "tray.settings"), true, None::<&str>)?;
    let history = MenuItem::with_id(app, "history", i18n::t(lang, "tray.history"), true, None::<&str>)?;
    let exit = MenuItem::with_id(app, "exit", i18n::t(lang, "tray.exit"), true, None::<&str>)?;
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

    app.manage(TrayItems { status, today, take_break, settings, history, exit });
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
            ui_strings,
            close_self,
            submit_passcode,
            set_passcode,
            get_settings,
            save_settings,
            open_advanced,
            get_advanced,
            save_advanced,
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
            let lang = core.lang().to_string();
            app.manage::<Shared>(Arc::new(Mutex::new(core)));
            build_tray(&handle, &lang)?;
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
