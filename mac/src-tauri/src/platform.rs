//! The Mac-only system calls. Other platforms get harmless stand-ins so the rules can still be built and tested there.

#[cfg(target_os = "macos")]
mod imp {
    use std::ffi::{c_char, c_void, CString};
    use std::process::Command;

    type Id = *mut c_void;
    type Sel = *mut c_void;

    #[link(name = "objc")]
    extern "C" {
        fn objc_getClass(name: *const c_char) -> Id;
        fn sel_registerName(name: *const c_char) -> Sel;
        fn objc_msgSend();
    }

    #[link(name = "AppKit", kind = "framework")]
    extern "C" {}

    #[link(name = "CoreGraphics", kind = "framework")]
    extern "C" {
        fn CGSessionCopyCurrentDictionary() -> *const c_void;
        fn CGEventSourceSecondsSinceLastEventType(state: i32, event_type: u32) -> f64;
    }

    #[link(name = "CoreFoundation", kind = "framework")]
    extern "C" {
        fn CFStringCreateWithCString(alloc: *const c_void, text: *const c_char, encoding: u32) -> *const c_void;
        fn CFDictionaryGetValue(dict: *const c_void, key: *const c_void) -> *const c_void;
        fn CFBooleanGetValue(value: *const c_void) -> u8;
        fn CFRelease(value: *const c_void);
    }

    extern "C" {
        fn dlopen(path: *const c_char, mode: i32) -> *mut c_void;
        fn dlsym(handle: *mut c_void, name: *const c_char) -> *mut c_void;
    }

    const UTF8: u32 = 0x0800_0100;

    // NSApplicationPresentationOptions. Force Quit is deliberately left working, so a bug can't trap anyone.
    const HIDE_DOCK: usize = 1 << 1;
    const HIDE_MENU_BAR: usize = 1 << 3;
    const DISABLE_PROCESS_SWITCHING: usize = 1 << 5;
    const DISABLE_HIDE_APPLICATION: usize = 1 << 8;

    const SCREEN_SAVER_LEVEL: isize = 1000;
    const JOIN_ALL_SPACES: usize = 1 << 0;
    const FULL_SCREEN_AUXILIARY: usize = 1 << 8;

    fn sel(name: &str) -> Sel {
        let name = CString::new(name).expect("selector name");
        unsafe { sel_registerName(name.as_ptr()) }
    }

    unsafe fn send_id(target: Id, selector: &str) -> Id {
        let f: unsafe extern "C" fn(Id, Sel) -> Id = std::mem::transmute(objc_msgSend as unsafe extern "C" fn());
        f(target, sel(selector))
    }

    unsafe fn send_usize(target: Id, selector: &str, value: usize) {
        let f: unsafe extern "C" fn(Id, Sel, usize) = std::mem::transmute(objc_msgSend as unsafe extern "C" fn());
        f(target, sel(selector), value)
    }

    unsafe fn send_isize(target: Id, selector: &str, value: isize) {
        let f: unsafe extern "C" fn(Id, Sel, isize) = std::mem::transmute(objc_msgSend as unsafe extern "C" fn());
        f(target, sel(selector), value)
    }

    unsafe fn send_bool(target: Id, selector: &str, value: bool) {
        let f: unsafe extern "C" fn(Id, Sel, u8) = std::mem::transmute(objc_msgSend as unsafe extern "C" fn());
        f(target, sel(selector), value as u8)
    }

    unsafe fn ns_app() -> Id {
        let class = CString::new("NSApplication").expect("class name");
        send_id(objc_getClass(class.as_ptr()), "sharedApplication")
    }

    /// Main thread only.
    pub fn activate() {
        unsafe { send_bool(ns_app(), "activateIgnoringOtherApps:", true) }
    }

    /// Main thread only. Hides the Dock and menu bar and turns off Command-Tab.
    pub fn enter_kiosk() {
        unsafe {
            activate();
            send_usize(ns_app(), "setPresentationOptions:", HIDE_DOCK | HIDE_MENU_BAR | DISABLE_PROCESS_SWITCHING | DISABLE_HIDE_APPLICATION);
        }
    }

    /// Main thread only.
    pub fn exit_kiosk() {
        unsafe { send_usize(ns_app(), "setPresentationOptions:", 0) }
    }

    /// Main thread only. Puts a window above everything, including other apps' full-screen spaces.
    pub fn raise_window(ns_window: *mut c_void) {
        unsafe {
            send_isize(ns_window, "setLevel:", SCREEN_SAVER_LEVEL);
            send_usize(ns_window, "setCollectionBehavior:", JOIN_ALL_SPACES | FULL_SCREEN_AUXILIARY);
            send_id(ns_window, "orderFrontRegardless");
        }
    }

    pub fn screen_is_locked() -> bool {
        unsafe {
            let dict = CGSessionCopyCurrentDictionary();
            if dict.is_null() {
                return false;
            }
            let name = CString::new("CGSSessionScreenIsLocked").expect("key");
            let key = CFStringCreateWithCString(std::ptr::null(), name.as_ptr(), UTF8);
            let value = CFDictionaryGetValue(dict, key);
            let locked = !value.is_null() && CFBooleanGetValue(value) != 0;
            CFRelease(key);
            CFRelease(dict);
            locked
        }
    }

    pub fn lock_screen() {
        unsafe {
            let path = CString::new("/System/Library/PrivateFrameworks/login.framework/Versions/Current/login").expect("path");
            let name = CString::new("SACLockScreenImmediate").expect("name");
            let library = dlopen(path.as_ptr(), 1);
            if !library.is_null() {
                let symbol = dlsym(library, name.as_ptr());
                if !symbol.is_null() {
                    let lock: unsafe extern "C" fn() -> i32 = std::mem::transmute(symbol);
                    lock();
                    return;
                }
            }
        }
        let _ = Command::new("pmset").arg("displaysleepnow").status();
    }

    /// Asks PowerPoint directly. macOS shows a one-time permission prompt the first time this finds it running.
    pub fn powerpoint_slide_show_running() -> bool {
        const SCRIPT: &str = r#"if application "Microsoft PowerPoint" is running then
tell application "Microsoft PowerPoint" to return count of slide show windows
end if
return 0"#;
        Command::new("osascript")
            .args(["-e", SCRIPT])
            .output()
            .ok()
            .and_then(|o| String::from_utf8(o.stdout).ok())
            .and_then(|s| s.trim().parse::<u32>().ok())
            .map_or(false, |count| count > 0)
    }

    pub fn open_folder(path: &std::path::Path) {
        let _ = Command::new("open").arg(path).spawn();
    }

    /// Seconds since the last keyboard or mouse input. Needs no permission.
    pub fn idle_seconds() -> f64 {
        const COMBINED_SESSION_STATE: i32 = 0;
        const ANY_INPUT_EVENT: u32 = u32::MAX;
        unsafe { CGEventSourceSecondsSinceLastEventType(COMBINED_SESSION_STATE, ANY_INPUT_EVENT) }
    }

    pub fn play_sound(path: &std::path::Path) {
        let _ = Command::new("afplay").arg(path).spawn();
    }
}

#[cfg(not(target_os = "macos"))]
mod imp {
    pub fn activate() {}
    pub fn enter_kiosk() {}
    pub fn exit_kiosk() {}
    pub fn screen_is_locked() -> bool { false }
    pub fn lock_screen() {}
    pub fn powerpoint_slide_show_running() -> bool { false }
    pub fn open_folder(_path: &std::path::Path) {}
    pub fn idle_seconds() -> f64 { 0.0 }
    pub fn play_sound(_path: &std::path::Path) {}
}

pub use imp::{activate, enter_kiosk, exit_kiosk, idle_seconds, lock_screen, open_folder, play_sound, powerpoint_slide_show_running, screen_is_locked};

#[cfg(target_os = "macos")]
pub use imp::raise_window;
