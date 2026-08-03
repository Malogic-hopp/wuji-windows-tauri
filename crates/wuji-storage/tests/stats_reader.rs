//! 统计主页 Reader 投影原语测试（11 实施方案阶段二）。
//!
//! 种子路径与生产一致：`bootstrap_with_timezone` → segment/work block/gap →
//! `recompute_hours`/`recompute_dates`；所有读者原语经 `Reader::with_snapshot`
//!（单一读事务快照）调用。

use std::path::PathBuf;

use tempfile::TempDir;
use wuji_core::domain::{ActivityState, CaptureQuality, GapKind};
use wuji_core::dto::{LocalDate, RuntimeId};
use wuji_core::error::SafeErrorCode;
use wuji_storage::writer::StorageTransaction;
use wuji_storage::{ObservationInsert, Reader, StorageError, Writer};

/// 2026-07-18T00:00:00Z（毫秒）。
const T0: i64 = 1_784_332_800_000;
const SHANGHAI: &str = "Asia/Shanghai";
const GAP_CAP_MS: i64 = 15_000;

fn db_path(dir: &TempDir) -> PathBuf {
    dir.path().join("wuji-rebuild-v0.1.db")
}

fn bootstrap(dir: &TempDir) -> Writer {
    Writer::bootstrap_with_timezone(&db_path(dir), SHANGHAI, T0).expect("bootstrap 应成功")
}

fn local(s: &str) -> LocalDate {
    LocalDate::parse(s).unwrap()
}

fn seed_app(tx: &StorageTransaction<'_>, name: &str, seen_at: i64) -> i64 {
    let hash = name.bytes().fold(0xcbf29ce484222325_u64, |h, b| {
        (h ^ u64::from(b)).wrapping_mul(0x100000001b3)
    });
    let app_key = format!("proc:{hash:064x}");
    tx.upsert_app_identity(&app_key, name, &format!("{name}.exe"), seen_at)
        .expect("app upsert")
}

fn insert_obs(
    tx: &StorageTransaction<'_>,
    runtime: &RuntimeId,
    seq: i64,
    at: i64,
    app: i64,
) -> i64 {
    match tx.insert_observation(
        runtime,
        seq,
        0,
        at,
        at, // monotonic 只需非负且随 at 递增（schema CHECK >= 0；跨 T0 前日期也合法）
        app,
        ActivityState::Active,
        CaptureQuality::Normal,
        0,
    ) {
        Ok(ObservationInsert::Inserted(id)) => id,
        other => panic!("observation 插入失败: {other:?}"),
    }
}

/// 种子构造器：持有事务、runtime 与自增 observation 序号。
struct Seed<'a, 'w> {
    tx: &'a StorageTransaction<'w>,
    runtime: RuntimeId,
    seq: i64,
}

impl Seed<'_, '_> {
    fn obs(&mut self, at: i64, app: i64) -> i64 {
        self.seq += 1;
        insert_obs(self.tx, &self.runtime, self.seq, at, app)
    }

    /// 插入一条 closed active segment `[start, end]`（两个观测点，时长 end-start）并
    /// 包裹一个 work block；`close_block = false` 时保持 work block 为 open（未闭合，
    /// 仅限最后一条）。
    fn session(&mut self, app: i64, start: i64, end: i64, close_block: bool) {
        let obs1 = self.obs(start, app);
        let obs2 = self.obs(end, app);
        let seg = self
            .tx
            .open_segment(&self.runtime, 0, app, ActivityState::Active, start, obs1)
            .expect("open segment");
        self.tx
            .update_open_segment(seg, end, obs2)
            .expect("update segment");
        self.tx
            .close_open_segment("capture_stopped")
            .expect("close segment");
        let wb = self
            .tx
            .open_work_block(&self.runtime, start, seg)
            .expect("open work block");
        self.tx
            .update_open_work_block(wb, end, end - start, 0, seg)
            .expect("update work block");
        if close_block {
            self.tx
                .close_open_work_block("capture_stopped")
                .expect("close work block");
        }
    }

    /// 两条 closed active segment 共用一个 work block（跨午夜/跨 cutoff 用；
    /// block 覆盖 `[seg1.0, seg2.1]`）。
    fn block_with_two_segments(&mut self, app: i64, seg1: (i64, i64), seg2: (i64, i64)) {
        let (a0, a1) = seg1;
        let (b0, b1) = seg2;
        let o1 = self.obs(a0, app);
        let o2 = self.obs(a1, app);
        let s1 = self
            .tx
            .open_segment(&self.runtime, 0, app, ActivityState::Active, a0, o1)
            .expect("open seg1");
        self.tx
            .update_open_segment(s1, a1, o2)
            .expect("update seg1");
        self.tx
            .close_open_segment("capture_stopped")
            .expect("close seg1");
        let o3 = self.obs(b0, app);
        let o4 = self.obs(b1, app);
        let s2 = self
            .tx
            .open_segment(&self.runtime, 0, app, ActivityState::Active, b0, o3)
            .expect("open seg2");
        self.tx
            .update_open_segment(s2, b1, o4)
            .expect("update seg2");
        self.tx
            .close_open_segment("capture_stopped")
            .expect("close seg2");
        let wb = self
            .tx
            .open_work_block(&self.runtime, a0, s1)
            .expect("open block");
        self.tx
            .update_open_work_block(wb, b1, b1 - a0, 0, s2)
            .expect("update block");
        self.tx
            .close_open_work_block("capture_stopped")
            .expect("close block");
    }

    /// 插入一条 closed capture_paused gap（无 segment）：用于构造"有 daily_work_metrics
    /// 行但 hourly_app_usage 无行"的有效日（阶段零 P0-4 回归）。
    fn gap_only(&mut self, start: i64, end: i64) {
        self.tx
            .open_gap(&self.runtime, GapKind::CapturePaused, start)
            .expect("open gap");
        self.tx.close_open_gap(end).expect("close gap");
    }
}

/// 会话覆盖的 UTC 小时桶起点集合（recompute_hours 输入）。
fn hour_starts_between(start: i64, end: i64) -> Vec<i64> {
    let first = start.div_euclid(3_600_000) * 3_600_000;
    let last = (end - 1).div_euclid(3_600_000) * 3_600_000;
    (0..=((last - first) / 3_600_000))
        .map(|i| first + i * 3_600_000)
        .collect()
}

fn open_reader(dir: &TempDir) -> Reader {
    Reader::open(&db_path(dir)).expect("reader 打开应成功")
}

/// 建库 + 注册 runtime + 返回 Seed（复用同一 runtime 满足 FK）。
fn begin_seed(writer: &mut Writer) -> (StorageTransaction<'_>, RuntimeId) {
    let tx = writer.transaction().unwrap();
    let runtime = RuntimeId::new();
    tx.insert_runtime(&runtime, T0).unwrap();
    (tx, runtime)
}

// ---- stats_daily_rows：日期骨架 ----

#[test]
fn stats_daily_rows_builds_full_date_skeleton() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let (tx, runtime) = begin_seed(&mut writer);
    let app = seed_app(&tx, "code", T0);
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    // 07-15 01:00-02:00Z（09:00-10:00 本地）、07-17 03:00-04:00Z（11:00-12:00 本地）；
    // 07-14/16/18 无记录。
    seed.session(
        app,
        T0 - 3 * 86_400_000 + 3_600_000,
        T0 - 3 * 86_400_000 + 7_200_000,
        true,
    );
    seed.session(
        app,
        T0 - 86_400_000 + 10_800_000,
        T0 - 86_400_000 + 14_400_000,
        true,
    );
    tx.recompute_dates(
        &tz,
        &[
            local("2026-07-15"),
            local("2026-07-16"),
            local("2026-07-17"),
        ],
        GAP_CAP_MS,
    )
    .unwrap();
    tx.commit().unwrap();

    let mut reader = open_reader(&dir);
    let rows = reader
        .with_snapshot(|snap| snap.stats_daily_rows(&local("2026-07-14"), &local("2026-07-18")))
        .unwrap();
    assert_eq!(rows.len(), 5, "骨架长度恒等 5");
    let dates: Vec<&str> = rows.iter().map(|r| r.local_date.as_str()).collect();
    assert_eq!(
        dates,
        [
            "2026-07-14",
            "2026-07-15",
            "2026-07-16",
            "2026-07-17",
            "2026-07-18"
        ]
    );
    assert!(!rows[0].has_data);
    assert_eq!(
        (rows[0].active_duration_ms, rows[0].work_block_count),
        (0, 0)
    );
    assert!(rows[1].has_data);
    assert_eq!(
        (rows[1].active_duration_ms, rows[1].work_block_count),
        (3_600_000, 1)
    );
    assert!(!rows[2].has_data);
    assert!(rows[3].has_data);
    assert_eq!(
        (rows[3].active_duration_ms, rows[3].work_block_count),
        (3_600_000, 1)
    );
    assert!(!rows[4].has_data);
    // 倒置范围显式报错。
    let err = reader
        .with_snapshot(|snap| snap.stats_daily_rows(&local("2026-07-18"), &local("2026-07-14")))
        .unwrap_err();
    assert_eq!(err.code, SafeErrorCode::InvalidArgument);
    // DoD：骨架长度恒等 7/14/30（不依赖数据库是否存在缺行日期）。
    for (days, start) in [(7, "2026-07-12"), (14, "2026-07-05"), (30, "2026-06-19")] {
        let rows = reader
            .with_snapshot(|snap| snap.stats_daily_rows(&local(start), &local("2026-07-18")))
            .unwrap();
        assert_eq!(rows.len(), days as usize, "骨架长度恒等 {days}");
        // 记录日 07-15/07-17 存在，其余 has_data=false。
        let recorded: Vec<&str> = rows
            .iter()
            .filter(|r| r.has_data)
            .map(|r| r.local_date.as_str())
            .collect();
        assert_eq!(recorded, vec!["2026-07-15", "2026-07-17"]);
    }
    // 防御护栏：范围超过 366 天拒绝（与 heatmap ≤31 天同类护栏）。
    let err = reader
        .with_snapshot(|snap| snap.stats_daily_rows(&local("2025-01-01"), &local("2026-07-18")))
        .unwrap_err();
    assert_eq!(err.code, SafeErrorCode::InvalidArgument);
}

// ---- recent_recorded_dates：前向寻日，非固定 lookback ----

#[test]
fn recent_recorded_dates_searches_forward_not_fixed_lookback() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let (tx, runtime) = begin_seed(&mut writer);
    let app = seed_app(&tx, "code", T0);
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    // 近两周只记录了 4 天：07-14、07-16、07-17、07-20（offset 为 4、2、1、-2 天）。
    for offset in [4_i64, 2, 1, -2] {
        let day_start = T0 - offset * 86_400_000;
        seed.session(app, day_start + 3_600_000, day_start + 7_200_000, true);
    }
    let recorded: Vec<LocalDate> = ["2026-07-14", "2026-07-16", "2026-07-17", "2026-07-20"]
        .iter()
        .map(|d| local(d))
        .collect();
    tx.recompute_dates(&tz, &recorded, GAP_CAP_MS).unwrap();
    tx.commit().unwrap();

    let mut reader = open_reader(&dir);
    // DoD：近两周只有 4 天 → 返回 4 个而非补足 7。
    let got = reader
        .with_snapshot(|snap| snap.recent_recorded_dates(&local("2026-07-21"), 7))
        .unwrap();
    let got_dates: Vec<String> = got.iter().map(|d| d.as_str().to_string()).collect();
    assert_eq!(
        got_dates,
        ["2026-07-14", "2026-07-16", "2026-07-17", "2026-07-20"]
    );
    // 不是固定 lookback：历史只记录 3 天就返回 3 个。
    let got = reader
        .with_snapshot(|snap| snap.recent_recorded_dates(&local("2026-07-19"), 7))
        .unwrap();
    assert_eq!(got.len(), 3);
    // limit 生效。
    let got = reader
        .with_snapshot(|snap| snap.recent_recorded_dates(&local("2026-07-21"), 1))
        .unwrap();
    assert_eq!(got, vec![local("2026-07-20")]);
    // 严格早于 before。
    let got = reader
        .with_snapshot(|snap| snap.recent_recorded_dates(&local("2026-07-14"), 7))
        .unwrap();
    assert!(got.is_empty());
}

// ---- stats_cutoff_series：LEFT JOIN 零活动日 + 未闭合块 ----

#[test]
fn cutoff_series_cross_midnight_block_not_offset_by_previous_day_segment() {
    // P1 回归：跨午夜工作块的"前一日 Segment"不得抵消今日 Segment。
    // 上海 block [07-17T15:00Z, 07-18T01:00Z]（07-17 23:00 本地 → 07-18 09:00 本地）：
    // seg1 07-17T15:00-15:30Z（完全早于今日 day_start 07-17T16:00Z）、
    // seg2 07-18T00:30-01:00Z（08:30-09:00 本地，cutoff 内）。
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let (tx, runtime) = begin_seed(&mut writer);
    let app = seed_app(&tx, "code", T0);
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    let d17 = T0 - 86_400_000;
    seed.block_with_two_segments(
        app,
        (d17 + 15 * 3_600_000, d17 + 15 * 3_600_000 + 1_800_000),
        (T0 + 30 * 60_000, T0 + 3_600_000),
    );
    tx.commit().unwrap();

    // now = 2026-07-18T06:00:00Z（本地 14:00）；today = 07-18。
    let now = T0 + 6 * 3_600_000;
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let mut reader = open_reader(&dir);
    let rows = reader
        .with_snapshot(|snap| {
            snap.stats_cutoff_series(&tz, &local("2026-07-18"), now, &[local("2026-07-18")])
        })
        .unwrap();
    // seg1（完全早于 day_start）不得产生负贡献抵消 seg2 → 恰好 seg2 的 30 分钟。
    assert_eq!(rows[0].active_duration_ms, 1_800_000);
    assert_eq!(rows[0].work_block_count, 1);
}

#[test]
fn cutoff_series_post_cutoff_segment_does_not_offset_pre_cutoff() {
    // P1 回归：历史日 cutoff 之后的 Segment 不得抵消 cutoff 之前的 Segment。
    // 07-17 block [03:00Z, 07:30Z]：seg1 03:00-03:30Z（11:00-11:30 本地，cutoff 前）、
    // seg2 07:00-07:30Z（15:00-15:30 本地，cutoff 06:00Z 之后）。
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let (tx, runtime) = begin_seed(&mut writer);
    let app = seed_app(&tx, "code", T0);
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    let d17 = T0 - 86_400_000;
    seed.block_with_two_segments(
        app,
        (d17 + 10_800_000, d17 + 12_600_000),
        (d17 + 25_200_000, d17 + 27_000_000),
    );
    tx.commit().unwrap();

    let now = T0 + 6 * 3_600_000; // 本地 14:00；昨日同时刻 cutoff = 06:00Z
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let mut reader = open_reader(&dir);
    let rows = reader
        .with_snapshot(|snap| {
            snap.stats_cutoff_series(&tz, &local("2026-07-18"), now, &[local("2026-07-17")])
        })
        .unwrap();
    // seg2（cutoff 后）不得产生负贡献抵消 seg1 → 恰好 seg1 的 30 分钟。
    assert_eq!(rows[0].active_duration_ms, 1_800_000);
    assert_eq!(rows[0].work_block_count, 1);
}

#[test]
fn cutoff_series_duplicate_dates_not_doubled_or_zeroed() {
    // P1 回归：输入含重复日期（阶段三组装"昨日 + 近 7 有效日"自然产生）时，
    // CTE 必须按首次出现去重查询、按原始输入映射——不翻倍、不归零。
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let (tx, runtime) = begin_seed(&mut writer);
    let app = seed_app(&tx, "code", T0);
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    // 07-17 11:00-11:30 本地（03:00-03:30Z，cutoff 前）。
    let d17 = T0 - 86_400_000;
    seed.session(app, d17 + 10_800_000, d17 + 12_600_000, true);
    tx.commit().unwrap();

    let now = T0 + 6 * 3_600_000; // 本地 14:00；昨日同时刻 cutoff = 06:00Z
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let mut reader = open_reader(&dir);
    // 昨日（07-17）在输入中出现两次。
    let dates = [local("2026-07-17"), local("2026-07-17")];
    let rows = reader
        .with_snapshot(|snap| snap.stats_cutoff_series(&tz, &local("2026-07-18"), now, &dates))
        .unwrap();
    assert_eq!(rows.len(), 2);
    // 两条结果都是同一正确值：不翻倍（各 30 分钟）、不归零、顺序保持输入。
    for row in &rows {
        assert_eq!(row.local_date, "2026-07-17");
        assert_eq!(row.active_duration_ms, 1_800_000);
        assert_eq!(row.work_block_count, 1);
    }
}

#[test]
fn recompute_dates_cross_midnight_block_counts_in_day_portion() {
    // P1 回归（recompute.rs 与 stats_cutoff_series 同一口径）：跨午夜工作块按日拆分
    // 计数——前一日 Segment 不得抵消今日 Segment（seed 同 cutoff 跨午夜用例）。
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let (tx, runtime) = begin_seed(&mut writer);
    let app = seed_app(&tx, "code", T0);
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    let d17 = T0 - 86_400_000;
    seed.block_with_two_segments(
        app,
        (d17 + 15 * 3_600_000, d17 + 15 * 3_600_000 + 1_800_000),
        (T0 + 30 * 60_000, T0 + 3_600_000),
    );
    tx.recompute_dates(&tz, &[local("2026-07-18")], GAP_CAP_MS)
        .unwrap();
    tx.commit().unwrap();

    let reader = open_reader(&dir);
    let today = reader.today(&local("2026-07-18")).expect("today 查询");
    assert_eq!(today.work_block_count.0, 1);
    assert_eq!(today.active_duration_ms.0, 1_800_000);
}

#[test]
fn cutoff_series_zero_when_all_activity_after_cutoff() {
    // DoD 点名的两种情景：
    // 1) 昨日有有效日期但同期无活动——昨日活动全部在同时刻 cutoff 之后；
    // 2) 今日刚开始——now 早于今日首段活动。
    // 两者都应返回 0（LEFT JOIN 相交条件不满足 → COALESCE 0），而非"无基线"缺失。
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let (tx, runtime) = begin_seed(&mut writer);
    let app = seed_app(&tx, "code", T0);
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    // 07-17 15:00-16:00 本地（07:00-08:00Z）——晚于昨日同时刻 cutoff（08:00 本地）。
    let d17 = T0 - 86_400_000;
    seed.session(app, d17 + 25_200_000, d17 + 28_800_000, true);
    // 07-18 09:00-10:00 本地（01:00-02:00Z）——晚于 now（今日刚开始）。
    seed.session(app, T0 + 3_600_000, T0 + 7_200_000, true);
    tx.commit().unwrap();

    // now = 2026-07-18T00:00:00Z = 本地 08:00（今日任何活动之前）。
    let now = T0;
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let mut reader = open_reader(&dir);
    let rows = reader
        .with_snapshot(|snap| {
            snap.stats_cutoff_series(
                &tz,
                &local("2026-07-18"),
                now,
                &[local("2026-07-18"), local("2026-07-17")],
            )
        })
        .unwrap();
    // 今日：cutoff = now = 00:00Z；今日段 01:00Z 起 → 尚未开始 → 0。
    assert_eq!(rows[0].local_date, "2026-07-18");
    assert_eq!(rows[0].active_duration_ms, 0);
    assert_eq!(rows[0].work_block_count, 0);
    // 昨日：同时刻 cutoff = 00:00Z（本地 08:00）；昨日段 07:00Z 起 → 同期无活动 → 0。
    assert_eq!(rows[1].local_date, "2026-07-17");
    assert_eq!(rows[1].active_duration_ms, 0);
    assert_eq!(rows[1].work_block_count, 0);
}

#[test]
fn cutoff_series_left_join_zero_active_and_open_block_count() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let (tx, runtime) = begin_seed(&mut writer);
    let app = seed_app(&tx, "code", T0);
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    // 07-17 11:00-14:30 本地（closed block）；07-18 09:00-10:00 本地（open block 未闭合）；
    // 07-16 无任何 segment。
    let d17 = T0 - 86_400_000;
    seed.session(app, d17 + 10_800_000, d17 + 23_400_000, true);
    seed.session(app, T0 + 3_600_000, T0 + 7_200_000, false);
    tx.commit().unwrap();

    // now = 2026-07-18T06:00:00Z = 本地 14:00；today = 07-18。
    let now = T0 + 6 * 3_600_000;
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let mut reader = open_reader(&dir);
    let rows = reader
        .with_snapshot(|snap| {
            snap.stats_cutoff_series(
                &tz,
                &local("2026-07-18"),
                now,
                &[
                    local("2026-07-18"),
                    local("2026-07-17"),
                    local("2026-07-16"),
                ],
            )
        })
        .unwrap();
    assert_eq!(rows.len(), 3, "按输入日期补齐，顺序确定");
    assert_eq!(rows[0].local_date, "2026-07-18");
    // 今日：cutoff = now；segment 09:00-10:00 全量计入；open 块未闭合也计数。
    assert_eq!(rows[0].active_duration_ms, 3_600_000);
    assert_eq!(rows[0].work_block_count, 1);
    // 昨日：同一墙钟 14:00 截断 → 11:00-14:00 = 3h。
    assert_eq!(rows[1].local_date, "2026-07-17");
    assert_eq!(rows[1].active_duration_ms, 10_800_000);
    assert_eq!(rows[1].work_block_count, 1);
    // 零活动日：LEFT JOIN 下返回 0 而非缺失。
    assert_eq!(rows[2].local_date, "2026-07-16");
    assert_eq!(rows[2].active_duration_ms, 0);
    assert_eq!(rows[2].work_block_count, 0);
    // 空输入返回空。
    let empty = reader
        .with_snapshot(|snap| snap.stats_cutoff_series(&tz, &local("2026-07-18"), now, &[]))
        .unwrap();
    assert!(empty.is_empty());
}

// ---- stats_hourly_profile：有效日来自 daily_work_metrics（P0-4 回归）----

#[test]
fn hourly_profile_counts_gap_only_day_in_denominator() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let (tx, runtime) = begin_seed(&mut writer);
    let app_a = seed_app(&tx, "code", T0);
    let app_b = seed_app(&tx, "notepad", T0);
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    // 07-15：app A 01:00-02:00Z（09:00-10:00 本地）。
    let d15 = T0 - 3 * 86_400_000;
    seed.session(app_a, d15 + 3_600_000, d15 + 7_200_000, true);
    // 07-16：仅一条 gap，无 segment（有 daily_work_metrics 行，但 hourly 无行）。
    let d16 = T0 - 2 * 86_400_000;
    seed.gap_only(d16, d16 + 1_800_000);
    // 07-17：app A 03:00-04:00Z（11:00-12:00 本地）+ app B 03:30-04:00Z（30 分钟）。
    let d17 = T0 - 86_400_000;
    seed.session(app_a, d17 + 10_800_000, d17 + 14_400_000, true);
    seed.session(app_b, d17 + 12_600_000, d17 + 14_400_000, true);

    let mut hours = hour_starts_between(d15 + 3_600_000, d15 + 7_200_000);
    hours.extend(hour_starts_between(d17 + 10_800_000, d17 + 14_400_000));
    tx.recompute_hours(&tz, &hours).unwrap();
    tx.recompute_dates(
        &tz,
        &[
            local("2026-07-15"),
            local("2026-07-16"),
            local("2026-07-17"),
        ],
        GAP_CAP_MS,
    )
    .unwrap();
    tx.commit().unwrap();

    let mut reader = open_reader(&dir);
    let (profile, effective_days) = reader
        .with_snapshot(|snap| snap.stats_hourly_profile(&local("2026-07-15"), &local("2026-07-17")))
        .unwrap();
    // 有效日 = 3：gap-only 日计入分母（P0-4），即使 hourly 无行。
    assert_eq!(effective_days, 3);
    // 09 点：仅 07-15 有 3600s → 3600/3 = 1200s。
    assert_eq!(profile[9], 1_200_000);
    // 11 点：07-17 A 3600s + B 1800s = 5400s → /3 = 1800s。
    assert_eq!(profile[11], 1_800_000);
    // 其余小时为 0（每个有效日补齐 24 小时）。
    let nonzero: Vec<usize> = profile
        .iter()
        .enumerate()
        .filter(|(_, v)| **v > 0)
        .map(|(i, _)| i)
        .collect();
    assert_eq!(nonzero, vec![9, 11]);
}

// ---- stats_app_totals / stats_app_rows / stats_recorded_dates ----

#[test]
fn app_totals_and_rows_and_recorded_dates() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let (tx, runtime) = begin_seed(&mut writer);
    let app_a = seed_app(&tx, "code", T0);
    let app_b = seed_app(&tx, "notepad", T0);
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    // 两个 app 在两个记录日各有 1h active → 总量并列，验证 app_id tie-break。
    let d15 = T0 - 3 * 86_400_000;
    let d17 = T0 - 86_400_000;
    seed.session(app_a, d15 + 3_600_000, d15 + 7_200_000, true);
    seed.session(app_b, d15 + 5_400_000, d15 + 9_000_000, true);
    seed.session(app_a, d17 + 10_800_000, d17 + 14_400_000, true);
    seed.session(app_b, d17 + 12_600_000, d17 + 16_200_000, true);
    tx.recompute_dates(&tz, &[local("2026-07-15"), local("2026-07-17")], GAP_CAP_MS)
        .unwrap();
    tx.commit().unwrap();

    let mut reader = open_reader(&dir);
    let totals = reader
        .with_snapshot(|snap| snap.stats_app_totals(&local("2026-07-14"), &local("2026-07-18")))
        .unwrap();
    assert_eq!(totals.len(), 2);
    assert_eq!(totals[0].total_active_ms, 7_200_000);
    assert_eq!(totals[1].total_active_ms, 7_200_000);
    // tie-break：总量并列时按 app_id 升序（先 seed 的 app 更小）。
    assert_eq!(totals[0].display_name, "code");
    assert_eq!(totals[1].display_name, "notepad");

    let rows = reader
        .with_snapshot(|snap| snap.stats_app_rows(&local("2026-07-15"), &local("2026-07-15")))
        .unwrap();
    assert_eq!(rows.len(), 2);
    assert_eq!(rows[0].local_date, "2026-07-15");
    assert_eq!(rows[0].active_ms, 3_600_000);
    assert_eq!(rows[1].active_ms, 3_600_000);

    let dates = reader
        .with_snapshot(|snap| snap.stats_recorded_dates())
        .unwrap();
    assert_eq!(
        dates,
        vec!["2026-07-15".to_string(), "2026-07-17".to_string()]
    );
}

// ---- with_snapshot：快照契约与错误回滚 ----

#[test]
fn snapshot_contract_sequential_and_error_rollback() {
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let (tx, runtime) = begin_seed(&mut writer);
    let app = seed_app(&tx, "code", T0);
    let mut seed = Seed {
        tx: &tx,
        runtime,
        seq: 0,
    };
    seed.session(app, T0 + 3_600_000, T0 + 7_200_000, true);
    tx.recompute_dates(&tz, &[local("2026-07-18")], GAP_CAP_MS)
        .unwrap();
    tx.commit().unwrap();

    let mut reader = open_reader(&dir);
    // 连续两个快照均成功。
    let first = reader
        .with_snapshot(|snap| snap.stats_recorded_dates())
        .unwrap();
    assert_eq!(first, vec!["2026-07-18".to_string()]);
    let second = reader
        .with_snapshot(|snap| snap.recent_recorded_dates(&local("2026-07-19"), 7))
        .unwrap();
    assert_eq!(second.len(), 1);
    // 闭包返回 Err → 事务自动回滚，连接不中毒，后续快照正常。
    let err = reader
        .with_snapshot(|_snap| -> Result<(), StorageError> {
            Err(StorageError::new(
                SafeErrorCode::InvalidArgument,
                "模拟失败",
            ))
        })
        .unwrap_err();
    assert_eq!(err.code, SafeErrorCode::InvalidArgument);
    let after = reader
        .with_snapshot(|snap| snap.stats_recorded_dates())
        .unwrap();
    assert_eq!(after.len(), 1);
    // 快照内直接使用 Reader 方法在编译期被借用规则禁止；常规只读方法不受影响。
    let today = reader
        .today(&local("2026-07-18"))
        .expect("today 查询不受快照影响");
    assert_eq!(today.active_duration_ms.0, 3_600_000);
}

#[test]
fn snapshot_sees_single_consistent_view_across_writer_commit() {
    // DoD 快照契约：WAL 写并发下，同一读事务内的多个子查询必须读取同一快照——
    // 快照内先查、writer 提交新数据后再查，结果必须一致（提交对新快照可见，
    // 对已打开快照不可见）。
    let dir = TempDir::new().unwrap();
    let mut writer = bootstrap(&dir);
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let (tx, runtime) = begin_seed(&mut writer);
    let app = seed_app(&tx, "code", T0);
    let mut seed = Seed {
        tx: &tx,
        runtime: runtime.clone(),
        seq: 0,
    };
    seed.session(app, T0 + 3_600_000, T0 + 7_200_000, true);
    tx.recompute_dates(&tz, &[local("2026-07-18")], GAP_CAP_MS)
        .unwrap();
    tx.commit().unwrap();

    // 未提交的 writer 事务内种 07-19 会话（recompute 在提交时一并生效）；
    // 同一 runtime 下 observation 序号须继续上一事务（seed 已用 1、2）。
    let tx2 = writer.transaction().unwrap();
    let mut seed2 = Seed {
        tx: &tx2,
        runtime,
        seq: 2,
    };
    seed2.session(
        app,
        T0 + 86_400_000 + 3_600_000,
        T0 + 86_400_000 + 7_200_000,
        true,
    );

    let mut reader = open_reader(&dir);
    let (before, during) = reader
        .with_snapshot(|snap| {
            let before = snap.stats_recorded_dates().unwrap();
            // 快照已建立后，writer 提交 07-19 新数据。
            tx2.recompute_dates(&tz, &[local("2026-07-19")], GAP_CAP_MS)
                .unwrap();
            tx2.commit().unwrap();
            let during = snap.stats_recorded_dates().unwrap();
            Ok((before, during))
        })
        .unwrap();
    assert_eq!(before, vec!["2026-07-18".to_string()]);
    assert_eq!(during, before, "同一读事务内跨写入提交仍看到同一快照");
    // 快照结束后，新读事务看到提交后的数据。
    let after = reader
        .with_snapshot(|snap| snap.stats_recorded_dates())
        .unwrap();
    assert_eq!(
        after,
        vec!["2026-07-18".to_string(), "2026-07-19".to_string()]
    );
}

// ---- 截止点一致性：cutoff 系列覆盖输入全部日期且顺序确定 ----

#[test]
fn cutoff_series_returns_all_input_dates_in_order() {
    let dir = TempDir::new().unwrap();
    let writer = bootstrap(&dir);
    let tz = writer.schema_meta().reporting_tz().unwrap();
    let mut reader = open_reader(&dir);
    let now = T0 + 6 * 3_600_000;
    let dates = [local("2026-07-17"), local("2026-07-16")];
    let rows = reader
        .with_snapshot(|snap| snap.stats_cutoff_series(&tz, &local("2026-07-18"), now, &dates))
        .unwrap();
    assert_eq!(rows.len(), 2);
    for (row, date) in rows.iter().zip(dates.iter()) {
        assert_eq!(row.local_date, date.as_str());
        // 空库（无 segment）下 LEFT JOIN 返回 0 而非缺失，且顺序与输入一致。
        assert_eq!(row.active_duration_ms, 0);
        assert_eq!(row.work_block_count, 0);
    }
}
