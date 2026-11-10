#!/usr/bin/env python3
"""Seeds one finished session into a Murmur data folder for the `--probe-open-latest` smoke test (#102).

Usage: seed-session.py <data-dir> [--imported]

The database must already exist with its schema (start the app once with `--startup-probe` against the same
MURMUR_DATA_DIR). The session is the newest one: a minute of audio (mic.wav and system.wav, a quiet tone), a transcript of
two speakers with an uncertain word, two bookmarks, two chapters and named speakers, so every part of the details page
renders. With --imported it is an imported file (sessions.source = 1) instead of a recording.
"""

import math
import os
import sqlite3
import struct
import sys
import time
import uuid
import wave

SAMPLE_RATE = 16000
DURATION_MS = 60_000
STATE_READY = 5


def words_blob(words):
    """WordsCodec v2: marker -2, count, then per word start_ms, end_ms, utf8 length, utf8 bytes, confidence byte."""
    out = bytearray(struct.pack("<ii", -2, len(words)))
    for text, start, end, confidence in words:
        data = text.encode("utf-8")
        out += struct.pack("<iiH", start, end, len(data)) + data
        out.append(255 if confidence is None else round(confidence * 254))
    return bytes(out)


def write_tone(path, frequency):
    frames = bytearray()
    for i in range(SAMPLE_RATE * DURATION_MS // 1000):
        frames += struct.pack("<h", int(3000 * math.sin(2 * math.pi * frequency * i / SAMPLE_RATE)))
    with wave.open(path, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(SAMPLE_RATE)
        wav.writeframes(bytes(frames))


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    root = os.path.abspath(sys.argv[1])
    imported = "--imported" in sys.argv[2:]
    database = os.path.join(root, "murmur.db")
    if not os.path.exists(database):
        sys.exit(f"{database} does not exist: start the app once with --startup-probe first.")

    session_id = uuid.uuid4().hex
    started = int(time.time() * 1000)
    folder = os.path.join("Recordings", time.strftime("%Y-%m-%d_%H-%M-%S", time.localtime(started / 1000)) + "_" + session_id[:8])
    os.makedirs(os.path.join(root, folder), exist_ok=True)
    write_tone(os.path.join(root, folder, "mic.wav"), 220)
    write_tone(os.path.join(root, folder, "system.wav"), 330)

    segments = [
        (0, 0, 0, 9000, "Good morning, let us start with the roadmap.",
         [("Good", 0, 400, 0.98), ("morning,", 400, 900, 0.97), ("let", 1000, 1200, 0.95), ("us", 1200, 1400, 0.96),
          ("start", 1400, 1900, 0.95), ("with", 1900, 2200, 0.97), ("the", 2200, 2400, 0.99), ("roadmap.", 2400, 3200, 0.31)]),
        (1, 1, 9000, 21000, "Sure. The release moves to next week.", None),
        (2, 0, 21000, 40000, "Then we review the budget and the hiring plan.", None),
        (3, 1, 40000, 60000, "Agreed, I will send the notes after the call.", None),
    ]

    with sqlite3.connect(database) as db:
        db.execute("PRAGMA foreign_keys = ON")
        db.execute(
            "INSERT INTO sessions (id, title, started_at, duration_ms, state, has_mic, has_system, screen_segments, folder, error, source, original_path) "
            "VALUES (?, ?, ?, ?, ?, 1, 1, '[]', ?, NULL, ?, ?)",
            (session_id, "Probe session", started, DURATION_MS, STATE_READY, folder, 1 if imported else 0,
             os.path.join(root, folder, "mic.wav") if imported else None))
        db.execute("INSERT INTO transcripts (session_id, engine, language, created_at) VALUES (?, 'probe', 'en', ?)", (session_id, started))
        for idx, speaker, start, end, text, words in segments:
            db.execute(
                "INSERT INTO segments (session_id, idx, speaker, start_ms, end_ms, text, words) VALUES (?, ?, ?, ?, ?, ?, ?)",
                (session_id, idx, speaker, start, end, text, words_blob(words) if words else None))
        db.execute("INSERT INTO chapters (session_id, idx, title, start_ms) VALUES (?, 0, 'Roadmap', 0)", (session_id,))
        db.execute("INSERT INTO chapters (session_id, idx, title, start_ms) VALUES (?, 1, 'Budget and hiring', 21000)", (session_id,))
        db.execute("INSERT INTO bookmarks (session_id, at_ms, note) VALUES (?, 9500, 'Release date')", (session_id,))
        db.execute("INSERT INTO bookmarks (session_id, at_ms, note) VALUES (?, 41000, '')", (session_id,))
        db.execute("INSERT INTO speaker_names (session_id, me, others) VALUES (?, 'Me', 'Alex')", (session_id,))

    print(f"seeded {'imported' if imported else 'recorded'} session {session_id} in {root}")


if __name__ == "__main__":
    main()
