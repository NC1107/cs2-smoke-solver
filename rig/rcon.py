#!/usr/bin/env python3
"""Minimal Source engine RCON client (protocol as used by CS2 dedicated servers)."""
import socket
import struct
import sys

SERVERDATA_AUTH = 3
SERVERDATA_EXECCOMMAND = 2


def send_packet(sock, pkt_id, pkt_type, body):
    payload = struct.pack("<ii", pkt_id, pkt_type) + body.encode() + b"\x00\x00"
    sock.sendall(struct.pack("<i", len(payload)) + payload)


def read_packet(sock):
    size = struct.unpack("<i", sock.recv(4))[0]
    data = b""
    while len(data) < size:
        data += sock.recv(size - len(data))
    pkt_id, pkt_type = struct.unpack("<ii", data[:8])
    body = data[8:-2].decode(errors="replace")
    return pkt_id, pkt_type, body


def rcon(host, port, password, command):
    with socket.create_connection((host, port), timeout=5) as sock:
        send_packet(sock, 1, SERVERDATA_AUTH, password)
        pkt_id, _, _ = read_packet(sock)
        if pkt_id == -1:
            raise RuntimeError("RCON auth failed")
        send_packet(sock, 2, SERVERDATA_EXECCOMMAND, command)
        _, _, body = read_packet(sock)
        return body


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("usage: rcon.py <command...>")
        sys.exit(1)
    result = rcon("127.0.0.1", 27021, "calib", " ".join(sys.argv[1:]))
    print(result)
