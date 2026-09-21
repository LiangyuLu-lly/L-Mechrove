//! Display write path. Hz/calibration/OD/LD on Setting/Control. Brightness is WMI, not MQTT.
//! allow: SIZE_OK — Real brightness STA + live WMI COM cannot split from these write arms.

use std::sync::{mpsc, Arc, Mutex, OnceLock};
use std::time::Duration;

use gcu_mqtt::client::MqttTransport;
use gcu_mqtt::topics;
use serde::Serialize;

use crate::hw_backend::Backend;
use crate::hw_error::HostError;
use crate::hw_wmi::FakeWmi;

pub use crate::hw_ccd::{
    hdr_acm_from_active_color_mode, hdr_acm_from_legacy_color_info, is_advanced_color_enabled,
    AdvancedColorProbe, AdvancedColorQuery, InjectedAdvancedColorGuard,
};

const WMI_BRIGHTNESS_TIMEOUT: u32 = 1;

#[derive(Serialize)]
struct ActionPayload<'a> {
    #[serde(rename = "Action")]
    action: &'a str,
}

#[derive(Serialize)]
struct HzPayload {
    #[serde(rename = "Action")]
    action: &'static str,
    #[serde(rename = "Hz")]
    hz: String,
}

/// Latest-wins brightness coalescer. Production debounce is 120 ms; tests inject `Duration::ZERO`.
pub struct BrightnessQueue {
    debounce: Duration,
    latest: Option<u8>,
}

impl BrightnessQueue {
    pub const fn new(debounce: Duration) -> Self {
        Self {
            debounce,
            latest: None,
        }
    }

    pub fn submit(&mut self, brightness: u8) {
        self.latest = Some(brightness);
    }

    fn set_debounce(&mut self, debounce: Duration) {
        self.debounce = debounce;
    }
}

/// WMI brightness write. Tests inject a recorder; production uses live COM on the STA thread.
pub trait BrightnessSink: Send + Sync {
    fn set_brightness(&self, timeout: u32, brightness: u8);
}

/// Clears a test-only brightness sink so CI never opens live WMI.
pub struct InjectedBrightnessGuard;

impl Drop for InjectedBrightnessGuard {
    fn drop(&mut self) {
        *lock_mutex(&INJECTED_WMI) = None;
        real_brightness_queue().set_debounce(Duration::from_millis(120));
    }
}

struct StaJob {
    timeout: u32,
    brightness: u8,
    sink: Option<Arc<dyn BrightnessSink>>,
    reply: tokio::sync::oneshot::Sender<Result<(), HostError>>,
}

struct StaHandle {
    tx: mpsc::Sender<StaJob>,
}

static INJECTED_WMI: Mutex<Option<Arc<dyn BrightnessSink>>> = Mutex::new(None);
static REAL_BRIGHTNESS_QUEUE: Mutex<BrightnessQueue> =
    Mutex::new(BrightnessQueue::new(Duration::from_millis(120)));
static STA: OnceLock<Result<StaHandle, ()>> = OnceLock::new();

fn lock_mutex<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|err| err.into_inner())
}

fn unavailable<T>() -> Result<T, HostError> {
    Err(HostError::RealUnavailable)
}

fn real_brightness_queue() -> std::sync::MutexGuard<'static, BrightnessQueue> {
    lock_mutex(&REAL_BRIGHTNESS_QUEUE)
}

fn snapshot_injected_wmi() -> Option<Arc<dyn BrightnessSink>> {
    lock_mutex(&INJECTED_WMI).clone()
}

fn sta_handle() -> Result<&'static StaHandle, HostError> {
    match STA.get_or_init(|| {
        let (tx, rx) = mpsc::channel();
        match std::thread::Builder::new()
            .name("wmi-com-sta".into())
            .spawn(move || sta_loop(rx))
        {
            Ok(_) => Ok(StaHandle { tx }),
            Err(_) => Err(()),
        }
    }) {
        Ok(handle) => Ok(handle),
        Err(()) => unavailable(),
    }
}

fn sta_loop(rx: mpsc::Receiver<StaJob>) {
    #[cfg(all(windows, not(miri)))]
    init_com_sta();
    while let Ok(job) = rx.recv() {
        let result = match job.sink {
            Some(sink) => {
                sink.set_brightness(job.timeout, job.brightness);
                Ok(())
            }
            None => live_wmi_set_brightness(job.timeout, job.brightness),
        };
        let _ = job.reply.send(result);
    }
}

#[cfg(all(windows, not(miri)))]
fn init_com_sta() {
    live_wmi::init_sta();
}

fn live_wmi_set_brightness(timeout: u32, brightness: u8) -> Result<(), HostError> {
    #[cfg(all(windows, not(miri)))]
    {
        return live_wmi::set_brightness(timeout, brightness);
    }
    #[cfg(not(all(windows, not(miri))))]
    {
        let _ = (timeout, brightness);
        unavailable()
    }
}

async fn commit_brightness_on_sta(timeout: u32, brightness: u8) -> Result<(), HostError> {
    let handle = sta_handle()?;
    let (reply, rx) = tokio::sync::oneshot::channel();
    handle
        .tx
        .send(StaJob {
            timeout,
            brightness,
            sink: snapshot_injected_wmi(),
            reply,
        })
        .or_else(|_| unavailable())?;
    rx.await.or_else(|_| unavailable())?
}

fn running_as_rust_test_binary() -> bool {
    let Ok(exe) = std::env::current_exe() else {
        return false;
    };
    let path = exe.to_string_lossy();
    path.contains("deps") && path.contains('-')
}

async fn apply_real_brightness(percent: u8) -> Result<(), HostError> {
    if snapshot_injected_wmi().is_none() && running_as_rust_test_binary() {
        return unavailable();
    }
    let debounce;
    {
        let mut queue = real_brightness_queue();
        queue.submit(percent);
        debounce = queue.debounce;
    }
    if !debounce.is_zero() {
        tokio::time::sleep(debounce).await;
    }
    let Some(brightness) = real_brightness_queue().latest.take() else {
        return Ok(());
    };
    commit_brightness_on_sta(WMI_BRIGHTNESS_TIMEOUT, brightness).await
}

#[derive(Serialize)]
struct DcHzPayload {
    #[serde(rename = "Action")]
    action: &'static str,
    #[serde(rename = "Enable")]
    enable: bool,
}

#[derive(Serialize)]
struct CalibrationOffPayload<'a> {
    #[serde(rename = "Action")]
    action: &'static str,
    #[serde(rename = "FileName")]
    file_name: &'a str,
}

/// C# `MechrevoService.ColorCalibrationFileName` (MechrevoService.cs:986-992).
pub const fn color_calibration_file_name(mode: i32) -> &'static str {
    match mode {
        2 => "sRGB",
        3 => "P3",
        4 => "AdobeRGB",
        _ => "Default",
    }
}

fn color_calibration_mode_from_action(action: &str) -> Option<i32> {
    match action {
        "COLOR_CALIBRATION_ON_DEFAULT" => Some(1),
        "COLOR_CALIBRATION_ON_SRGB" => Some(2),
        "COLOR_CALIBRATION_ON_P3" => Some(3),
        "COLOR_CALIBRATION_ON_ADOBERGB" => Some(4),
        _ => None,
    }
}

pub async fn apply_display_hz<T: MqttTransport>(
    transport: &mut T,
    hz: u32,
) -> Result<(), HostError> {
    let payload = HzPayload {
        action: "GPU_HZSETTING",
        hz: hz.to_string(),
    };
    let bytes = serde_json::to_vec(&payload)?;
    transport.publish(topics::SETTING_CONTROL, &bytes).await?;
    Ok(())
}

pub async fn apply_auto_refresh_rate<T: MqttTransport>(
    transport: &mut T,
    dc_hz_seen: bool,
    on: bool,
) -> Result<(), HostError> {
    if !dc_hz_seen {
        return Err(HostError::DisplayDenied("GPU_DC_HZ".to_owned()));
    }
    let payload = DcHzPayload {
        action: "GPU_DC_HZ",
        enable: on,
    };
    let bytes = serde_json::to_vec(&payload)?;
    transport.publish(topics::SETTING_CONTROL, &bytes).await?;
    Ok(())
}

pub async fn apply_brightness<T: MqttTransport>(
    _transport: &mut T,
    wmi: &mut FakeWmi,
    queue: &mut BrightnessQueue,
) -> Result<(), HostError> {
    let Some(brightness) = queue.latest.take() else {
        return Ok(());
    };
    if !queue.debounce.is_zero() {
        tokio::time::sleep(queue.debounce).await;
    }
    wmi.set_brightness(WMI_BRIGHTNESS_TIMEOUT, brightness);
    Ok(())
}

pub async fn apply_calibration<T: MqttTransport>(
    transport: &mut T,
    action: &str,
    hdr_on: bool,
    file_name: &str,
) -> Result<(), HostError> {
    if hdr_on {
        return Err(HostError::DisplayDenied(action.to_owned()));
    }
    if action == "COLOR_CALIBRATION_OFF" {
        let payload = CalibrationOffPayload {
            action: "COLOR_CALIBRATION_OFF",
            file_name,
        };
        let bytes = serde_json::to_vec(&payload)?;
        transport.publish(topics::SETTING_CONTROL, &bytes).await?;
        return Ok(());
    }
    publish_action(transport, action).await
}

pub async fn apply_direct_connect_restart<T: MqttTransport>(
    transport: &mut T,
    delay: Duration,
) -> Result<(), HostError> {
    // Vendor waits 800 ms before DGPU_DIRECT_CONNECT_RESTART (MechrevoService.cs:1113, CCUWinUI:86511-86515).
    // Timing is not a unit-test claim; the delay is a no-op under cfg!(test).
    let delay = if cfg!(test) { Duration::ZERO } else { delay };
    if !delay.is_zero() {
        tokio::time::sleep(delay).await;
    }
    publish_action(transport, "DGPU_DIRECT_CONNECT_RESTART").await
}

pub async fn apply_overdrive<T: MqttTransport>(
    transport: &mut T,
    on: bool,
) -> Result<(), HostError> {
    let action = if on {
        "LCDOverdrive_ON"
    } else {
        "LCDOverdrive_OFF"
    };
    publish_action(transport, action).await
}

pub async fn apply_local_dimming<T: MqttTransport>(
    transport: &mut T,
    on: bool,
) -> Result<(), HostError> {
    let action = if on {
        "LOCALDIMMING_ON"
    } else {
        "LOCALDIMMING_OFF"
    };
    publish_action(transport, action).await
}

async fn publish_action<T: MqttTransport>(
    transport: &mut T,
    action: &str,
) -> Result<(), HostError> {
    let bytes = serde_json::to_vec(&ActionPayload { action })?;
    transport.publish(topics::SETTING_CONTROL, &bytes).await?;
    Ok(())
}

impl Backend {
    pub fn with_brightness_debounce(mut self, debounce: Duration) -> Self {
        if let Self::Fake { state } = &mut self {
            state.brightness_queue = BrightnessQueue::new(debounce);
        }
        self
    }

    pub fn with_dc_hz_seen(mut self, seen: bool) -> Self {
        if let Self::Fake { state } = &mut self {
            state.dc_hz_seen = seen;
        }
        self
    }

    pub async fn set_auto_refresh_rate(&mut self, on: bool) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                let seen = state.dc_hz_seen;
                apply_auto_refresh_rate(&mut state.broker, seen, on).await
            }
            Self::Real { state } => {
                apply_auto_refresh_rate(&mut state.client, state.dc_hz_seen, on).await
            }
        }
    }

    pub async fn set_display_hz(&mut self, hz: &str) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                let parsed = hz
                    .parse::<u32>()
                    .map_err(|_| HostError::DisplayDenied(hz.to_owned()))?;
                apply_display_hz(&mut state.broker, parsed).await
            }
            Self::Real { state } => {
                let parsed = hz
                    .parse::<u32>()
                    .map_err(|_| HostError::DisplayDenied(hz.to_owned()))?;
                apply_display_hz(&mut state.client, parsed).await
            }
        }
    }

    /// Install a brightness sink for the Real path. Never opens live WMI.
    pub fn inject_brightness_sink(sink: Arc<dyn BrightnessSink>) -> InjectedBrightnessGuard {
        *lock_mutex(&INJECTED_WMI) = Some(sink);
        real_brightness_queue().set_debounce(Duration::ZERO);
        InjectedBrightnessGuard
    }

    /// Install a CCD advanced-colour probe for the Real path. Never opens live display config.
    pub fn inject_advanced_color_probe(
        probe: Arc<dyn AdvancedColorProbe>,
    ) -> InjectedAdvancedColorGuard {
        crate::hw_ccd::inject(probe)
    }

    pub async fn set_brightness(&mut self, percent: u8) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                state.brightness_queue.submit(percent);
                apply_brightness(
                    &mut state.broker,
                    &mut state.wmi,
                    &mut state.brightness_queue,
                )
                .await
            }
            Self::Real { .. } => apply_real_brightness(percent).await,
        }
    }

    pub async fn set_calibration(&mut self, mode: &str) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                let file_name = color_calibration_file_name(state.color_calibration_mode);
                apply_calibration(&mut state.broker, mode, state.hdr_on, file_name).await?;
                if let Some(parsed) = color_calibration_mode_from_action(mode) {
                    state.color_calibration_mode = parsed;
                }
                Ok(())
            }
            Self::Real { state } => {
                // C# `_readHdrEnabled = IsAdvancedColorEnabled` (MechrevoService.cs:118, 972)
                // via ScreenCCD.GetHDRStatus (ScreenCCD.cs:11). Not MQTT.
                let hdr_on = match crate::hw_ccd::query_advanced_color() {
                    AdvancedColorQuery::Enabled => true,
                    AdvancedColorQuery::Disabled => false,
                    AdvancedColorQuery::Unavailable => {
                        return Err(HostError::DisplayDenied(format!(
                            "{mode}: advanced-colour query unavailable"
                        )));
                    }
                };
                let file_name = color_calibration_file_name(state.color_calibration_mode);
                apply_calibration(&mut state.client, mode, hdr_on, file_name).await?;
                if let Some(parsed) = color_calibration_mode_from_action(mode) {
                    state.color_calibration_mode = parsed;
                }
                Ok(())
            }
        }
    }

    pub async fn set_overdrive(&mut self, on: bool) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                apply_overdrive(&mut state.broker, on).await
            }
            Self::Real { state } => apply_overdrive(&mut state.client, on).await,
        }
    }

    pub async fn set_local_dimming(&mut self, on: bool) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                apply_local_dimming(&mut state.broker, on).await
            }
            Self::Real { state } => apply_local_dimming(&mut state.client, on).await,
        }
    }
}

#[cfg(all(windows, not(miri)))]
mod live_wmi {
    use std::ffi::c_void;
    use std::ptr;

    use crate::hw_error::HostError;

    const COINIT_APARTMENTTHREADED: u32 = 0x2;
    const CLSCTX_INPROC_SERVER: u32 = 0x1;
    const VT_UI1: u16 = 17;
    const VT_UI4: u16 = 19;
    const VT_BSTR: u16 = 8;
    const WBEM_FLAG_FORWARD_ONLY: i32 = 0x20;
    const WBEM_FLAG_RETURN_IMMEDIATELY: i32 = 0x10;
    const WBEM_INFINITE: i32 = -1;

    #[repr(C)]
    struct Guid {
        data1: u32,
        data2: u16,
        data3: u16,
        data4: [u8; 8],
    }

    const CLSID_WBEM_LOCATOR: Guid = Guid {
        data1: 0x4590_f811,
        data2: 0x1d3a,
        data3: 0x11d0,
        data4: [0x89, 0x1f, 0x00, 0xaa, 0x00, 0x4b, 0x2e, 0x24],
    };
    const IID_IWBEM_LOCATOR: Guid = Guid {
        data1: 0xdc12_a687,
        data2: 0x737f,
        data3: 0x11cf,
        data4: [0x88, 0x4d, 0x00, 0xaa, 0x00, 0x4b, 0x2e, 0x24],
    };

    #[repr(C)]
    struct Variant {
        vt: u16,
        _r1: u16,
        _r2: u16,
        _r3: u16,
        data: u64,
        _pad: u64,
    }

    impl Variant {
        const fn empty() -> Self {
            Self {
                vt: 0,
                _r1: 0,
                _r2: 0,
                _r3: 0,
                data: 0,
                _pad: 0,
            }
        }
    }

    struct BStr(*mut u16);
    struct ComPtr(*mut c_void);

    impl Drop for BStr {
        fn drop(&mut self) {
            if !self.0.is_null() {
                // SAFETY: [Category 8 — FFI boundary]
                // `self.0` is a SysAllocStringLen BSTR unique to this value.
                unsafe { SysFreeString(self.0) }
            }
        }
    }

    impl Drop for ComPtr {
        fn drop(&mut self) {
            if !self.0.is_null() {
                // SAFETY: [Category 8 — FFI boundary]
                // COM object at IUnknown vtable slot 2 (Release). Unique owner.
                unsafe { release(self.0) }
            }
        }
    }

    #[link(name = "ole32")]
    extern "system" {
        fn CoInitializeEx(reserved: *mut c_void, coinit: u32) -> i32;
        fn CoCreateInstance(
            clsid: *const Guid,
            outer: *mut c_void,
            ctx: u32,
            iid: *const Guid,
            ppv: *mut *mut c_void,
        ) -> i32;
    }

    #[link(name = "oleaut32")]
    extern "system" {
        fn SysAllocStringLen(src: *const u16, len: u32) -> *mut u16;
        fn SysFreeString(bstr: *mut u16);
        fn VariantClear(variant: *mut Variant) -> i32;
    }

    pub fn init_sta() {
        // SAFETY: [Category 8 — FFI boundary]
        // Dedicated OS thread; first COM call; pvReserved is null.
        // COINIT_APARTMENTTHREADED matches C# STA.
        let _hr = unsafe { CoInitializeEx(ptr::null_mut(), COINIT_APARTMENTTHREADED) };
    }

    pub fn set_brightness(timeout: u32, brightness: u8) -> Result<(), HostError> {
        let locator = create_locator()?;
        let services = connect_server(locator.0)?;
        let class_obj = get_object(services.0, "WmiMonitorBrightnessMethods")?;
        let in_sig = get_method(class_obj.0, "WmiSetBrightness")?;
        let in_params = spawn_instance(in_sig.0)?;
        put_ui4(in_params.0, "Timeout", timeout)?;
        put_ui1(in_params.0, "Brightness", brightness)?;
        let enumerator = create_instance_enum(services.0, "WmiMonitorBrightnessMethods")?;
        let mut wrote = false;
        while let Some(instance) = next_object(enumerator.0)? {
            let path = object_path(instance.0)?;
            exec_method(services.0, path.0, "WmiSetBrightness", in_params.0)?;
            wrote = true;
        }
        if wrote {
            Ok(())
        } else {
            super::unavailable()
        }
    }

    fn bstr(text: &str) -> Result<BStr, HostError> {
        let units: Vec<u16> = text.encode_utf16().collect();
        let len = u32::try_from(units.len()).or_else(|_| super::unavailable())?;
        // SAFETY: [Category 8 — FFI boundary]
        // `units` is a valid UTF-16 buffer of `len` code units; OLE copies it.
        let ptr = unsafe { SysAllocStringLen(units.as_ptr(), len) };
        if ptr.is_null() {
            return super::unavailable();
        }
        Ok(BStr(ptr))
    }

    fn wide(text: &str) -> Vec<u16> {
        text.encode_utf16().chain(Some(0)).collect()
    }

    fn create_locator() -> Result<ComPtr, HostError> {
        let mut locator = ptr::null_mut();
        // SAFETY: [Category 8 — FFI boundary]
        // CLSID/IID are static GUIDs. `locator` outlives the call. outer is null
        // (no aggregation). CLSCTX_INPROC_SERVER is the WbemLocator in-proc server.
        let hr = unsafe {
            CoCreateInstance(
                &CLSID_WBEM_LOCATOR,
                ptr::null_mut(),
                CLSCTX_INPROC_SERVER,
                &IID_IWBEM_LOCATOR,
                &mut locator,
            )
        };
        com_ok(hr, locator)
    }

    fn connect_server(locator: *mut c_void) -> Result<ComPtr, HostError> {
        let resource = bstr("ROOT\\WMI")?;
        let mut services = ptr::null_mut();
        // SAFETY: [Category 8 — FFI boundary]
        // IWbemLocator::ConnectServer is vtable slot 3. `locator` is a live
        // WbemLocator. BSTR outlives the call. Out pointer is a stack local.
        let hr = unsafe {
            let connect: unsafe extern "system" fn(
                *mut c_void,
                *mut u16,
                *mut u16,
                *mut u16,
                *mut u16,
                i32,
                *mut u16,
                *mut c_void,
                *mut *mut c_void,
            ) -> i32 = vcall(locator, 3);
            connect(
                locator,
                resource.0,
                ptr::null_mut(),
                ptr::null_mut(),
                ptr::null_mut(),
                0,
                ptr::null_mut(),
                ptr::null_mut(),
                &mut services,
            )
        };
        com_ok(hr, services)
    }

    fn get_object(services: *mut c_void, path: &str) -> Result<ComPtr, HostError> {
        let path = bstr(path)?;
        let mut object = ptr::null_mut();
        // SAFETY: [Category 8 — FFI boundary]
        // IWbemServices::GetObject is vtable slot 6. `services` is live.
        let hr = unsafe {
            let get_object: unsafe extern "system" fn(
                *mut c_void,
                *mut u16,
                i32,
                *mut c_void,
                *mut *mut c_void,
                *mut *mut c_void,
            ) -> i32 = vcall(services, 6);
            get_object(
                services,
                path.0,
                0,
                ptr::null_mut(),
                &mut object,
                ptr::null_mut(),
            )
        };
        com_ok(hr, object)
    }

    fn get_method(class_obj: *mut c_void, name: &str) -> Result<ComPtr, HostError> {
        let name = wide(name);
        let mut in_sig = ptr::null_mut();
        // SAFETY: [Category 8 — FFI boundary]
        // IWbemClassObject::GetMethod is vtable slot 19. `name` is NUL-terminated.
        let hr = unsafe {
            let get_method: unsafe extern "system" fn(
                *mut c_void,
                *const u16,
                i32,
                *mut *mut c_void,
                *mut *mut c_void,
            ) -> i32 = vcall(class_obj, 19);
            get_method(class_obj, name.as_ptr(), 0, &mut in_sig, ptr::null_mut())
        };
        com_ok(hr, in_sig)
    }

    fn spawn_instance(in_sig: *mut c_void) -> Result<ComPtr, HostError> {
        let mut spawned = ptr::null_mut();
        // SAFETY: [Category 8 — FFI boundary]
        // IWbemClassObject::SpawnInstance is vtable slot 15.
        let hr = unsafe {
            let spawn: unsafe extern "system" fn(*mut c_void, i32, *mut *mut c_void) -> i32 =
                vcall(in_sig, 15);
            spawn(in_sig, 0, &mut spawned)
        };
        com_ok(hr, spawned)
    }

    fn put_ui4(object: *mut c_void, name: &str, value: u32) -> Result<(), HostError> {
        put_variant(object, name, VT_UI4, u64::from(value))
    }

    fn put_ui1(object: *mut c_void, name: &str, value: u8) -> Result<(), HostError> {
        put_variant(object, name, VT_UI1, u64::from(value))
    }

    fn put_variant(object: *mut c_void, name: &str, vt: u16, value: u64) -> Result<(), HostError> {
        let name = wide(name);
        let mut variant = Variant::empty();
        variant.vt = vt;
        variant.data = value;
        // SAFETY: [Category 8 — FFI boundary]
        // IWbemClassObject::Put is vtable slot 5. VARIANT is 24-byte x64 layout.
        let hr = unsafe {
            let put: unsafe extern "system" fn(
                *mut c_void,
                *const u16,
                i32,
                *mut Variant,
                i32,
            ) -> i32 = vcall(object, 5);
            put(object, name.as_ptr(), 0, &mut variant, 0)
        };
        if hr < 0 {
            return super::unavailable();
        }
        Ok(())
    }

    fn create_instance_enum(services: *mut c_void, class: &str) -> Result<ComPtr, HostError> {
        let class = bstr(class)?;
        let mut enumerator = ptr::null_mut();
        // SAFETY: [Category 8 — FFI boundary]
        // IWbemServices::CreateInstanceEnum is vtable slot 18.
        let hr = unsafe {
            let create_enum: unsafe extern "system" fn(
                *mut c_void,
                *mut u16,
                i32,
                *mut c_void,
                *mut *mut c_void,
            ) -> i32 = vcall(services, 18);
            create_enum(
                services,
                class.0,
                WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY,
                ptr::null_mut(),
                &mut enumerator,
            )
        };
        com_ok(hr, enumerator)
    }

    fn next_object(enumerator: *mut c_void) -> Result<Option<ComPtr>, HostError> {
        let mut object = ptr::null_mut();
        let mut returned = 0_u32;
        // SAFETY: [Category 8 — FFI boundary]
        // IEnumWbemClassObject::Next is vtable slot 4.
        let hr = unsafe {
            let next: unsafe extern "system" fn(
                *mut c_void,
                i32,
                u32,
                *mut *mut c_void,
                *mut u32,
            ) -> i32 = vcall(enumerator, 4);
            next(enumerator, WBEM_INFINITE, 1, &mut object, &mut returned)
        };
        if hr < 0 {
            return super::unavailable();
        }
        if returned == 0 || object.is_null() {
            return Ok(None);
        }
        Ok(Some(ComPtr(object)))
    }

    fn object_path(instance: *mut c_void) -> Result<BStr, HostError> {
        let name = wide("__PATH");
        let mut variant = Variant::empty();
        // SAFETY: [Category 8 — FFI boundary]
        // IWbemClassObject::Get is vtable slot 4. VariantClear frees the BSTR.
        let hr = unsafe {
            let get: unsafe extern "system" fn(
                *mut c_void,
                *const u16,
                i32,
                *mut Variant,
                *mut i32,
                *mut i32,
            ) -> i32 = vcall(instance, 4);
            get(
                instance,
                name.as_ptr(),
                0,
                &mut variant,
                ptr::null_mut(),
                ptr::null_mut(),
            )
        };
        if hr < 0 || variant.vt != VT_BSTR || variant.data == 0 {
            unsafe { VariantClear(&mut variant) };
            return super::unavailable();
        }
        let ptr = variant.data as *mut u16;
        variant.vt = 0;
        variant.data = 0;
        unsafe { VariantClear(&mut variant) };
        Ok(BStr(ptr))
    }

    fn exec_method(
        services: *mut c_void,
        path: *mut u16,
        method: &str,
        in_params: *mut c_void,
    ) -> Result<(), HostError> {
        let method = bstr(method)?;
        // SAFETY: [Category 8 — FFI boundary]
        // IWbemServices::ExecMethod is vtable slot 24.
        let hr = unsafe {
            let exec: unsafe extern "system" fn(
                *mut c_void,
                *mut u16,
                *mut u16,
                i32,
                *mut c_void,
                *mut c_void,
                *mut *mut c_void,
                *mut *mut c_void,
            ) -> i32 = vcall(services, 24);
            exec(
                services,
                path,
                method.0,
                0,
                ptr::null_mut(),
                in_params,
                ptr::null_mut(),
                ptr::null_mut(),
            )
        };
        if hr < 0 {
            return super::unavailable();
        }
        Ok(())
    }

    fn com_ok(hr: i32, ptr: *mut c_void) -> Result<ComPtr, HostError> {
        if hr < 0 || ptr.is_null() {
            return super::unavailable();
        }
        Ok(ComPtr(ptr))
    }

    unsafe fn vcall<F: Copy>(obj: *mut c_void, idx: usize) -> F {
        // SAFETY: [Category 8 — FFI boundary]
        // `obj` is a live COM interface pointer. Slot `idx` is the documented
        // vtable index for that interface. The function pointer is `extern "system"`.
        let vtbl = *(obj as *const *const usize);
        std::mem::transmute_copy(&*vtbl.add(idx))
    }

    unsafe fn release(obj: *mut c_void) {
        // SAFETY: [Category 8 — FFI boundary]
        // IUnknown::Release is vtable slot 2. Unique owner; Drop runs once.
        let release_fn: unsafe extern "system" fn(*mut c_void) -> u32 = vcall(obj, 2);
        let _ = release_fn(obj);
    }
}
