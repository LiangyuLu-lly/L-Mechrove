//! Thread execution-state hold for screen blank. Same flags as ScreenBlankController.cs.

pub const ES_CONTINUOUS: u32 = 0x8000_0000;
pub const ES_SYSTEM_REQUIRED: u32 = 0x0000_0001;
pub const ES_DISPLAY_REQUIRED: u32 = 0x0000_0002;
pub const BLANK_EXECUTION_STATE: u32 = ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED;

#[derive(Debug, Default)]
pub struct RecordingExecState {
    flags: Vec<u32>,
}

impl RecordingExecState {
    pub fn apply(&mut self, flags: u32) {
        self.flags.push(flags);
    }

    pub fn flags(&self) -> &[u32] {
        &self.flags
    }
}

pub const fn flags_for(on: bool) -> u32 {
    if on {
        BLANK_EXECUTION_STATE
    } else {
        ES_CONTINUOUS
    }
}

pub fn apply_fake(exec: &mut RecordingExecState, on: bool) {
    exec.apply(flags_for(on));
}

#[derive(Debug, thiserror::Error)]
pub enum ExecStateError {
    #[error("SetThreadExecutionState failed")]
    Native,
    #[error("SetThreadExecutionState unavailable")]
    Unavailable,
}

#[cfg(all(windows, not(miri)))]
#[link(name = "kernel32")]
extern "system" {
    fn SetThreadExecutionState(es_flags: u32) -> u32;
}

pub fn system_apply(on: bool) -> Result<(), ExecStateError> {
    set_flags(flags_for(on))
}

fn set_flags(flags: u32) -> Result<(), ExecStateError> {
    #[cfg(all(windows, not(miri)))]
    {
        // SAFETY: [Category 8 — FFI boundary]
        // SetThreadExecutionState takes a DWORD flag word and no pointers.
        // `flags` is ES_CONTINUOUS with optional ES_SYSTEM_REQUIRED |
        // ES_DISPLAY_REQUIRED, matching ScreenBlankController.cs.
        let previous = unsafe { SetThreadExecutionState(flags) };
        if previous == 0 {
            Err(ExecStateError::Native)
        } else {
            Ok(())
        }
    }
    #[cfg(not(all(windows, not(miri))))]
    {
        let _ = flags;
        Err(ExecStateError::Unavailable)
    }
}
