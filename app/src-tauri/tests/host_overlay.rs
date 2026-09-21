//! Given HardwareOverlay.cs host state. When scale/mode/anchor/game/update. Then C# persist, no MQTT.

#[path = "../src/overlay_state.rs"]
mod overlay_state;

use app_lib::Backend;
use overlay_state::{
    anchor_from_center, clamp_scale, is_game_foreground, overlay_should_show, overlay_update,
    restore_position, OverlayMode, OverlayStore, Point, ScreenRect, Size,
};

#[test]
fn clamp_scale_pins_below_min_above_max_and_keeps_step_ten() {
    // Given: C# MinScalePercent=35, MaxScalePercent=300, ScaleStepPercent=10
    // When: clamp_scale on below-min, above-max, and an in-range step
    // Then: 34→35, 301→300, 120→120
    assert_eq!(clamp_scale(34), 35);
    assert_eq!(clamp_scale(301), 300);
    assert_eq!(clamp_scale(120), 120);
}

#[test]
fn click_cycle_light_default_full_complete_light_persists_overlay_mode() {
    // Given: overlay starts in Light (legacy overlay_light_mode = 1)
    let mut store = OverlayStore::memory();
    store.seed_legacy_light_mode(1);

    // When: four click-cycles
    // Then: Light → Default → Full → Complete → Light, each writes overlay_mode
    let order = [
        OverlayMode::Default,
        OverlayMode::Full,
        OverlayMode::Complete,
        OverlayMode::Light,
    ];
    for expected in order {
        let next = store.click_cycle();
        assert_eq!(next, expected);
        assert_eq!(store.get_i32("overlay_mode"), Some(expected as i32));
    }
}

#[test]
fn anchor_from_center_quadrant_bits_and_restore_round_trips_four_corners() {
    // Given: 1920×1080 screen, 320×96 overlay, 10px MarginFromEdge
    let screen = ScreenRect {
        x: 0,
        y: 0,
        width: 1920,
        height: 1080,
    };
    let size = Size {
        width: 320,
        height: 96,
    };
    const MARGIN: i32 = 10;
    let corners = [
        (
            Point {
                x: MARGIN,
                y: MARGIN,
            },
            0,
        ),
        (
            Point {
                x: screen.width - size.width - MARGIN,
                y: MARGIN,
            },
            1,
        ),
        (
            Point {
                x: MARGIN,
                y: screen.height - size.height - MARGIN,
            },
            2,
        ),
        (
            Point {
                x: screen.width - size.width - MARGIN,
                y: screen.height - size.height - MARGIN,
            },
            3,
        ),
    ];

    for (location, expected_anchor) in corners {
        let center = Point {
            x: location.x + size.width / 2,
            y: location.y + size.height / 2,
        };
        // When: quadrant bits from center (bit0 right, bit1 bottom)
        let anchor = anchor_from_center(center, screen);
        assert_eq!(anchor, expected_anchor, "center={center:?}");

        let is_right = (expected_anchor & 1) != 0;
        let is_bottom = (expected_anchor & 2) != 0;
        let offset_x = if is_right {
            screen.x + screen.width - location.x - size.width
        } else {
            location.x - screen.x
        };
        let offset_y = if is_bottom {
            screen.y + screen.height - location.y - size.height
        } else {
            location.y - screen.y
        };

        // Then: restore_position round-trips the 10px corner
        let restored = restore_position(anchor, offset_x, offset_y, screen, size);
        assert_eq!(restored, location, "anchor={anchor}");
    }
}

#[test]
fn is_game_foreground_rejects_explorer_and_accepts_allowlisted_exe() {
    // Given: a configured game allowlist
    let allowlist = ["eldenring.exe", "hl2.exe"];

    // When/Then: explorer is not a game; an allowlisted exe is
    assert!(!is_game_foreground("explorer.exe", &allowlist));
    assert!(is_game_foreground("eldenring.exe", &allowlist));
}

#[tokio::test]
async fn overlay_update_on_fake_publishes_no_mqtt_and_persists() {
    // Given: Fake GCU after handshake, empty overlay store on a unique dir
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    let before = backend.recorded_publishes().len();
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .expect("time")
        .as_nanos();
    let dir =
        std::env::temp_dir().join(format!("ccx_host_overlay_{}_{}", std::process::id(), nanos));
    std::fs::create_dir_all(&dir).expect("temp dir");
    let mut store = OverlayStore::load(&dir).expect("load empty");
    let prefs = serde_json::json!({
        "mode": "full",
        "scalePercent": 120,
        "anchor": 3,
        "offsetX": 10,
        "offsetY": 10,
        "screen": "",
        "gameOnly": true,
        "displayOff": true,
        "showTemp": true,
        "showFans": false,
        "showPower": true,
        "showUsage": true,
        "showRam": false,
        "showBattery": true,
        "names": true,
        "alpha": 200,
        "colors": {
            "cpu": "#3CDCFF",
            "gpu": "#00FF50",
            "temp": "#FF8800",
            "fans": "#88FF88",
            "power": "#FFCC00",
            "usage": "#66AAFF",
            "ram": "#CC66FF",
            "battery": "#FFFFFF"
        }
    });

    // When: overlay_update writes prefs
    overlay_update(&mut store, &prefs).expect("update");
    store.save(&dir).expect("save");

    // Then: Fake published nothing extra, and keys survive reload
    assert_eq!(
        backend.recorded_publishes().len(),
        before,
        "overlay_update must not publish MQTT: {:?}",
        backend.recorded_publishes()
    );
    let loaded = OverlayStore::load(&dir).expect("reload");
    assert_eq!(
        loaded.get_i32("overlay_mode"),
        Some(OverlayMode::Full as i32)
    );
    assert_eq!(loaded.get_i32("overlay_scale_percent"), Some(120));
    assert_eq!(loaded.get_i32("overlay_anchor"), Some(3));
    assert_eq!(loaded.get_i32("overlay_offset_x"), Some(10));
    assert_eq!(loaded.get_i32("overlay_offset_y"), Some(10));
    assert_eq!(loaded.get_str("overlay_screen"), Some(""));
    assert_eq!(loaded.get_i32("overlay_game_only"), Some(1));
    assert_eq!(loaded.get_i32("overlay_display_off"), Some(1));
    assert_eq!(loaded.get_i32("overlay_show_temp"), Some(1));
    assert_eq!(loaded.get_i32("overlay_show_fans"), Some(0));
    assert_eq!(loaded.get_i32("overlay_show_power"), Some(1));
    assert_eq!(loaded.get_i32("overlay_show_usage"), Some(1));
    assert_eq!(loaded.get_i32("overlay_show_ram"), Some(0));
    assert_eq!(loaded.get_i32("overlay_show_battery"), Some(1));
    assert_eq!(loaded.get_i32("overlay_names"), Some(1));
    assert_eq!(loaded.get_i32("overlay_alpha"), Some(200));
    assert_eq!(loaded.get_str("overlay_color_cpu"), Some("#3CDCFF"));
    assert_eq!(loaded.get_str("overlay_color_gpu"), Some("#00FF50"));
    assert_eq!(loaded.get_str("overlay_color_temp"), Some("#FF8800"));
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn overlay_should_show_hides_when_game_only_and_not_game() {
    // Given: 仅游戏显示 on. When: injected is_game=false. Then: hide.
    // Hardware ceiling: this is a boolean predicate, not z-order.
    assert!(!overlay_should_show(true, false, false, false));
    assert!(overlay_should_show(true, true, false, false));
}

#[test]
fn overlay_should_show_hides_when_display_off_pref_and_display_off() {
    // Given: 熄屏挂起 on. When: injected display_off=true. Then: hide.
    // Hardware ceiling: this is a boolean predicate, not real screen blanking.
    assert!(!overlay_should_show(false, true, true, true));
    assert!(overlay_should_show(false, true, true, false));
}
