//! Slot-4 connect params and rumqttc 0.25 MQTT 3.1.1 adapter.

use std::future::Future;
use std::time::Duration;

pub use rumqttc::QoS;
use rumqttc::{AsyncClient, EventLoop, MqttOptions};

const DEFAULT_HOST: &str = "127.0.0.1";
const DEFAULT_PORT: u16 = 13688;
const DEFAULT_CLIENT_ID: &str = "UWPClient_4";
const DEFAULT_USERNAME: &str = "UWPClient_User_4";
const DEFAULT_KEEPALIVE_SECS: u16 = 3;

/// Transport used by handshake. Tests inject [`crate::fake::FakeBroker`].
pub trait MqttTransport {
    /// Subscribe one topic filter and wait for SUBACK.
    fn subscribe(&mut self, filter: &str) -> impl Future<Output = Result<(), MqttError>> + Send;
    /// Publish raw bytes to `topic`.
    fn publish(
        &mut self,
        topic: &str,
        payload: &[u8],
    ) -> impl Future<Output = Result<(), MqttError>> + Send;
}

/// Connect / handshake failures.
#[derive(Debug, thiserror::Error)]
#[non_exhaustive]
pub enum MqttError {
    /// rumqttc or fake transport failure.
    #[error("MQTT transport failed: {0}")]
    Transport(String),
    /// Payload JSON encode failed.
    #[error(transparent)]
    Json(#[from] serde_json::Error),
}

/// Rejected product-forbidden MQTT slot 5.
#[derive(Debug, thiserror::Error)]
#[non_exhaustive]
pub enum ConnectError {
    /// `client_id` is `UWPClient_5` or ends with `_5`.
    #[error("MQTT product slot 5 is forbidden (client_id={client_id})")]
    ForbiddenSlot5 { client_id: String },
}

/// Product MQTT connect parameters. Password is supplied by the host.
#[derive(Debug, Clone)]
pub struct ConnectParams {
    host: String,
    port: u16,
    client_id: String,
    username: String,
    password: String,
    clean_session: bool,
    keepalive_secs: u16,
}

/// Builder that refuses slot 5 at [`ConnectParamsBuilder::build`].
#[derive(Debug, Clone)]
pub struct ConnectParamsBuilder {
    host: String,
    port: u16,
    client_id: String,
    username: String,
    password: String,
    clean_session: bool,
    keepalive_secs: u16,
}

fn reject_slot5(client_id: &str) -> Result<(), ConnectError> {
    if client_id == "UWPClient_5" || client_id.ends_with("_5") {
        return Err(ConnectError::ForbiddenSlot5 {
            client_id: client_id.to_owned(),
        });
    }
    Ok(())
}

impl ConnectParams {
    /// Slot-4 defaults. Host must pass the password; it is never read from TypeScript.
    pub fn builder() -> ConnectParamsBuilder {
        ConnectParamsBuilder {
            host: DEFAULT_HOST.to_owned(),
            port: DEFAULT_PORT,
            client_id: DEFAULT_CLIENT_ID.to_owned(),
            username: DEFAULT_USERNAME.to_owned(),
            password: String::new(),
            clean_session: true,
            keepalive_secs: DEFAULT_KEEPALIVE_SECS,
        }
    }

    pub fn host(&self) -> &str {
        &self.host
    }
    pub fn port(&self) -> u16 {
        self.port
    }
    pub fn client_id(&self) -> &str {
        &self.client_id
    }
    pub fn username(&self) -> &str {
        &self.username
    }
    pub fn password(&self) -> &str {
        &self.password
    }
    pub fn clean_session(&self) -> bool {
        self.clean_session
    }
    pub fn keepalive_secs(&self) -> u16 {
        self.keepalive_secs
    }
}

impl ConnectParamsBuilder {
    pub fn host(mut self, host: impl Into<String>) -> Self {
        self.host = host.into();
        self
    }
    pub fn port(mut self, port: u16) -> Self {
        self.port = port;
        self
    }
    pub fn client_id(mut self, client_id: impl Into<String>) -> Self {
        self.client_id = client_id.into();
        self
    }
    pub fn username(mut self, username: impl Into<String>) -> Self {
        self.username = username.into();
        self
    }
    pub fn password(mut self, password: impl Into<String>) -> Self {
        self.password = password.into();
        self
    }
    pub fn clean_session(mut self, clean_session: bool) -> Self {
        self.clean_session = clean_session;
        self
    }
    pub fn keepalive_secs(mut self, keepalive_secs: u16) -> Self {
        self.keepalive_secs = keepalive_secs;
        self
    }

    /// Errors if `client_id` is slot 5.
    pub fn build(self) -> Result<ConnectParams, ConnectError> {
        reject_slot5(&self.client_id)?;
        Ok(ConnectParams {
            host: self.host,
            port: self.port,
            client_id: self.client_id,
            username: self.username,
            password: self.password,
            clean_session: self.clean_session,
            keepalive_secs: self.keepalive_secs,
        })
    }
}

/// rumqttc MQTT 3.1.1 options. Does not open a socket.
pub fn mqtt_options(params: &ConnectParams) -> MqttOptions {
    let mut opts = MqttOptions::new(params.client_id.as_str(), params.host.as_str(), params.port);
    opts.set_keep_alive(Duration::from_secs(u64::from(params.keepalive_secs)));
    opts.set_clean_session(params.clean_session);
    opts.set_credentials(params.username.as_str(), params.password.as_str());
    opts
}

/// rumqttc AsyncClient. Event loop is taken by the host poll task.
pub struct GcuClient {
    client: AsyncClient,
    eventloop: Option<EventLoop>,
}

/// `{"Action":"System_OFF"}` — static so shutdown never unwraps serde.
pub const SYSTEM_OFF_JSON: &[u8] = br#"{"Action":"System_OFF"}"#;

impl GcuClient {
    pub fn new(params: &ConnectParams) -> Self {
        let (client, eventloop) = AsyncClient::new(mqtt_options(params), 32);
        Self {
            client,
            eventloop: Some(eventloop),
        }
    }

    pub fn take_eventloop(&mut self) -> Option<crate::eventloop::GcuEventLoop> {
        self.eventloop
            .take()
            .map(crate::eventloop::GcuEventLoop::new)
    }

    pub fn publisher(&self) -> crate::eventloop::GcuPublisher {
        crate::eventloop::GcuPublisher::new(self.client.clone())
    }

    /// C# `MechrevoHw.Publish` defaults to QoS2 (`ExactlyOnce`).
    pub const fn publish_qos() -> QoS {
        QoS::ExactlyOnce
    }

    /// Requested subscribe QoS for the product client.
    pub const fn subscribe_qos() -> QoS {
        QoS::ExactlyOnce
    }

    /// C# exit path uses AtMostOnce: a QoS2 four-step handshake dies on a clean-session disconnect.
    pub const fn system_off_qos() -> QoS {
        QoS::AtMostOnce
    }

    pub async fn publish_system_off(&self) -> Result<(), MqttError> {
        // QoS0: a QoS2 four-step handshake dies on a clean-session disconnect.
        self.client
            .publish(
                crate::topics::SYSTEM_CONTROL,
                Self::system_off_qos(),
                false,
                SYSTEM_OFF_JSON,
            )
            .await
            .map_err(|err| MqttError::Transport(err.to_string()))
    }

    pub async fn disconnect(&self) -> Result<(), MqttError> {
        self.client
            .disconnect()
            .await
            .map_err(|err| MqttError::Transport(err.to_string()))
    }
}

impl MqttTransport for GcuClient {
    fn subscribe(&mut self, filter: &str) -> impl Future<Output = Result<(), MqttError>> + Send {
        let client = self.client.clone();
        let filter = filter.to_owned();
        async move {
            client
                .subscribe(filter, Self::subscribe_qos())
                .await
                .map_err(|err| MqttError::Transport(err.to_string()))
        }
    }

    fn publish(
        &mut self,
        topic: &str,
        payload: &[u8],
    ) -> impl Future<Output = Result<(), MqttError>> + Send {
        let client = self.client.clone();
        let topic = topic.to_owned();
        let payload = payload.to_vec();
        async move {
            client
                .publish(topic, Self::publish_qos(), false, payload)
                .await
                .map_err(|err| MqttError::Transport(err.to_string()))
        }
    }
}
