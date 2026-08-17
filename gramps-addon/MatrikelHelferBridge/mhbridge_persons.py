# -*- coding: utf-8 -*-
"""
MatrikelHelfer Bridge - person search and detail (stage 4, spec 5.4/5.5).

Every function here MUST run on the GTK main thread (callers go through
mhbridge_service.run_in_main); the database is accessed directly.

Search strategy (spec 6.4): the first search after a tree change builds
an in-memory index over all names, birth/death summaries and event
place names. Any person/family/event/place change invalidates the whole
index (coarse, rebuilt on next search) - correctness over cleverness.
"""

import logging
import time

from gramps.gen.datehandler import get_date
from gramps.gen.display.name import displayer as name_displayer
from gramps.gen.display.place import displayer as place_displayer
from gramps.gen.errors import HandleError
from gramps.gen.lib import Person
from gramps.gen.utils.db import get_birth_or_fallback, get_death_or_fallback

LOG = logging.getLogger("MatrikelHelferBridge")

DEFAULT_LIMIT = 50
MAX_LIMIT = 500

_OTHER = getattr(Person, "OTHER", None)


def _gender_code(gender):
    if gender == Person.MALE:
        return "M"
    if gender == Person.FEMALE:
        return "F"
    if _OTHER is not None and gender == _OTHER:
        return "O"
    return "U"


def _event_summary(db, event):
    """date/place summary used for the birth/death fields (5.4)."""
    if event is None:
        return None
    date = event.get_date_object()
    year = date.get_year() if date is not None else 0
    return {
        "date_text": get_date(event) or None,
        "sort_year": year or None,
        "place": place_displayer.display_event(db, event) or None,
    }


def _person_brief(db, handle):
    try:
        person = db.get_person_from_handle(handle)
    except HandleError:
        return None
    return {
        "handle": handle,
        "gramps_id": person.get_gramps_id(),
        "primary_name": name_displayer.display(person),
    }


class PersonIndex:
    """In-memory search index; invalidated on any relevant db change."""

    def __init__(self):
        self._entries = None
        self._by_handle = None

    def invalidate(self):
        self._entries = None
        self._by_handle = None

    # -- build -------------------------------------------------------

    def _ensure(self, db):
        if self._entries is not None:
            return
        start = time.perf_counter()
        entries = []
        for person in db.iter_people():
            entries.append(self._index_person(db, person))
        entries.sort(key=lambda e: (e["surname"].casefold(),
                                    e["given"].casefold()))
        self._entries = entries
        self._by_handle = {e["handle"]: e for e in entries}
        LOG.info("person index built: %d persons in %.0f ms",
                 len(entries), (time.perf_counter() - start) * 1000)

    @staticmethod
    def _index_person(db, person):
        primary = person.get_primary_name()

        surname_parts, given_parts = [], []
        for name in [primary] + person.get_alternate_names():
            given_parts.append(name.get_first_name())
            given_parts.append(name.get_call_name())
            for surname in name.get_surname_list():
                surname_parts.append(surname.get_surname())
        surnames_blob = " ".join(p for p in surname_parts if p).casefold()
        givens_blob = " ".join(p for p in given_parts if p).casefold()

        places = set()
        for ref in person.get_event_ref_list():
            try:
                event = db.get_event_from_handle(ref.ref)
            except HandleError:
                continue
            place = place_displayer.display_event(db, event)
            if place:
                places.add(place.casefold())

        parent_handles = []
        family_handle = person.get_main_parents_family_handle()
        if family_handle:
            try:
                family = db.get_family_from_handle(family_handle)
                for handle in (family.get_father_handle(),
                               family.get_mother_handle()):
                    if handle:
                        parent_handles.append(handle)
            except HandleError:
                pass

        birth = _event_summary(db, get_birth_or_fallback(db, person))
        death = _event_summary(db, get_death_or_fallback(db, person))

        return {
            "handle": person.get_handle(),
            "gramps_id": person.get_gramps_id(),
            "primary_name": name_displayer.display(person),
            "surname": primary.get_surname() or "",
            "given": primary.get_first_name() or "",
            "call_name": primary.get_call_name() or None,
            "gender": _gender_code(person.get_gender()),
            "birth": birth,
            "death": death,
            "parent_handles": parent_handles,
            "_surnames": surnames_blob,
            "_givens": givens_blob,
            "_names": surnames_blob + " " + givens_blob,
            "_places": " | ".join(places),
        }

    # -- query (5.4) -------------------------------------------------

    def search(self, db, q=None, surname=None, given=None,
               birth_from=None, birth_to=None, place=None,
               limit=DEFAULT_LIMIT, offset=0):
        self._ensure(db)

        q_tokens = q.casefold().split() if q else None
        surname_cf = surname.casefold() if surname else None
        given_cf = given.casefold() if given else None
        place_cf = place.casefold() if place else None

        def matches(entry):
            if q_tokens and not all(tok in entry["_names"]
                                    for tok in q_tokens):
                return False
            if surname_cf and surname_cf not in entry["_surnames"]:
                return False
            if given_cf and given_cf not in entry["_givens"]:
                return False
            if birth_from is not None or birth_to is not None:
                year = entry["birth"]["sort_year"] if entry["birth"] else None
                if year is None:
                    return False
                if birth_from is not None and year < birth_from:
                    return False
                if birth_to is not None and year > birth_to:
                    return False
            if place_cf and place_cf not in entry["_places"]:
                return False
            return True

        hits = [e for e in self._entries if matches(e)]
        page = hits[offset:offset + limit]
        return {"total": len(hits),
                "results": [self._to_result(e) for e in page]}

    def _to_result(self, entry):
        parents = []
        for handle in entry["parent_handles"]:
            parent = self._by_handle.get(handle)
            if parent is not None:
                parents.append({"handle": handle,
                                "primary_name": parent["primary_name"]})
        return {key: entry[key]
                for key in ("handle", "gramps_id", "primary_name", "surname",
                            "given", "call_name", "gender", "birth", "death")
                } | {"parents": parents}


def person_detail(db, handle):
    """Full view for the client's detail display (5.5).

    Raises HandleError for an unknown handle -> mapped to 404 NOT_FOUND
    by the HTTP layer (FA-C5: client re-validates before writing).
    """
    person = db.get_person_from_handle(handle)

    names = []
    for position, name in enumerate([person.get_primary_name()]
                                    + person.get_alternate_names()):
        names.append({
            "type": str(name.get_type()),
            "primary": position == 0,
            "given": name.get_first_name() or None,
            "call_name": name.get_call_name() or None,
            "surname": name.get_surname() or None,
            "display": name_displayer.display_name(name) or None,
        })

    def _event_row(event_ref, scope, family_handle=None):
        try:
            event = db.get_event_from_handle(event_ref.ref)
        except HandleError:
            return None
        date = event.get_date_object()
        return {
            "handle": event.get_handle(),
            "gramps_id": event.get_gramps_id(),
            "type": str(event.get_type()),
            "role": str(event_ref.get_role()),
            "scope": scope,                     # person | family
            "family_handle": family_handle,
            "date_text": get_date(event) or None,
            "sort_year": (date.get_year() or None) if date else None,
            "place": place_displayer.display_event(db, event) or None,
            "description": event.get_description() or None,
            "citation_count": len(event.get_citation_list()),
        }

    events = []
    for ref in person.get_event_ref_list():
        row = _event_row(ref, "person")
        if row:
            events.append(row)
    # family events (Marriage etc.) live on the Family object in Gramps;
    # without them the client could neither see nor target a marriage
    for fam_handle in person.get_family_handle_list():
        try:
            fam = db.get_family_from_handle(fam_handle)
        except HandleError:
            continue
        for ref in fam.get_event_ref_list():
            row = _event_row(ref, "family", fam_handle)
            if row:
                events.append(row)

    parents = []
    family_handle = person.get_main_parents_family_handle()
    if family_handle:
        try:
            family = db.get_family_from_handle(family_handle)
            for parent_handle in (family.get_father_handle(),
                                  family.get_mother_handle()):
                if parent_handle:
                    brief = _person_brief(db, parent_handle)
                    if brief:
                        parents.append(brief)
        except HandleError:
            pass

    families = []
    for family_handle in person.get_family_handle_list():
        try:
            family = db.get_family_from_handle(family_handle)
        except HandleError:
            continue
        spouse_handle = family.get_father_handle()
        if spouse_handle == handle:
            spouse_handle = family.get_mother_handle()
        children = []
        for child_ref in family.get_child_ref_list():
            brief = _person_brief(db, child_ref.ref)
            if brief:
                children.append(brief)
        families.append({
            "handle": family_handle,
            "gramps_id": family.get_gramps_id(),
            "spouse": _person_brief(db, spouse_handle) if spouse_handle else None,
            "children": children,
        })

    return {
        "handle": handle,
        "gramps_id": person.get_gramps_id(),
        "primary_name": name_displayer.display(person),
        "gender": _gender_code(person.get_gender()),
        "citation_count": len(person.get_citation_list()),
        "names": names,
        "events": events,
        "parents": parents,
        "families": families,
    }
