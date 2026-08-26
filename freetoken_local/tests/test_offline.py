"""Offline unit tests for freetoken_local - no server, no network.

A real loopback HTTP server stands in for FreeToken's endpoints so client
parsing, error mapping, SSE framing, retries, and handler behavior are all
exercised without the desktop app installed. The live selftest
(``python -m freetoken_local selftest``) remains the end-to-end check; these
make the package CI-runnable on a clean box.

Run: python -m unittest discover -s freetoken_local/tests
"""

from __future__ import annotations

import json
import threading
import unittest
import urllib.error
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest import mock

from freetoken_local.client import (
    ChatMessage,
    FreeTokenAPIError,
    FreeTokenClient,
    FreeTokenConnectionError,
)
from freetoken_local.handler import LocalLLM

# Mutable server state. Tests mutate entries via `set_state()` inside
# setUp()/finally; setUp() restores defaults every test so run order can
# never pollute results.
DEFAULTS: dict = {
    "models_body": json.dumps({"data": [{"id": "Qwen3.6-35B-A3B"}]}),
    "chat_body": json.dumps(
        {
            "id": "chatcmpl-1",
            "choices": [{"message": {"content": "PONG"}}],
            "usage": {"prompt_tokens": 5, "completion_tokens": 1},
        }
    ),
    "stream_frames": 'data: {"choices":[{"delta":{"content":"Hel"}}]}\n\ndata: [DONE]\n\n',
    "status": 200,
}
STATE: dict = {}


def set_state(**kwargs):
    STATE.update(kwargs)


class _FakeFreeToken(BaseHTTPRequestHandler):
    """Configurable stand-in for FreeToken's HTTP surface."""

    server_version = "FakeFreeToken/1"

    def log_message(self, *args):  # silence test output
        pass

    def _send(self, body: str, status: int | None = None,
              content_type: str = "application/json"):
        payload = body.encode("utf-8")
        self.send_response(STATE.get("status", 200) if status is None else status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        if self.path == "/v1/models":
            self._send(STATE["models_body"])
        else:
            self._send('{"error":"not found"}', status=404)

    def do_POST(self):
        if self.path == "/v1/chat/completions":
            length = int(self.headers.get("Content-Length", 0))
            self.rfile.read(length)
            if self.headers.get("Accept") == "text/event-stream":
                self._send(
                    STATE["stream_frames"],
                    content_type="text/event-stream",
                )
            else:
                self._send(STATE["chat_body"])
        else:
            self._send('{"error":"boom"}', status=500)


class FakeServerTestCase(unittest.TestCase):
    maxDiff = None

    @classmethod
    def setUpClass(cls):
        cls.httpd = ThreadingHTTPServer(("127.0.0.1", 0), _FakeFreeToken)
        cls.port = cls.httpd.server_address[1]
        cls.thread = threading.Thread(target=cls.httpd.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.httpd.shutdown()
        cls.httpd.server_close()

    def setUp(self):
        STATE.clear()
        STATE.update(DEFAULTS)
        self.client = FreeTokenClient(port=self.port, retries=0)


class ClientTests(FakeServerTestCase):
    def test_health_true_when_model_loaded(self):
        self.assertTrue(self.client.health())

    def test_server_up_even_without_models(self):
        set_state(models_body=json.dumps({"data": []}))
        self.assertTrue(self.client.server_up())
        self.assertFalse(self.client.health())  # up but unusable

    def test_model_ids_parses_response(self):
        self.assertEqual(self.client.model_ids(), ["Qwen3.6-35B-A3B"])

    def test_chat_returns_full_json(self):
        resp = self.client.chat([ChatMessage("user", "hi")])
        self.assertEqual(resp["choices"][0]["message"]["content"], "PONG")

    def test_http_error_maps_to_api_error_with_status(self):
        self.client.chat([ChatMessage("user", "hi")])  # sanity: baseline works
        set_state(status=503)
        with self.assertRaises(FreeTokenAPIError) as ctx:
            self.client.chat([ChatMessage("user", "hi")])
        self.assertEqual(ctx.exception.status, 503)

    def test_connection_error_on_dead_port(self):
        dead = FreeTokenClient(port=self.port + 1, retries=0, timeout=2)
        with self.assertRaises(FreeTokenConnectionError):
            dead.list_models()

    def test_auto_model_raises_instead_of_fabricating(self):
        set_state(models_body=json.dumps({"data": []}))
        with self.assertRaises(Exception) as ctx:
            self.client.chat([ChatMessage("user", "hi")])
        # FreeTokenError base covers both subclasses; message must be honest.
        self.assertIn("no loaded models", str(ctx.exception))


class StreamTests(FakeServerTestCase):
    def test_stream_yields_deltas_and_stops_at_done(self):
        deltas = list(self.client.stream_chat([ChatMessage("user", "count")]))
        self.assertEqual(deltas, ["Hel"])

    def test_stream_tolerates_crlf_framing(self):
        # Spec-legal CRLF delimiters must not stall the parser.
        set_state(stream_frames=(
            'data: {"choices":[{"delta":{"content":"A"}}]}\r\n\r\n'
            'data: {"choices":[{"delta":{"content":"B"}}]}\r\n\r\n'
            "data: [DONE]\r\n\r\n"
        ))
        deltas = list(self.client.stream_chat([ChatMessage("user", "x")]))
        self.assertEqual(deltas, ["A", "B"])

    def test_stream_http_error_surfaces_status_not_connection_error(self):
        set_state(status=429)
        with self.assertRaises(FreeTokenAPIError) as ctx:
            list(self.client.stream_chat([ChatMessage("user", "x")]))
        self.assertEqual(ctx.exception.status, 429)


class RetryTests(FakeServerTestCase):
    def test_transient_failure_retries_then_succeeds(self):
        attempts = {"n": 0}
        real_urlopen = __import__("urllib.request", fromlist=["urlopen"]).urlopen

        def flaky(req, timeout=None):
            attempts["n"] += 1
            if attempts["n"] == 1:
                raise urllib.error.URLError("connection reset")
            return real_urlopen(req, timeout=timeout)

        import urllib.request as ur
        with mock.patch.object(ur, "urlopen", side_effect=flaky):
            client = FreeTokenClient(port=self.port, retries=2, backoff_seconds=0.01)
            self.assertEqual(client.model_ids(), ["Qwen3.6-35B-A3B"])
        self.assertEqual(attempts["n"], 2)


class HandlerTests(FakeServerTestCase):
    def _handler(self) -> LocalLLM:
        llm = LocalLLM.make_default()
        llm.client = FreeTokenClient(port=self.port, retries=0)
        return llm

    def test_complete_returns_text(self):
        self.assertEqual(self._handler().complete("hi"), "PONG")

    def test_complete_with_usage_reports_real_tokens(self):
        text, usage = self._handler().complete_with_usage("hi")
        self.assertEqual(text, "PONG")
        self.assertFalse(usage["estimated"])
        self.assertEqual(usage["usage"]["prompt_tokens"], 5)

    def test_usage_estimated_flagged_when_server_omits_usage(self):
        set_state(chat_body=json.dumps({"choices": [{"message": {"content": "hello"}}]}))
        text, usage = self._handler().complete_with_usage("hi")
        self.assertEqual(text, "hello")
        self.assertTrue(usage["estimated"])
        self.assertGreater(usage["usage"]["completion_tokens_est"], 0)

    def test_complete_raises_honestly_when_up_but_empty(self):
        set_state(models_body=json.dumps({"data": []}))
        with self.assertRaises(Exception) as ctx:
            self._handler().complete("hi")
        self.assertIn("no loaded models", str(ctx.exception))


if __name__ == "__main__":
    unittest.main()
