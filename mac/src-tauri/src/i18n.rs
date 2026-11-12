//! The app's text in each language, from i18n/strings.json (shared with the Windows app).

use std::collections::HashMap;
use std::sync::OnceLock;

pub const LANGUAGES: [&str; 3] = ["en", "es", "vi"];

type Table = HashMap<String, HashMap<String, String>>;

fn table() -> &'static Table {
    static TABLE: OnceLock<Table> = OnceLock::new();
    TABLE.get_or_init(|| serde_json::from_str(include_str!("../../../i18n/strings.json")).expect("strings.json is valid"))
}

pub fn valid(language: &str) -> &str {
    if LANGUAGES.contains(&language) { language } else { "en" }
}

/// Every string in one language, for the web pages.
pub fn all(language: &str) -> HashMap<String, String> {
    let mut strings = table()["en"].clone();
    if let Some(chosen) = table().get(valid(language)) {
        strings.extend(chosen.clone());
    }
    strings
}

pub fn t(language: &str, key: &str) -> String {
    table()
        .get(valid(language))
        .and_then(|l| l.get(key))
        .or_else(|| table()["en"].get(key))
        .cloned()
        .unwrap_or_else(|| key.to_string())
}

/// Like `t`, filling in {0}, {1}, ...
pub fn f(language: &str, key: &str, args: &[&str]) -> String {
    let mut text = t(language, key);
    for (i, arg) in args.iter().enumerate() {
        text = text.replace(&format!("{{{i}}}"), arg);
    }
    text
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_language_has_every_key() {
        for language in LANGUAGES {
            assert_eq!(table()[language].len(), table()["en"].len(), "{language}");
        }
    }

    #[test]
    fn placeholders_are_filled() {
        assert_eq!(f("en", "tray.nextBreak", &["7"]), "Next break in 7 min");
        assert_eq!(f("vi", "tray.nextLongBreak", &["3"]), "Nghỉ dài sau 3 phút");
        assert_eq!(t("xx", "common.save"), "Save");
    }
}
