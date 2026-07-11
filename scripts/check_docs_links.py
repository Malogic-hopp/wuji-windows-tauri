#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Check markdown links under docs/ for validity."""
import re
import sys
from pathlib import Path
from urllib.parse import urlparse, unquote

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs"

# Regex to match markdown links [text](url) and image links ![alt](url)
LINK_RE = re.compile(r"!?\[([^\]]*)\]\(([^)]+)\)")
# Regex for anchors in headings: any text followed by one or more {#anchor}
HEADING_ANCHOR_RE = re.compile(r"^#{1,6}\s+.*?\{#([\w\-]+)\}\s*$")
# Regex for auto-generated anchors from heading text (GitHub/GitLab style)
HEADING_TEXT_RE = re.compile(r"^#{1,6}\s+(.*)$")


def slugify(text: str) -> str:
    """GitHub-style heading slug."""
    s = text.strip().lower()
    s = re.sub(r"[^\w\s\-]", "", s)
    s = re.sub(r"\s+", "-", s)
    s = re.sub(r"-+", "-", s)
    return s.strip("-")


def collect_headings(md_path: Path) -> set[str]:
    anchors: set[str] = set()
    try:
        text = md_path.read_text(encoding="utf-8")
    except Exception as e:
        print(f"WARN: cannot read {md_path}: {e}")
        return anchors
    for line in text.splitlines():
        m = HEADING_ANCHOR_RE.match(line)
        if m:
            anchors.add(m.group(1))
        m2 = HEADING_TEXT_RE.match(line)
        if m2:
            anchors.add(slugify(m2.group(1)))
    return anchors


def main():
    md_files = sorted(DOCS.rglob("*.md"))
    all_files = {f.relative_to(ROOT).as_posix().lower() for f in md_files}
    # Also store relative-to-docs paths for docs-only link resolution
    headings: dict[Path, set[str]] = {}

    errors = []
    warnings = []
    external_links = []
    checked = 0

    for md_path in md_files:
        rel = md_path.relative_to(ROOT)
        try:
            text = md_path.read_text(encoding="utf-8")
        except Exception as e:
            warnings.append(f"{rel}: cannot read file: {e}")
            continue

        for match in LINK_RE.finditer(text):
            link_text = match.group(1)
            url = match.group(2).strip()
            checked += 1

            # Ignore raw URLs that aren't links? Already matched as link.
            if url.startswith(("http://", "https://")):
                external_links.append((rel, url, link_text))
                continue

            if url.startswith("mailto:") or url.startswith("tel:"):
                continue

            # Anchor-only link
            if url.startswith("#"):
                anchor = url[1:]
                if md_path not in headings:
                    headings[md_path] = collect_headings(md_path)
                if anchor not in headings[md_path] and slugify(anchor) not in headings[md_path]:
                    errors.append(f"{rel}: anchor '{anchor}' not found in same file")
                continue

            # Split file path and anchor
            if "#" in url:
                file_part, anchor = url.split("#", 1)
            else:
                file_part, anchor = url, None

            if not file_part:
                # e.g. "#anchor" handled above; this would be just "#"
                target_path = md_path
            elif file_part.startswith("/"):
                target_path = ROOT / file_part.lstrip("/").replace("/", "/")
                # We'll check existence later
            else:
                target_path = (md_path.parent / file_part).resolve()

            # If target is a directory without trailing slash, maybe README inside? Accept both.
            if target_path.is_dir() and (target_path / "README.md").exists():
                target_path = target_path / "README.md"

            if not target_path.exists():
                # Try case-insensitive match under docs
                found = False
                for candidate in all_files:
                    candidate_abs = ROOT / candidate
                    if candidate_abs.resolve() == target_path.resolve():
                        found = True
                        break
                if not found:
                    errors.append(f"{rel}: broken link to '{url}' (file not found: {target_path.relative_to(ROOT) if target_path.is_relative_to(ROOT) else target_path})")
                    continue

            # If anchor specified, only validate it for markdown targets.
            # Anchors in source files (e.g. #L796, #L1210-L1211) are GitHub/GitLab
            # line references and cannot be verified as markdown headings.
            if anchor and target_path.suffix.lower() == ".md":
                if target_path not in headings:
                    headings[target_path] = collect_headings(target_path)
                if anchor not in headings[target_path] and slugify(anchor) not in headings[target_path]:
                    errors.append(f"{rel}: anchor '{anchor}' not found in {target_path.relative_to(ROOT) if target_path.is_relative_to(ROOT) else target_path}")

    print(f"Checked {checked} links in {len(md_files)} markdown files under docs/")
    print()

    if errors:
        print(f"BROKEN LINKS ({len(errors)}):")
        for e in errors:
            print(f"  - {e}")
        print()

    if warnings:
        print(f"WARNINGS ({len(warnings)}):")
        for w in warnings:
            print(f"  - {w}")
        print()

    if external_links:
        print(f"EXTERNAL LINKS ({len(external_links)}) – not checked automatically:")
        for rel, url, link_text in external_links:
            print(f"  - {rel}: [{link_text}]({url})")
        print()

    if not errors:
        print("All internal links appear valid.")
    else:
        sys.exit(1)


if __name__ == "__main__":
    main()
