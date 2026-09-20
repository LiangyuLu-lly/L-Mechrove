use crate::item_support::ItemSupport;
use crate::matrix::{FeatureBit, FeatureMatrix};

const LIGHTBAR_ALIASES: &[&str] = &[
    "LightbarSupport",
    "LightBarSupport",
    "HidLightbarSupport",
    "IsLightbarSupport",
    "Lightbar_Support",
];

const RGB_LIGHTBAR_ALIASES: &[&str] = &[
    "RGBLightbarSupport",
    "RgbLightbarSupport",
    "IsRGBLightbarSupport",
];

const LOGO_ALIASES: &[&str] = &[
    "LogoLightSupport",
    "LogoLightbarSupport",
    "LightbarLogoSupport",
    "HidLightbarLogoSupport",
    "RGBLogoLightSupport",
    "LogoSupport",
    "MBLogoSupport",
    "MBlogoSupport",
    "NewlogoSupport",
    "NewLogoSupport",
];

const KEYBOARD_ALIASES: &[&str] = &[
    "KeyboardSupport",
    "RGBKeyboardSupport",
    "SingleColorKeyboardSupport",
    "KeyboardLightSupport",
    "KeyboardBacklightSupport",
];

const KEYBOARD_TYPE_ALIASES: &[&str] = &["KeyboardType", "RGBKeyboardType", "KeyboardLightType"];

/// UI lighting rows. Derived only from ItemSupport; MQTT Seen is not an input.
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct LightingVisibility {
    pub keyboard: bool,
    pub lightbar: bool,
    pub logo: bool,
    #[serde(rename = "keyboardType")]
    pub keyboard_type: i64,
}

impl LightingVisibility {
    pub fn from_item_support(map: &ItemSupport) -> Self {
        let matrix = FeatureMatrix::from_values(map);
        let keyboard_type = map.first_number(KEYBOARD_TYPE_ALIASES);
        Self {
            keyboard: matrix.is_supported(FeatureBit::Keyboard)
                || map.any_truthy(KEYBOARD_ALIASES)
                || keyboard_type > 0,
            lightbar: map.any_truthy(LIGHTBAR_ALIASES) || map.any_truthy(RGB_LIGHTBAR_ALIASES),
            logo: map.any_truthy(LOGO_ALIASES),
            keyboard_type,
        }
    }
}
