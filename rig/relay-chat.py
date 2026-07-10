"""Send chat lines to the server via the plugin's request-file channel.
Usage: relay-chat.py "line 1" "line 2" ..."""
import json
import os
import sys
import time

path = "/home/npc/Documents/projects/cs2-smoke-solver/data/calib/request.json"
for _ in range(100):
    if not os.path.exists(path):
        break
    time.sleep(0.1)
with open(path, "w") as f:
    json.dump({"chat": [f" [calib] {line}" for line in sys.argv[1:]]}, f)
