# -*- coding: utf-8 -*-
"""
MatrikelHelfer Bridge - POST /capture (stage 6, spec 5.7/5.9).

Runs the whole capture in ONE DbTxn: repository match-or-create, source
match-or-create (linked to the repository), citation, optional event
creation, attachment to targets. DbTxn.__exit__ aborts the transaction
on any exception, so a failure anywhere leaves the tree untouched
(T-10/T-11), and a single undo in Gramps reverts a successful capture
completely (T-06).

Main-thread only (callers go through mhbridge_service.run_in_main).

Type strings in the payload ("Baptism", "Book", "Primary", ...) are
interpreted as Gramps XML type names via set_from_xml_str(), which is
locale-independent; unknown values silently become custom types.
"""

import logging

from gramps.gen.db import DbTxn
from gramps.gen.display.name import displayer as name_displayer
from gramps.gen.lib import (Citation, Date, Event, EventRef, EventRoleType,
                            EventType, Note, NoteType, RepoRef, Repository,
                            RepositoryType, Source, SourceMediaType,
                            SrcAttribute, Url, UrlType)

LOG = logging.getLogger("MatrikelHelferBridge")


class InvalidPayload(Exception):
    """Maps to 400 INVALID_REQUEST in the HTTP layer."""


# -- date building (5.9) ---------------------------------------------

_MODIFIERS = {
    "regular": Date.MOD_NONE,
    "about": Date.MOD_ABOUT,
    "before": Date.MOD_BEFORE,
    "after": Date.MOD_AFTER,
    "range": Date.MOD_RANGE,
    "span": Date.MOD_SPAN,
    "textonly": Date.MOD_TEXTONLY,
}
_CALENDARS = {"gregorian": Date.CAL_GREGORIAN, "julian": Date.CAL_JULIAN}
_QUALITIES = {
    "none": Date.QUAL_NONE,
    "estimated": Date.QUAL_ESTIMATED,
    "calculated": Date.QUAL_CALCULATED,
}
_CONFIDENCE = {
    "very_low": Citation.CONF_VERY_LOW,
    "low": Citation.CONF_LOW,
    "normal": Citation.CONF_NORMAL,
    "high": Citation.CONF_HIGH,
    "very_high": Citation.CONF_VERY_HIGH,
}


def _part(spec, name, required=False):
    value = spec.get(name)
    if value in (None, ""):
        if required:
            raise InvalidPayload("date: '%s' is required" % name)
        return 0
    if not isinstance(value, int):
        raise InvalidPayload("date: '%s' must be an integer" % name)
    return value


def build_date(spec):
    if spec is None:
        return None
    if not isinstance(spec, dict):
        raise InvalidPayload("date must be an object (see spec 5.9)")
    date_type = spec.get("type", "regular")
    if date_type not in _MODIFIERS:
        raise InvalidPayload("unknown date type '%s'" % date_type)
    calendar = spec.get("calendar") or "gregorian"
    if calendar not in _CALENDARS:
        raise InvalidPayload("unknown calendar '%s'" % calendar)
    quality = spec.get("quality") or "none"
    if quality not in _QUALITIES:
        raise InvalidPayload("unknown quality '%s'" % quality)

    date = Date()
    if date_type == "textonly":
        text = spec.get("text")
        if not text:
            raise InvalidPayload("textonly date requires 'text'")
        date.set(modifier=Date.MOD_TEXTONLY, text=text, value=(0, 0, 0, False))
        return date

    year = _part(spec, "year", required=True)
    value = (_part(spec, "day"), _part(spec, "month"), year, False)
    if date_type in ("range", "span"):
        year_end = _part(spec, "year_end", required=True)
        value = value + (_part(spec, "day_end"), _part(spec, "month_end"),
                         year_end, False)
    date.set(quality=_QUALITIES[quality], modifier=_MODIFIERS[date_type],
             calendar=_CALENDARS[calendar], value=value)
    return date


# -- helpers ---------------------------------------------------------

def _set_type(gramps_type, value):
    """Set a GrampsType from a stable XML name; unknown -> custom."""
    if value:
        gramps_type.set_from_xml_str(value)
    return gramps_type


def _add_attributes(obj, attributes, what):
    for attribute in attributes or []:
        key = attribute.get("key")
        value = attribute.get("value")
        if not key or value is None:
            raise InvalidPayload("%s: attribute needs 'key' and 'value'" % what)
        src_attribute = SrcAttribute()
        src_attribute.set_type(key)
        src_attribute.set_value(str(value))
        obj.add_attribute(src_attribute)


def _brief(obj, was_existing):
    return {"handle": obj.get_handle(), "gramps_id": obj.get_gramps_id(),
            "was_existing": was_existing}


def _partners(family):
    return [handle for handle in (family.get_father_handle(),
                                  family.get_mother_handle()) if handle]


def _event_participants(db, event_handle):
    """Persons carrying the event, incl. partners of carrying families."""
    recipients = []
    for class_name, ref_handle in db.find_backlink_handles(
            event_handle, include_classes=["Person", "Family"]):
        if class_name == "Person":
            recipients.append(ref_handle)
        else:
            recipients.extend(_partners(
                db.get_family_from_handle(ref_handle)))
    return recipients


def _get_target(db, target_type, handle):
    """Fetch a citation target; HandleError propagates -> 404 + abort."""
    if target_type == "person":
        return db.get_person_from_handle(handle)
    if target_type == "event":
        return db.get_event_from_handle(handle)
    if target_type == "family":
        return db.get_family_from_handle(handle)
    raise InvalidPayload("target type must be person, event or family")


def _commit_target(db, target_type, obj, trans):
    if target_type == "person":
        db.commit_person(obj, trans)
    elif target_type == "event":
        db.commit_event(obj, trans)
    else:
        db.commit_family(obj, trans)


# -- match-or-create -------------------------------------------------

def _resolve_repository(db, block, trans, counters):
    if not block:
        return None, None
    match = block.get("match")
    if match:
        by = match.get("by")
        if by == "handle":
            repository = db.get_repository_from_handle(match.get("value"))
            return _brief(repository, True), repository.get_handle()
        if by == "name":
            wanted = (match.get("value") or "").casefold()
            if not wanted:
                raise InvalidPayload("repository match by name needs 'value'")
            for repository in db.iter_repositories():
                if (repository.get_name() or "").casefold() == wanted:
                    return _brief(repository, True), repository.get_handle()
        else:
            raise InvalidPayload("repository match 'by' must be "
                                 "'name' or 'handle'")
    create = block.get("create_if_missing")
    if not create:
        raise InvalidPayload("repository not found and no "
                             "create_if_missing given")
    name = create.get("name")
    if not name:
        raise InvalidPayload("repository create_if_missing needs 'name'")
    repository = Repository()
    repository.set_name(name)
    repository.set_type(_set_type(RepositoryType(), create.get("type")))
    if create.get("url"):
        url = Url()
        url.set_path(create["url"])
        url.set_type(UrlType(UrlType.WEB_HOME))
        repository.add_url(url)
    db.add_repository(repository, trans)
    counters["created"] += 1
    return _brief(repository, False), repository.get_handle()


def _source_brief(source, was_existing):
    """Source briefs carry the title so the client can show the user
    WHICH source a citation was attached to - crucial when an attribute
    match reuses an existing source and ignores create_if_missing."""
    brief = _brief(source, was_existing)
    brief["title"] = source.get_title() or None
    return brief


def _resolve_source(db, block, repository_handle, trans, counters):
    if not block:
        raise InvalidPayload("'source' block is required")
    match = block.get("match")
    if match:
        by = match.get("by")
        if by == "handle":
            source = db.get_source_from_handle(match.get("value"))
            return _source_brief(source, True), source
        if by == "attribute":
            key, value = match.get("key"), match.get("value")
            if not key or value is None:
                raise InvalidPayload("source match by attribute needs "
                                     "'key' and 'value'")
            for source in db.iter_sources():
                for attribute in source.get_attribute_list():
                    if (str(attribute.get_type()) == key
                            and attribute.get_value() == value):
                        return _source_brief(source, True), source
        elif by == "title":
            wanted = (match.get("value") or "").casefold()
            if not wanted:
                raise InvalidPayload("source match by title needs 'value'")
            for source in db.iter_sources():
                if (source.get_title() or "").casefold() == wanted:
                    return _source_brief(source, True), source
        else:
            raise InvalidPayload("source match 'by' must be 'attribute', "
                                 "'title' or 'handle'")
    create = block.get("create_if_missing")
    if not create:
        raise InvalidPayload("source not found and no create_if_missing given")
    title = create.get("title")
    if not title:
        raise InvalidPayload("source create_if_missing needs 'title'")
    source = Source()
    source.set_title(title)
    if create.get("author"):
        source.set_author(create["author"])
    if create.get("publication_info"):
        source.set_publication_info(create["publication_info"])
    if create.get("abbreviation"):
        source.set_abbreviation(create["abbreviation"])
    _add_attributes(source, create.get("attributes"), "source")
    repo_ref_spec = create.get("repository_ref")
    if repo_ref_spec is not None:
        if repository_handle is None:
            raise InvalidPayload("source.repository_ref given but no "
                                 "repository resolved")
        repo_ref = RepoRef()
        repo_ref.set_reference_handle(repository_handle)
        if repo_ref_spec.get("call_number"):
            repo_ref.set_call_number(repo_ref_spec["call_number"])
        repo_ref.set_media_type(_set_type(SourceMediaType(),
                                          repo_ref_spec.get("media_type")))
        source.add_repo_reference(repo_ref)
    db.add_source(source, trans)
    counters["created"] += 1
    return _source_brief(source, False), source


# -- the capture itself ----------------------------------------------

def _transaction_label(payload):
    source_block = payload.get("source") or {}
    title = ((source_block.get("create_if_missing") or {}).get("title")
             or (source_block.get("match") or {}).get("value") or "Quelle")
    page = (payload.get("citation") or {}).get("page")
    label = "MatrikelHelfer: Zitat %s" % title
    if page:
        label += ", " + page
    return label[:100]


def do_capture(db, payload):
    """Execute spec 5.7. Returns (response, created_object_count)."""
    if not isinstance(payload.get("citation"), dict):
        raise InvalidPayload("'citation' block is required")
    citation_block = payload["citation"]
    if "urls" in citation_block:
        raise InvalidPayload("citation.urls is not representable in Gramps "
                             "(Citation has no URL list); put the permalink "
                             "into an attribute or a note")
    targets = payload.get("targets") or []
    for target in targets:
        if not isinstance(target, dict) or not target.get("handle"):
            raise InvalidPayload("each target needs 'type' and 'handle'")

    counters = {"created": 0}
    created = {}
    attached_to = []
    label = _transaction_label(payload)

    with DbTxn(label, db) as trans:
        created["repository"], repository_handle = _resolve_repository(
            db, payload.get("repository"), trans, counters)
        created["source"], source = _resolve_source(
            db, payload.get("source"), repository_handle, trans, counters)

        citation = Citation()
        citation.set_reference_handle(source.get_handle())
        if citation_block.get("page"):
            citation.set_page(citation_block["page"])
        date = build_date(citation_block.get("date"))
        if date is not None:
            citation.set_date_object(date)
        confidence = citation_block.get("confidence") or "normal"
        if confidence not in _CONFIDENCE:
            raise InvalidPayload("unknown confidence '%s'" % confidence)
        citation.set_confidence_level(_CONFIDENCE[confidence])
        _add_attributes(citation, citation_block.get("attributes"), "citation")

        note_briefs = []
        for note_spec in citation_block.get("notes") or []:
            text = note_spec.get("text")
            if not text:
                raise InvalidPayload("note needs 'text'")
            note = Note()
            note.set(text)
            note.set_type(_set_type(NoteType(), note_spec.get("type")))
            db.add_note(note, trans)
            counters["created"] += 1
            citation.add_note(note.get_handle())
            note_briefs.append({"handle": note.get_handle(),
                                "gramps_id": note.get_gramps_id()})

        db.add_citation(citation, trans)
        counters["created"] += 1
        created["citation"] = _brief(citation, False)
        created["notes"] = note_briefs

        url_recipients = []     # persons who get the permalink (5.7 person_url)

        if targets:
            for target in targets:
                target_type = target.get("type")
                obj = _get_target(db, target_type, target["handle"])
                obj.add_citation(citation.get_handle())
                _commit_target(db, target_type, obj, trans)
                attached_to.append({"type": target_type,
                                    "handle": obj.get_handle(),
                                    "gramps_id": obj.get_gramps_id()})
                if target_type == "person":
                    url_recipients.append(obj.get_handle())
                elif target_type == "family":
                    url_recipients.extend(_partners(obj))
                else:
                    url_recipients.extend(
                        _event_participants(db, obj.get_handle()))
        else:
            event_spec = payload.get("create_event_if_missing")
            if event_spec:
                person_handle = event_spec.get("person_handle")
                family_handle = event_spec.get("family_handle")
                if bool(person_handle) == bool(family_handle):
                    raise InvalidPayload(
                        "create_event_if_missing needs exactly one of "
                        "person_handle or family_handle (family events "
                        "like Marriage belong on the family)")
                if person_handle:
                    owner = db.get_person_from_handle(person_handle)
                    default_role = "Primary"
                else:
                    owner = db.get_family_from_handle(family_handle)
                    default_role = "Family"

                event = Event()
                event.set_type(_set_type(EventType(),
                                         event_spec.get("event_type")))
                event_date = build_date(event_spec.get("date"))
                if event_date is not None:
                    event.set_date_object(event_date)
                if event_spec.get("place_handle"):
                    # validate only - places are referenced, never created
                    db.get_place_from_handle(event_spec["place_handle"])
                    event.set_place_handle(event_spec["place_handle"])
                if event_spec.get("description"):
                    event.set_description(event_spec["description"])
                event.add_citation(citation.get_handle())
                db.add_event(event, trans)
                counters["created"] += 1
                created["event"] = _brief(event, False)

                event_ref = EventRef()
                event_ref.set_reference_handle(event.get_handle())
                event_ref.set_role(_set_type(EventRoleType(),
                                             event_spec.get("role")
                                             or default_role))
                owner.add_event_ref(event_ref)
                if person_handle:
                    db.commit_person(owner, trans)
                    url_recipients.append(person_handle)
                else:
                    db.commit_family(owner, trans)
                    url_recipients.extend(_partners(owner))
                attached_to.append({"type": "event",
                                    "handle": event.get_handle(),
                                    "gramps_id": event.get_gramps_id()})

        person_url = payload.get("person_url")
        if person_url:
            path = person_url.get("path")
            if not path:
                raise InvalidPayload("person_url needs 'path'")
            url_results = []
            description = person_url.get("description") or ""
            for handle in dict.fromkeys(url_recipients):  # dedup, keep order
                person = db.get_person_from_handle(handle)
                entry = {"handle": handle,
                         "gramps_id": person.get_gramps_id(),
                         "name": name_displayer.display(person)}
                # one Internet row PER EVENT: the description carries the
                # event label, so the dedup key is path+description - the
                # same scan may legitimately appear on several rows (one
                # record backs marriage, residence, occupation, ...), but
                # re-capturing the same event never piles up duplicates
                if any(u.get_path() == path
                       and u.get_description() == description
                       for u in person.get_url_list()):
                    url_results.append(entry | {"was_existing": True})
                    continue
                url = Url()
                url.set_path(path)
                url.set_description(description)
                url.set_type(_set_type(UrlType(),
                                       person_url.get("type") or "Digitalisat"))
                person.add_url(url)
                db.commit_person(person, trans)
                url_results.append(entry | {"was_existing": False})
            created["person_urls"] = url_results

    response = {
        "request_id": payload.get("request_id"),
        "created": created,
        "attached_to": attached_to,
        "transaction_label": label,
    }
    LOG.info("capture: %s (%d new objects)", label, counters["created"])
    return response, counters["created"]
