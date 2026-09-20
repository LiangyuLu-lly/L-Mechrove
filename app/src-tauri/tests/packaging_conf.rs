//! Given tauri.conf.json. When packaging keys are read. Then NSIS is per-machine and WebView2 embeds the bootstrapper.

const TAURI_CONF: &str = include_str!("../tauri.conf.json");

#[test]
fn nsis_install_mode_is_per_machine_when_packaging_conf_is_read() {
    assert!(
        TAURI_CONF.contains(r#""installMode": "perMachine""#),
        "bundle.windows.nsis.installMode must be the string perMachine"
    );
    assert!(
        !TAURI_CONF.contains(r#""installMode": true"#),
        "installMode must not be a boolean"
    );
}

#[test]
fn webview_install_mode_embeds_bootstrapper_when_packaging_conf_is_read() {
    assert!(
        TAURI_CONF.contains(r#""type": "embedBootstrapper""#),
        "bundle.windows.webviewInstallMode.type must be embedBootstrapper"
    );
}
