use std::collections::HashMap;

use serde::Deserialize;
use serde_json::Value;

use crate::Error;

/// Service-written ItemSupport / GpuConfig map. Keys are matched case-insensitively.
#[derive(Debug, Clone, Default)]
pub struct ItemSupport {
    values: HashMap<String, Value>,
}

impl ItemSupport {
    /// Build from a JSON object, lowercasing keys for OrdinalIgnoreCase lookup.
    pub fn from_map(map: HashMap<String, Value>) -> Self {
        let mut values = HashMap::with_capacity(map.len());
        for (key, value) in map {
            values.insert(normalize_key(&key), value);
        }
        Self { values }
    }

    /// Parse a JSON object of service values.
    ///
    /// # Errors
    /// Returns [`Error::Json`] when the text is not JSON, [`Error::RootNotObject`] when the root is not an object.
    pub fn parse_json(text: &str) -> Result<Self, Error> {
        let value: Value = serde_json::from_str(text)?;
        Self::try_from_value(value)
    }

    /// Parse an already-decoded JSON value.
    ///
    /// # Errors
    /// Returns [`Error::RootNotObject`] when `value` is not an object.
    pub fn try_from_value(value: Value) -> Result<Self, Error> {
        match value {
            Value::Object(map) => Ok(Self::from_map(HashMap::from_iter(map))),
            _ => Err(Error::RootNotObject),
        }
    }

    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }

    /// JSON object dump for diagnostics. Keys are normalized (lowercase).
    pub fn dump_json(&self) -> serde_json::Map<String, Value> {
        self.values
            .iter()
            .map(|(k, v)| (k.clone(), v.clone()))
            .collect()
    }

    pub fn is_present(&self, key: &str) -> bool {
        self.values.contains_key(&normalize_key(key))
    }

    pub fn is_truthy(&self, key: &str) -> bool {
        self.values.get(&normalize_key(key)).is_some_and(truthy)
    }

    pub fn any_truthy(&self, keys: &[&str]) -> bool {
        keys.iter().any(|key| self.is_truthy(key))
    }

    /// First non-null value among `keys`, converted like C# `Number()`. Missing → 0.
    pub fn first_number(&self, keys: &[&str]) -> i64 {
        for key in keys {
            match self.values.get(&normalize_key(key)) {
                Some(Value::Null) | None => continue,
                Some(value) => return number_of(value),
            }
        }
        0
    }
}

impl<'de> Deserialize<'de> for ItemSupport {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let map = HashMap::<String, Value>::deserialize(deserializer)?;
        Ok(Self::from_map(map))
    }
}

fn normalize_key(key: &str) -> String {
    key.to_ascii_lowercase()
}

fn truthy(value: &Value) -> bool {
    match value {
        Value::Null => false,
        Value::Bool(flag) => *flag,
        Value::Number(number) => number_truthy(number),
        Value::String(text) => string_truthy(text),
        Value::Array(_) | Value::Object(_) => false,
    }
}

fn number_truthy(number: &serde_json::Number) -> bool {
    if let Some(value) = number.as_i64() {
        return value > 0;
    }
    if let Some(value) = number.as_u64() {
        return value > 0;
    }
    number.as_f64().is_some_and(|value| value > 0.0)
}

fn string_truthy(text: &str) -> bool {
    let trimmed = text.trim();
    if let Ok(number) = trimmed.parse::<i64>() {
        return number > 0;
    }
    matches!(
        trimmed.to_ascii_lowercase().as_str(),
        "true" | "yes" | "on" | "supported" | "enable" | "enabled"
    )
}

fn number_of(value: &Value) -> i64 {
    match value {
        Value::Bool(true) => 1,
        Value::Bool(false) => 0,
        Value::Number(number) => json_number_i64(number),
        Value::String(text) => string_number(text),
        Value::Null | Value::Array(_) | Value::Object(_) => 0,
    }
}

fn json_number_i64(number: &serde_json::Number) -> i64 {
    if let Some(value) = number.as_i64() {
        return value;
    }
    if let Some(value) = number.as_u64() {
        return i64::try_from(value).unwrap_or(0);
    }
    0
}

fn string_number(text: &str) -> i64 {
    let trimmed = text.trim();
    if let Ok(number) = trimmed.parse::<i64>() {
        return number;
    }
    if string_truthy(trimmed) {
        1
    } else {
        0
    }
}
