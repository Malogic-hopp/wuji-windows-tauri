-- WUJI Rebuild v0.1 SQLite contract.
-- Authority: 09-Tauri-Rust-Rebuild-v0.1实施基线.md
-- This schema creates a new dev-only database. It is not a migration script.

PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 750;
PRAGMA wal_autocheckpoint = 1000;

CREATE TABLE schema_meta (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
    schema_version INTEGER NOT NULL CHECK (schema_version = 1),
    algorithm_version TEXT NOT NULL CHECK (length(algorithm_version) > 0),
    created_at_utc_ms INTEGER NOT NULL CHECK (created_at_utc_ms >= 0),
    reporting_time_zone_id TEXT NOT NULL CHECK (length(reporting_time_zone_id) > 0)
) STRICT;

CREATE TABLE settings_revisions (
    revision INTEGER PRIMARY KEY CHECK (revision >= 0),
    -- digest 是内容指纹，用于一致性核对，不是身份：改回历史设置值会复用旧 digest，
    -- 因此不得对 content_digest 加 UNIQUE（09 §9.1）。
    content_digest TEXT NOT NULL CHECK (length(content_digest) = 64),
    applied_at_utc_ms INTEGER NOT NULL CHECK (applied_at_utc_ms >= 0)
) STRICT;

CREATE TABLE app_identities (
    app_id INTEGER PRIMARY KEY,
    app_key TEXT NOT NULL UNIQUE CHECK (length(app_key) BETWEEN 1 AND 128),
    display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 256),
    normalized_process_name TEXT NOT NULL UNIQUE
        CHECK (length(normalized_process_name) BETWEEN 1 AND 260),
    first_seen_at_utc_ms INTEGER NOT NULL CHECK (first_seen_at_utc_ms >= 0),
    last_seen_at_utc_ms INTEGER NOT NULL CHECK (last_seen_at_utc_ms >= first_seen_at_utc_ms)
) STRICT;

CREATE TABLE agent_runtime (
    runtime_id TEXT PRIMARY KEY CHECK (length(runtime_id) = 26),
    process_state TEXT NOT NULL
        CHECK (process_state IN ('starting', 'running', 'degraded', 'faulted', 'shutting_down', 'stopped')),
    capture_state TEXT NOT NULL
        CHECK (capture_state IN ('stopped', 'running', 'paused')),
    writer_state TEXT NOT NULL
        CHECK (writer_state IN ('healthy', 'degraded', 'faulted')),
    started_at_utc_ms INTEGER NOT NULL CHECK (started_at_utc_ms >= 0),
    ended_at_utc_ms INTEGER CHECK (ended_at_utc_ms IS NULL OR ended_at_utc_ms >= 0),
    heartbeat_at_utc_ms INTEGER NOT NULL CHECK (heartbeat_at_utc_ms >= 0),
    last_observation_at_utc_ms INTEGER CHECK (last_observation_at_utc_ms IS NULL OR last_observation_at_utc_ms >= 0),
    last_write_at_utc_ms INTEGER CHECK (last_write_at_utc_ms IS NULL OR last_write_at_utc_ms >= 0),
    capture_queue_depth INTEGER NOT NULL DEFAULT 0 CHECK (capture_queue_depth >= 0),
    writer_queue_depth INTEGER NOT NULL DEFAULT 0 CHECK (writer_queue_depth >= 0),
    dropped_capture_count INTEGER NOT NULL DEFAULT 0 CHECK (dropped_capture_count >= 0),
    dropped_writer_count INTEGER NOT NULL DEFAULT 0 CHECK (dropped_writer_count >= 0),
    continuity_epoch INTEGER NOT NULL DEFAULT 0 CHECK (continuity_epoch >= 0),
    safe_error_code TEXT
) STRICT;

CREATE TABLE foreground_observations (
    observation_id INTEGER PRIMARY KEY,
    runtime_id TEXT NOT NULL REFERENCES agent_runtime(runtime_id),
    capture_sequence INTEGER NOT NULL CHECK (capture_sequence >= 0),
    continuity_epoch INTEGER NOT NULL CHECK (continuity_epoch >= 0),
    captured_at_utc_ms INTEGER NOT NULL CHECK (captured_at_utc_ms >= 0),
    captured_monotonic_ms INTEGER NOT NULL CHECK (captured_monotonic_ms >= 0),
    app_id INTEGER NOT NULL REFERENCES app_identities(app_id),
    activity_state TEXT NOT NULL CHECK (activity_state IN ('active', 'idle', 'unknown')),
    quality TEXT NOT NULL
        CHECK (quality IN ('normal', 'process_name_fallback', 'idle_unavailable')),
    settings_revision INTEGER NOT NULL REFERENCES settings_revisions(revision),
    UNIQUE (runtime_id, capture_sequence)
) STRICT;

CREATE INDEX ix_observations_captured
    ON foreground_observations(captured_at_utc_ms, observation_id);
CREATE INDEX ix_observations_app_captured
    ON foreground_observations(app_id, captured_at_utc_ms);

CREATE TABLE capture_gaps (
    gap_id INTEGER PRIMARY KEY,
    runtime_id TEXT NOT NULL REFERENCES agent_runtime(runtime_id),
    start_at_utc_ms INTEGER NOT NULL CHECK (start_at_utc_ms >= 0),
    end_at_utc_ms INTEGER CHECK (end_at_utc_ms IS NULL OR end_at_utc_ms >= start_at_utc_ms),
    kind TEXT NOT NULL CHECK (kind IN (
        'sampling_transition',
        'capture_delayed',
        'privacy_excluded',
        'capture_queue_drop',
        'writer_queue_drop',
        'capture_paused',
        'capture_stopped',
        'system_sleep',
        'session_locked',
        'agent_restart',
        'clock_changed',
        'capture_error'
    )),
    status TEXT NOT NULL CHECK (status IN ('open', 'closed')),
    event_count INTEGER NOT NULL DEFAULT 1 CHECK (event_count >= 1),
    CHECK (
        (status = 'open' AND end_at_utc_ms IS NULL) OR
        (status = 'closed' AND end_at_utc_ms IS NOT NULL)
    )
) STRICT;

CREATE INDEX ix_capture_gaps_range
    ON capture_gaps(start_at_utc_ms, end_at_utc_ms);
CREATE UNIQUE INDEX ux_capture_gaps_one_open
    ON capture_gaps(status) WHERE status = 'open';

CREATE TABLE activity_segments (
    segment_id INTEGER PRIMARY KEY,
    runtime_id TEXT NOT NULL REFERENCES agent_runtime(runtime_id),
    continuity_epoch INTEGER NOT NULL CHECK (continuity_epoch >= 0),
    app_id INTEGER NOT NULL REFERENCES app_identities(app_id),
    activity_state TEXT NOT NULL CHECK (activity_state IN ('active', 'idle', 'unknown')),
    start_at_utc_ms INTEGER NOT NULL CHECK (start_at_utc_ms >= 0),
    end_at_utc_ms INTEGER NOT NULL CHECK (end_at_utc_ms >= start_at_utc_ms),
    duration_ms INTEGER NOT NULL CHECK (duration_ms >= 0),
    first_observation_id INTEGER NOT NULL REFERENCES foreground_observations(observation_id),
    last_observation_id INTEGER NOT NULL REFERENCES foreground_observations(observation_id),
    status TEXT NOT NULL CHECK (status IN ('open', 'closed')),
    close_reason TEXT CHECK (close_reason IN (
        'app_changed',
        'state_changed',
        'capture_delayed',
        'capture_error',
        'privacy_excluded',
        'queue_drop',
        'capture_paused',
        'capture_stopped',
        'system_sleep',
        'session_locked',
        'agent_restart',
        'clock_changed',
        'agent_shutdown'
    )),
    CHECK (duration_ms = end_at_utc_ms - start_at_utc_ms),
    CHECK (
        (status = 'open' AND close_reason IS NULL) OR
        (status = 'closed' AND close_reason IS NOT NULL)
    )
) STRICT;

CREATE INDEX ix_activity_segments_range
    ON activity_segments(start_at_utc_ms, end_at_utc_ms);
CREATE INDEX ix_activity_segments_app_range
    ON activity_segments(app_id, start_at_utc_ms, end_at_utc_ms);
CREATE UNIQUE INDEX ux_activity_segments_one_open
    ON activity_segments(status) WHERE status = 'open';

CREATE TABLE work_blocks (
    work_block_id INTEGER PRIMARY KEY,
    runtime_id TEXT NOT NULL REFERENCES agent_runtime(runtime_id),
    start_at_utc_ms INTEGER NOT NULL CHECK (start_at_utc_ms >= 0),
    end_at_utc_ms INTEGER NOT NULL CHECK (end_at_utc_ms >= start_at_utc_ms),
    active_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (active_duration_ms >= 0),
    short_idle_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (short_idle_duration_ms >= 0),
    first_activity_segment_id INTEGER NOT NULL REFERENCES activity_segments(segment_id),
    last_activity_segment_id INTEGER NOT NULL REFERENCES activity_segments(segment_id),
    status TEXT NOT NULL CHECK (status IN ('open', 'closed')),
    close_reason TEXT CHECK (close_reason IN (
        'idle_break',
        'capture_delayed',
        'capture_error',
        'unknown',
        'privacy_excluded',
        'queue_drop',
        'capture_paused',
        'capture_stopped',
        'system_sleep',
        'session_locked',
        'agent_restart',
        'clock_changed',
        'agent_shutdown'
    )),
    CHECK (active_duration_ms + short_idle_duration_ms <= end_at_utc_ms - start_at_utc_ms),
    CHECK (
        (status = 'open' AND close_reason IS NULL) OR
        (status = 'closed' AND close_reason IS NOT NULL)
    )
) STRICT;

CREATE INDEX ix_work_blocks_range
    ON work_blocks(start_at_utc_ms, end_at_utc_ms);
CREATE UNIQUE INDEX ux_work_blocks_one_open
    ON work_blocks(status) WHERE status = 'open';

CREATE TABLE hourly_app_usage (
    utc_hour_start_ms INTEGER NOT NULL CHECK (utc_hour_start_ms >= 0),
    local_date TEXT NOT NULL CHECK (local_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
    local_hour INTEGER NOT NULL CHECK (local_hour BETWEEN 0 AND 23),
    local_utc_offset_minutes INTEGER NOT NULL CHECK (local_utc_offset_minutes BETWEEN -840 AND 840),
    app_id INTEGER NOT NULL REFERENCES app_identities(app_id),
    active_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (active_duration_ms >= 0),
    idle_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (idle_duration_ms >= 0),
    unknown_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (unknown_duration_ms >= 0),
    segment_count INTEGER NOT NULL DEFAULT 0 CHECK (segment_count >= 0),
    PRIMARY KEY (utc_hour_start_ms, app_id)
) STRICT, WITHOUT ROWID;

CREATE INDEX ix_hourly_app_usage_local
    ON hourly_app_usage(local_date, local_hour, local_utc_offset_minutes);

CREATE TABLE daily_app_usage (
    local_date TEXT NOT NULL CHECK (local_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
    app_id INTEGER NOT NULL REFERENCES app_identities(app_id),
    active_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (active_duration_ms >= 0),
    idle_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (idle_duration_ms >= 0),
    unknown_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (unknown_duration_ms >= 0),
    segment_count INTEGER NOT NULL DEFAULT 0 CHECK (segment_count >= 0),
    PRIMARY KEY (local_date, app_id)
) STRICT, WITHOUT ROWID;

CREATE TABLE daily_work_metrics (
    local_date TEXT PRIMARY KEY
        CHECK (local_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
    active_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (active_duration_ms >= 0),
    short_idle_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (short_idle_duration_ms >= 0),
    work_block_count INTEGER NOT NULL DEFAULT 0 CHECK (work_block_count >= 0),
    longest_work_block_active_ms INTEGER NOT NULL DEFAULT 0
        CHECK (longest_work_block_active_ms >= 0),
    raw_app_switch_count INTEGER NOT NULL DEFAULT 0 CHECK (raw_app_switch_count >= 0),
    data_gap_count INTEGER NOT NULL DEFAULT 0 CHECK (data_gap_count >= 0)
) STRICT, WITHOUT ROWID;
