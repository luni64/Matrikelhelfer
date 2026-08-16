# -*- coding: utf-8 -*-
"""
MatrikelHelfer Bridge - gramplet (status display and controls, FA-5).

All server logic lives in mhbridge_service.BridgeService; this file is
only the GTK face of it: status rows, start/stop, new-token and port
controls, plus a tail of the request ring buffer.

The gramplet hooks (init, db_changed, on_save) run on the GTK main
thread, so calling into the service's lifecycle methods from here
satisfies the threading contract.
"""

from gi.repository import Gtk, Pango

from gramps.gen.plug import Gramplet

from mhbridge_service import DEFAULT_PORT, BridgeService


class MatrikelHelferBridgeGramplet(Gramplet):

    def init(self):
        self.service = BridgeService(self.dbstate, notify=self._refresh)
        root = self._build_gui()
        container = self.gui.get_container_widget()
        container.remove(self.gui.textview)
        container.add(root)
        root.show_all()

        try:
            self.dbstate.connect("no-database", self._on_tree_closed)
        except Exception:  # noqa: BLE001 - ping falls back to a live is_open() check
            pass

        self.service.refresh_session(new_session=True)
        self.service.start(self._port_spin.get_value_as_int())

    # -- gramplet hooks (main thread) --------------------------------

    def db_changed(self):
        self.service.refresh_session(new_session=True)

    def _on_tree_closed(self, *args):
        self.service.refresh_session(new_session=True)

    def on_save(self):  # Gramps shuts the gramplet down
        self.service.stop()

    # -- UI ----------------------------------------------------------

    def _build_gui(self):
        grid = Gtk.Grid(column_spacing=8, row_spacing=4,
                        margin_start=8, margin_end=8,
                        margin_top=8, margin_bottom=8)

        def row(index, title):
            caption = Gtk.Label(label=title, xalign=1.0)
            caption.get_style_context().add_class("dim-label")
            value = Gtk.Label(label="-", xalign=0.0, selectable=True)
            value.set_hexpand(True)
            value.set_ellipsize(Pango.EllipsizeMode.END)
            grid.attach(caption, 0, index, 1, 1)
            grid.attach(value, 1, index, 1, 1)
            return value

        self._lbl_server = row(0, "Server:")
        self._lbl_tree = row(1, "Tree:")
        self._lbl_requests = row(2, "Requests:")
        self._lbl_created = row(3, "Created:")

        controls = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6)
        self._btn_toggle = Gtk.Button(label="Stop")
        self._btn_toggle.connect("clicked", self._on_toggle)
        controls.pack_start(self._btn_toggle, False, False, 0)

        self._btn_token = Gtk.Button(label="New token")
        self._btn_token.connect("clicked", self._on_new_token)
        controls.pack_start(self._btn_token, False, False, 0)

        controls.pack_start(Gtk.Label(label="Port:"), False, False, 6)
        self._port_spin = Gtk.SpinButton.new_with_range(1024, 65535, 1)
        self._port_spin.set_value(DEFAULT_PORT)
        controls.pack_start(self._port_spin, False, False, 0)
        grid.attach(controls, 0, 4, 2, 1)

        self._log_view = Gtk.TextView(editable=False, cursor_visible=False,
                                      monospace=True)
        scroller = Gtk.ScrolledWindow()
        scroller.set_policy(Gtk.PolicyType.AUTOMATIC, Gtk.PolicyType.AUTOMATIC)
        scroller.set_vexpand(True)
        scroller.set_shadow_type(Gtk.ShadowType.IN)
        scroller.add(self._log_view)
        grid.attach(scroller, 0, 5, 2, 1)

        return grid

    def _on_toggle(self, _button):
        if self.service.running:
            self.service.stop()
        else:
            self.service.start(self._port_spin.get_value_as_int())

    def _on_new_token(self, _button):
        self.service.regenerate_token()

    def _refresh(self):
        svc = self.service
        if svc.running:
            self._lbl_server.set_text("running on 127.0.0.1:%d (since %s)"
                                      % (svc.port, svc.started))
        elif svc.last_error:
            self._lbl_server.set_text("ERROR: " + svc.last_error)
        else:
            self._lbl_server.set_text("stopped")

        session = svc.session
        self._lbl_tree.set_text(session["tree_name"] if session["tree_open"]
                                else "(no tree open)")
        last = svc.request_log[-1] if svc.request_log else "-"
        self._lbl_requests.set_text("%d   last: %s" % (svc.request_count, last))
        self._lbl_created.set_text("%d objects in this session"
                                   % svc.objects_created)

        self._btn_toggle.set_label("Stop" if svc.running else "Start")
        self._btn_token.set_sensitive(svc.running)
        self._port_spin.set_sensitive(not svc.running)

        tail = list(svc.request_log)[-10:]
        self._log_view.get_buffer().set_text("\n".join(tail))
