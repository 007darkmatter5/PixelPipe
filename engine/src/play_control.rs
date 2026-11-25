//! Decisions the play loop and the disconnect watchdog share.
//! The emulator thread runs a frame only when the step is `Run`.
//! A dropped browser ends the session after a short grace period so the core
//! can auto-save and the encoder can be released.

use std::time::{Duration, Instant};

pub const DROP_GRACE: Duration = Duration::from_secs(8);
pub const CLOSED_GRACE: Duration = Duration::from_secs(3);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EmulationStep {
    Run,
    Hold,
}

/// Pause holds the core. It does not call `retro_run`.
pub fn emulation_step(paused: bool) -> EmulationStep {
    if paused {
        EmulationStep::Hold
    } else {
        EmulationStep::Run
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PeerLink {
    Connected,
    Dropped,
    Closed,
}

#[derive(Debug)]
pub struct DisconnectWatch {
    ever_connected: bool,
    dropped_at: Option<Instant>,
    closed: bool,
}

impl DisconnectWatch {
    pub fn new() -> Self {
        Self {
            ever_connected: false,
            dropped_at: None,
            closed: false,
        }
    }

    pub fn observe(&mut self, link: PeerLink, now: Instant) {
        match link {
            PeerLink::Connected => {
                self.ever_connected = true;
                self.dropped_at = None;
                self.closed = false;
            }
            PeerLink::Dropped => {
                if self.dropped_at.is_none() {
                    self.dropped_at = Some(now);
                }
            }
            PeerLink::Closed => {
                self.closed = true;
                if self.dropped_at.is_none() {
                    self.dropped_at = Some(now);
                }
            }
        }
    }

    pub fn should_end(&self, now: Instant) -> bool {
        let Some(at) = self.dropped_at else {
            return false;
        };
        if !self.ever_connected && !self.closed {
            return false;
        }
        let grace = if self.closed { CLOSED_GRACE } else { DROP_GRACE };
        now.saturating_duration_since(at) >= grace
    }
}

impl Default for DisconnectWatch {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pause_holds_the_core() {
        assert_eq!(emulation_step(true), EmulationStep::Hold);
        assert_eq!(emulation_step(false), EmulationStep::Run);
    }

    #[test]
    fn a_live_connection_does_not_end() {
        let start = Instant::now();
        let mut watch = DisconnectWatch::new();
        watch.observe(PeerLink::Connected, start);
        assert!(!watch.should_end(start + Duration::from_secs(30)));
    }

    #[test]
    fn drop_ends_after_the_grace_and_not_before() {
        let start = Instant::now();
        let mut watch = DisconnectWatch::new();
        watch.observe(PeerLink::Connected, start);
        watch.observe(PeerLink::Dropped, start);
        assert!(!watch.should_end(start + DROP_GRACE - Duration::from_millis(1)));
        assert!(watch.should_end(start + DROP_GRACE));
    }

    #[test]
    fn repeated_drops_do_not_extend_the_grace() {
        let start = Instant::now();
        let mut watch = DisconnectWatch::new();
        watch.observe(PeerLink::Connected, start);
        watch.observe(PeerLink::Dropped, start);
        watch.observe(PeerLink::Dropped, start + Duration::from_secs(5));
        assert!(!watch.should_end(start + Duration::from_secs(7)));
        assert!(watch.should_end(start + DROP_GRACE));
    }

    #[test]
    fn reconnect_cancels_the_deadline() {
        let start = Instant::now();
        let mut watch = DisconnectWatch::new();
        watch.observe(PeerLink::Connected, start);
        watch.observe(PeerLink::Dropped, start);
        watch.observe(PeerLink::Connected, start + Duration::from_secs(2));
        assert!(!watch.should_end(start + Duration::from_secs(20)));
        watch.observe(PeerLink::Dropped, start + Duration::from_secs(4));
        assert!(!watch.should_end(start + Duration::from_secs(4) + Duration::from_secs(7)));
        assert!(watch.should_end(start + Duration::from_secs(4) + DROP_GRACE));
    }

    #[test]
    fn closed_uses_the_shorter_grace() {
        let start = Instant::now();
        let mut watch = DisconnectWatch::new();
        watch.observe(PeerLink::Connected, start);
        watch.observe(PeerLink::Closed, start);
        assert!(!watch.should_end(start + CLOSED_GRACE - Duration::from_millis(1)));
        assert!(watch.should_end(start + CLOSED_GRACE));
    }

    #[test]
    fn a_drop_before_the_first_connection_keeps_the_session() {
        let start = Instant::now();
        let mut watch = DisconnectWatch::new();
        watch.observe(PeerLink::Dropped, start);
        assert!(!watch.should_end(start + Duration::from_secs(30)));
    }

    #[test]
    fn a_close_before_the_first_connection_still_ends() {
        let start = Instant::now();
        let mut watch = DisconnectWatch::new();
        watch.observe(PeerLink::Closed, start);
        assert!(watch.should_end(start + CLOSED_GRACE));
    }
}
