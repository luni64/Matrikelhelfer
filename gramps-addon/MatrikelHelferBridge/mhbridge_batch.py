# -*- coding: utf-8 -*-
"""
MatrikelHelfer Bridge - POST /capture-batch.

The whole upload of a Gramps-Modus session in ONE DbTxn, ordered
internally so every reference resolves: bare persons first, then family
links, then events, then citations and existing-citation attaches.
Temp ids (any string the client chooses, e.g. "new:...", "evt:...")
map to the created objects; a reference is either such a temp id or a
real Gramps handle. All-or-nothing: any error aborts the transaction
and Gramps never sees a partial state - a single undo reverts the
entire batch (spec 7.3 "Batch-Endpunkt").

Main-thread only (callers go through mhbridge_service.run_in_main).
"""

import logging

from gramps.gen.db import DbTxn
from gramps.gen.lib import (ChildRef, Date, Event, EventRef, EventRoleType,
                            EventType, Family, Surname)

from mhbridge_capture import (InvalidPayload, StaleObject, _GENDERS, _brief,
                              _event_participants, _partners, _resolve_place,
                              _resolve_repository, _resolve_source,
                              _set_type, _source_brief, apply_person_url,
                              build_citation, build_date,
                              build_person_object)

LOG = logging.getLogger("MatrikelHelferBridge")


def do_capture_batch(db, payload):
    """Execute the batch. Returns (response, created_object_count)."""
    persons_spec = payload.get("persons") or []
    families_spec = payload.get("families") or []
    events_spec = payload.get("events") or []
    citations_spec = payload.get("citations") or []
    attach_spec = payload.get("attach") or []
    updates_spec = payload.get("updates") or []
    deletes_spec = payload.get("deletes") or []
    if not (persons_spec or families_spec or events_spec
            or citations_spec or attach_spec or updates_spec
            or deletes_spec):
        raise InvalidPayload("empty batch")

    corrections = len(updates_spec) + len(deletes_spec)
    label = ("MatrikelHelfer: Stapel (%d Personen, %d Ereignisse, %d Zitate%s)"
             % (len(persons_spec), len(events_spec), len(citations_spec),
                ", %d Korrekturen" % corrections if corrections else ""))[:100]

    tmp_persons = {}
    tmp_families = {}
    tmp_events = {}
    event_owner = {}      # tmp event -> ("person"|"family", owner object)
    counters = {"created": 0}
    created_persons = {}
    created_families = {}
    created_events = {}
    citation_results = []
    attach_results = []
    updated = []
    deleted = []

    with DbTxn(label, db) as trans:
        def person_ref(ref, what):
            if not ref or not isinstance(ref, str):
                raise InvalidPayload("%s: person reference missing" % what)
            if ref in tmp_persons:
                return tmp_persons[ref]
            return db.get_person_from_handle(ref)

        def family_ref(ref, what):
            if not ref or not isinstance(ref, str):
                raise InvalidPayload("%s: family reference missing" % what)
            if ref in tmp_families:
                return tmp_families[ref]
            return db.get_family_from_handle(ref)

        def event_ref_obj(ref, what):
            if not ref or not isinstance(ref, str):
                raise InvalidPayload("%s: event reference missing" % what)
            if ref in tmp_events:
                return tmp_events[ref]
            return db.get_event_from_handle(ref)

        # -- 0) staleness guard, BEFORE this batch commits anything ---
        # expect_change protects against edits made OUTSIDE this batch,
        # so it must be evaluated up front: later steps legitimately
        # commit the very objects an update targets (adding a parent
        # commits the CHILD person; an event commits its owner), which
        # bumps their change time inside the transaction and would trip
        # a false CONFLICT if checked at apply time.
        def check_change(obj, spec, what):
            expect = spec.get("expect_change")
            if expect is not None and obj.get_change_time() != expect:
                raise StaleObject(
                    "%s wurde inzwischen in Gramps geändert - bitte neu "
                    "laden" % what)

        for spec in updates_spec:
            kind = spec.get("type")
            if kind == "person":
                check_change(db.get_person_from_handle(spec.get("handle")),
                             spec, "Person")
            elif kind == "event":
                check_change(db.get_event_from_handle(spec.get("handle")),
                             spec, "Ereignis")
            elif kind == "citation":
                check_change(db.get_citation_from_handle(spec.get("handle")),
                             spec, "Zitat")
            # unknown kinds are rejected in the update step below
        for spec in deletes_spec:
            if spec.get("type") == "event":
                check_change(db.get_event_from_handle(spec.get("handle")),
                             spec, "Ereignis")

        # -- 1) bare persons ------------------------------------------
        for spec in persons_spec:
            tmp = spec.get("tmp")
            if not tmp:
                raise InvalidPayload("batch person needs 'tmp'")
            if tmp in tmp_persons:
                raise InvalidPayload("duplicate person tmp '%s'" % tmp)
            person = build_person_object(db, spec, trans, counters)
            tmp_persons[tmp] = person
            created_persons[tmp] = _brief(person, False)

        # -- 2) families: create new ones or ADD members to existing --
        for spec in families_spec:
            tmp = spec.get("tmp")
            handle = spec.get("handle")
            if bool(tmp) == bool(handle):
                raise InvalidPayload(
                    "batch family needs exactly one of 'tmp'/'handle'")
            if tmp:
                if tmp in tmp_families:
                    raise InvalidPayload("duplicate family tmp '%s'" % tmp)
                family = Family()
                db.add_family(family, trans)
                counters["created"] += 1
                tmp_families[tmp] = family
                created_families[tmp] = _brief(family, False)
            else:
                family = db.get_family_from_handle(handle)

            for key, get_slot, set_slot in (
                    ("father", family.get_father_handle,
                     family.set_father_handle),
                    ("mother", family.get_mother_handle,
                     family.set_mother_handle)):
                if not spec.get(key):
                    continue
                partner = person_ref(spec[key], "family." + key)
                if get_slot() == partner.get_handle():
                    continue
                if get_slot():
                    raise InvalidPayload("family already has a " + key)
                set_slot(partner.get_handle())
                partner.add_family_handle(family.get_handle())
                db.commit_person(partner, trans)

            existing_children = {ref.ref
                                 for ref in family.get_child_ref_list()}
            for child_spec in spec.get("children") or []:
                child = person_ref(child_spec, "family.children")
                if child.get_handle() in existing_children:
                    continue
                child_ref = ChildRef()
                child_ref.set_reference_handle(child.get_handle())
                family.add_child_ref(child_ref)
                child.add_parent_family_handle(family.get_handle())
                db.commit_person(child, trans)
            db.commit_family(family, trans)

        # -- 3) events -------------------------------------------------
        for spec in events_spec:
            tmp = spec.get("tmp")
            if not tmp:
                raise InvalidPayload("batch event needs 'tmp'")
            if tmp in tmp_events:
                raise InvalidPayload("duplicate event tmp '%s'" % tmp)
            person_r = spec.get("person")
            family_r = spec.get("family")
            if bool(person_r) == bool(family_r):
                raise InvalidPayload(
                    "batch event needs exactly one of 'person'/'family'")
            event = Event()
            event.set_type(_set_type(EventType(), spec.get("type")))
            date = build_date(spec.get("date"))
            if date is not None:
                event.set_date_object(date)
            if spec.get("place"):
                # by name: reuse an existing place, else create one
                event.set_place_handle(_resolve_place(
                    db, spec["place"], trans, counters))
            if spec.get("description"):
                event.set_description(spec["description"])
            db.add_event(event, trans)
            counters["created"] += 1
            tmp_events[tmp] = event
            created_events[tmp] = _brief(event, False)

            event_ref = EventRef()
            event_ref.set_reference_handle(event.get_handle())
            if person_r:
                owner = person_ref(person_r, "event.person")
                event_ref.set_role(_set_type(EventRoleType(),
                                             spec.get("role") or "Primary"))
                owner.add_event_ref(event_ref)
                db.commit_person(owner, trans)
                event_owner[tmp] = ("person", owner)
            else:
                owner = family_ref(family_r, "event.family")
                event_ref.set_role(_set_type(EventRoleType(),
                                             spec.get("role") or "Family"))
                owner.add_event_ref(event_ref)
                db.commit_family(owner, trans)
                event_owner[tmp] = ("family", owner)

        # -- 4) citations (create + attach) ---------------------------
        # repo/source resolution cached per identical block, so several
        # citations of the same book share ONE created source even
        # inside this transaction
        source_cache = {}

        def resolve_source_cached(repo_block, source_block):
            key = repr((repo_block, source_block))
            if key not in source_cache:
                repo_brief, repo_handle = _resolve_repository(
                    db, repo_block, trans, counters)
                src_brief, source = _resolve_source(
                    db, source_block, repo_handle, trans, counters)
                source_cache[key] = (repo_brief, src_brief, source)
            return source_cache[key]

        for spec in citations_spec:
            citation_block = spec.get("citation")
            if not isinstance(citation_block, dict):
                raise InvalidPayload("batch citation needs a 'citation' block")
            repo_brief, src_brief, source = resolve_source_cached(
                spec.get("repository"), spec.get("source"))
            citation, note_briefs = build_citation(
                db, citation_block, source, trans, counters)

            url_recipients = []
            for target in spec.get("targets") or []:
                target_type = target.get("type")
                ref = target.get("ref")
                if target_type == "event":
                    obj = event_ref_obj(ref, "citation target")
                    if citation.get_handle() not in obj.get_citation_list():
                        obj.add_citation(citation.get_handle())
                        db.commit_event(obj, trans)
                    if ref in event_owner:
                        kind, owner = event_owner[ref]
                        url_recipients += ([owner.get_handle()]
                                           if kind == "person"
                                           else _partners(owner))
                    else:
                        url_recipients += _event_participants(
                            db, obj.get_handle())
                elif target_type == "person":
                    obj = person_ref(ref, "citation target")
                    if citation.get_handle() not in obj.get_citation_list():
                        obj.add_citation(citation.get_handle())
                        db.commit_person(obj, trans)
                    url_recipients.append(obj.get_handle())
                elif target_type == "family":
                    obj = family_ref(ref, "citation target")
                    if citation.get_handle() not in obj.get_citation_list():
                        obj.add_citation(citation.get_handle())
                        db.commit_family(obj, trans)
                    url_recipients += _partners(obj)
                else:
                    raise InvalidPayload(
                        "citation target type must be person, event or family")

            result = {"handle": citation.get_handle(),
                      "gramps_id": citation.get_gramps_id(),
                      "repository": repo_brief,
                      "source": src_brief,
                      "notes": note_briefs}
            person_url = spec.get("person_url")
            if person_url:
                result["person_urls"] = apply_person_url(
                    db, trans, person_url, url_recipients)
            citation_results.append(result)

        # -- 5) attach EXISTING citations to further targets ----------
        for spec in attach_spec:
            citation = db.get_citation_from_handle(spec.get("citation"))
            attached = []
            for target in spec.get("targets") or []:
                target_type = target.get("type")
                ref = target.get("ref")
                obj = (event_ref_obj(ref, "attach target")
                       if target_type == "event"
                       else person_ref(ref, "attach target")
                       if target_type == "person"
                       else family_ref(ref, "attach target")
                       if target_type == "family"
                       else None)
                if obj is None:
                    raise InvalidPayload(
                        "attach target type must be person, event or family")
                was_existing = citation.get_handle() in obj.get_citation_list()
                if not was_existing:
                    obj.add_citation(citation.get_handle())
                    if target_type == "event":
                        db.commit_event(obj, trans)
                    elif target_type == "person":
                        db.commit_person(obj, trans)
                    else:
                        db.commit_family(obj, trans)
                attached.append({"type": target_type,
                                 "handle": obj.get_handle(),
                                 "was_existing": was_existing})
            attach_results.append({"citation": citation.get_handle(),
                                   "attached_to": attached})

        # -- 6) updates: EXISTING persons (name/gender) and events ----
        # Sparse semantics: only keys present in 'set' change (a null
        # value clears the field); everything else - alternate names,
        # suffixes, notes, media - stays untouched. The staleness guard
        # already ran in step 0 (a mismatch aborts the whole batch with
        # 409 CONFLICT, so a newer Gramps-side edit is never silently
        # overwritten).
        for spec in updates_spec:
            kind = spec.get("type")
            set_block = spec.get("set")
            if not isinstance(set_block, dict) or not set_block:
                raise InvalidPayload("update needs a non-empty 'set' block")
            if kind == "person":
                person = db.get_person_from_handle(spec.get("handle"))
                name = person.get_primary_name()
                if "given" in set_block:
                    name.set_first_name(set_block["given"] or "")
                if "surname" in set_block:
                    surnames = name.get_surname_list()
                    if surnames:
                        # only the FIRST surname's value - prefixes,
                        # patronymics and further surnames survive
                        surnames[0].set_surname(set_block["surname"] or "")
                        name.set_surname_list(surnames)
                    else:
                        surname = Surname()
                        surname.set_surname(set_block["surname"] or "")
                        name.add_surname(surname)
                if "gender" in set_block:
                    code = set_block["gender"] or "U"
                    if code not in _GENDERS:
                        raise InvalidPayload("gender must be M, F or U")
                    person.set_gender(_GENDERS[code])
                db.commit_person(person, trans)
                updated.append({"type": "person",
                                "handle": person.get_handle()})
            elif kind == "event":
                event = db.get_event_from_handle(spec.get("handle"))
                if "type" in set_block:
                    event.set_type(_set_type(EventType(),
                                             set_block["type"]))
                if "date" in set_block:
                    date = build_date(set_block["date"])
                    event.set_date_object(date if date is not None
                                          else Date())
                if "place" in set_block:
                    place_spec = set_block["place"]
                    event.set_place_handle(
                        _resolve_place(db, place_spec, trans, counters)
                        if place_spec else "")
                if "description" in set_block:
                    event.set_description(set_block["description"] or "")
                db.commit_event(event, trans)
                updated.append({"type": "event",
                                "handle": event.get_handle()})
            elif kind == "citation":
                citation = db.get_citation_from_handle(spec.get("handle"))
                if "page" in set_block:
                    citation.set_page(set_block["page"] or "")
                db.commit_citation(citation, trans)
                updated.append({"type": "citation",
                                "handle": citation.get_handle()})
            else:
                raise InvalidPayload(
                    "update type must be person, event or citation")

        # -- 7) deletes: EXISTING events only ------------------------
        # remove_handle_references does the full internal cleanup
        # (event_ref lists AND the birth/death indices pointing into
        # them) - the same call Gramps' own delete uses. Persons,
        # families, sources stay undeletable by design. Staleness was
        # checked in step 0.
        for spec in deletes_spec:
            if spec.get("type") != "event":
                raise InvalidPayload("only events can be deleted")
            event = db.get_event_from_handle(spec.get("handle"))
            handle_list = [event.get_handle()]
            for class_name, ref_handle in list(
                    db.find_backlink_handles(event.get_handle())):
                if class_name == "Person":
                    obj = db.get_person_from_handle(ref_handle)
                    obj.remove_handle_references("Event", handle_list)
                    db.commit_person(obj, trans)
                elif class_name == "Family":
                    obj = db.get_family_from_handle(ref_handle)
                    obj.remove_handle_references("Event", handle_list)
                    db.commit_family(obj, trans)
            db.remove_event(event.get_handle(), trans)
            deleted.append({"type": "event",
                            "handle": spec.get("handle")})

    response = {
        "request_id": payload.get("request_id"),
        "created": {
            "persons": created_persons,
            "families": created_families,
            "events": created_events,
            "citations": citation_results,
            "attaches": attach_results,
        },
        "updated": updated,
        "deleted": deleted,
        "transaction_label": label,
    }
    LOG.info("capture-batch: %s (%d new objects)", label, counters["created"])
    return response, counters["created"]
