# -*- coding: utf-8 -*-
"""
MatrikelHelfer Bridge - service layer (no GTK widgets in here).

Owns the HTTP server, the main-thread marshalling layer, the discovery
file and the token. The gramplet (MatrikelHelferBridge.py) is only a
thin status/control UI on top of this.

Threading contract (requirements doc section 6.2):
  * All Gramps database access happens on the GTK main thread.
  * HTTP handlers run on worker threads and must reach the database
    exclusively via run_in_main().
  * The session cache (tree name/id, session id) is written only on the
    main thread (signal handlers in the gramplet) and read by handlers.

/ping is served from the session cache without touching the database,
so it stays responsive even while the main thread is busy.
"""

import json
import logging
import os
import secrets
import threading
import time
import urllib.parse
import uuid
from collections import deque
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from gi.repository import GLib

# Gramps 6.0 removed HOME_DIR in favor of XDG-style dirs; USER_DATA is
# <data dir>/gramps (Windows: %APPDATA%\gramps), matching FA-3/FA-C1.
from gramps.gen.const import USER_DATA, VERSION as GRAMPS_VERSION
from gramps.gen.errors import HandleError

from mhbridge_capture import InvalidPayload, do_attach, do_capture
from mhbridge_events import event_type_catalog
from mhbridge_persons import (DEFAULT_LIMIT, MAX_LIMIT, PersonIndex,
                              person_detail)
from mhbridge_sources import search_repositories, search_sources

API_VERSION = 1
ADDON_VERSION = "0.10.0"
API_PREFIX = "/api/v1"
DEFAULT_PORT = 8791
PORT_SEARCH_RANGE = 20          # FA-2: try DEFAULT_PORT .. +19
MAIN_THREAD_TIMEOUT_S = 30.0
DISCOVERY_DIR = os.path.join(USER_DATA, "matrikelhelfer")
DISCOVERY_FILE = os.path.join(DISCOVERY_DIR, "endpoint.json")

LOG = logging.getLogger("MatrikelHelferBridge")


class ExclusiveHTTPServer(ThreadingHTTPServer):
    """ThreadingHTTPServer without SO_REUSEADDR.

    http.server sets allow_reuse_address, and on Windows SO_REUSEADDR
    allows binding a port that is ALREADY in active use - a second
    bridge instance would silently double-bind 8791 instead of falling
    through to the next free port (FA-2), leaving two servers with two
    tokens and only one discovery file (symptom: /ping works, everything
    else 401).
    """

    allow_reuse_address = False


class BridgeError(Exception):
    """Error with a defined HTTP status and machine-readable code (5.1)."""

    def __init__(self, status, code, message):
        super().__init__(message)
        self.status = status
        self.code = code


class MainThreadTimeout(BridgeError):
    def __init__(self):
        super().__init__(504, "MAIN_THREAD_TIMEOUT",
                         "Main thread did not finish within the time limit.")


def run_in_main(func, timeout=MAIN_THREAD_TIMEOUT_S):
    """Run func() on the GTK main thread and wait for the result.

    Verified by the stage-0 spike (S-01..S-09). Every database access
    from a handler thread must go through here - no exceptions.
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


class BridgeService:
    """Server lifecycle, token, discovery file, request bookkeeping."""

    def __init__(self, dbstate, notify=None, discovery_file=None):
        self.dbstate = dbstate
        self._listeners = [notify] if notify else []
        self.discovery_file = discovery_file or DISCOVERY_FILE
        self._server = None
        self._thread = None
        self.port = None
        self.token = None
        self.started = None            # ISO timestamp of server start
        self.last_error = None         # shown in the gramplet on failure
        self.request_count = 0
        self.objects_created = 0       # incremented by write endpoints (stage 6)
        self.request_log = deque(maxlen=50)   # FA-6 ring buffer
        self.person_index = PersonIndex()
        self._signals_db = None
        self._idempotency = {}         # FA-7: request_id -> response, per session
        self.session = {
            "session_id": str(uuid.uuid4()),
            "tree_open": False,
            "tree_name": None,
            "tree_id": None,
        }

    # -- session cache: main thread only ----------------------------

    def refresh_session(self, new_session=True):
        """Rebuild the cached tree info; new session id on tree change."""
        tree_open = self.dbstate.is_open()
        tree_name = tree_id = None
        if tree_open:
            db = self.dbstate.db
            try:
                tree_name = db.get_dbname()
                tree_id = db.get_dbid()
            except Exception:  # noqa: BLE001 - cache must never break on odd backends
                LOG.exception("could not read tree name/id")
        self.session = {
            "session_id": (str(uuid.uuid4()) if new_session
                           else self.session["session_id"]),
            "tree_open": tree_open,
            "tree_name": tree_name,
            "tree_id": tree_id,
        }
        if new_session:
            self.person_index.invalidate()
            self._idempotency.clear()
        self._connect_db_signals()
        if self.running:
            self._write_discovery()
        self.notify()

    _INVALIDATE_SIGNALS = tuple(
        "%s-%s" % (obj, op)
        for obj in ("person", "family", "event", "place")
        for op in ("add", "update", "delete", "rebuild"))

    def _connect_db_signals(self):
        """Invalidate the person index on any relevant change (6.4).

        Main thread only. Reconnected whenever the tree changes; signals
        connected to a stale db object die with it.
        """
        db = self.dbstate.db if self.dbstate.is_open() else None
        if db is None or db is self._signals_db:
            return
        self._signals_db = db
        for signal in self._INVALIDATE_SIGNALS:
            try:
                db.connect(signal, self._on_db_modified)
            except Exception:  # noqa: BLE001 - a missing signal must not break startup
                LOG.debug("signal %s not available", signal)

    def _on_db_modified(self, *_args):
        self.person_index.invalidate()

    # -- endpoint tasks: main thread only (called via run_in_main) ---

    def _open_db(self):
        if not self.dbstate.is_open():
            raise BridgeError(409, "NO_TREE_OPEN", "No family tree is open.")
        return self.dbstate.db

    def search_persons(self, **kwargs):
        db = self._open_db()
        return self.person_index.search(db, **kwargs)

    def get_person_detail(self, handle):
        db = self._open_db()
        return person_detail(db, handle)

    def list_sources(self, **kwargs):
        db = self._open_db()
        return search_sources(db, **kwargs)

    def list_repositories(self, **kwargs):
        db = self._open_db()
        return search_repositories(db, **kwargs)

    def list_event_types(self):
        db = self._open_db()
        return event_type_catalog(db)

    def capture(self, payload):
        request_id = payload.get("request_id")
        if request_id and request_id in self._idempotency:
            LOG.info("capture: request_id %s replayed from cache", request_id)
            return self._idempotency[request_id]
        db = self._open_db()
        response, created_count = do_capture(db, payload)
        self.objects_created += created_count
        if request_id:
            self._idempotency[request_id] = response
        return response

    def attach_citation(self, citation_handle, payload):
        request_id = payload.get("request_id")
        if request_id and request_id in self._idempotency:
            LOG.info("attach: request_id %s replayed from cache", request_id)
            return self._idempotency[request_id]
        db = self._open_db()
        response = do_attach(db, citation_handle, payload)
        if request_id:
            self._idempotency[request_id] = response
        return response

    # -- lifecycle: main thread only ---------------------------------

    @property
    def running(self):
        return self._server is not None

    def start(self, base_port=DEFAULT_PORT):
        if self.running:
            return
        self.last_error = None
        handler = type("BoundHandler", (BridgeHandler,), {"service": self})
        server = None
        for port in range(base_port, base_port + PORT_SEARCH_RANGE):
            try:
                server = ExclusiveHTTPServer(("127.0.0.1", port), handler)
                self.port = port
                break
            except OSError:
                continue
        if server is None:
            self.last_error = ("no free port in %d-%d"
                               % (base_port, base_port + PORT_SEARCH_RANGE - 1))
            LOG.error("bridge server not started: %s", self.last_error)
            self.notify()
            return
        server.daemon_threads = True
        self._server = server
        self.token = secrets.token_hex(32)   # FA-4: new token per start
        self.started = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        self._thread = threading.Thread(
            target=server.serve_forever, name="MHBridgeHTTP", daemon=True)
        self._thread.start()
        self._write_discovery()
        LOG.info("bridge server listening on 127.0.0.1:%d", self.port)
        self.notify()

    def stop(self):
        server = self._server
        self._server = None
        if server is not None:
            def stopper():  # shutdown() must not run on the serve_forever thread
                try:
                    server.shutdown()
                    server.server_close()
                except Exception:  # noqa: BLE001
                    LOG.exception("error during server shutdown")
            threading.Thread(target=stopper, name="MHBridgeStop",
                             daemon=True).start()
            LOG.info("bridge server stopped")
        self.token = None
        self._delete_discovery()
        self.notify()

    def regenerate_token(self):
        if self.running:
            self.token = secrets.token_hex(32)
            self._write_discovery()
            LOG.info("token regenerated")
            self.notify()

    # -- discovery file (FA-3) ---------------------------------------

    def _write_discovery(self):
        payload = {
            "api_version": API_VERSION,
            "port": self.port,
            "token": self.token,
            "pid": os.getpid(),
            "tree_name": self.session["tree_name"],
            "gramps_version": GRAMPS_VERSION,
            "addon_version": ADDON_VERSION,
            "started": self.started,
        }
        try:
            os.makedirs(os.path.dirname(self.discovery_file), exist_ok=True)
            tmp = self.discovery_file + ".tmp"
            with open(tmp, "w", encoding="utf-8") as fh:
                json.dump(payload, fh, indent=2)
            os.replace(tmp, self.discovery_file)
        except OSError:
            LOG.exception("could not write discovery file %s",
                          self.discovery_file)

    def _delete_discovery(self):
        try:
            os.remove(self.discovery_file)
        except FileNotFoundError:
            pass
        except OSError:
            LOG.exception("could not delete discovery file %s",
                          self.discovery_file)

    # -- bookkeeping: called from handler threads --------------------

    def record_request(self, method, path, status):
        self.request_count += 1
        self.request_log.append("%s  %s %s -> %d"
                                % (time.strftime("%H:%M:%S"), method, path, status))
        self.notify()

    def add_listener(self, callback):
        """Register a UI refresh callback (called on the main thread)."""
        if callback not in self._listeners:
            self._listeners.append(callback)

    def notify(self):
        """Ask the gramplet(s) to refresh their display (any thread)."""
        def once():
            for callback in list(self._listeners):
                callback()
            return False
        GLib.idle_add(once)

    def ping_payload(self):
        """5.3 - served without touching the database.

        tree_open is read live from dbstate (plain flag, safe under the
        GIL) so a closed tree is reported even before the signal-driven
        cache refresh has run.
        """
        session = self.session
        tree_open = self.dbstate.is_open()
        return {
            "api_version": API_VERSION,
            "addon_version": ADDON_VERSION,
            "gramps_version": GRAMPS_VERSION,
            "tree_open": tree_open,
            "tree_name": session["tree_name"] if tree_open else None,
            "tree_id": session["tree_id"] if tree_open else None,
            "session_id": session["session_id"],
        }


class BridgeHandler(BaseHTTPRequestHandler):
    """HTTP handler; a subclass bound to a BridgeService is created in
    BridgeService.start(). Runs on worker threads."""

    service = None
    protocol_version = "HTTP/1.1"
    server_version = "MHBridge/" + ADDON_VERSION
    sys_version = ""

    def log_message(self, fmt, *args):  # keep stderr clean, route to logging
        LOG.debug("%s - %s", self.address_string(), fmt % args)

    # -- responses ---------------------------------------------------

    def _respond(self, status, payload):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        # NFA-3: deliberately no CORS headers
        self.end_headers()
        self.wfile.write(body)
        self.service.record_request(self.command, self.path, status)

    def _respond_error(self, status, code, message):
        self._respond(status, {"error": {"code": code,
                                         "message": message,
                                         "detail": None}})

    # -- dispatch ----------------------------------------------------

    def do_GET(self):
        self._handle()

    def do_POST(self):
        # drain the body so keep-alive connections stay usable even on
        # early rejections; stage 4+ endpoints will parse it themselves
        length = int(self.headers.get("Content-Length") or 0)
        self._body = self.rfile.read(length) if length else b""
        self._handle()

    def _handle(self):
        try:
            parsed = urllib.parse.urlparse(self.path)
            path = parsed.path

            # NFA-3: a browser always sends an Origin header on
            # cross-origin requests - the C# client never does.
            if self.headers.get("Origin"):
                self._respond_error(403, "ORIGIN_FORBIDDEN",
                                    "Requests from browsers are not accepted.")
                return

            if path == API_PREFIX + "/ping" and self.command == "GET":
                self._respond(200, self.service.ping_payload())
                return

            # FA-4: everything except /ping requires the token
            token = self.headers.get("X-MatrikelHelfer-Token") or ""
            if not self.service.token or not secrets.compare_digest(
                    token, self.service.token):
                self._respond_error(401, "UNAUTHORIZED",
                                    "Missing or invalid token.")
                return

            # NFA-3: only JSON bodies, no simple form POSTs
            if self.command == "POST":
                ctype = self.headers.get("Content-Type") or ""
                if not ctype.lower().startswith("application/json"):
                    self._respond_error(400, "INVALID_REQUEST",
                                        "Content-Type must be application/json.")
                    return

            service = self.service
            persons_prefix = API_PREFIX + "/persons"

            if self.command == "GET" and path == persons_prefix:
                args = self._person_search_args(
                    urllib.parse.parse_qs(parsed.query))
                self._respond(200, run_in_main(
                    lambda: service.search_persons(**args)))
                return

            if self.command == "GET" and path.startswith(persons_prefix + "/"):
                handle = urllib.parse.unquote(
                    path[len(persons_prefix) + 1:])
                self._respond(200, run_in_main(
                    lambda: service.get_person_detail(handle)))
                return

            if self.command == "GET" and path == API_PREFIX + "/sources":
                args = self._list_args(urllib.parse.parse_qs(parsed.query),
                                       with_attribute=True)
                self._respond(200, run_in_main(
                    lambda: service.list_sources(**args)))
                return

            if self.command == "GET" and path == API_PREFIX + "/repositories":
                args = self._list_args(urllib.parse.parse_qs(parsed.query))
                self._respond(200, run_in_main(
                    lambda: service.list_repositories(**args)))
                return

            if self.command == "GET" and path == API_PREFIX + "/event-types":
                self._respond(200, run_in_main(service.list_event_types))
                return

            if self.command == "POST" and path == API_PREFIX + "/capture":
                payload = self._json_body()
                self._respond(200, run_in_main(
                    lambda: service.capture(payload)))
                return

            citations_prefix = API_PREFIX + "/citations/"
            if (self.command == "POST" and path.startswith(citations_prefix)
                    and path.endswith("/attach")):
                handle = urllib.parse.unquote(
                    path[len(citations_prefix):-len("/attach")])
                payload = self._json_body()
                self._respond(200, run_in_main(
                    lambda: service.attach_citation(handle, payload)))
                return

            self._respond_error(404, "NOT_FOUND", "Unknown endpoint: " + path)
        except InvalidPayload as err:
            self._respond_error(400, "INVALID_REQUEST", str(err))
        except HandleError:
            self._respond_error(404, "NOT_FOUND",
                                "Referenced handle does not exist.")
        except BridgeError as err:
            self._respond_error(err.status, err.code, str(err))
        except Exception:  # noqa: BLE001 - NFA-4: no details in the response
            LOG.exception("unexpected error while handling %s", self.path)
            self._respond_error(500, "INTERNAL_ERROR",
                                "Unexpected error, details in the Gramps log.")

    def _json_body(self):
        """Parse the drained POST body; raises BridgeError on bad JSON."""
        try:
            payload = json.loads(self._body or b"{}")
        except (ValueError, UnicodeDecodeError):
            raise BridgeError(400, "INVALID_REQUEST",
                              "Body is not valid JSON.")
        if not isinstance(payload, dict):
            raise BridgeError(400, "INVALID_REQUEST",
                              "Body must be a JSON object.")
        return payload

    def _person_search_args(self, params):
        """Validate 5.4 query parameters."""
        return {
            "q": _q_text(params, "q"),
            "surname": _q_text(params, "surname"),
            "given": _q_text(params, "given"),
            "birth_from": _q_int(params, "birth_year_from"),
            "birth_to": _q_int(params, "birth_year_to"),
            "place": _q_text(params, "place"),
            **_paging(params),
        }

    def _list_args(self, params, with_attribute=False):
        """Validate 5.6 query parameters."""
        args = {"q": _q_text(params, "q"), **_paging(params)}
        if with_attribute:
            key = _q_text(params, "attribute_key")
            value = _q_text(params, "attribute_value")
            if (key is None) != (value is None):
                raise BridgeError(
                    400, "INVALID_REQUEST",
                    "attribute_key and attribute_value must be used together.")
            args["attribute_key"] = key
            args["attribute_value"] = value
        return args


def _q_text(params, name):
    values = params.get(name)
    value = values[0].strip() if values else ""
    return value or None


def _q_int(params, name, default=None):
    values = params.get(name)
    if not values or not values[0].strip():
        return default
    try:
        return int(values[0])
    except ValueError:
        raise BridgeError(400, "INVALID_REQUEST",
                          "'%s' must be an integer." % name)


def _paging(params):
    return {
        "limit": min(max(_q_int(params, "limit", DEFAULT_LIMIT), 1), MAX_LIMIT),
        "offset": max(_q_int(params, "offset", 0), 0),
    }
