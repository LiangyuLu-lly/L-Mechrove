//! Real GCU live fields. ItemSupport from the registry map; MQTT fills Seen/telemetry.

use std::path::PathBuf;
use std::sync::atomic::AtomicBool;
use std::sync::Arc;

use capabilities::{DgpuGeneration, ItemSupport};
use gcu_mqtt::client::GcuClient;
use gcu_mqtt::eventloop::{GcuEventLoop, GcuPublisher};

use crate::hw_mqtt_inbound::InboundLive;
use crate::hw_snapshot::MqttStatus;
use crate::hw_switches::SeenFlags;

pub struct RealState {
    pub(crate) client: GcuClient,
    pub(crate) item_support: ItemSupport,
    pub(crate) seen: SeenFlags,
    pub(crate) mqtt_status: MqttStatus,
    pub(crate) charge_percent: u8,
    pub(crate) gpu_generation: DgpuGeneration,
    pub(crate) gpu_three_mode: bool,
    pub(crate) hz_list: Vec<String>,
    pub(crate) dc_hz_seen: bool,
    pub(crate) hdr_on: bool,
    pub(crate) cpu_temp_c: Option<f64>,
    pub(crate) gpu_temp_c: Option<f64>,
    pub(crate) cpu_rpm: Option<i64>,
    pub(crate) gpu_rpm: Option<i64>,
    pub(crate) cpu_watt: Option<f64>,
    pub(crate) gpu_watt: Option<f64>,
    pub(crate) project_id: String,
    pub(crate) custom_profile_index: u8,
    pub(crate) profile_dir: Option<PathBuf>,
    pub(crate) theme_mode: String,
    pub(crate) keyboard_hid_unavailable: bool,
    pub(crate) lighting_off_on_battery: bool,
    pub(crate) lighting_idle_seconds: i32,
    pub(crate) stop: Arc<AtomicBool>,
}

pub struct MqttLoopParts {
    pub eventloop: GcuEventLoop,
    pub publisher: GcuPublisher,
    pub stop: Arc<AtomicBool>,
}

impl RealState {
    pub fn new(client: GcuClient) -> Self {
        Self {
            client,
            item_support: ItemSupport::default(),
            seen: SeenFlags::default(),
            mqtt_status: MqttStatus::Disconnected,
            charge_percent: 100,
            gpu_generation: DgpuGeneration::Unknown,
            gpu_three_mode: false,
            hz_list: Vec::new(),
            dc_hz_seen: false,
            hdr_on: false,
            cpu_temp_c: None,
            gpu_temp_c: None,
            cpu_rpm: None,
            gpu_rpm: None,
            cpu_watt: None,
            gpu_watt: None,
            project_id: String::new(),
            custom_profile_index: 0,
            profile_dir: None,
            theme_mode: "night".to_owned(),
            keyboard_hid_unavailable: false,
            lighting_off_on_battery: false,
            lighting_idle_seconds: 0,
            stop: Arc::new(AtomicBool::new(false)),
        }
    }

    pub fn take_mqtt_loop(&mut self) -> Option<MqttLoopParts> {
        let eventloop = self.client.take_eventloop()?;
        Some(MqttLoopParts {
            eventloop,
            publisher: self.client.publisher(),
            stop: Arc::clone(&self.stop),
        })
    }

    pub fn apply_inbound_live(&mut self, live: &InboundLive) {
        self.seen = live.seen;
        self.charge_percent = live.charge_percent;
        self.hz_list.clone_from(&live.hz_list);
        self.dc_hz_seen = live.dc_hz_seen;
        self.cpu_temp_c = live.cpu_temp_c;
        self.gpu_temp_c = live.gpu_temp_c;
        self.cpu_rpm = live.cpu_rpm;
        self.gpu_rpm = live.gpu_rpm;
        if let Some(index) = live.custom_profile_index {
            self.custom_profile_index = index;
        }
    }

    pub fn inbound_live(&self) -> InboundLive {
        InboundLive {
            seen: self.seen,
            charge_percent: self.charge_percent,
            hz_list: self.hz_list.clone(),
            dc_hz_seen: self.dc_hz_seen,
            cpu_temp_c: self.cpu_temp_c,
            gpu_temp_c: self.gpu_temp_c,
            cpu_rpm: self.cpu_rpm,
            gpu_rpm: self.gpu_rpm,
            custom_profile_index: Some(self.custom_profile_index),
        }
    }
}
