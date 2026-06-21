import argparse
import os
import sqlite3
from datetime import datetime, timedelta, timezone


def default_agent_root() -> str:
    d_root = r"D:\WUJI\WindowsAgent"
    if os.path.isdir(d_root):
        return d_root

    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise RuntimeError("LOCALAPPDATA is not set; pass --agent-root explicitly.")

    return os.path.join(local_app_data, "WUJI", "WindowsAgent")


def parse_utc(value: str) -> datetime:
    normalized = value.replace("Z", "+00:00")
    parsed = datetime.fromisoformat(normalized)
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def scalar(connection: sqlite3.Connection, sql: str, args: tuple = ()) -> int | str | None:
    row = connection.execute(sql, args).fetchone()
    return None if row is None else row[0]


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Check whether agent_events volume stays lower than foreground_samples in a 30 minute window."
    )
    parser.add_argument("--agent-root", default=default_agent_root())
    parser.add_argument("--end-utc", help="Window end in UTC ISO-8601. Defaults to latest foreground sample time.")
    parser.add_argument("--minutes", type=int, default=30)
    args = parser.parse_args()

    database_path = os.path.join(args.agent_root, "data", "quantified_self_windows.db")
    if not os.path.exists(database_path):
        raise FileNotFoundError(database_path)

    with sqlite3.connect(f"file:{database_path}?mode=ro", uri=True) as connection:
        latest_sample_time = scalar(connection, "SELECT MAX(sample_time_utc) FROM foreground_samples;")
        if latest_sample_time is None:
            print(f"database={database_path}")
            print("foreground_samples is empty; cannot evaluate a 30 minute normal-run window.")
            return 2

        end_utc = parse_utc(args.end_utc) if args.end_utc else parse_utc(str(latest_sample_time))
        start_utc = end_utc - timedelta(minutes=args.minutes)
        start_text = start_utc.isoformat().replace("+00:00", "Z")
        end_text = end_utc.isoformat().replace("+00:00", "Z")

        foreground_samples = scalar(
            connection,
            """
            SELECT COUNT(*)
            FROM foreground_samples
            WHERE sample_time_utc >= ?
              AND sample_time_utc <= ?;
            """,
            (start_text, end_text),
        )
        agent_events = scalar(
            connection,
            """
            SELECT COUNT(*)
            FROM agent_events
            WHERE event_time_utc >= ?
              AND event_time_utc <= ?;
            """,
            (start_text, end_text),
        )
        noisy_events = scalar(
            connection,
            """
            SELECT COUNT(*)
            FROM agent_events
            WHERE event_time_utc >= ?
              AND event_time_utc <= ?
              AND event_type IN ('SampleCaptured', 'Heartbeat');
            """,
            (start_text, end_text),
        )

        event_types = connection.execute(
            """
            SELECT event_type, COUNT(*) AS count
            FROM agent_events
            WHERE event_time_utc >= ?
              AND event_time_utc <= ?
            GROUP BY event_type
            ORDER BY count DESC, event_type ASC;
            """,
            (start_text, end_text),
        ).fetchall()

    ratio = None
    if foreground_samples:
        ratio = agent_events / foreground_samples

    print(f"database={database_path}")
    print(f"window_start_utc={start_text}")
    print(f"window_end_utc={end_text}")
    print(f"window_minutes={args.minutes}")
    print(f"foreground_samples={foreground_samples}")
    print(f"agent_events={agent_events}")
    print(f"samplecaptured_or_heartbeat_events={noisy_events}")
    if ratio is not None:
        print(f"agent_events_to_foreground_samples_ratio={ratio:.3f}")

    print("event_type_counts:")
    if event_types:
        for event_type, count in event_types:
            print(f"  {event_type}: {count}")
    else:
        print("  <none>")

    print("pass_no_high_frequency_event_types=" + str(noisy_events == 0).lower())
    print("pass_agent_events_less_than_foreground_samples=" + str(agent_events < foreground_samples).lower())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
