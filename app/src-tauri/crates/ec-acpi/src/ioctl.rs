//! IOCTL codes, 5-byte write layout, and the transport trait.

use crate::Error;

/// `IOCTL_GPD_ACPI_ECREAD`.
pub const IOCTL_READ: u32 = 0x9C40A488;
/// `IOCTL_GPD_ACPI_ECWRITE`.
pub const IOCTL_WRITE: u32 = 0x9C40A48C;
/// Write in-buffer is `[u32 LE addr][u8 value]`. 3/4/8-byte layouts are silent no-ops.
pub const WRITE_IN_LEN: usize = 5;
/// Read in-buffer is `[u32 LE addr]`.
pub const READ_IN_LEN: usize = 4;
/// Read/write out-buffer must be non-empty; vendor driver uses 16 bytes.
pub const READ_OUT_LEN: usize = 16;

/// Device IOCTL seam. Tests inject [`crate::FakeIoctl`]; production uses [`WindowsIoctl`].
pub trait IoctlTransport {
    /// Run one DeviceIoControl-shaped call. `out` must be non-empty.
    ///
    /// # Errors
    /// Returns [`Error::Ioctl`] when the driver (or fake) rejects the call.
    fn device_io_control(
        &mut self,
        code: u32,
        in_bytes: &[u8],
        out: &mut [u8],
    ) -> Result<usize, Error>;
}

#[cfg(all(windows, feature = "real-ioctl"))]
mod windows_impl {
    use super::IoctlTransport;
    use crate::Error;
    use windows_sys::Win32::Foundation::{
        CloseHandle, GENERIC_READ, GENERIC_WRITE, HANDLE, INVALID_HANDLE_VALUE,
    };
    use windows_sys::Win32::Storage::FileSystem::{
        CreateFileW, FILE_ATTRIBUTE_NORMAL, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
    };
    use windows_sys::Win32::System::IO::DeviceIoControl;

    const fn w(b: u8) -> u16 {
        u16::from(b)
    }

    /// `\\.\ACPIDriver` as a NUL-terminated UTF-16 path.
    const DEVICE: [u16; 15] = [
        w(b'\\'),
        w(b'\\'),
        w(b'.'),
        w(b'\\'),
        w(b'A'),
        w(b'C'),
        w(b'P'),
        w(b'I'),
        w(b'D'),
        w(b'r'),
        w(b'i'),
        w(b'v'),
        w(b'e'),
        w(b'r'),
        0,
    ];

    /// Live handle to `\\.\ACPIDriver`. Never constructed by unit tests.
    pub struct WindowsIoctl {
        handle: HANDLE,
    }

    impl WindowsIoctl {
        /// Open the vendor ACPI device.
        ///
        /// # Errors
        /// [`Error::DeviceUnavailable`] when `CreateFileW` returns `INVALID_HANDLE_VALUE`.
        pub fn open() -> Result<Self, Error> {
            let handle = {
                // SAFETY: [Category 8 — FFI boundary UB]
                // `DEVICE` is a valid NUL-terminated UTF-16 path that outlives the call.
                // `lpSecurityAttributes` is null (default DACL). The HANDLE is checked
                // against `INVALID_HANDLE_VALUE` before any later use; we never form a
                // reference from an invalid handle.
                unsafe {
                    CreateFileW(
                        DEVICE.as_ptr(),
                        GENERIC_READ | GENERIC_WRITE,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        std::ptr::null(),
                        OPEN_EXISTING,
                        FILE_ATTRIBUTE_NORMAL,
                        std::ptr::null_mut(),
                    )
                }
            };
            if handle == INVALID_HANDLE_VALUE {
                return Err(Error::DeviceUnavailable);
            }
            Ok(Self { handle })
        }
    }

    impl Drop for WindowsIoctl {
        fn drop(&mut self) {
            // SAFETY: [Category 12 — Double free / invalid free]
            // `handle` is the unique owner of a `CreateFileW` HANDLE. `Drop` runs once
            // per value; we do not copy the HANDLE or call `CloseHandle` elsewhere.
            unsafe {
                CloseHandle(self.handle);
            }
        }
    }

    impl IoctlTransport for WindowsIoctl {
        fn device_io_control(
            &mut self,
            code: u32,
            in_bytes: &[u8],
            out: &mut [u8],
        ) -> Result<usize, Error> {
            if out.is_empty() {
                return Err(Error::Ioctl { code });
            }
            let in_len = u32::try_from(in_bytes.len()).map_err(|_| Error::Ioctl { code })?;
            let out_len = u32::try_from(out.len()).map_err(|_| Error::Ioctl { code })?;
            let mut returned = 0_u32;
            let ok = {
                // SAFETY: [Category 8 — FFI boundary UB]
                // `self.handle` is a live `CreateFileW` HANDLE. `in_bytes` / `out` are
                // valid for the call; lengths match the slice sizes. `lpOverlapped` is
                // null (synchronous). `returned` is a stack u32 that outlives the call.
                unsafe {
                    DeviceIoControl(
                        self.handle,
                        code,
                        in_bytes.as_ptr().cast(),
                        in_len,
                        out.as_mut_ptr().cast(),
                        out_len,
                        &mut returned,
                        std::ptr::null_mut(),
                    )
                }
            };
            if ok == 0 {
                return Err(Error::Ioctl { code });
            }
            let n = usize::try_from(returned).map_err(|_| Error::Ioctl { code })?;
            Ok(n.min(out.len()))
        }
    }
}

#[cfg(all(windows, feature = "real-ioctl"))]
pub use windows_impl::WindowsIoctl;
