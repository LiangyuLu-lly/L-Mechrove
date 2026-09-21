//! Dedicated GcuClient event-loop task. Re-handshake on reconnect; never a second client.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

use gcu_mqtt::eventloop::{ClientEvent, GcuEventLoop, GcuPublisher};
use gcu_mqtt::handshake::run_handshake;
use tauri::{AppHandle, Manager};

use crate::hw_backend::AppState;
use crate::hw_mqtt_reconnect::{next_retry_delay_ms, ReconnectCoordinator, INITIAL_RETRY_DELAY_MS};
use crate::hw_real::MqttLoopParts;
use crate::hw_snapshot::MqttStatus;
use crate::{events, HwSnapshot};

pub async fn run_mqtt_loop(parts: MqttLoopParts, app: AppHandle) {
    let MqttLoopParts {
        mut eventloop,
        mut publisher,
        stop,
    } = parts;
    poll_until_stop(&mut eventloop, &mut publisher, &stop, &app).await;
}

async fn poll_until_stop(
    eventloop: &mut GcuEventLoop,
    publisher: &mut GcuPublisher,
    stop: &Arc<AtomicBool>,
    app: &AppHandle,
) {
    let mut coord = ReconnectCoordinator::new();
    let mut delay = INITIAL_RETRY_DELAY_MS;
    let mut ever_connected = false;
    loop {
        if stop.load(Ordering::Acquire) {
            break;
        }
        match eventloop.poll().await {
            Ok(ClientEvent::Connected) => {
                delay = INITIAL_RETRY_DELAY_MS;
                if ever_connected {
                    let _ = run_handshake(publisher).await;
                }
                ever_connected = true;
                emit_status(app, MqttStatus::Connected).await;
            }
            Ok(ClientEvent::Disconnected) => {
                coord.mark_disconnect_requested();
                emit_status(app, MqttStatus::Disconnected).await;
            }
            Ok(ClientEvent::Publish { topic, payload }) => {
                emit_inbound(app, &topic, &payload).await;
            }
            Err(_) => {
                if stop.load(Ordering::Acquire) {
                    break;
                }
                coord.mark_disconnect_requested();
                if !coord.try_enter_loop() {
                    continue;
                }
                emit_status(app, MqttStatus::Disconnected).await;
                tokio::time::sleep(Duration::from_millis(delay)).await;
                delay = next_retry_delay_ms(delay);
                let _ = coord.exit_loop();
            }
        }
    }
}

async fn emit_status(app: &AppHandle, status: MqttStatus) {
    let snap = {
        let state = app.state::<AppState>();
        let mut backend = state.backend.lock().await;
        backend.set_mqtt_status(status);
        backend.snapshot()
    };
    emit_all(app, &snap);
}

async fn emit_inbound(app: &AppHandle, topic: &str, payload: &[u8]) {
    let snap = {
        let state = app.state::<AppState>();
        let mut backend = state.backend.lock().await;
        backend.apply_inbound(topic, payload);
        backend.snapshot()
    };
    emit_all(app, &snap);
}

fn emit_all(app: &AppHandle, snap: &HwSnapshot) {
    let _ = events::emit_mqtt_status(app, snap.mqtt);
    let _ = events::emit_capabilities(app, snap.lighting);
    let _ = events::emit_snapshot(app, snap);
}
