"""Send chat lines to the server via the plugin's request mailbox.
Usage: relay-chat.py "line 1" "line 2" ..."""
import sys

import calibipc

calibipc.send_chat(*sys.argv[1:])
