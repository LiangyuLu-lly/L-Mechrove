//! Given tauri.conf.json and Cargo.toml. When packaging keys are read. Then NSIS is per-machine, WebView2 embeds the bootstrapper, and Windows-only native crate features stay off the live device in tests.

const TAURI_CONF: &str = include_str!("../tauri.conf.json");
const CARGO_TOML: &str = include_str!("../Cargo.toml");

fn table_body<'a>(manifest: &'a str, header: &str) -> Option<&'a str> {
    let start = manifest.find(header)? + header.len();
    let rest = manifest[start..].trim_start_matches(['\r', '\n']);
    let end = rest.find("\n[").unwrap_or(rest.len());
    Some(rest[..end].trim())
}

#[test]
fn custom_mode_window_declares_custom_url_when_packaging_conf_is_read() {
    assert!(
        TAURI_CONF.contains(r#""label": "custom-mode""#),
        "app.windows must declare label custom-mode"
    );
    assert!(
        TAURI_CONF.contains(r#""url": "index.html?window=custom""#),
        "custom-mode window url must be index.html?window=custom"
    );
}

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

#[test]
fn windows_target_table_requests_real_ioctl_and_native_open_when_manifest_is_read() {
    let table = table_body(CARGO_TOML, "[target.'cfg(windows)'.dependencies]")
        .expect("host Cargo.toml must declare [target.'cfg(windows)'.dependencies]");
    assert!(
        table.contains("real-ioctl"),
        "Windows target table must request ec-acpi feature real-ioctl, got {table:?}"
    );
    assert!(
        table.contains("native-open"),
        "Windows target table must request hid-kb feature native-open, got {table:?}"
    );
}

#[test]
fn untargeted_dependencies_omit_native_features_when_manifest_is_read() {
    let table = table_body(CARGO_TOML, "[dependencies]")
        .expect("host Cargo.toml must declare [dependencies]");
    assert!(
        !table.contains("real-ioctl"),
        "untargeted [dependencies] must not enable real-ioctl"
    );
    assert!(
        !table.contains("native-open"),
        "untargeted [dependencies] must not enable native-open"
    );
}

#[test]
fn test_build_resolves_fake_ioctl_and_fake_hid_when_windows_native_features_are_requested() {
    let ioctl = ec_acpi::FakeIoctl::default();
    let hid = hid_kb::FakeHid::new();
    assert!(ioctl.calls().is_empty());
    assert!(hid.feature_reports().is_empty());
}
