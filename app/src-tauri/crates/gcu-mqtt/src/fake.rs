//! In-memory broker. Records subscribe/publish order. No TCP.

use std::future::Future;

use crate::client::{MqttError, MqttTransport};

/// One recorded transport call.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Recorded {
    /// Topic filter passed to subscribe.
    Subscribe(String),
    /// Topic + raw payload passed to publish.
    Publish { topic: String, payload: Vec<u8> },
}

/// Fake GCU that never opens 13688.
#[derive(Debug, Default)]
pub struct FakeBroker {
    recorded: Vec<Recorded>,
}

impl FakeBroker {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn recorded(&self) -> &[Recorded] {
        &self.recorded
    }
}

impl MqttTransport for FakeBroker {
    fn subscribe(&mut self, filter: &str) -> impl Future<Output = Result<(), MqttError>> + Send {
        self.recorded.push(Recorded::Subscribe(filter.to_owned()));
        async { Ok(()) }
    }

    fn publish(
        &mut self,
        topic: &str,
        payload: &[u8],
    ) -> impl Future<Output = Result<(), MqttError>> + Send {
        self.recorded.push(Recorded::Publish {
            topic: topic.to_owned(),
            payload: payload.to_vec(),
        });
        async { Ok(()) }
    }
}
