#!/usr/bin/env python3
"""Runner/gabp.py — a GABP client, so the runner can ask a live RimWorld what it is doing.

WHY THIS EXISTS
---------------
When a run stalls, the only evidence we have ever had is `Player.log` plus whatever the scenario
had already written to its report. That is evidence about the past. It cannot answer the question
that actually matters at that moment — *what is the game doing right now* — so a stall gets
diagnosed by re-running with pieces commented out until it stops stalling. One 900s timeout in this
repo was bisected that way down to FastForward/preset/Screenshot, and the memory-pressure hangs look
identical to "your last edit broke it" from the outside.

RimBridgeServer (Workshop 3727949765, `brrainz.rimbridgeserver`) runs a GABP server *inside* the
game and exposes a large tool surface over it — game/map/camera/selection/UI state, long-event
state, screenshots, mod settings, debug actions. This module is the client half: enough GABP to
connect to that server and call its tools. It is deliberately a client and nothing more. We call
their tools rather than growing our own probes for the same facts.

WHY WE SPEAK GABP DIRECTLY RATHER THAN GOING THROUGH GABS
---------------------------------------------------------
GABS (github.com/pardeike/GABS) is the upstream host-side companion, and it is the right tool for
driving a game interactively from an MCP client. It is the wrong tool *here*, because its job is to
start and supervise the game process — which is precisely the job `run_test.sh` cannot delegate. The
run guard's exclusive flock, the asset-claim ledger and the minimal ModsConfig it writes are what
make a run's results attributable to a known build; a second supervisor launching RimWorld beside
that would boot the user's real mod list outside the lock. GABS and this module are not competing:
they consume the *same* contract (below), so both can point at the same game.

THE CONTRACT
------------
RimBridgeServer calls `UseGabsEnvironmentIfAvailable()` at startup, which reads three environment
variables from the game process:

    GABP_SERVER_PORT   port the in-game server listens on   (required)
    GABP_TOKEN         shared secret for the handshake      (required)
    GABS_GAME_ID       cosmetic; becomes the server's agent id (optional)

Port and token together are enough — `GABS_GAME_ID` only names the agent. So `run_test.sh` sets the
two it needs on the launch line and the endpoint is *known* rather than discovered. That matters
more than it sounds: the documented alternative is scraping `[RimBridge] Bridge token: ...` out of
Player.log, which is a race against a game that may already be wedged, and unavailable at exactly
the moment we most want it. With neither variable set the mod falls back to port 5174 and a random
token, and only then is the log the only way in.

THE WIRE
--------
TCP to 127.0.0.1, LSP framing (`Content-Length: N\\r\\n\\r\\n` then N bytes of JSON), carrying the
`gabp/1` envelope:

    {"v":"gabp/1","id":"<uuid>","type":"request"|"response"|"event", ...}

Requests carry `method`/`params`; responses carry `result` or `error` and echo the request's `id`;
events arrive unsolicited on subscribed channels and are keyed by `channel`/`seq`/`payload`. The
session opens with a `session/hello` request bearing the token, answered by a welcome result. After
that: `tools/list`, `tools/call`, `events/subscribe`, `events/unsubscribe`.

One shape here is easy to get wrong and silent when you do: `tools/call` takes its arguments under
**`parameters`**, not `arguments`. A call with the wrong key reaches the tool with everything at its
default, which for a read-only diagnostic looks like a plausible answer rather than an error.

TIMEOUTS ARE A MEASUREMENT, NOT JUST A SAFETY NET
-------------------------------------------------
RimBridgeServer marshals tool bodies onto RimWorld's main thread, for the same reason our own live
channel does — game state may not be touched from anywhere else. So when the main thread is wedged,
the socket still connects and the handshake still completes (both are handled off-thread) while the
tool call never returns. That difference is the diagnosis, and `probe()` below reports the three
outcomes separately for exactly that reason:

    no connection    -> the process is gone, or the bridge never started
    handshake only   -> the process is alive but its main thread is not running
    tools respond    -> the game is running and the stall is above us, in the scenario

Standard library only, python3 already being a hard dependency of the runner.
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import sys
import time
import uuid

GABP_VERSION = "gabp/1"
DEFAULT_HOST = "127.0.0.1"

# The mod's own fallback when neither environment variable is set (Lib.GAB's UsePortIfNotSet). Worth
# naming so a caller that forgot --bridge still has somewhere to look rather than a bare "refused".
FALLBACK_PORT = 5174

# Distinct from the tool timeout on purpose. Connect and handshake are answered off the game's main
# thread, so they stay fast even when the game is wedged — which is what makes a slow *tool* call
# against a fast handshake mean something specific.
CONNECT_TIMEOUT = 5.0
HANDSHAKE_TIMEOUT = 10.0
DEFAULT_TOOL_TIMEOUT = 30.0


class GabpError(RuntimeError):
    """A GABP-level error response (the peer answered, and the answer was an error)."""

    def __init__(self, code, message, data=None):
        super().__init__(f"GABP error {code}: {message}")
        self.code = code
        self.message = message
        self.data = data


class GabpTimeout(RuntimeError):
    """No response arrived in time. Against this bridge that usually means a wedged main thread."""


class GabpClient:
    """One GABP session. Use as a context manager; `connect()` includes the handshake."""

    def __init__(self, port, token, host=DEFAULT_HOST, client_name="rwth-runner"):
        self.host = host
        self.port = int(port)
        self.token = token
        self.client_name = client_name
        self.welcome = None
        self._sock = None
        self._buf = b""
        # Events are unsolicited and can land between a request and its response. Keeping them
        # instead of discarding them means a caller that subscribes later can still see what arrived
        # while it was waiting on something else.
        self.events = []

    # -- lifecycle ---------------------------------------------------------

    def __enter__(self):
        self.connect()
        return self

    def __exit__(self, *_exc):
        self.close()
        return False

    def connect(self):
        self._sock = socket.create_connection((self.host, self.port), timeout=CONNECT_TIMEOUT)
        self._sock.settimeout(HANDSHAKE_TIMEOUT)
        try:
            self.welcome = self._request(
                "session/hello",
                {
                    "token": self.token,
                    # Named for what is talking, not for what it is pretending to be. The peer logs
                    # this and it ends up in bug reports; "gabs" here would send someone reading them
                    # to the wrong repo.
                    "bridgeVersion": "rwth-runner/1",
                    "platform": sys.platform,
                    "launchId": str(uuid.uuid4()),
                    "clientInfo": {"name": self.client_name, "version": "1"},
                },
                timeout=HANDSHAKE_TIMEOUT,
            )
        except BaseException:
            # A rejected or timed-out handshake must not leave the socket open. This client gets
            # called in a retry loop while waiting for a game to finish booting, so leaking one
            # connection per attempt would exhaust descriptors on the side that is already unwell.
            self.close()
            raise
        return self.welcome

    def close(self):
        if self._sock is not None:
            try:
                self._sock.close()
            finally:
                self._sock = None

    # -- protocol ----------------------------------------------------------

    def list_tools(self, timeout=DEFAULT_TOOL_TIMEOUT):
        result = self._request("tools/list", {}, timeout=timeout)
        return result.get("tools", []) if isinstance(result, dict) else []

    def call(self, tool, arguments=None, timeout=DEFAULT_TOOL_TIMEOUT):
        # "parameters", not "arguments" — see the module header. Getting this wrong is silent.
        params = {"name": tool, "parameters": arguments or {}}
        return self._request("tools/call", params, timeout=timeout)

    def _request(self, method, params, timeout):
        request_id = str(uuid.uuid4())
        self._send({
            "v": GABP_VERSION,
            "id": request_id,
            "type": "request",
            "method": method,
            "params": params,
        })
        return self._await_response(request_id, timeout)

    def _await_response(self, request_id, timeout):
        """Read until the response with our id arrives, parking events seen along the way.

        The deadline is on the whole exchange rather than per-recv, so a peer that dribbles frames
        cannot extend the wait indefinitely one byte at a time.
        """
        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise GabpTimeout(f"no response to {request_id} within {timeout:.0f}s")
            message = self._read_message(remaining)
            if message.get("type") == "event":
                self.events.append(message)
            elif message.get("id") == request_id:
                error = message.get("error")
                if error:
                    raise GabpError(error.get("code"), error.get("message", ""), error.get("data"))
                return message.get("result")
            # Anything else is a response to a request we are no longer waiting on — a call that
            # timed out earlier and arrived late. Dropping it is right; it belongs to nobody.

    # -- framing -----------------------------------------------------------

    def _send(self, message):
        body = json.dumps(message).encode("utf-8")
        header = f"Content-Length: {len(body)}\r\n\r\n".encode("ascii")
        self._sock.sendall(header + body)

    def _read_message(self, timeout):
        header = self._read_until(b"\r\n\r\n", timeout)
        length = None
        for line in header.decode("ascii", "replace").split("\r\n"):
            if line.lower().startswith("content-length:"):
                length = int(line.split(":", 1)[1].strip())
        if length is None:
            raise RuntimeError(f"GABP frame had no Content-Length header: {header!r}")
        body = self._read_exactly(length, timeout)
        return json.loads(body.decode("utf-8"))

    def _read_until(self, delimiter, timeout):
        while delimiter not in self._buf:
            self._fill(timeout)
        head, _, self._buf = self._buf.partition(delimiter)
        return head

    def _read_exactly(self, count, timeout):
        while len(self._buf) < count:
            self._fill(timeout)
        body, self._buf = self._buf[:count], self._buf[count:]
        return body

    def _fill(self, timeout):
        self._sock.settimeout(max(timeout, 0.1))
        try:
            chunk = self._sock.recv(65536)
        except socket.timeout:
            raise GabpTimeout(f"no data within {timeout:.0f}s")
        if not chunk:
            raise ConnectionError("bridge closed the connection")
        self._buf += chunk


# -- endpoint resolution ---------------------------------------------------
#
# Three sources, most explicit first. The endpoint file is what run_test.sh writes; the environment
# is what a caller inside a bridge run already has; the fallback is the mod's own default, which is
# the only thing available for a game somebody started by hand.


def resolve_endpoint(args):
    if args.endpoint:
        with open(args.endpoint, encoding="utf-8") as handle:
            data = json.load(handle)
        return int(data["port"]), data["token"]

    port = args.port or os.environ.get("GABP_SERVER_PORT")
    token = args.token or os.environ.get("GABP_TOKEN")
    if not token:
        raise SystemExit(
            "gabp: no token. Pass --token, --endpoint <file>, or set GABP_TOKEN.\n"
            "      A run started with run_test.sh --bridge writes an endpoint file and logs its path.\n"
            "      For a hand-started game, read '[RimBridge] Bridge token:' out of the Player.log."
        )
    return int(port or FALLBACK_PORT), token


def probe(port, token, host=DEFAULT_HOST, tool_timeout=10.0):
    """Classify the bridge into the three states the module header describes.

    Returns a plain dict so callers can put it straight into a report. Never raises: this runs when
    something has *already* gone wrong, and a diagnostic that fails to report is worse than useless.
    """
    result = {"host": host, "port": port, "reachable": False, "handshake": False, "responsive": False}
    try:
        client = GabpClient(port, token, host=host)
    except Exception as exc:  # pragma: no cover - constructor is trivial
        result["error"] = f"{type(exc).__name__}: {exc}"
        return result

    try:
        welcome = client.connect()
        result["reachable"] = True
        result["handshake"] = True
        result["welcome"] = welcome
    except Exception as exc:
        result["error"] = f"{type(exc).__name__}: {exc}"
        # Distinguishing "refused" from "connected but no welcome" tells a reader whether the game
        # process is gone or merely deaf, which is the difference between a crash and a hang.
        result["reachable"] = not isinstance(exc, (ConnectionRefusedError, socket.timeout, OSError))
        client.close()
        return result

    try:
        tools = client.list_tools(timeout=tool_timeout)
        result["responsive"] = True
        result["toolCount"] = len(tools)
        result["tools"] = sorted(tool.get("name", "") for tool in tools)
    except GabpTimeout as exc:
        # The interesting case: handshake fine, tools silent. Say what that means, here, rather than
        # leaving whoever reads the report to work it out from a bare timeout.
        result["error"] = f"handshake succeeded but tools/list timed out ({exc}) — main thread wedged"
    except Exception as exc:
        result["error"] = f"{type(exc).__name__}: {exc}"
    finally:
        client.close()
    return result


# -- the stall dump --------------------------------------------------------
#
# What to ask a game that has stopped making progress. Ordered deliberately, in two groups.
#
# The first group is what RimBridgeServer tags `attention-bypass`: when the bridge has raised a
# blocking attention item (a hard error after a call boundary), ordinary `rimworld/*` calls are held
# until somebody acknowledges it, while these stay answerable. They therefore come first, so a dump
# against a bridge in that state still comes back with something rather than a column of timeouts.
#
# The second group is the state we actually want, cheapest and most diagnostic first.
# `rimworld/get_ui_state` earns its place at the front of it: an undismissed modal dialog is this
# repo's classic silent hang, and it is invisible in Player.log because nothing is wrong from the
# game's point of view — it is waiting for a click that a batch run will never make.
#
# Every entry is read-only. A diagnostic that changed the state it was called to describe would
# destroy the evidence it exists to collect.
STALL_DUMP_TOOLS = [
    "rimbridge/get_bridge_status",
    "rimbridge/list_operation_events",
    "rimbridge/list_logs",
    "rimworld/get_ui_state",
    "rimworld/get_game_info",
    "rimworld/list_letters",
    "rimworld/list_alerts",
    "rimworld/list_messages",
    "rimworld/get_camera_state",
]


def wait_for_bridge(port, token, host=DEFAULT_HOST, deadline_secs=120.0, poll_secs=2.0):
    """Poll until the bridge is answering tools, or the deadline passes.

    Needed because the bridge comes up when RimWorld finishes loading mods, which is a minute or more
    behind the process starting, and varies with the mod list. The last attempt's report is returned
    either way, so a caller that gave up still has the reason rather than just a boolean.
    """
    deadline = time.monotonic() + deadline_secs
    report = None
    while True:
        report = probe(port, token, host=host, tool_timeout=min(10.0, deadline_secs))
        if report.get("responsive"):
            return report
        if time.monotonic() >= deadline:
            report["gaveUpAfterSecs"] = deadline_secs
            return report
        time.sleep(poll_secs)


def dump(port, token, host=DEFAULT_HOST, tools=None, tool_timeout=15.0):
    """Ask a live game what it is doing. Never raises; partial answers are the normal case.

    Each tool is attempted independently and its failure recorded in place, because the failures are
    themselves the finding — a dump where the attention-bypass tools answered and everything else
    timed out says "blocked on an attention item", and one where nothing answered says "main thread".
    """
    report = {"host": host, "port": port, "reachable": False, "handshake": False,
              "responsive": False, "results": {}}
    try:
        client = GabpClient(port, token, host=host)
        client.connect()
    except Exception as exc:
        report["error"] = f"{type(exc).__name__}: {exc}"
        report["reachable"] = not isinstance(exc, (ConnectionRefusedError, socket.timeout, OSError))
        return report

    # One session for the whole dump rather than a probe followed by a second connection. Against a
    # game that is already unwell, every extra handshake is another thing that can hang, and two
    # connections could straddle a state change and describe two different moments as one.
    report["reachable"] = True
    report["handshake"] = True
    report["welcome"] = client.welcome
    try:
        try:
            advertised = client.list_tools(timeout=tool_timeout)
        except GabpTimeout as exc:
            report["error"] = f"handshake succeeded but tools/list timed out ({exc}) — main thread wedged"
            return report

        report["responsive"] = True
        report["toolCount"] = len(advertised)
        available = {tool.get("name", "") for tool in advertised}
        report["tools"] = sorted(available)

        wanted = tools if tools is not None else STALL_DUMP_TOOLS
        # Intersecting with what the bridge actually advertises, rather than calling the list blind,
        # so an upstream rename shows up as a named absence instead of a wall of unknown-tool errors.
        report["missingTools"] = [name for name in wanted if name not in available]

        for name in wanted:
            if name in available:
                report["results"][name] = _try_call(client, name, tool_timeout)
    finally:
        client.close()
    return report


def _try_call(client, name, timeout):
    """One tool, with its failure recorded rather than raised — the failures are the finding."""
    try:
        return client.call(name, timeout=timeout)
    except GabpTimeout as exc:
        return {"__error": f"timeout: {exc}"}
    except Exception as exc:
        return {"__error": f"{type(exc).__name__}: {exc}"}


def summarise_dump(report):
    """One line for a runner log. The file has everything; this is what a human reads first."""
    if not report.get("reachable"):
        return "bridge unreachable — the game process is gone or never started its bridge"
    if not report.get("handshake"):
        return "bridge accepted a connection but never completed a handshake"
    if not report.get("responsive"):
        return "bridge handshook but tools/list did not answer — RimWorld's main thread is wedged"

    results = report.get("results", {})
    answered = [n for n, r in results.items() if not isinstance(r, dict) or "__error" not in r]
    failed = [n for n in results if n not in answered]
    parts = [f"bridge responsive, {report.get('toolCount', 0)} tools; {len(answered)} answered"]
    if failed:
        parts.append(f"{len(failed)} did not ({', '.join(sorted(failed))})")

    parts.extend(_ui_findings(results.get("rimworld/get_ui_state")))
    parts.extend(_clock_findings(results.get("rimbridge/get_bridge_status")))
    return "; ".join(parts)


def _ok(payload):
    """A tool answered usefully — not absent, not an error we recorded in its place."""
    return isinstance(payload, dict) and "__error" not in payload


def _ui_findings(ui_state):
    """The stall shapes `rimworld/get_ui_state` can name outright.

    Field names are upstream's and were read off a live dump rather than guessed. They are the
    reason this whole feature is worth having: a batch run blocked behind a modal dialog is waiting
    for a click that will never come, and *nothing is wrong* from the game's point of view, so the
    log says nothing at all. `.get` throughout so a renamed field degrades to silence rather than
    taking the summary down with it.
    """
    if not _ok(ui_state):
        return []

    findings = []
    # Ordered by how conclusive each one is. A window forcing pause explains a stalled run on its
    # own; a dialog is nearly as good; the rest is context.
    if ui_state.get("windowsForcePause"):
        findings.append("A WINDOW IS FORCING PAUSE — the scenario clock cannot advance")
    if ui_state.get("nonImmediateDialogWindowOpen"):
        findings.append("a modal dialog is open")
    if ui_state.get("anyWindowAbsorbingAllInput"):
        findings.append("a window is absorbing all input")
    if ui_state.get("floatMenuOpen"):
        findings.append("a float menu is open")

    top = ui_state.get("topWindowType") or ui_state.get("topWindowTitle")
    if top:
        findings.append(f"top window: {top}")
    elif ui_state.get("windowCount"):
        findings.append(f"{ui_state['windowCount']} window(s) open")
    return findings


def _clock_findings(status):
    """Whether the game is actually running, from `rimbridge/get_bridge_status`.

    Paused-and-not-in-a-long-event is the difference between "this scenario is slow" and "this
    scenario is never going to finish", and it is invisible from outside the process.
    """
    if not _ok(status):
        return []
    state = status.get("state")
    if not isinstance(state, dict):
        return []

    findings = []
    if state.get("longEventPending"):
        findings.append("a long event is pending (still loading/generating)")
    elif state.get("paused"):
        findings.append(f"game is PAUSED (timeSpeed {state.get('timeSpeed')})")
    if state.get("programState") and state["programState"] != "Playing":
        findings.append(f"programState {state['programState']}")
    return findings


# -- CLI -------------------------------------------------------------------


def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="gabp.py",
        description="Talk to RimBridgeServer inside a running RimWorld.",
    )
    parser.add_argument("--endpoint", help="JSON file with {port, token} (run_test.sh --bridge writes one)")
    parser.add_argument("--port", type=int, help="bridge port (default: $GABP_SERVER_PORT, else 5174)")
    parser.add_argument("--token", help="bridge token (default: $GABP_TOKEN)")
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--timeout", type=float, default=DEFAULT_TOOL_TIMEOUT)

    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("probe", help="classify the bridge: reachable / handshaking / responsive")
    dump_cmd = sub.add_parser("dump", help="ask a stalled game what it is doing")
    dump_cmd.add_argument("--out", help="write the full JSON here (stdout gets the summary line)")
    wait_cmd = sub.add_parser("wait", help="wait for the bridge to come up, then report its surface")
    wait_cmd.add_argument("--for", dest="wait_for", type=float, default=120.0, help="seconds")
    tools_cmd = sub.add_parser("tools", help="list the tool surface")
    tools_cmd.add_argument("--filter", help="only names containing this substring")
    tools_cmd.add_argument("--json", action="store_true", help="full descriptors rather than names")
    call_cmd = sub.add_parser("call", help="call one tool")
    call_cmd.add_argument("tool")
    call_cmd.add_argument("arguments", nargs="?", default="{}", help="JSON object (default: {})")

    args = parser.parse_args(argv)
    port, token = resolve_endpoint(args)

    if args.command == "probe":
        report = probe(port, token, host=args.host, tool_timeout=args.timeout)
        print(json.dumps(report, indent=2))
        return 0 if report["responsive"] else 1

    if args.command == "dump":
        report = dump(port, token, host=args.host, tool_timeout=args.timeout)
        if args.out:
            with open(args.out, "w", encoding="utf-8") as handle:
                json.dump(report, handle, indent=2, default=str)
        else:
            print(json.dumps(report, indent=2, default=str))
        print(summarise_dump(report), file=sys.stderr)
        return 0 if report["responsive"] else 1

    if args.command == "wait":
        report = wait_for_bridge(port, token, host=args.host, deadline_secs=args.wait_for)
        print(json.dumps(report, indent=2))
        return 0 if report["responsive"] else 1

    with GabpClient(port, token, host=args.host) as client:
        if args.command == "tools":
            tools = client.list_tools(timeout=args.timeout)
            if args.filter:
                tools = [t for t in tools if args.filter in t.get("name", "")]
            if args.json:
                print(json.dumps(tools, indent=2))
            else:
                for tool in sorted(tools, key=lambda t: t.get("name", "")):
                    print(f"{tool.get('name','')}\t{tool.get('description','')}")
            return 0

        if args.command == "call":
            result = client.call(args.tool, json.loads(args.arguments), timeout=args.timeout)
            print(json.dumps(result, indent=2))
            return 0

    return 2


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (GabpError, GabpTimeout, ConnectionError) as exc:
        print(f"gabp: {exc}", file=sys.stderr)
        sys.exit(1)
