"""The writes of the bench. Fails when a write acknowledged to the client is missing
(fewer rows than acknowledgements); prints the longest write pauses, for the record."""
import datetime
import sys

log, rows = sys.argv[1], int(sys.argv[2])
oks, kos = [], 0
for line in open(log):
    parts = line.split()
    if len(parts) >= 2 and parts[1] == "ok":
        oks.append(float(parts[0]))
    elif len(parts) >= 2 and parts[1] == "ko":
        kos += 1
gaps = sorted(((b - a, a) for a, b in zip(oks, oks[1:])), reverse=True)[:5]
print(f"acknowledged={len(oks)} failed_attempts={kos} rows={rows}")
for gap, at in gaps:
    print(f"pause {gap:.2f} s at {datetime.datetime.fromtimestamp(at, datetime.timezone.utc):%H:%M:%S.%f}")
if len(oks) < 100:
    sys.exit("FAIL: the writer barely wrote; the bench measured nothing")
if rows < len(oks):
    sys.exit(f"FAIL: {len(oks) - rows} acknowledged writes lost")
