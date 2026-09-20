//! In-memory Fake GCU fields. Never constructs rumqttc / NativeHid / Windows IOCTL.

use std::path::PathBuf;
use std::time::Duration;

use capabilities::{DgpuGeneration, ItemSupport};
use ec_acpi::{ChargeLimit, FakeIoctl};
use gcu_mqtt::fake::FakeBroker;
use hid_kb::FakeHid;

use crate::hw_ble::FakeBle;
use crate::hw_display::BrightnessQueue;
use crate::hw_error::HostError;
use crate::hw_snapshot::MqttStatus;
use crate::hw_switches::SeenFlags;
use crate::hw_wmi::FakeWmi;

pub struct FakeState {
    pub(crate) broker: FakeBroker,
    pub(crate) item_support: ItemSupport,
    pub(crate) mqtt_status: MqttStatus,
    pub(crate) hid: FakeHid,
    pub(crate) charge: ChargeLimit<FakeIoctl>,
    pub(crate) charge_percent: u8,
    pub(crate) gpu_generation: DgpuGeneration,
    pub(crate) gpu_three_mode: bool,
    pub(crate) light_cfg_dir: Option<PathBuf>,
    pub(crate) profile_dir: Option<PathBuf>,
    pub(crate) current_mode: Option<String>,
    pub(crate) wmi: FakeWmi,
    pub(crate) ble: FakeBle,
    pub(crate) write_allowed: bool,
    pub(crate) hdr_on: bool,
    pub(crate) hz_list: Vec<String>,
    pub(crate) seen: SeenFlags,
    pub(crate) lc_mqtt_disconnected: bool,
    pub(crate) brightness_queue: BrightnessQueue,
}

impl FakeState {
    pub(crate) fn new(item_support: ItemSupport) -> Self {
        Self {
            broker: FakeBroker::new(),
            item_support,
            mqtt_status: MqttStatus::Disconnected,
            hid: FakeHid::new(),
            charge: ChargeLimit::new(FakeIoctl::default()),
            charge_percent: 100,
            gpu_generation: DgpuGeneration::Unknown,
            gpu_three_mode: false,
            light_cfg_dir: None,
            profile_dir: None,
            current_mode: None,
            wmi: FakeWmi::new(),
            ble: FakeBle::new(),
            write_allowed: true,
            hdr_on: false,
            hz_list: vec!["60".to_owned(), "165".to_owned()],
            seen: SeenFlags::default(),
            lc_mqtt_disconnected: false,
            brightness_queue: BrightnessQueue::new(Duration::from_millis(120)),
        }
    }

    pub(crate) fn ensure_writable(&self) -> Result<(), HostError> {
        if self.write_allowed {
            Ok(())
        } else {
            Err(HostError::WritesDisabled)
        }
    }
}
