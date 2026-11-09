//! Settings, passcode and break history, kept in the app's data folder.

use crate::engine::Config;
use base64::{engine::general_purpose::STANDARD, Engine as _};
use chrono::{Local, TimeZone};
use pbkdf2::pbkdf2_hmac;
use rand::RngCore;
use serde::{Deserialize, Serialize};
use sha2::Sha256;
use std::collections::BTreeMap;
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};

const ITERATIONS: u32 = 100_000;

#[derive(Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct Settings {
    pub work_minutes: f64,
    pub break_minutes: f64,
    pub snooze_minutes: f64,
    pub start_with_mac: bool,
    pub passcode_salt: String,
    pub passcode_hash: String,
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            work_minutes: 50.0,
            break_minutes: 2.0,
            snooze_minutes: 2.0,
            start_with_mac: true,
            passcode_salt: String::new(),
            passcode_hash: String::new(),
        }
    }
}

impl Settings {
    pub fn load(path: &Path) -> Self {
        fs::read_to_string(path).ok().and_then(|text| serde_json::from_str(&text).ok()).unwrap_or_default()
    }

    pub fn save(&self, path: &Path) -> std::io::Result<()> {
        fs::write(path, serde_json::to_string_pretty(self).expect("settings serialize"))
    }

    pub fn engine_config(&self) -> Config {
        Config {
            work_seconds: (self.work_minutes * 60.0) as i64,
            break_seconds: (self.break_minutes * 60.0) as i64,
            snooze_seconds: (self.snooze_minutes * 60.0) as i64,
            ..Config::default()
        }
    }

    pub fn has_passcode(&self) -> bool {
        !self.passcode_hash.is_empty()
    }

    pub fn set_passcode(&mut self, passcode: &str) {
        let mut salt = [0u8; 16];
        rand::thread_rng().fill_bytes(&mut salt);
        self.passcode_salt = STANDARD.encode(salt);
        self.passcode_hash = STANDARD.encode(derive(passcode, &salt));
    }

    pub fn check_passcode(&self, passcode: &str) -> bool {
        let (Ok(salt), Ok(expected)) = (STANDARD.decode(&self.passcode_salt), STANDARD.decode(&self.passcode_hash)) else {
            return false;
        };
        let actual = derive(passcode, &salt);
        expected.len() == actual.len() && expected.iter().zip(actual.iter()).fold(0u8, |d, (a, b)| d | (a ^ b)) == 0
    }
}

fn derive(passcode: &str, salt: &[u8]) -> [u8; 32] {
    let mut out = [0u8; 32];
    pbkdf2_hmac::<Sha256>(passcode.as_bytes(), salt, ITERATIONS, &mut out);
    out
}

#[derive(Clone, Default, Serialize)]
pub struct DayStats {
    pub date: String,
    pub breaks: u32,
    pub snoozes: u32,
    pub break_seconds: i64,
}

/// An append-only CSV: local time, "break" or "snooze", seconds. Same format as the Windows app.
pub struct History {
    path: PathBuf,
    days: BTreeMap<String, DayStats>,
}

impl History {
    pub fn load(path: PathBuf) -> Self {
        let mut history = History { path, days: BTreeMap::new() };
        if let Ok(text) = fs::read_to_string(&history.path) {
            for line in text.lines() {
                let parts: Vec<&str> = line.split(',').collect();
                if parts.len() != 3 || parts[0].len() < 10 || !parts[0].is_char_boundary(10) {
                    continue;
                }
                if let Ok(seconds) = parts[2].parse::<i64>() {
                    history.count(&parts[0][..10], parts[1], seconds);
                }
            }
        }
        history
    }

    pub fn breaks_today(&self) -> u32 {
        let today = Local::now().format("%Y-%m-%d").to_string();
        self.days.get(&today).map_or(0, |d| d.breaks)
    }

    pub fn all(&self) -> Vec<DayStats> {
        self.days.values().cloned().collect()
    }

    pub fn record(&mut self, at_ms: i64, kind: &str, seconds: i64) {
        let Some(local) = Local.timestamp_millis_opt(at_ms).single() else { return };
        let stamp = local.format("%Y-%m-%dT%H:%M:%S").to_string();
        self.count(&stamp[..10], kind, seconds);
        if let Ok(mut file) = OpenOptions::new().create(true).append(true).open(&self.path) {
            let _ = writeln!(file, "{stamp},{kind},{seconds}");
        }
    }

    fn count(&mut self, date: &str, kind: &str, seconds: i64) {
        if kind != "break" && kind != "snooze" {
            return;
        }
        let day = self.days.entry(date.to_string()).or_insert_with(|| DayStats { date: date.to_string(), ..DayStats::default() });
        if kind == "break" {
            day.breaks += 1;
            day.break_seconds += seconds;
        } else {
            day.snoozes += 1;
        }
    }
}

const BUILT_IN: [&[u8]; 12] = [
    include_bytes!("../../../art/photos/dog01.png"),
    include_bytes!("../../../art/photos/dog02.png"),
    include_bytes!("../../../art/photos/dog03.png"),
    include_bytes!("../../../art/photos/dog04.png"),
    include_bytes!("../../../art/photos/dog05.png"),
    include_bytes!("../../../art/photos/dog06.png"),
    include_bytes!("../../../art/photos/baby01.png"),
    include_bytes!("../../../art/photos/baby02.png"),
    include_bytes!("../../../art/photos/baby03.png"),
    include_bytes!("../../../art/photos/baby04.png"),
    include_bytes!("../../../art/photos/babydog01.png"),
    include_bytes!("../../../art/photos/babydog02.png"),
];

const EXTENSIONS: [(&str, &str); 7] = [
    ("png", "image/png"),
    ("jpg", "image/jpeg"),
    ("jpeg", "image/jpeg"),
    ("gif", "image/gif"),
    ("webp", "image/webp"),
    ("heic", "image/heic"),
    ("bmp", "image/bmp"),
];

fn mime_for(path: &Path) -> Option<&'static str> {
    let ext = path.extension()?.to_str()?.to_lowercase();
    EXTENSIONS.iter().find(|(e, _)| *e == ext).map(|(_, m)| *m)
}

/// Built-in pictures plus anything the user adds.
pub struct PhotoPool {
    dir: PathBuf,
    last: Option<usize>,
}

impl PhotoPool {
    pub fn new(dir: PathBuf) -> Self {
        let _ = fs::create_dir_all(&dir);
        PhotoPool { dir, last: None }
    }

    pub fn dir(&self) -> &Path {
        &self.dir
    }

    pub fn built_in_count(&self) -> usize {
        BUILT_IN.len()
    }

    pub fn user_files(&self) -> Vec<PathBuf> {
        let mut files: Vec<PathBuf> = fs::read_dir(&self.dir)
            .map(|entries| entries.flatten().map(|e| e.path()).filter(|p| mime_for(p).is_some()).collect())
            .unwrap_or_default();
        files.sort();
        files
    }

    /// Returns a picture as a data URL, never the same one twice in a row.
    pub fn next(&mut self) -> Option<String> {
        let files = self.user_files();
        let total = BUILT_IN.len() + files.len();
        let mut pick = (random_u32() as usize) % total;
        if total > 1 && Some(pick) == self.last {
            pick = (pick + 1) % total;
        }
        self.last = Some(pick);

        if pick < BUILT_IN.len() {
            return Some(format!("data:image/png;base64,{}", STANDARD.encode(BUILT_IN[pick])));
        }
        let path = &files[pick - BUILT_IN.len()];
        let bytes = fs::read(path).ok()?;
        Some(format!("data:{};base64,{}", mime_for(path)?, STANDARD.encode(bytes)))
    }

    pub fn add(&self, name: &str, base64_data: &str) -> Result<(), String> {
        let safe = Path::new(name).file_name().ok_or("That file has no name.")?;
        if mime_for(Path::new(safe)).is_none() {
            return Err(format!("{name} isn't a picture type StandUp can use."));
        }
        let bytes = STANDARD.decode(base64_data).map_err(|e| e.to_string())?;
        let mut target = self.dir.join(safe);
        let mut n = 2;
        while target.exists() {
            let stem = Path::new(safe).file_stem().and_then(|s| s.to_str()).unwrap_or("picture");
            let ext = Path::new(safe).extension().and_then(|s| s.to_str()).unwrap_or("png");
            target = self.dir.join(format!("{stem} ({n}).{ext}"));
            n += 1;
        }
        fs::write(target, bytes).map_err(|e| e.to_string())
    }
}

fn random_u32() -> u32 {
    rand::thread_rng().next_u32()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn passcode_round_trip() {
        let mut s = Settings::default();
        assert!(!s.has_passcode());
        s.set_passcode("4321");
        assert!(s.has_passcode());
        assert!(s.check_passcode("4321"));
        assert!(!s.check_passcode("1234"));
    }

    #[test]
    fn history_skips_bad_lines_and_counts_days() {
        let path = std::env::temp_dir().join(format!("standup-history-{}.csv", std::process::id()));
        fs::write(&path, "2026-11-07T10:00:00,break,120\ngarbage\n2026-11-08T09:00:00,break,120\n2026-11-08T09:01:00,snooze,120\n2026-11-08T10:00:00,break,120\n").unwrap();
        let days = History::load(path.clone()).all();
        fs::remove_file(path).unwrap();
        assert_eq!(days.len(), 2);
        assert_eq!(days[1].date, "2026-11-08");
        assert_eq!((days[1].breaks, days[1].snoozes, days[1].break_seconds), (2, 1, 240));
    }

    #[test]
    fn picture_names_cannot_escape_the_folder() {
        let dir = std::env::temp_dir().join(format!("standup-photos-{}", std::process::id()));
        let pool = PhotoPool::new(dir.clone());
        pool.add("../../escape.png", &STANDARD.encode(b"x")).unwrap();
        assert!(dir.join("escape.png").exists());
        assert!(pool.add("notes.txt", &STANDARD.encode(b"x")).is_err());
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn pictures_never_repeat_back_to_back() {
        let dir = std::env::temp_dir().join(format!("standup-pool-{}", std::process::id()));
        let mut pool = PhotoPool::new(dir.clone());
        let mut previous = String::new();
        for _ in 0..50 {
            let next = pool.next().unwrap();
            assert_ne!(next, previous);
            previous = next;
        }
        fs::remove_dir_all(dir).unwrap();
    }
}
