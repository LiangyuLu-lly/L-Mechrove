//! rumqttc event-loop poll + cloned publisher. One client; the loop is taken once.

use std::future::Future;

use rumqttc::{AsyncClient, Event, EventLoop, Incoming, QoS};

use crate::client::{MqttError, MqttTransport};
use crate::topics;

/// Inbound event from the dedicated poll task.
#[derive(Debug)]
pub enum ClientEvent {
    Connected,
    Disconnected,
    Publish { topic: String, payload: Vec<u8> },
}

/// Owned rumqttc event loop. Polled on one task only.
pub struct GcuEventLoop {
    inner: EventLoop,
}

impl GcuEventLoop {
    pub(crate) fn new(inner: EventLoop) -> Self {
        Self { inner }
    }

    pub async fn poll(&mut self) -> Result<ClientEvent, MqttError> {
        loop {
            match self.inner.poll().await {
                Ok(Event::Incoming(Incoming::ConnAck(_))) => return Ok(ClientEvent::Connected),
                Ok(Event::Incoming(Incoming::Publish(publish))) => {
                    return Ok(ClientEvent::Publish {
                        topic: publish.topic,
                        payload: publish.payload.to_vec(),
                    });
                }
                Ok(Event::Incoming(Incoming::Disconnect)) => {
                    return Ok(ClientEvent::Disconnected);
                }
                Ok(_) => continue,
                Err(err) => return Err(MqttError::Transport(err.to_string())),
            }
        }
    }
}

/// Cloned AsyncClient for handshake/publish while the event loop is polled elsewhere.
#[derive(Clone)]
pub struct GcuPublisher {
    client: AsyncClient,
}

impl GcuPublisher {
    pub(crate) fn new(client: AsyncClient) -> Self {
        Self { client }
    }

    pub async fn publish_system_off(&self) -> Result<(), MqttError> {
        // QoS0: a QoS2 four-step handshake dies on a clean-session disconnect.
        self.client
            .publish(
                topics::SYSTEM_CONTROL,
                QoS::AtMostOnce,
                false,
                crate::client::SYSTEM_OFF_JSON,
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

impl MqttTransport for GcuPublisher {
    fn subscribe(&mut self, filter: &str) -> impl Future<Output = Result<(), MqttError>> + Send {
        let client = self.client.clone();
        let filter = filter.to_owned();
        async move {
            client
                .subscribe(filter, QoS::ExactlyOnce)
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
                .publish(topic, QoS::ExactlyOnce, false, payload)
                .await
                .map_err(|err| MqttError::Transport(err.to_string()))
        }
    }
}
