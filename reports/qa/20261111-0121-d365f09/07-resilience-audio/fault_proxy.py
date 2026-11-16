"""A TCP proxy that injects faults, for QA prompt 07. Local test use only.

    python3 fault_proxy.py LISTEN_PORT TARGET_PORT MODE_FILE

MODE_FILE holds one word, re-read for every new connection and every chunk:
  pass        forward normally
  delay:MS    hold each chunk from the target MS milliseconds
  refuse      close new connections at once (service down)
  blackhole   accept, read, never answer (a hung dependency)
  cut         also drop connections already open (process died mid-request)
"""
import asyncio
import sys
from pathlib import Path

LISTEN, TARGET, MODE = int(sys.argv[1]), int(sys.argv[2]), Path(sys.argv[3])
open_conns: set[asyncio.StreamWriter] = set()


def mode() -> str:
    try:
        return MODE.read_text().strip() or "pass"
    except OSError:
        return "pass"


async def pipe(reader, writer, delayed: bool):
    try:
        while data := await reader.read(65536):
            m = mode()
            if m == "cut":
                break
            if delayed and m.startswith("delay:"):
                await asyncio.sleep(int(m.split(":")[1]) / 1000)
            writer.write(data)
            await writer.drain()
    except (ConnectionError, asyncio.CancelledError):
        pass
    finally:
        writer.close()


async def handle(client_r, client_w):
    m = mode()
    if m in ("refuse", "cut"):
        client_w.close()
        return
    if m == "blackhole":
        open_conns.add(client_w)
        while await client_r.read(65536):
            pass
        return
    try:
        up_r, up_w = await asyncio.open_connection("127.0.0.1", TARGET)
    except OSError:
        client_w.close()
        return
    open_conns.update({client_w, up_w})
    await asyncio.gather(pipe(client_r, up_w, False), pipe(up_r, client_w, True))
    open_conns.difference_update({client_w, up_w})


async def watch_cut():
    while True:
        if mode() == "cut":
            for w in list(open_conns):
                w.close()
            open_conns.clear()
        await asyncio.sleep(0.2)


async def main():
    server = await asyncio.start_server(handle, "127.0.0.1", LISTEN)
    asyncio.create_task(watch_cut())
    async with server:
        await server.serve_forever()


asyncio.run(main())
