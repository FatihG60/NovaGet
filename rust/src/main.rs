use futures_util::StreamExt;
use reqwest::header::{ACCEPT_ENCODING, ACCEPT_RANGES, CONTENT_LENGTH, CONTENT_RANGE, ETAG, IF_RANGE, RANGE, USER_AGENT};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::env;
use std::fs::{self, File, OpenOptions};
use std::io::Read;
use std::os::windows::fs::FileExt;
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

#[derive(Debug, Clone, Serialize, Deserialize)]
struct PersistedSegment {
    id: usize,
    start_offset: i64,
    current_offset: i64,
    end_offset: i64,
    is_completed: bool,
}

struct LiveSegment {
    id: usize,
    start_offset: i64,
    current_offset: AtomicI64,
    end_offset: AtomicI64,
    is_completed: AtomicBool,
    is_active: AtomicBool,
    needs_overlap_verification: AtomicBool,
}

impl LiveSegment {
    fn from_persisted(p: &PersistedSegment) -> Arc<Self> {
        let needs_overlap = !p.is_completed && p.current_offset > p.start_offset;
        Arc::new(Self {
            id: p.id,
            start_offset: p.start_offset,
            current_offset: AtomicI64::new(p.current_offset),
            end_offset: AtomicI64::new(p.end_offset),
            is_completed: AtomicBool::new(p.is_completed),
            is_active: AtomicBool::new(false),
            needs_overlap_verification: AtomicBool::new(needs_overlap),
        })
    }

    fn to_persisted(&self) -> PersistedSegment {
        PersistedSegment {
            id: self.id,
            start_offset: self.start_offset,
            current_offset: self.current_offset.load(Ordering::Acquire),
            end_offset: self.end_offset.load(Ordering::Acquire),
            is_completed: self.is_completed.load(Ordering::Acquire),
        }
    }

    fn remaining_bytes(&self) -> i64 {
        let cur = self.current_offset.load(Ordering::Acquire);
        let end = self.end_offset.load(Ordering::Acquire);
        (end - cur + 1).max(0)
    }

    fn downloaded_bytes(&self) -> i64 {
        let cur = self.current_offset.load(Ordering::Acquire);
        (cur - self.start_offset).max(0)
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct DownloadCheckpoint {
    url: String,
    output_path: String,
    total_bytes: i64,
    supports_ranges: bool,
    etag: Option<String>,
    segments: Vec<PersistedSegment>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "PascalCase")]
struct DownloadMetrics {
    engine_name: String,
    total_bytes: i64,
    elapsed_milliseconds: f64,
    throughput_m_bps: f64,
    max_connections: usize,
    dynamic_splits_performed: usize,
    overlap_verifications_passed: usize,
    sha256_hex: String,
}

struct EngineOptions {
    max_connections: usize,
    min_split_size_bytes: i64,
    overlap_bytes: usize,
}

impl Default for EngineOptions {
    fn default() -> Self {
        Self {
            max_connections: 8,
            min_split_size_bytes: 512 * 1024, // 512 KB
            overlap_bytes: 64,                // 0x40 (64 bytes) IDM FUN_0058b320 overlap
        }
    }
}

struct SharedDownloadState {
    url: String,
    output_path: String,
    total_bytes: i64,
    supports_ranges: bool,
    etag: Option<String>,
    segments: Mutex<Vec<Arc<LiveSegment>>>,
    dynamic_splits: AtomicUsize,
    overlap_verified: AtomicUsize,
    cancelled: AtomicBool,
}

/// Clean-room Rust + Tokio implementation of IDM's dynamic multi-segment engine:
/// - Initial probe with "Range: bytes=0-"
/// - Lock-free concurrent positional Windows FileExt::seek_write (WriteFile at offset)
/// - Dynamic in-flight work-stealing (splits the largest remaining active segment in half)
/// - 64-byte (0x40) overlap boundary verification on resumed segments
async fn run_download(
    client: &reqwest::Client,
    url: &str,
    output_path: &str,
    options: &EngineOptions,
    pause_at_fraction: Option<f64>,
) -> Result<(DownloadMetrics, bool), Box<dyn std::error::Error + Send + Sync>> {
    let start_instant = Instant::now();
    let checkpoint_path = format!("{}.idm.part.json", output_path);

    let state: Arc<SharedDownloadState> = if Path::new(&checkpoint_path).exists() && Path::new(output_path).exists() {
        let raw = fs::read_to_string(&checkpoint_path)?;
        let cp: DownloadCheckpoint = serde_json::from_str(&raw)?;
        let live_segs = cp.segments.iter().map(LiveSegment::from_persisted).collect();
        Arc::new(SharedDownloadState {
            url: cp.url,
            output_path: cp.output_path,
            total_bytes: cp.total_bytes,
            supports_ranges: cp.supports_ranges,
            etag: cp.etag,
            segments: Mutex::new(live_segs),
            dynamic_splits: AtomicUsize::new(0),
            overlap_verified: AtomicUsize::new(0),
            cancelled: AtomicBool::new(false),
        })
    } else {
        let resp = client
            .get(url)
            .header(RANGE, "bytes=0-")
            .send()
            .await?
            .error_for_status()?;

        let supports_ranges = resp.status() == reqwest::StatusCode::PARTIAL_CONTENT
            || resp
                .headers()
                .get(ACCEPT_RANGES)
                .and_then(|v| v.to_str().ok())
                .map(|s| s.contains("bytes"))
                .unwrap_or(false);

        let total_bytes = resp
            .headers()
            .get(CONTENT_RANGE)
            .and_then(|v| v.to_str().ok())
            .and_then(|s| s.split('/').nth(1))
            .and_then(|n| n.parse::<i64>().ok())
            .or_else(|| {
                resp.headers()
                    .get(CONTENT_LENGTH)
                    .and_then(|v| v.to_str().ok())
                    .and_then(|n| n.parse::<i64>().ok())
            })
            .ok_or("Missing Content-Length / Content-Range")?;

        let etag = resp
            .headers()
            .get(ETAG)
            .and_then(|v| v.to_str().ok())
            .map(|s| s.to_string());

        drop(resp);

        if let Some(parent) = Path::new(output_path).parent() {
            fs::create_dir_all(parent)?;
        }

        let initial_seg = Arc::new(LiveSegment {
            id: 1,
            start_offset: 0,
            current_offset: AtomicI64::new(0),
            end_offset: AtomicI64::new(total_bytes - 1),
            is_completed: AtomicBool::new(false),
            is_active: AtomicBool::new(false),
            needs_overlap_verification: AtomicBool::new(false),
        });

        Arc::new(SharedDownloadState {
            url: url.to_string(),
            output_path: output_path.to_string(),
            total_bytes,
            supports_ranges,
            etag,
            segments: Mutex::new(vec![initial_seg]),
            dynamic_splits: AtomicUsize::new(0),
            overlap_verified: AtomicUsize::new(0),
            cancelled: AtomicBool::new(false),
        })
    };

    let file = Arc::new(
        OpenOptions::new()
            .create(true)
            .read(true)
            .write(true)
            .open(output_path)?,
    );
    file.set_len(state.total_bytes as u64)?;

    // Perform initial splits up to max_connections
    if state.supports_ranges {
        let mut segs = state.segments.lock().unwrap();
        while segs.len() < options.max_connections {
            if try_split_largest_locked(&mut segs, options.min_split_size_bytes, &state.dynamic_splits).is_none() {
                break;
            }
        }
    }
    save_checkpoint(&state, &checkpoint_path)?;

    let worker_count = if state.supports_ranges { options.max_connections } else { 1 };
    let mut handles = Vec::with_capacity(worker_count);

    for _ in 0..worker_count {
        let client_clone = client.clone();
        let state_clone = Arc::clone(&state);
        let file_clone = Arc::clone(&file);
        let min_split = options.min_split_size_bytes;
        let overlap_bytes = options.overlap_bytes;

        handles.push(tokio::spawn(async move {
            while !state_clone.cancelled.load(Ordering::Relaxed) {
                let seg_opt = acquire_next_or_split(&state_clone, min_split);
                let Some(seg) = seg_opt else { break };

                let res = download_segment(
                    &client_clone,
                    &state_clone,
                    &seg,
                    &file_clone,
                    overlap_bytes,
                    pause_at_fraction,
                )
                .await;

                seg.is_active.store(false, Ordering::Release);
                if res.is_err() {
                    break;
                }
            }
        }));
    }

    for h in handles {
        let _ = h.await;
    }

    file.sync_data()?;

    if state.cancelled.load(Ordering::Acquire) {
        save_checkpoint(&state, &checkpoint_path)?;
        let elapsed_ms = start_instant.elapsed().as_secs_f64() * 1000.0;
        return Ok((
            DownloadMetrics {
                engine_name: "Rust (Tokio + Reqwest + FileExt::seek_write)".to_string(),
                total_bytes: state.total_bytes,
                elapsed_milliseconds: elapsed_ms,
                throughput_m_bps: 0.0,
                max_connections: options.max_connections,
                dynamic_splits_performed: state.dynamic_splits.load(Ordering::Acquire),
                overlap_verifications_passed: state.overlap_verified.load(Ordering::Acquire),
                sha256_hex: String::new(),
            },
            true,
        ));
    }

    let _ = fs::remove_file(&checkpoint_path);
    let elapsed_ms = start_instant.elapsed().as_secs_f64() * 1000.0;
    let throughput = (state.total_bytes as f64 / (1024.0 * 1024.0)) / (elapsed_ms / 1000.0);
    let sha256_hex = compute_sha256(output_path)?;

    Ok((
        DownloadMetrics {
            engine_name: "Rust (Tokio + Reqwest + FileExt::seek_write)".to_string(),
            total_bytes: state.total_bytes,
            elapsed_milliseconds: elapsed_ms,
            throughput_m_bps: throughput,
            max_connections: options.max_connections,
            dynamic_splits_performed: state.dynamic_splits.load(Ordering::Acquire),
            overlap_verifications_passed: state.overlap_verified.load(Ordering::Acquire),
            sha256_hex,
        },
        false,
    ))
}

fn acquire_next_or_split(state: &SharedDownloadState, min_split: i64) -> Option<Arc<LiveSegment>> {
    let mut segs = state.segments.lock().unwrap();
    for s in segs.iter() {
        if !s.is_completed.load(Ordering::Acquire) && !s.is_active.load(Ordering::Acquire) {
            s.is_active.store(true, Ordering::Release);
            return Some(Arc::clone(s));
        }
    }
    if !state.supports_ranges {
        return None;
    }
    if let Some(new_seg) = try_split_largest_locked(&mut segs, min_split, &state.dynamic_splits) {
        new_seg.is_active.store(true, Ordering::Release);
        return Some(new_seg);
    }
    None
}

fn try_split_largest_locked(
    segs: &mut Vec<Arc<LiveSegment>>,
    min_split: i64,
    splits_counter: &AtomicUsize,
) -> Option<Arc<LiveSegment>> {
    let mut largest_idx = None;
    let mut max_rem = 0i64;

    for (idx, s) in segs.iter().enumerate() {
        if s.is_completed.load(Ordering::Acquire) {
            continue;
        }
        let rem = s.remaining_bytes();
        if rem > max_rem {
            max_rem = rem;
            largest_idx = Some(idx);
        }
    }

    let idx = largest_idx?;
    if max_rem < min_split * 2 {
        return None;
    }

    let target = &segs[idx];
    let cur = target.current_offset.load(Ordering::Acquire);
    let old_end = target.end_offset.load(Ordering::Acquire);
    let rem = old_end - cur + 1;
    if rem < min_split * 2 {
        return None;
    }

    let split_offset = cur + (rem / 2);
    target.end_offset.store(split_offset - 1, Ordering::Release);

    let new_seg = Arc::new(LiveSegment {
        id: segs.len() + 1,
        start_offset: split_offset,
        current_offset: AtomicI64::new(split_offset),
        end_offset: AtomicI64::new(old_end),
        is_completed: AtomicBool::new(false),
        is_active: AtomicBool::new(false),
        needs_overlap_verification: AtomicBool::new(false),
    });

    segs.push(Arc::clone(&new_seg));
    splits_counter.fetch_add(1, Ordering::Relaxed);
    Some(new_seg)
}

async fn download_segment(
    client: &reqwest::Client,
    state: &SharedDownloadState,
    seg: &LiveSegment,
    file: &File,
    overlap_bytes: usize,
    pause_at_fraction: Option<f64>,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let start_cur = seg.current_offset.load(Ordering::Acquire);
    let end = seg.end_offset.load(Ordering::Acquire);
    if start_cur > end {
        seg.is_completed.store(true, Ordering::Release);
        return Ok(());
    }

    let overlap = if seg.needs_overlap_verification.load(Ordering::Acquire)
        && state.supports_ranges
        && (start_cur - seg.start_offset) >= overlap_bytes as i64
    {
        overlap_bytes
    } else {
        0
    };

    let request_from = start_cur - (overlap as i64);
    let request_to = if end + (overlap_bytes as i64) < state.total_bytes - 1 {
        end + (overlap_bytes as i64)
    } else {
        end
    };

    let mut req = client.get(&state.url);
    if state.supports_ranges {
        req = req.header(RANGE, format!("bytes={}-{}", request_from, request_to));
        if let Some(ref etag) = state.etag {
            req = req.header(IF_RANGE, etag.as_str());
        }
    }

    let resp = req.send().await?.error_for_status()?;
    let mut stream = resp.bytes_stream();

    let mut overlap_buf = Vec::with_capacity(overlap);

    while let Some(chunk_res) = stream.next().await {
        if state.cancelled.load(Ordering::Relaxed) {
            return Ok(());
        }
        let chunk = chunk_res?;
        let mut slice: &[u8] = &chunk;

        // Complete 64-byte overlap verification before writing new bytes
        if overlap_buf.len() < overlap {
            let needed = overlap - overlap_buf.len();
            let take = needed.min(slice.len());
            overlap_buf.extend_from_slice(&slice[..take]);
            slice = &slice[take..];

            if overlap_buf.len() == overlap {
                let mut disk_bytes = vec![0u8; overlap];
                let read_n = file.seek_read(&mut disk_bytes, request_from as u64)?;
                if read_n != overlap || disk_bytes != overlap_buf {
                    return Err(format!(
                        "Segment #{} failed 64-byte overlap check at offset {}",
                        seg.id, request_from
                    )
                    .into());
                }
                seg.needs_overlap_verification.store(false, Ordering::Release);
                state.overlap_verified.fetch_add(1, Ordering::Relaxed);
            } else {
                continue;
            }
        }

        if slice.is_empty() {
            continue;
        }

        let cur = seg.current_offset.load(Ordering::Acquire);
        let dyn_end = seg.end_offset.load(Ordering::Acquire);
        let allowed = (dyn_end - cur + 1).max(0) as usize;
        if allowed == 0 {
            seg.is_completed.store(true, Ordering::Release);
            break;
        }

        let to_commit = slice.len().min(allowed);
        file.seek_write(&slice[..to_commit], cur as u64)?;
        let new_cur = seg.current_offset.fetch_add(to_commit as i64, Ordering::AcqRel) + (to_commit as i64);

        if let Some(frac) = pause_at_fraction {
            let total_down: i64 = state
                .segments
                .lock()
                .unwrap()
                .iter()
                .map(|s| s.downloaded_bytes())
                .sum();
            if (total_down as f64) >= (state.total_bytes as f64) * frac {
                state.cancelled.store(true, Ordering::Release);
                return Ok(());
            }
        }

        if new_cur > seg.end_offset.load(Ordering::Acquire) {
            seg.is_completed.store(true, Ordering::Release);
            break;
        }
    }

    if seg.current_offset.load(Ordering::Acquire) > seg.end_offset.load(Ordering::Acquire) {
        seg.is_completed.store(true, Ordering::Release);
    }

    Ok(())
}

fn save_checkpoint(state: &SharedDownloadState, checkpoint_path: &str) -> std::io::Result<()> {
    let persisted: Vec<PersistedSegment> = state
        .segments
        .lock()
        .unwrap()
        .iter()
        .map(|s| s.to_persisted())
        .collect();

    let cp = DownloadCheckpoint {
        url: state.url.clone(),
        output_path: state.output_path.clone(),
        total_bytes: state.total_bytes,
        supports_ranges: state.supports_ranges,
        etag: state.etag.clone(),
        segments: persisted,
    };

    let tmp_path = format!("{}.tmp", checkpoint_path);
    let json = serde_json::to_string_pretty(&cp)?;
    fs::write(&tmp_path, json)?;
    fs::rename(&tmp_path, checkpoint_path)?;
    Ok(())
}

fn compute_sha256(path: &str) -> std::io::Result<String> {
    let mut f = File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buf = vec![0u8; 64 * 1024];
    loop {
        let n = f.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
    }
    Ok(hex::encode(hasher.finalize()))
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let args: Vec<String> = env::args().collect();
    let url = args.get(1).map(|s| s.as_str()).unwrap_or("http://127.0.0.1:18080/payload.bin");
    let work_dir = args.get(2).map(|s| s.as_str()).unwrap_or("bench_out_rust");
    let expected_sha = args.get(3).map(|s| s.as_str()).unwrap_or("");

    fs::create_dir_all(work_dir)?;

    let client = reqwest::Client::builder()
        .user_agent("MyIDM/1.0 (Rust Tokio DynamicSegmentEngine)")
        .pool_max_idle_per_host(32)
        .default_headers({
            let mut h = reqwest::header::HeaderMap::new();
            h.insert(ACCEPT_ENCODING, "identity".parse().unwrap());
            h.insert(USER_AGENT, "MyIDM/1.0 (Rust Tokio DynamicSegmentEngine)".parse().unwrap());
            h
        })
        .build()?;

    println!("[1/2] Rust Engine: Testing Pause + Resume + 64-Byte (0x40) Overlap Verification...");
    let pause_file = format!("{}/test_pause_resume_rust.bin", work_dir);
    let _ = fs::remove_file(&pause_file);
    let _ = fs::remove_file(format!("{}.idm.part.json", pause_file));

    let (_, paused) = run_download(
        &client,
        url,
        &pause_file,
        &EngineOptions { max_connections: 4, ..Default::default() },
        Some(0.25),
    )
    .await?;
    println!("  -> Paused at ~25% (paused={}) and saved .idm.part.json checkpoint.", paused);

    let (resume_metrics, _) = run_download(
        &client,
        url,
        &pause_file,
        &EngineOptions { max_connections: 8, ..Default::default() },
        None,
    )
    .await?;
    println!(
        "  -> Resumed & Completed! 64-Byte Overlap Verifications Passed: {}",
        resume_metrics.overlap_verifications_passed
    );
    println!("  -> Dynamic Work-Stealing Splits: {}", resume_metrics.dynamic_splits_performed);
    if !expected_sha.is_empty() {
        println!(
            "  -> SHA-256 Verified: {} ({})",
            resume_metrics.sha256_hex.eq_ignore_ascii_case(expected_sha),
            resume_metrics.sha256_hex
        );
    }

    println!("\n[2/2] Rust Engine: Running Clean 128 MB Multi-Segment Throughput Benchmark (8 Connections)...");
    let bench_file = format!("{}/bench_rust.bin", work_dir);
    let _ = fs::remove_file(&bench_file);

    let (bench_metrics, _) = run_download(
        &client,
        url,
        &bench_file,
        &EngineOptions { max_connections: 8, ..Default::default() },
        None,
    )
    .await?;

    if !expected_sha.is_empty() {
        println!(
            "  -> SHA-256 Integrity Match: {}",
            bench_metrics.sha256_hex.eq_ignore_ascii_case(expected_sha)
        );
    }
    println!("{}", serde_json::to_string_pretty(&bench_metrics)?);

    Ok(())
}
