#!/usr/bin/env python3
"""Offline tests for Runner/gabp.py, against a fake GABP server rather than a real RimWorld.

The point of a fake peer here is that the parts of a protocol client that break are the parts a
live smoke test is *worst* at catching. A wrong `tools/call` parameters key still round-trips
against a real bridge — the tool runs with everything at its default and answers plausibly. An
event arriving mid-request only interleaves under load. A frame split across two TCP reads only
happens once a payload is big enough, which for this bridge means the screenshot tools and nothing
else. So the fake peer does all three on purpose, every run, in a few milliseconds.

The fake speaks the wire we decompiled out of Lib.GAB (see gabp.py's header): LSP `Content-Length`
framing around a `gabp/1` envelope. If upstream ever changes that, these tests keep passing while
the live bridge stops answering — which is the honest division of labour. They pin *our* half.
"""

import json
import os
import socket
import sys
import threading
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Runner"))

import gabp  # noqa: E402


class FakeBridge:
    """A one-connection GABP server driven by a script of behaviours."""

    def __init__(self, tools=None, hang_on=(), error_on=(), emit_event_before_response=False,
                 chunk_writes=False):
        self.tools = tools if tools is not None else [{"name": "rimbridge/get_bridge_status"}]
        self.hang_on = set(hang_on)
        self.error_on = set(error_on)
        self.emit_event_before_response = emit_event_before_response
        self.chunk_writes = chunk_writes
        self.calls = []  # every request the client sent, for assertions about shape

        self._server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._server.bind(("127.0.0.1", 0))
        self._server.listen(1)
        self.port = self._server.getsockname()[1]
        self._thread = threading.Thread(target=self._serve, daemon=True)
        self._thread.start()

    def _serve(self):
        # Serves connections one after another rather than only the first, because callers
        # legitimately reconnect (wait_for_bridge polls, and a caller may probe then dump).
        while True:
            try:
                conn, _ = self._server.accept()
            except OSError:
                return
            self._serve_one(conn)

    def _serve_one(self, conn):
        buf = b""
        with conn:
            while True:
                try:
                    message, buf = self._read(conn, buf)
                except (OSError, ValueError):
                    return
                if message is None:
                    return
                self.calls.append(message)
                method = message.get("method")
                if method in self.hang_on:
                    continue  # accept it and never answer — a wedged main thread
                if self.emit_event_before_response:
                    self._write(conn, {
                        "v": "gabp/1", "id": "evt-1", "type": "event",
                        "channel": "game/tick", "seq": 1, "payload": {"tick": 12345},
                    })
                envelope = {"v": "gabp/1", "id": message["id"], "type": "response"}
                if method in self.error_on:
                    envelope["error"] = {"code": -32601, "message": f"unknown tool: {method}"}
                else:
                    envelope["result"] = self._result_for(method, message.get("params") or {})
                self._write(conn, envelope)

    def _result_for(self, method, params):
        if method == "session/hello":
            return {"agentId": "fake", "app": {"name": "FakeBridge", "version": "0"}}
        if method == "tools/list":
            return {"tools": self.tools}
        if method == "tools/call":
            # Echo what arrived so a test can prove the arguments actually reached the tool.
            return {"echo": params}
        return {}

    @staticmethod
    def _read(conn, buf):
        while b"\r\n\r\n" not in buf:
            chunk = conn.recv(65536)
            if not chunk:
                return None, buf
            buf += chunk
        header, _, buf = buf.partition(b"\r\n\r\n")
        length = int(header.decode().split(":", 1)[1].strip())
        while len(buf) < length:
            chunk = conn.recv(65536)
            if not chunk:
                return None, buf
            buf += chunk
        return json.loads(buf[:length].decode()), buf[length:]

    def _write(self, conn, message):
        body = json.dumps(message).encode()
        frame = f"Content-Length: {len(body)}\r\n\r\n".encode() + body
        if self.chunk_writes:
            # Split mid-header so the client cannot assume one recv == one frame.
            midpoint = len(frame) // 3
            conn.sendall(frame[:midpoint])
            conn.sendall(frame[midpoint:])
        else:
            conn.sendall(frame)

    def close(self):
        self._server.close()


class GabpClientTest(unittest.TestCase):
    def bridge(self, **kwargs):
        fake = FakeBridge(**kwargs)
        self.addCleanup(fake.close)
        return fake

    def test_handshake_sends_token_and_returns_welcome(self):
        fake = self.bridge()
        with gabp.GabpClient(fake.port, "sekrit") as client:
            self.assertEqual(client.welcome["agentId"], "fake")
        hello = fake.calls[0]
        self.assertEqual(hello["method"], "session/hello")
        self.assertEqual(hello["v"], "gabp/1")
        self.assertEqual(hello["type"], "request")
        self.assertEqual(hello["params"]["token"], "sekrit")

    def test_call_puts_arguments_under_parameters(self):
        """The silent-failure case: `arguments` would be dropped and the tool would run defaulted."""
        fake = self.bridge()
        with gabp.GabpClient(fake.port, "t") as client:
            result = client.call("rimworld/take_screenshot", {"clipTargetId": "window-42"})
        call = [c for c in fake.calls if c["method"] == "tools/call"][0]
        self.assertEqual(call["params"]["name"], "rimworld/take_screenshot")
        self.assertEqual(call["params"]["parameters"], {"clipTargetId": "window-42"})
        self.assertNotIn("arguments", call["params"])
        self.assertEqual(result["echo"]["parameters"], {"clipTargetId": "window-42"})

    def test_events_do_not_satisfy_a_pending_request(self):
        fake = self.bridge(emit_event_before_response=True)
        with gabp.GabpClient(fake.port, "t") as client:
            tools = client.list_tools()
            self.assertEqual([t["name"] for t in tools], ["rimbridge/get_bridge_status"])
            self.assertEqual(len(client.events), 2)  # one before each of hello and tools/list
            self.assertEqual(client.events[0]["channel"], "game/tick")

    def test_frames_split_across_reads_are_reassembled(self):
        fake = self.bridge(chunk_writes=True)
        with gabp.GabpClient(fake.port, "t") as client:
            self.assertEqual(len(client.list_tools()), 1)

    def test_error_response_raises_with_its_code(self):
        """An error reply must raise, not return None — a caller treating None as 'no data' would
        report a rejected call as an empty answer."""
        fake = self.bridge(error_on={"tools/call"})
        with gabp.GabpClient(fake.port, "t") as client:
            with self.assertRaises(gabp.GabpError) as caught:
                client.call("rimworld/nope")
        self.assertEqual(caught.exception.code, -32601)
        self.assertIn("unknown tool", caught.exception.message)

    def test_a_rejected_handshake_raises_rather_than_leaving_a_half_open_session(self):
        fake = self.bridge(error_on={"session/hello"})
        with self.assertRaises(gabp.GabpError):
            gabp.GabpClient(fake.port, "wrong-token").connect()


class ProbeTest(unittest.TestCase):
    """probe() is the stall diagnostic, so its job is to never raise and to name the state."""

    def test_nothing_listening_is_unreachable(self):
        closed = socket.socket()
        closed.bind(("127.0.0.1", 0))
        port = closed.getsockname()[1]
        closed.close()

        report = gabp.probe(port, "t", tool_timeout=1.0)
        self.assertFalse(report["reachable"])
        self.assertFalse(report["responsive"])
        self.assertIn("error", report)

    def test_healthy_bridge_is_responsive_and_lists_tools(self):
        fake = FakeBridge(tools=[{"name": "a"}, {"name": "b"}])
        self.addCleanup(fake.close)

        report = gabp.probe(fake.port, "t", tool_timeout=5.0)
        self.assertTrue(report["handshake"])
        self.assertTrue(report["responsive"])
        self.assertEqual(report["toolCount"], 2)
        self.assertEqual(report["tools"], ["a", "b"])

    def test_handshake_without_tools_is_reported_as_a_wedged_main_thread(self):
        """The whole reason probe() exists: alive-but-not-ticking must not read as 'crashed'."""
        fake = FakeBridge(hang_on={"tools/list"})
        self.addCleanup(fake.close)

        report = gabp.probe(fake.port, "t", tool_timeout=1.0)
        self.assertTrue(report["reachable"])
        self.assertTrue(report["handshake"])
        self.assertFalse(report["responsive"])
        self.assertIn("main thread wedged", report["error"])

    def test_hung_handshake_is_not_reported_as_a_refused_connection(self):
        fake = FakeBridge(hang_on={"session/hello"})
        self.addCleanup(fake.close)

        report = gabp.probe(fake.port, "t", tool_timeout=1.0)
        self.assertTrue(report["reachable"])
        self.assertFalse(report["handshake"])
        self.assertFalse(report["responsive"])


class StallDumpTest(unittest.TestCase):
    """The dump is what a stalled run leaves behind, so its failure modes matter more than its
    success one: it has to come back with *something* from a bridge that is only half answering."""

    def test_dump_collects_every_advertised_tool(self):
        fake = FakeBridge(tools=[{"name": n} for n in gabp.STALL_DUMP_TOOLS])
        self.addCleanup(fake.close)

        report = gabp.dump(fake.port, "t", tool_timeout=5.0)
        self.assertTrue(report["responsive"])
        self.assertEqual(sorted(report["results"]), sorted(gabp.STALL_DUMP_TOOLS))
        self.assertEqual(report["missingTools"], [])

    def test_tools_the_bridge_does_not_advertise_are_named_not_called(self):
        """An upstream rename should read as a named absence, not a wall of unknown-tool errors."""
        fake = FakeBridge(tools=[{"name": "rimbridge/get_bridge_status"}])
        self.addCleanup(fake.close)

        report = gabp.dump(fake.port, "t", tool_timeout=5.0)
        self.assertEqual(list(report["results"]), ["rimbridge/get_bridge_status"])
        self.assertIn("rimworld/get_ui_state", report["missingTools"])
        called = [c["params"]["name"] for c in fake.calls if c["method"] == "tools/call"]
        self.assertEqual(called, ["rimbridge/get_bridge_status"])

    def test_one_hanging_tool_does_not_take_the_whole_dump_down(self):
        fake = FakeBridge(
            tools=[{"name": n} for n in gabp.STALL_DUMP_TOOLS],
            hang_on={"tools/call"},
        )
        self.addCleanup(fake.close)

        # Every call hangs; the dump must still return, with each failure recorded in place.
        report = gabp.dump(fake.port, "t", tool_timeout=1.0)
        self.assertTrue(report["responsive"])
        self.assertEqual(len(report["results"]), len(gabp.STALL_DUMP_TOOLS))
        for name, value in report["results"].items():
            self.assertIn("__error", value, name)
            self.assertIn("timeout", value["__error"])

    def test_dump_of_an_unreachable_bridge_still_returns_a_report(self):
        closed = socket.socket()
        closed.bind(("127.0.0.1", 0))
        port = closed.getsockname()[1]
        closed.close()

        report = gabp.dump(port, "t", tool_timeout=1.0)
        self.assertFalse(report["responsive"])
        self.assertEqual(report["results"], {})

    def test_summary_names_a_wedged_main_thread(self):
        fake = FakeBridge(hang_on={"tools/list"})
        self.addCleanup(fake.close)

        summary = gabp.summarise_dump(gabp.dump(fake.port, "t", tool_timeout=1.0))
        self.assertIn("main thread is wedged", summary)

    def test_summary_tolerates_a_ui_state_shape_it_does_not_recognise(self):
        fake = FakeBridge(tools=[{"name": "rimworld/get_ui_state"}])
        fake._result_for = lambda method, params: (
            {"agentId": "x"} if method == "session/hello"
            else {"tools": fake.tools} if method == "tools/list"
            else {"somethingElseEntirely": 42}
        )
        self.addCleanup(fake.close)

        summary = gabp.summarise_dump(gabp.dump(fake.port, "t", tool_timeout=5.0))
        self.assertIn("1 answered", summary)


class SummaryFindingsTest(unittest.TestCase):
    """The summary line is what a human reads first, so what it does and does not call out matters.

    These payloads are trimmed from a real dump taken against a running game (see
    Tests/fixtures/live_bridge_dump.json), not invented — the field names are upstream's, and
    inventing them is exactly the mistake that shipped a summary matching nothing.
    """

    HEALTHY_UI = {
        "success": True, "programState": "Playing", "nonImmediateDialogWindowOpen": False,
        "windowsForcePause": False, "anyWindowAbsorbingAllInput": False, "floatMenuOpen": False,
        "windowCount": 0, "topWindowType": None, "topWindowTitle": None, "windows": [],
    }

    def test_a_window_forcing_pause_is_called_out_loudly(self):
        ui = dict(self.HEALTHY_UI, windowsForcePause=True, topWindowType="Dialog_NamePawn",
                  windowCount=1, nonImmediateDialogWindowOpen=True)
        findings = gabp._ui_findings(ui)
        self.assertIn("A WINDOW IS FORCING PAUSE — the scenario clock cannot advance", findings)
        self.assertIn("a modal dialog is open", findings)
        self.assertIn("top window: Dialog_NamePawn", findings)

    def test_a_healthy_ui_produces_no_findings(self):
        """Noise is the failure mode here: a summary that always warns gets ignored."""
        self.assertEqual(gabp._ui_findings(self.HEALTHY_UI), [])

    def test_a_failed_ui_call_produces_no_findings_rather_than_raising(self):
        self.assertEqual(gabp._ui_findings({"__error": "timeout: no data"}), [])
        self.assertEqual(gabp._ui_findings(None), [])

    def test_paused_clock_is_distinguished_from_a_pending_long_event(self):
        paused = {"success": True, "state": {"programState": "Playing", "paused": True,
                                             "timeSpeed": "Paused", "longEventPending": False}}
        self.assertIn("game is PAUSED (timeSpeed Paused)", gabp._clock_findings(paused))

        loading = {"success": True, "state": {"programState": "Playing", "paused": True,
                                              "timeSpeed": "Paused", "longEventPending": True}}
        findings = gabp._clock_findings(loading)
        self.assertIn("a long event is pending (still loading/generating)", findings)
        # Still loading is not the same finding as stuck paused, and reporting both would send a
        # reader after a stall that is really just a slow map generate.
        self.assertNotIn("game is PAUSED (timeSpeed Paused)", findings)

    def test_a_running_game_produces_no_clock_findings(self):
        running = {"success": True, "state": {"programState": "Playing", "paused": False,
                                              "timeSpeed": "Normal", "longEventPending": False}}
        self.assertEqual(gabp._clock_findings(running), [])


class WaitForBridgeTest(unittest.TestCase):
    def test_returns_as_soon_as_the_bridge_answers(self):
        fake = FakeBridge()
        self.addCleanup(fake.close)

        report = gabp.wait_for_bridge(fake.port, "t", deadline_secs=10.0, poll_secs=0.1)
        self.assertTrue(report["responsive"])
        self.assertNotIn("gaveUpAfterSecs", report)

    def test_gives_up_with_the_last_reason_rather_than_a_bare_false(self):
        closed = socket.socket()
        closed.bind(("127.0.0.1", 0))
        port = closed.getsockname()[1]
        closed.close()

        report = gabp.wait_for_bridge(port, "t", deadline_secs=0.5, poll_secs=0.1)
        self.assertFalse(report["responsive"])
        self.assertEqual(report["gaveUpAfterSecs"], 0.5)
        self.assertIn("error", report)


class LiveCaptureTest(unittest.TestCase):
    """Checks gabp.py against a dump taken from a real running RimWorld, not a fake.

    Everything else in this file pins our half of the protocol. This pins the half that is upstream's
    to change: the tool names we ask for and the field names we read out of the answers. A rename
    there does not break the client — it makes the summary quietly stop saying anything, which is the
    failure mode a diagnostic can least afford. Re-capture with:

        ./Runner/run_test.sh --bridge Scenarios/daycycle_timelapse.json      # in one shell
        python3 Runner/gabp.py --endpoint <report>-bridge.json dump --out -  # in another
    """

    @classmethod
    def setUpClass(cls):
        path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "fixtures",
                            "live_bridge_dump.json")
        with open(path, encoding="utf-8") as handle:
            cls.capture = json.load(handle)

    def test_every_tool_the_dump_asks_for_existed_on_the_live_bridge(self):
        self.assertEqual(self.capture["missingTools"], [],
                         "a tool in STALL_DUMP_TOOLS was not advertised by the live bridge")
        self.assertEqual(sorted(self.capture["results"]), sorted(gabp.STALL_DUMP_TOOLS),
                         "STALL_DUMP_TOOLS has drifted from what the capture was taken with")

    def test_every_tool_answered(self):
        for name, payload in self.capture["results"].items():
            self.assertNotIn("__error", payload, f"{name} failed against a healthy live game")

    def test_the_ui_fields_the_summary_reads_are_present_in_the_real_payload(self):
        ui = self.capture["results"]["rimworld/get_ui_state"]
        for field in ("windowsForcePause", "nonImmediateDialogWindowOpen",
                      "anyWindowAbsorbingAllInput", "floatMenuOpen", "windowCount",
                      "topWindowType", "topWindowTitle"):
            self.assertIn(field, ui, f"summary reads {field}, which the live bridge no longer sends")

    def test_the_clock_fields_the_summary_reads_are_present_in_the_real_payload(self):
        state = self.capture["results"]["rimbridge/get_bridge_status"]["state"]
        for field in ("paused", "timeSpeed", "longEventPending", "programState"):
            self.assertIn(field, state, f"summary reads {field}, which the live bridge no longer sends")

    def test_a_healthy_capture_summarises_without_alarm(self):
        """The capture was taken from a working game, so the only finding should be the paused clock
        the harness itself imposes between steps — no dialogs, no forced pause."""
        summary = gabp.summarise_dump(self.capture)
        self.assertIn("bridge responsive", summary)
        self.assertNotIn("FORCING PAUSE", summary)
        self.assertNotIn("modal dialog", summary)


class EndpointResolutionTest(unittest.TestCase):
    class Args:
        def __init__(self, endpoint=None, port=None, token=None):
            self.endpoint = endpoint
            self.port = port
            self.token = token

    def test_endpoint_file_wins(self):
        import tempfile

        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as handle:
            json.dump({"port": 6000, "token": "from-file"}, handle)
            path = handle.name
        self.addCleanup(os.unlink, path)

        port, token = gabp.resolve_endpoint(self.Args(endpoint=path))
        self.assertEqual((port, token), (6000, "from-file"))

    def test_environment_is_used_when_no_flags_given(self):
        os.environ["GABP_SERVER_PORT"] = "6001"
        os.environ["GABP_TOKEN"] = "from-env"
        self.addCleanup(os.environ.pop, "GABP_SERVER_PORT", None)
        self.addCleanup(os.environ.pop, "GABP_TOKEN", None)

        port, token = gabp.resolve_endpoint(self.Args())
        self.assertEqual((port, token), (6001, "from-env"))

    def test_token_with_no_port_falls_back_to_the_mods_default_port(self):
        port, token = gabp.resolve_endpoint(self.Args(token="t"))
        self.assertEqual((port, token), (gabp.FALLBACK_PORT, "t"))

    def test_missing_token_explains_where_to_get_one(self):
        os.environ.pop("GABP_TOKEN", None)
        with self.assertRaises(SystemExit) as caught:
            gabp.resolve_endpoint(self.Args(port=5174))
        self.assertIn("--bridge", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
