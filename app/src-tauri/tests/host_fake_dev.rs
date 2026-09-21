//! Given the Fake GCU env. When from_env builds the backend. Then the empty profile
//! stays the fail-closed default and the full-dev fixture is strictly opt-in.

use app_lib::Backend;

const WINDOWS_SWITCHES: [&str; 4] = ["startup", "taskbarautohide", "transparency", "darktheme"];

#[test]
fn fake_gcu_defaults_to_empty_and_full_dev_is_opt_in() {
    std::env::set_var("LMECHREVO_FAKE_GCU", "1");

    // Given: Fake GCU with no explicit profile
    std::env::remove_var("LMECHREVO_FAKE_PROFILE");
    let empty = Backend::from_env().snapshot();

    // Then: the fail-closed defaults survive, so N8/S6 keep their meaning
    assert!(!empty.write_allowed, "empty profile must stay unserved");
    // Keyboard is vendor-constant-on (C# FeatureMatrix), so only the optional
    // channels must hide on an empty profile.
    assert!(
        !empty.lighting.lightbar,
        "empty profile must hide the lightbar row"
    );
    assert!(!empty.lighting.logo, "empty profile must hide the logo row");
    assert!(
        !empty.liquid_cooling,
        "empty profile must hide liquid cooling"
    );
    assert!(!empty.silent_turbo, "empty profile must hide silentTurbo");
    assert!(
        empty.gpu_actions.is_empty(),
        "empty profile must offer no GPU route, got {:?}",
        empty.gpu_actions
    );

    // When: the full-dev fixture is requested explicitly
    std::env::set_var("LMECHREVO_FAKE_PROFILE", "full");
    let full = Backend::from_env().snapshot();

    // Then: a fully-served console is offered, matching C# on a served machine
    assert!(full.write_allowed, "full-dev ItemSupport must be served");
    assert!(
        full.lighting.keyboard,
        "full-dev KeyboardSupport must show the keyboard row"
    );
    assert!(
        !full.gpu_actions.is_empty(),
        "full-dev GPU route must offer actions, got {full:?}"
    );
    assert!(
        full.liquid_cooling,
        "full-dev LiquidCoolingSupport must enable LC"
    );
    assert!(
        full.silent_turbo,
        "full-dev IsTurboSubModeSupport must offer silentTurbo"
    );
    assert!(
        full.offered_switches.iter().any(|key| key == "touchpad"),
        "触摸板 (touchpad) must be offered: {:?}",
        full.offered_switches
    );
    for key in WINDOWS_SWITCHES {
        assert!(
            full.offered_switches.iter().any(|offered| offered == key),
            "Windows switch {key} must be offered: {:?}",
            full.offered_switches
        );
    }

    std::env::remove_var("LMECHREVO_FAKE_PROFILE");
    std::env::remove_var("LMECHREVO_FAKE_GCU");
}
