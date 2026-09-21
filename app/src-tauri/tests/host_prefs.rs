//! Given C# ApplyThemeMode + UiLanguage. When set_theme_mode / set_ui_language. Then persist; language records restart.

use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

#[path = "../src/hw_prefs.rs"]
mod hw_prefs;

use hw_prefs::{load, set_theme_mode, set_ui_language, ThemeMode, UiLanguage};

static PREFS_SEQ: AtomicU64 = AtomicU64::new(0);

fn unique_prefs_dir() -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "lmechrevo-prefs-{}-{}",
        std::process::id(),
        PREFS_SEQ.fetch_add(1, Ordering::Relaxed)
    ));
    fs::create_dir_all(&dir).expect("temp prefs dir");
    dir
}

#[test]
fn set_theme_mode_day_survives_fresh_read() {
    // Given: empty prefs dir (default theme is night)
    let dir = unique_prefs_dir();

    // When: set_theme_mode("day") then a new load
    set_theme_mode(&dir, "day").expect("day is a C# theme");
    let prefs = load(&dir, "en").expect("fresh read");

    // Then: persisted theme is day
    assert_eq!(prefs.theme_mode, ThemeMode::Day);
}

#[test]
fn set_theme_mode_rejects_invalid_including_follow_system() {
    // Given: empty prefs dir
    let dir = unique_prefs_dir();

    // When: a mode other than night/day is written
    // Then: rejected (C# ApplyThemeMode is night/day only)
    for mode in ["follow-system", "system", "auto", "DAY", ""] {
        assert!(set_theme_mode(&dir, mode).is_err(), "must reject {mode:?}");
    }
}

#[test]
fn set_ui_language_en_persists_across_fresh_read() {
    // Given: empty prefs dir
    let dir = unique_prefs_dir();

    // When: set_ui_language("en") then a new load
    set_ui_language(&dir, "en").expect("en is a C# language");
    let prefs = load(&dir, "zh-CN").expect("fresh read");

    // Then: stored language wins over OS culture
    assert_eq!(prefs.language, UiLanguage::En);
}

#[test]
fn set_ui_language_zh_cn_persists_across_fresh_read() {
    // Given: empty prefs dir
    let dir = unique_prefs_dir();

    // When: set_ui_language("zh-CN") then a new load
    set_ui_language(&dir, "zh-CN").expect("zh-CN is a C# language");
    let prefs = load(&dir, "en").expect("fresh read");

    // Then: stored language wins over OS culture
    assert_eq!(prefs.language, UiLanguage::ZhCn);
}

#[test]
fn set_ui_language_rejects_anything_else() {
    // Given: empty prefs dir
    let dir = unique_prefs_dir();

    // When: a code other than en / zh-CN is written
    // Then: rejected
    for code in ["zh", "en-US", "zh-TW", "de", ""] {
        assert!(set_ui_language(&dir, code).is_err(), "must reject {code:?}");
    }
}

#[test]
fn persist_io_failure_returns_err() {
    // Given: prefs.json path is a directory, so write cannot succeed
    let dir = unique_prefs_dir();
    fs::create_dir_all(dir.join("prefs.json")).expect("block prefs path");

    // When: set_theme_mode tries to persist
    let result = set_theme_mode(&dir, "day");

    // Then: Err, never a silent Ok
    assert!(
        result.is_err(),
        "IO failure must not be silent Ok: {result:?}"
    );
}

#[test]
fn set_ui_language_records_restart_request() {
    // Given: empty prefs dir (Fake does not spawn a process)
    let dir = unique_prefs_dir();

    // When: language is changed
    let restart = set_ui_language(&dir, "en").expect("en accepted");

    // Then: Fake records the restart request (C# restarts to apply)
    assert!(restart.requested, "language change must request restart");
}
