# -*- coding: utf-8 -*-
"""
Bridge Spike - stage 0 of the MatrikelHelfer <-> Gramps bridge.

Throwaway addon that answers one question: can an HTTP server running
inside the Gramps process write to the open tree reliably, with all
database work marshalled to the GTK main thread?

Deliberately NOT spec-compliant (no token, no discovery file, fixed
port). The one part built properly is the marshalling layer
(run_in_main), because that is the actual test subject and will be
reused unchanged in the real bridge.

Endpoints (127.0.0.1:8791):
  GET  /spike/read                      -> tree name + person count
  POST /spike/write?gramps_id=I0001     -> in ONE transaction: create a
       Source (with an MH_SourceKey attribute) and a Citation, attach
       the citation to the given person. Without gramps_id the default
       person (or the first person found) is used.

Use against a TEST TREE only.
"""

import json
import logging
import threading
import urllib.parse
from http.server import BaseHTTPRequestHandler, HTTPServer

from gi.repository import GLib

from gramps.gen.db import DbTxn
from gramps.gen.display.name import displayer as name_displayer
from gramps.gen.lib import Citation, Source, SrcAttribute
from gramps.gen.plug import Gramplet

PORT = 8791
MAIN_THREAD_TIMEOUT_S = 30.0

LOG = logging.getLogger("BridgeSpike")


class SpikeError(Exception):
    """Error with a defined HTTP status and machine-readable code."""

    def __init__(self, status, code, message):
        super().__init__(message)
        self.status = status
        self.code = code


class MainThreadTimeout(SpikeError):
    def __init__(self):
        super().__init__(504, "MAIN_THREAD_TIMEOUT",
                         "Main thread did not finish within the time limit.")


def run_in_main(func, timeout=MAIN_THREAD_TIMEOUT_S):
    """Run func() on the GTK main thread, wait for the result.

    This is THE marshalling layer (requirements doc section 6.2):
    queue via GLib.idle_add, hand back result/exception via a
    threading.Event. Every database access goes through here;
    no handler may touch the db directly from the worker thread.
    """
    box = {}
    done = threading.Event()

    def task():
        try:
            box["result"] = func()
        except BaseException as exc:  # noqa: BLE001 - must never kill the main loop
            box["error"] = exc
        finally:
            done.set()
        return False  # one-shot idle source

    GLib.idle_add(task)
    if not done.wait(timeout):
        raise MainThreadTimeout()
    if "error" in box:
        raise box["error"]
    return box["result"]


class SpikeHandler(BaseHTTPRequestHandler):
    """HTTP handler. Runs on the server worker thread."""

    gramplet = None  # set by the gramplet on server start
    protocol_version = "HTTP/1.1"

    # -- helpers -----------------------------------------------------

    def _send_json(self, status, payload):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _send_error(self, status, code, message):
        self._send_json(status, {"error": {"code": code, "message": message}})

    def _dispatch(self, func):
        """Marshal func to the main thread and translate errors."""
        try:
            result = run_in_main(func)
        except SpikeError as err:
            self._send_error(err.status, err.code, str(err))
        except Exception:  # noqa: BLE001
            LOG.exception("unexpected error while handling %s", self.path)
            self._send_error(500, "INTERNAL_ERROR",
                            "Unexpected error, details in the Gramps log.")
        else:
            self._send_json(200, result)

    def log_message(self, fmt, *args):  # silence stderr, route to logging
        LOG.debug("%s - %s", self.address_string(), fmt % args)

    # -- routes ------------------------------------------------------

    def do_GET(self):
        path = urllib.parse.urlparse(self.path).path
        if path == "/spike/read":
            self._dispatch(self.gramplet.read_task)
        else:
            self._send_error(404, "NOT_FOUND", "Unknown path: " + path)

    def do_POST(self):
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path != "/spike/write":
            self._send_error(404, "NOT_FOUND", "Unknown path: " + parsed.path)
            return

        # gramps_id from query string; JSON body is accepted as fallback
        query = urllib.parse.parse_qs(parsed.query)
        gramps_id = (query.get("gramps_id") or [None])[0]
        length = int(self.headers.get("Content-Length") or 0)
        if gramps_id is None and length:
            try:
                payload = json.loads(self.rfile.read(length) or b"{}")
                gramps_id = payload.get("gramps_id")
            except (ValueError, UnicodeDecodeError):
                self._send_error(400, "INVALID_REQUEST", "Body is not valid JSON.")
                return
        elif length:
            self.rfile.read(length)  # drain body so keep-alive stays usable

        self._dispatch(lambda: self.gramplet.write_task(gramps_id))


class BridgeSpikeGramplet(Gramplet):
    """Hosts the HTTP server, shows status, holds the task code."""

    def init(self):
        self.write_count = 0
        self._lines = []
        self._server = None
        self._server_thread = None
        self._start_server()

    # -- server lifecycle (called on the main thread) ----------------

    def _start_server(self):
        try:
            SpikeHandler.gramplet = self
            self._server = HTTPServer(("127.0.0.1", PORT), SpikeHandler)
        except OSError as err:
            self._note("ERROR: cannot bind port %d (%s)" % (PORT, err))
            return
        self._server_thread = threading.Thread(
            target=self._server.serve_forever, name="BridgeSpikeHTTP", daemon=True)
        self._server_thread.start()
        self._note("server running on http://127.0.0.1:%d" % PORT)
        self._note("write test:  POST /spike/write?gramps_id=I0001")

    def _stop_server(self):
        server = self._server
        self._server = None
        if server is None:
            return

        def stopper():  # shutdown() must not run on the serve_forever thread
            try:
                server.shutdown()
                server.server_close()
            except Exception:  # noqa: BLE001
                LOG.exception("error during server shutdown")

        threading.Thread(target=stopper, name="BridgeSpikeStop", daemon=True).start()
        self._note("server stopped")

    def on_save(self):  # called when Gramps shuts the gramplet down
        self._stop_server()

    # -- status display (main thread only) ---------------------------

    def _note(self, text):
        self._lines.append(text)
        self.set_text("\n".join(self._lines[-12:]))

    # -- tasks: run exclusively on the main thread via run_in_main ---

    def _open_db(self):
        if not self.dbstate.is_open():
            raise SpikeError(409, "NO_TREE_OPEN", "No family tree is open.")
        return self.dbstate.db

    def read_task(self):
        db = self._open_db()
        result = {
            "tree_open": True,
            "tree_name": db.get_dbname(),
            "person_count": db.get_number_of_people(),
        }
        self._note("read: %s, %d persons" % (result["tree_name"],
                                             result["person_count"]))
        return result

    def write_task(self, gramps_id):
        db = self._open_db()

        if gramps_id:
            person = db.get_person_from_gramps_id(gramps_id)
            if person is None:
                raise SpikeError(404, "NOT_FOUND",
                                 "No person with ID %s." % gramps_id)
        else:
            person = db.get_default_person()
            if person is None:
                person = next(
                    (db.get_person_from_handle(h)
                     for h in db.iter_person_handles()), None)
            if person is None:
                raise SpikeError(404, "NOT_FOUND", "Tree has no persons.")

        self.write_count += 1
        n = self.write_count

        with DbTxn("Bridge Spike: source+citation #%d" % n, db) as trans:
            source = Source()
            source.set_title("Spike source #%03d" % n)
            attr = SrcAttribute()
            attr.set_type("MH_SourceKey")
            attr.set_value("spike-%04d" % n)
            source.add_attribute(attr)
            db.add_source(source, trans)

            citation = Citation()
            citation.set_reference_handle(source.get_handle())
            citation.set_page("p. %d, entry %d" % (n, n))
            db.add_citation(citation, trans)

            person.add_citation(citation.get_handle())
            db.commit_person(person, trans)

        result = {
            "write_no": n,
            "person": {
                "gramps_id": person.get_gramps_id(),
                "name": name_displayer.display(person),
            },
            "source": {
                "handle": source.get_handle(),
                "gramps_id": source.get_gramps_id(),
            },
            "citation": {
                "handle": citation.get_handle(),
                "gramps_id": citation.get_gramps_id(),
            },
        }
        self._note("write #%d -> %s (%s)" % (n, result["person"]["name"],
                                             result["citation"]["gramps_id"]))
        return result
