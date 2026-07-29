//! 进程句柄身份（阶段 4.2 第六/七轮复审 P1-02/P1-01）。
//!
//! 裸 PID 会被系统复用；PID→OpenProcess 之间存在"原进程退出 + PID 复用"的
//! 身份错绑竞态。正确做法：在创建进程后立即通过 `DuplicateHandle` 复制
//! `std::process::Child` 自带的原始进程句柄——句柄指向的进程对象从创建
//! 一刻起就是确定的，永不代表被复用 PID 的新进程。

#[cfg(windows)]
mod imp {
    use std::io;
    use std::os::windows::io::AsRawHandle;
    use std::time::Duration;

    use windows_sys::Win32::Foundation::{
        CloseHandle, DUPLICATE_SAME_ACCESS, DuplicateHandle, HANDLE, WAIT_FAILED, WAIT_OBJECT_0,
        WAIT_TIMEOUT,
    };
    use windows_sys::Win32::System::Threading::{
        GetCurrentProcess, TerminateProcess, WaitForSingleObject,
    };

    /// 等待结果（明确区分退出/超时，WAIT_FAILED 走 Err）。
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub enum ProcessWaitOutcome {
        /// 进程已退出（signaled）。
        Exited,
        /// 超时仍存活。
        Timeout,
    }

    /// 可等待、可终止的进程句柄（DuplicateHandle 复制而来，RAII CloseHandle）。
    pub struct ProcessHandle {
        handle: HANDLE,
    }

    // HANDLE 是 *mut c_void；Windows 句柄可跨线程等待/关闭。
    unsafe impl Send for ProcessHandle {}
    unsafe impl Sync for ProcessHandle {}

    impl ProcessHandle {
        /// 复制 Child 自带的原始进程句柄（唯一认可的身份获取方式）。
        ///
        /// 句柄指向 Child 创建时确定的进程对象，不存在 PID 交付竞态。
        /// 失败时调用方仍持有原始 Child，可立即 kill + wait，不产生孤儿。
        pub fn duplicate_from_child(child: &std::process::Child) -> io::Result<Self> {
            let mut duplicated: HANDLE = std::ptr::null_mut();
            let ok = unsafe {
                DuplicateHandle(
                    GetCurrentProcess(),
                    child.as_raw_handle() as HANDLE,
                    GetCurrentProcess(),
                    &mut duplicated,
                    0,
                    0,
                    DUPLICATE_SAME_ACCESS,
                )
            };
            if ok == 0 || duplicated.is_null() {
                return Err(io::Error::last_os_error());
            }
            Ok(Self { handle: duplicated })
        }

        /// 从另一个仍存活的进程中复制其持有的句柄。
        ///
        /// `source_handle` 是句柄在 `source_process` 地址空间中的数值。调用必须发生在
        /// source process 退出之前；返回的句柄属于当前进程，之后不依赖 source process
        /// 生命周期。该入口用于真实 launcher-exit 集成测试的句柄安全交付。
        #[doc(hidden)]
        pub fn duplicate_from_remote_process(
            source_process: &std::process::Child,
            source_handle: usize,
        ) -> io::Result<Self> {
            let mut duplicated: HANDLE = std::ptr::null_mut();
            let ok = unsafe {
                DuplicateHandle(
                    source_process.as_raw_handle() as HANDLE,
                    source_handle as HANDLE,
                    GetCurrentProcess(),
                    &mut duplicated,
                    0,
                    0,
                    DUPLICATE_SAME_ACCESS,
                )
            };
            if ok == 0 || duplicated.is_null() {
                return Err(io::Error::last_os_error());
            }
            Ok(Self { handle: duplicated })
        }

        fn wait(&self, timeout_ms: u32) -> io::Result<ProcessWaitOutcome> {
            let result = unsafe { WaitForSingleObject(self.handle, timeout_ms) };
            match result {
                WAIT_OBJECT_0 => Ok(ProcessWaitOutcome::Exited),
                WAIT_TIMEOUT => Ok(ProcessWaitOutcome::Timeout),
                WAIT_FAILED => Err(io::Error::last_os_error()),
                other => Err(io::Error::other(format!(
                    "WaitForSingleObject 返回意外值 {other}"
                ))),
            }
        }

        /// 进程当前是否仍存活。查询失败返回 Err，绝不把失败当成"已退出"（复审 P2-01）。
        pub fn is_alive(&self) -> io::Result<bool> {
            match self.wait(0)? {
                ProcessWaitOutcome::Exited => Ok(false),
                ProcessWaitOutcome::Timeout => Ok(true),
            }
        }

        /// 等待进程退出；Ok(Exited) 表示已退出，Ok(Timeout) 表示仍存活。
        pub fn wait_exit(&self, timeout: Duration) -> io::Result<ProcessWaitOutcome> {
            let millis = timeout.as_millis().min(u32::MAX as u128) as u32;
            self.wait(millis)
        }

        /// 强制终止（只对句柄指向的原始进程生效，绝不误杀 PID 复用进程）。
        pub fn terminate(&self, exit_code: u32) -> io::Result<()> {
            let ok = unsafe { TerminateProcess(self.handle, exit_code) };
            if ok == 0 {
                return Err(io::Error::last_os_error());
            }
            Ok(())
        }

        /// 测试专用：用无效句柄值构造，用于 WAIT_FAILED 解码测试。
        #[cfg(test)]
        pub(crate) fn from_raw_for_tests(raw: usize) -> Self {
            Self {
                handle: raw as HANDLE,
            }
        }
    }

    impl Drop for ProcessHandle {
        fn drop(&mut self) {
            unsafe {
                let _ = CloseHandle(self.handle);
            }
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        fn spawn_sleepy_child() -> std::process::Child {
            std::process::Command::new("powershell")
                .args(["-NoProfile", "-Command", "Start-Sleep -Seconds 60"])
                .stdin(std::process::Stdio::null())
                .stdout(std::process::Stdio::null())
                .stderr(std::process::Stdio::null())
                .spawn()
                .expect("spawn")
        }

        #[test]
        fn duplicate_handle_tracks_exact_process_lifecycle() {
            let mut child = spawn_sleepy_child();
            let handle = ProcessHandle::duplicate_from_child(&child).expect("duplicate");
            assert!(handle.is_alive().expect("alive 查询"), "运行中必须为存活");
            assert_eq!(
                handle.wait_exit(Duration::from_millis(50)).expect("wait"),
                ProcessWaitOutcome::Timeout,
                "未退出"
            );

            handle.terminate(1).expect("terminate");
            assert_eq!(
                handle.wait_exit(Duration::from_secs(5)).expect("wait"),
                ProcessWaitOutcome::Exited,
                "终止后必须转为退出"
            );
            assert!(!handle.is_alive().expect("alive 查询"));
            let _ = child.wait();
        }

        /// 复审 P2-01：WAIT_FAILED 必须解码为错误，不得当作"已退出"。
        #[test]
        fn wait_failed_is_error_not_exited() {
            // 无效句柄值必然触发 WAIT_FAILED。
            let bogus = ProcessHandle::from_raw_for_tests(0xDEAD);
            assert!(bogus.is_alive().is_err(), "WAIT_FAILED 必须是错误");
            assert!(bogus.wait_exit(Duration::from_millis(1)).is_err());
            // 避免 RAII 对伪句柄 CloseHandle（无副作用，但语义上不依赖它）。
            std::mem::forget(bogus);
        }

        /// 复审第七轮 §8.2：旧复制句柄在原进程退出后只表示"旧进程已退出"，
        /// 与同二进制的新实例无关。
        #[test]
        fn old_duplicate_handle_refers_only_to_original_process() {
            let mut child_a = spawn_sleepy_child();
            let handle_a = ProcessHandle::duplicate_from_child(&child_a).expect("duplicate a");
            // 再启动同二进制实例 B。
            let mut child_b = spawn_sleepy_child();
            // 终止 A。
            child_a.kill().expect("kill a");
            let _ = child_a.wait();
            // 旧句柄必须只表示 A 已退出；B 不受影响。
            assert_eq!(
                handle_a.wait_exit(Duration::from_secs(5)).expect("wait a"),
                ProcessWaitOutcome::Exited
            );
            assert!(!handle_a.is_alive().expect("alive 查询"));
            handle_b_guard(&mut child_b);
        }

        fn handle_b_guard(child_b: &mut std::process::Child) {
            let alive = ProcessHandle::duplicate_from_child(child_b)
                .expect("duplicate b")
                .is_alive()
                .expect("alive b");
            assert!(alive, "B 必须不受 A 的句柄影响");
            let _ = child_b.kill();
            let _ = child_b.wait();
        }
    }
}

#[cfg(windows)]
pub use imp::{ProcessHandle, ProcessWaitOutcome};
