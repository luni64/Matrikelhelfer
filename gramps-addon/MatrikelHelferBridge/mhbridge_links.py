# -*- coding: utf-8 -*-
"""
MatrikelHelfer Bridge - "Digitalisate" gramplet (clickable scan links).

Lists, for the active person, every citation that carries an
MH_Permalink attribute - grouped by what the citation is attached to
(person, personal events, families, family events). Deliberately NOT
deduplicated by URL: one church record often backs several events
(a marriage entry is also evidence for residence, occupation, the
fathers of bride and groom, ...), so the same scan link must show up
under every event it is attached to. The person's Internet tab is the
deduplicated one-bookmark-per-scan list instead; this panel is the
event-accurate view.

Data collection (collect_scan_links) is separated from the GTK widget
so the headless test suite can cover it.
"""

import logging

from gi.repository import GLib, Gtk

from gramps.gen.datehandler import get_date
from gramps.gen.errors import HandleError
from gramps.gen.plug import Gramplet

LOG = logging.getLogger("MatrikelHelferBridge")

PERMALINK_ATTRIBUTE = "MH_Permalink"


def _links_of(db, citation_handles):
    links = []
    for handle in citation_handles:
        try:
            citation = db.get_citation_from_handle(handle)
        except HandleError:
            continue
        url = None
        for attribute in citation.get_attribute_list():
            if str(attribute.get_type()) == PERMALINK_ATTRIBUTE:
                url = attribute.get_value()
                break
        if url:
            links.append({"url": url,
                          "label": citation.get_page() or url})
    return links


def collect_scan_links(db, person_handle):
    """Rows: {"group": label, "sort_year": int, "links": [{url, label}]}.

    Main thread only. Only groups that actually have links are returned.
    """
    person = db.get_person_from_handle(person_handle)
    rows = []

    def add(group, sort_year, citation_handles):
        links = _links_of(db, citation_handles)
        if links:
            rows.append({"group": group, "sort_year": sort_year,
                         "links": links})

    def add_event(ref):
        try:
            event = db.get_event_from_handle(ref.ref)
        except HandleError:
            return
        label = str(event.get_type())
        date_text = get_date(event)
        if date_text:
            label += " " + date_text
        date = event.get_date_object()
        add(label, (date.get_year() if date else 0) or 0,
            event.get_citation_list())

    add("Person", -1, person.get_citation_list())
    for ref in person.get_event_ref_list():
        add_event(ref)
    for family_handle in person.get_family_handle_list():
        try:
            family = db.get_family_from_handle(family_handle)
        except HandleError:
            continue
        add("Familie", -1, family.get_citation_list())
        for ref in family.get_event_ref_list():
            add_event(ref)

    rows.sort(key=lambda row: row["sort_year"])
    return rows


class MHBridgeLinksGramplet(Gramplet):
    """Sidebar panel: one bold row per event, clickable page links below."""

    def init(self):
        self._box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL,
                            spacing=2, margin_start=6, margin_end=6,
                            margin_top=6, margin_bottom=6)
        container = self.gui.get_container_widget()
        container.remove(self.gui.textview)
        container.add(self._box)
        self._box.show_all()

    def db_changed(self):
        for signal in ("person-update", "event-update", "family-update",
                       "citation-add", "citation-update"):
            try:
                self.connect(self.dbstate.db, signal, self.update)
            except Exception:  # noqa: BLE001 - a missing signal must not break startup
                LOG.debug("signal %s not available", signal)

    def active_changed(self, handle):
        self.update()

    def main(self):
        for child in self._box.get_children():
            self._box.remove(child)

        rows = []
        if self.dbstate.is_open():
            active = self.get_active("Person")
            if active:
                try:
                    rows = collect_scan_links(self.dbstate.db, active)
                except HandleError:
                    pass

        if not rows:
            empty = Gtk.Label(label="(keine Digitalisat-Links)", xalign=0.0)
            empty.get_style_context().add_class("dim-label")
            self._box.add(empty)
        for row in rows:
            header = Gtk.Label(xalign=0.0)
            header.set_markup(
                "<b>%s</b>" % GLib.markup_escape_text(row["group"]))
            self._box.add(header)
            for link in row["links"]:
                button = Gtk.LinkButton.new_with_label(link["url"],
                                                       link["label"])
                button.set_halign(Gtk.Align.START)
                button.set_tooltip_text(link["url"])
                self._box.add(button)
        self._box.show_all()
