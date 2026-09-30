"""
Stamp a chart preview with a digest of the spec it was drawn from (a PNG tEXt chunk).

The agent's image tool reads the preview, so a stale one shows it a different chart.
generate_chart_preview.py writes the stamp and tests/test_chart_previews.py checks it.
Keys that aren't drawn (presentation text, metadata, description) are left out of the
digest. Standard library only, so the test runs without the renderer.
"""
import hashlib
import json
import struct
import zlib

STAMP_KEY = b"feelogue-spec-digest"
NOT_DRAWN = ("overview", "usermeta", "metadata", "description")


def spec_digest(spec: dict) -> str:
    """Digest of the parts of a spec that affect the drawing."""
    drawn = {k: v for k, v in spec.items() if k not in NOT_DRAWN}
    text = json.dumps(drawn, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _chunks(png: bytes):
    """(type, data, start, end) for each chunk after the PNG signature."""
    pos = 8
    while pos + 8 <= len(png):
        length = struct.unpack(">I", png[pos:pos + 4])[0]
        ctype = png[pos + 4:pos + 8]
        end = pos + 12 + length
        yield ctype, png[pos + 8:pos + 8 + length], pos, end
        pos = end


def stamp_png(png: bytes, digest: str) -> bytes:
    """The PNG with its stamp set to digest (any old stamp replaced), right after IHDR."""
    out, stamped = [png[:8]], False
    for ctype, data, start, end in _chunks(png):
        if ctype == b"tEXt" and data.split(b"\0", 1)[0] == STAMP_KEY:
            continue
        out.append(png[start:end])
        if ctype == b"IHDR" and not stamped:
            payload = STAMP_KEY + b"\0" + digest.encode("ascii")
            chunk = b"tEXt" + payload
            out.append(struct.pack(">I", len(payload)) + chunk + struct.pack(">I", zlib.crc32(chunk) & 0xFFFFFFFF))
            stamped = True
    return b"".join(out)


def read_stamp(png: bytes) -> str | None:
    """The stamped digest, or None for a preview the tool didn't draw."""
    for ctype, data, _, _ in _chunks(png):
        if ctype == b"tEXt":
            key, _, value = data.partition(b"\0")
            if key == STAMP_KEY:
                return value.decode("ascii")
        if ctype == b"IDAT":
            break
    return None
