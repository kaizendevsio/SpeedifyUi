//! Whole-file publication that a concurrent reader can never catch half-finished.
//!
//! Status files are written on a timer and read by unrelated processes (the dashboard, the
//! deploy scripts) with no locking between them. `std::fs::write` truncates the target and
//! then streams into it, so a reader that opens the path mid-write sees a prefix and fails
//! to parse — and the longer the document, the wider that window gets.

use std::fs;
use std::io::Write;
use std::path::Path;

/// Writes `contents` to `path` so readers observe either the previous file or the complete
/// new one, never a partial write.
///
/// The temporary file is created alongside the target so the final `rename` stays within one
/// filesystem, which is what makes the swap atomic. Callers add their own path context; this
/// crate stays free of `anyhow`.
pub fn write_atomic(path: &Path, contents: &[u8]) -> std::io::Result<()> {
    let parent = path.parent().unwrap_or_else(|| Path::new("."));
    let file_name = path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("status");
    // Keeping the pid in the name lets two writers target the same path without one
    // clobbering the other's half-written temporary.
    let temp_path = parent.join(format!(".{file_name}.{}.tmp", std::process::id()));

    let write_result = (|| -> std::io::Result<()> {
        let mut file = fs::File::create(&temp_path)?;
        file.write_all(contents)?;
        // Without this the rename can land before the bytes do, leaving a truncated file
        // to be found after a crash or power cut.
        file.sync_all()
    })();

    if let Err(error) = write_result {
        let _ = fs::remove_file(&temp_path);
        return Err(error);
    }

    if let Err(error) = fs::rename(&temp_path, path) {
        let _ = fs::remove_file(&temp_path);
        return Err(error);
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_dir(label: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("xbond-atomic-{label}-{}", std::process::id()));
        fs::create_dir_all(&dir).expect("create temp dir");
        dir
    }

    #[test]
    fn a_written_file_reads_back_whole() {
        let dir = temp_dir("whole");
        let target = dir.join("status.json");

        write_atomic(&target, b"{\"ok\":true}").expect("write");

        assert_eq!(fs::read_to_string(&target).expect("read"), "{\"ok\":true}");
        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn rewriting_replaces_the_previous_contents_entirely() {
        let dir = temp_dir("replace");
        let target = dir.join("status.json");

        write_atomic(&target, b"a-much-longer-first-document").expect("first write");
        write_atomic(&target, b"short").expect("second write");

        // A truncating writer that failed midway would leave trailing bytes of the longer
        // first document behind; the rename cannot.
        assert_eq!(fs::read_to_string(&target).expect("read"), "short");
        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn no_temporary_file_is_left_behind() {
        let dir = temp_dir("cleanup");
        let target = dir.join("status.json");

        write_atomic(&target, b"{}").expect("write");

        let leftovers: Vec<_> = fs::read_dir(&dir)
            .expect("read dir")
            .filter_map(|entry| entry.ok())
            .map(|entry| entry.file_name().to_string_lossy().to_string())
            .filter(|name| name.ends_with(".tmp"))
            .collect();
        assert!(leftovers.is_empty(), "left behind: {leftovers:?}");
        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn a_reader_never_observes_a_partial_document() {
        let dir = temp_dir("torn");
        let target = dir.join("status.json");
        // Comfortably past the 16 KiB boundary where the real status document tore.
        let long = format!("{{\"payload\":\"{}\"}}", "x".repeat(64 * 1024));
        let short = format!("{{\"payload\":\"{}\"}}", "y".repeat(64 * 1024));
        write_atomic(&target, long.as_bytes()).expect("seed");

        let writer_target = target.clone();
        let writer = std::thread::spawn(move || {
            for index in 0..200 {
                let body = if index % 2 == 0 { &long } else { &short };
                write_atomic(&writer_target, body.as_bytes()).expect("write");
            }
        });

        // Every read must yield one of the two complete documents, never a prefix.
        for _ in 0..400 {
            if let Ok(observed) = fs::read_to_string(&target) {
                assert!(
                    observed.starts_with("{\"payload\":\"") && observed.ends_with("\"}"),
                    "observed a torn document of {} bytes",
                    observed.len()
                );
            }
        }

        writer.join().expect("writer thread");
        fs::remove_dir_all(&dir).ok();
    }
}
