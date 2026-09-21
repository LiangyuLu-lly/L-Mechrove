//! Host theme/language prefs.json. C# night/day + zh-CN/en; language change requests restart.

use std::path::Path;

use serde::{Deserialize, Serialize};

const FILE_NAME: &str = "prefs.json";

#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub struct FakeRestart {
    pub requested: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ThemeMode {
    Night,
    Day,
}

impl ThemeMode {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Night => "night",
            Self::Day => "day",
        }
    }

    fn parse(mode: &str) -> Result<Self, PrefsError> {
        match mode {
            "night" => Ok(Self::Night),
            "day" => Ok(Self::Day),
            other => Err(PrefsError::InvalidTheme(other.to_owned())),
        }
    }

    fn from_stored(saved: &str) -> Self {
        if saved.eq_ignore_ascii_case("day") {
            Self::Day
        } else {
            Self::Night
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UiLanguage {
    ZhCn,
    En,
}

impl UiLanguage {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::ZhCn => "zh-CN",
            Self::En => "en",
        }
    }

    fn parse(code: &str) -> Result<Self, PrefsError> {
        match code {
            "zh-CN" => Ok(Self::ZhCn),
            "en" => Ok(Self::En),
            other => Err(PrefsError::InvalidLanguage(other.to_owned())),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Prefs {
    pub theme_mode: ThemeMode,
    pub language: UiLanguage,
}

#[derive(Debug, thiserror::Error)]
pub enum PrefsError {
    #[error("invalid theme mode: {0}")]
    InvalidTheme(String),
    #[error("invalid language: {0}")]
    InvalidLanguage(String),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error(transparent)]
    Json(#[from] serde_json::Error),
}

#[derive(Debug, Serialize, Deserialize)]
struct PrefsFile {
    #[serde(rename = "themeMode", default = "default_theme_mode")]
    theme_mode: String,
    #[serde(default)]
    language: String,
}

fn default_theme_mode() -> String {
    ThemeMode::Night.as_str().to_owned()
}

fn starts_with_ignore_ascii(value: &str, prefix: &str) -> bool {
    let value = value.as_bytes();
    let prefix = prefix.as_bytes();
    value.len() >= prefix.len() && value[..prefix.len()].eq_ignore_ascii_case(prefix)
}

fn normalize_language(stored: Option<&str>, ui_culture: &str) -> UiLanguage {
    if let Some(stored) = stored.map(str::trim).filter(|value| !value.is_empty()) {
        if starts_with_ignore_ascii(stored, "zh") {
            return UiLanguage::ZhCn;
        }
        if starts_with_ignore_ascii(stored, "en") {
            return UiLanguage::En;
        }
    }
    if starts_with_ignore_ascii(ui_culture, "zh") {
        UiLanguage::ZhCn
    } else {
        UiLanguage::En
    }
}

fn os_ui_culture() -> String {
    ["LC_ALL", "LC_MESSAGES", "LANG"]
        .into_iter()
        .find_map(|key| std::env::var(key).ok().filter(|value| !value.is_empty()))
        .unwrap_or_default()
}

fn prefs_path(dir: &Path) -> std::path::PathBuf {
    dir.join(FILE_NAME)
}

pub fn load(dir: &Path, ui_culture: &str) -> Result<Prefs, PrefsError> {
    match std::fs::read_to_string(prefs_path(dir)) {
        Ok(text) => {
            let file: PrefsFile = serde_json::from_str(&text)?;
            let stored = Some(file.language.as_str()).filter(|value| !value.is_empty());
            Ok(Prefs {
                theme_mode: ThemeMode::from_stored(&file.theme_mode),
                language: normalize_language(stored, ui_culture),
            })
        }
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => Ok(Prefs {
            theme_mode: ThemeMode::Night,
            language: normalize_language(None, ui_culture),
        }),
        Err(err) => Err(err.into()),
    }
}

fn save(dir: &Path, prefs: &Prefs) -> Result<(), PrefsError> {
    std::fs::create_dir_all(dir)?;
    let file = PrefsFile {
        theme_mode: prefs.theme_mode.as_str().to_owned(),
        language: prefs.language.as_str().to_owned(),
    };
    std::fs::write(prefs_path(dir), serde_json::to_string_pretty(&file)?)?;
    Ok(())
}

pub fn set_theme_mode(dir: &Path, mode: &str) -> Result<(), PrefsError> {
    let theme_mode = ThemeMode::parse(mode)?;
    let mut prefs = load(dir, &os_ui_culture())?;
    prefs.theme_mode = theme_mode;
    save(dir, &prefs)
}

pub fn set_ui_language(dir: &Path, code: &str) -> Result<FakeRestart, PrefsError> {
    let language = UiLanguage::parse(code)?;
    let mut prefs = load(dir, &os_ui_culture())?;
    prefs.language = language;
    save(dir, &prefs)?;
    Ok(FakeRestart { requested: true })
}
