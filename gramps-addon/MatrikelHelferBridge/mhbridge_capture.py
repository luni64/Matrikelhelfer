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
from gramps.gen.lib import (ChildRef, Citation, Date, Event, EventRef,
                            EventRoleType, EventType, Family, Name, Note,
                            NoteType, Person, Place, PlaceName, RepoRef,
                            Repository, RepositoryType, Source,
                            SourceMediaType, SrcAttribute, Surname, Url,
                            UrlType)

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

def _resolve_place(db, spec, trans, counters):
    """Event place by NAME: reuse an existing place whose name (or
    legacy title) matches casefolded, else create a bare top-level
    place with that name in the same transaction. Deliberately simple
    - no hierarchy, no place type: church-book work cycles through a
    handful of parish/village names, and an existing curated place
    must win over a duplicate. Returns the place handle."""
    if not isinstance(spec, dict) or not (spec.get("title") or "").strip():
        raise InvalidPayload("event place needs a 'title'")
    wanted = spec["title"].strip()
    wanted_cf = wanted.casefold()
    for place in db.iter_places():
        name = place.get_name()
        value = name.get_value() if name else ""
        if (value.casefold() == wanted_cf
                or (place.get_title() or "").casefold() == wanted_cf):
            return place.get_handle()
    place = Place()
    place_name = PlaceName()
    place_name.set_value(wanted)
    place.set_name(place_name)
    place.set_title(wanted)
    db.add_place(place, trans)
    counters["created"] += 1
    return place.get_handle()


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


# -- create_person (spec 7.3 v2: series of finds for missing persons) --

_GENDERS = {"M": Person.MALE, "F": Person.FEMALE, "U": Person.UNKNOWN}

_PERSON_LINKS = ("child_of_family", "child_of_person", "spouse_of",
                 "parent_of")


def _fill_partner_slot(family, person, gender_code):
    """Put person into the family's free father/mother slot by gender;
    unknown gender takes whichever slot is empty. Occupied -> 400."""
    if gender_code == "M" or (gender_code == "U"
                              and not family.get_father_handle()):
        if family.get_father_handle():
            raise InvalidPayload("family already has a father/partner")
        family.set_father_handle(person.get_handle())
    else:
        if family.get_mother_handle():
            raise InvalidPayload("family already has a mother/partner")
        family.set_mother_handle(person.get_handle())


def _add_child(db, family, person, trans):
    ref = ChildRef()
    ref.set_reference_handle(person.get_handle())
    family.add_child_ref(ref)
    db.commit_family(family, trans)
    person.add_parent_family_handle(family.get_handle())
    db.commit_person(person, trans)


def _new_family(db, trans, counters):
    family = Family()
    db.add_family(family, trans)
    counters["created"] += 1
    return family


def build_person_object(db, spec, trans, counters):
    """Bare person from given/surname/gender — shared by create_person
    and the batch endpoint (which links separately)."""
    given = (spec.get("given") or "").strip()
    surname_text = (spec.get("surname") or "").strip()
    if not given and not surname_text:
        raise InvalidPayload("person needs 'given' and/or 'surname'")
    gender_code = spec.get("gender") or "U"
    if gender_code not in _GENDERS:
        raise InvalidPayload("person: gender must be M, F or U")

    person = Person()
    person.set_gender(_GENDERS[gender_code])
    name = Name()
    if given:
        name.set_first_name(given)
    if surname_text:
        surname = Surname()
        surname.set_surname(surname_text)
        name.add_surname(surname)
    person.set_primary_name(name)
    db.add_person(person, trans)
    counters["created"] += 1
    return person


def _create_person(db, spec, trans, counters):
    """Create a person and link them as child/spouse/parent.

    Returns (person, created_family_or_None). Exactly one link kind is
    required - a person floating outside the tree structure is never
    what the walkable-tree workflow means.
    """
    links = [key for key in _PERSON_LINKS if spec.get(key)]
    if len(links) != 1:
        raise InvalidPayload("create_person needs exactly one of %s"
                             % ", ".join(_PERSON_LINKS))
    person = build_person_object(db, spec, trans, counters)
    gender_code = spec.get("gender") or "U"

    created_family = None
    kind = links[0]
    if kind == "child_of_family":
        family = db.get_family_from_handle(spec["child_of_family"])
        _add_child(db, family, person, trans)
    elif kind == "child_of_person":
        # single known parent, no family object yet -> create one
        parent = db.get_person_from_handle(spec["child_of_person"])
        family = _new_family(db, trans, counters)
        created_family = family
        _fill_partner_slot(family, parent,
                           {Person.MALE: "M", Person.FEMALE: "F"}
                           .get(parent.get_gender(), "U"))
        parent.add_family_handle(family.get_handle())
        db.commit_person(parent, trans)
        _add_child(db, family, person, trans)
    elif kind == "spouse_of":
        partner = db.get_person_from_handle(spec["spouse_of"])
        family_handle = spec.get("family_handle")
        if family_handle:
            # join an existing partner-less family (partner + children)
            family = db.get_family_from_handle(family_handle)
            _fill_partner_slot(family, person, gender_code)
            db.commit_family(family, trans)
        else:
            family = _new_family(db, trans, counters)
            created_family = family
            _fill_partner_slot(family, person, gender_code)
            _fill_partner_slot(family, partner,
                               {Person.MALE: "M", Person.FEMALE: "F"}
                               .get(partner.get_gender(), "U"))
            db.commit_family(family, trans)
            partner.add_family_handle(family.get_handle())
            db.commit_person(partner, trans)
        person.add_family_handle(family.get_handle())
        db.commit_person(person, trans)
    else:  # parent_of
        child = db.get_person_from_handle(spec["parent_of"])
        family_handle = (spec.get("family_handle")
                         or child.get_main_parents_family_handle())
        if family_handle:
            family = db.get_family_from_handle(family_handle)
            _fill_partner_slot(family, person, gender_code)
            db.commit_family(family, trans)
            person.add_family_handle(family.get_handle())
            db.commit_person(person, trans)
        else:
            family = _new_family(db, trans, counters)
            created_family = family
            _fill_partner_slot(family, person, gender_code)
            person.add_family_handle(family.get_handle())
            db.commit_person(person, trans)
            _add_child(db, family, child, trans)

    return person, created_family


def build_citation(db, citation_block, source, trans, counters):
    """Citation object incl. notes, attached to the source — shared by
    /capture and /capture-batch."""
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
    return citation, note_briefs


def apply_person_url(db, trans, spec, url_recipients):
    """Permalink onto the involved persons' Internet tab (5.7) — shared
    by /capture and /capture-batch. One Internet row PER EVENT: the
    description carries the event label, so the dedup key is
    path+description - the same scan may legitimately appear on several
    rows (one record backs marriage, residence, occupation, ...), but
    re-capturing the same event never piles up duplicates."""
    path = spec.get("path")
    if not path:
        raise InvalidPayload("person_url needs 'path'")
    url_results = []
    description = spec.get("description") or ""
    for handle in dict.fromkeys(url_recipients):  # dedup, keep order
        person = db.get_person_from_handle(handle)
        entry = {"handle": handle,
                 "gramps_id": person.get_gramps_id(),
                 "name": name_displayer.display(person)}
        if any(u.get_path() == path
               and u.get_description() == description
               for u in person.get_url_list()):
            url_results.append(entry | {"was_existing": True})
            continue
        url = Url()
        url.set_path(path)
        url.set_description(description)
        url.set_type(_set_type(UrlType(), spec.get("type") or "Digitalisat"))
        person.add_url(url)
        db.commit_person(person, trans)
        url_results.append(entry | {"was_existing": False})
    return url_results


# -- the capture itself ----------------------------------------------

def _transaction_label(payload):
    if payload.get("citation") is None and payload.get("create_person"):
        spec = payload["create_person"]
        name = " ".join(part for part in (spec.get("given"),
                                          spec.get("surname")) if part)
        return ("MatrikelHelfer: Person %s" % (name or "?"))[:100]
    source_block = payload.get("source") or {}
    title = ((source_block.get("create_if_missing") or {}).get("title")
             or (source_block.get("match") or {}).get("value") or "Quelle")
    page = (payload.get("citation") or {}).get("page")
    label = "MatrikelHelfer: Zitat %s" % title
    if page:
        label += ", " + page
    return label[:100]


def do_capture(db, payload):
    """Execute spec 5.7. Returns (response, created_object_count).

    The citation block is optional since create_person exists: a bare
    person creation carries no evidence to attach. Without a citation,
    targets are meaningless and rejected; a created event then simply
    has no citation.
    """
    citation_block = payload.get("citation")
    if citation_block is not None and not isinstance(citation_block, dict):
        raise InvalidPayload("'citation' must be an object")
    if citation_block is None and not payload.get("create_person") \
            and not payload.get("create_event_if_missing"):
        raise InvalidPayload("'citation' block is required (it may only be "
                             "omitted for create_person/create_event)")
    if citation_block is not None and "urls" in citation_block:
        raise InvalidPayload("citation.urls is not representable in Gramps "
                             "(Citation has no URL list); put the permalink "
                             "into an attribute or a note")
    targets = payload.get("targets") or []
    if targets and citation_block is None:
        raise InvalidPayload("targets given but no citation block")
    for target in targets:
        if not isinstance(target, dict) or not target.get("handle"):
            raise InvalidPayload("each target needs 'type' and 'handle'")

    counters = {"created": 0}
    created = {}
    attached_to = []
    label = _transaction_label(payload)

    with DbTxn(label, db) as trans:
        citation = None
        if citation_block is not None:
            created["repository"], repository_handle = _resolve_repository(
                db, payload.get("repository"), trans, counters)
            created["source"], source = _resolve_source(
                db, payload.get("source"), repository_handle, trans, counters)
            citation, note_briefs = build_citation(
                db, citation_block, source, trans, counters)
            created["citation"] = _brief(citation, False)
            created["notes"] = note_briefs

        # spec 7.3 v2: person + family link (+ event + citation below) in
        # the same transaction; "@new" in create_event refers to these
        new_person = new_person_family = None
        person_spec = payload.get("create_person")
        if person_spec:
            if not isinstance(person_spec, dict):
                raise InvalidPayload("'create_person' must be an object")
            new_person, new_person_family = _create_person(
                db, person_spec, trans, counters)
            created["person"] = _brief(new_person, False)
            if new_person_family is not None:
                created["family"] = _brief(new_person_family, False)

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
        # may be combined with targets: one shared citation attached to
        # existing objects AND a newly created event (the same record
        # often evidences several facts at once)
        event_spec = payload.get("create_event_if_missing")
        if event_spec:
            person_handle = event_spec.get("person_handle")
            family_handle = event_spec.get("family_handle")
            if bool(person_handle) == bool(family_handle):
                raise InvalidPayload(
                    "create_event_if_missing needs exactly one of "
                    "person_handle or family_handle (family events "
                    "like Marriage belong on the family)")
            # "@new" = the person/family just made by create_person
            if person_handle == "@new":
                if new_person is None:
                    raise InvalidPayload("person_handle '@new' needs a "
                                         "create_person block")
                person_handle = new_person.get_handle()
            if family_handle == "@new":
                if new_person_family is None:
                    raise InvalidPayload("family_handle '@new' needs a "
                                         "create_person that created a "
                                         "family (spouse_of/parent_of)")
                family_handle = new_person_family.get_handle()
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
                # validate only - a handle references, never creates
                db.get_place_from_handle(event_spec["place_handle"])
                event.set_place_handle(event_spec["place_handle"])
            elif event_spec.get("place"):
                event.set_place_handle(_resolve_place(
                    db, event_spec["place"], trans, counters))
            if event_spec.get("description"):
                event.set_description(event_spec["description"])
            if citation is not None:
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
            created["person_urls"] = apply_person_url(
                db, trans, person_url, url_recipients)

    response = {
        "request_id": payload.get("request_id"),
        "created": created,
        "attached_to": attached_to,
        "transaction_label": label,
    }
    LOG.info("capture: %s (%d new objects)", label, counters["created"])
    return response, counters["created"]


# -- POST /citations/{handle}/attach (5.8) ----------------------------

def do_attach(db, citation_handle, payload):
    """Attach an EXISTING citation to further objects, own transaction.

    The Gramps-Modus link view uses this when the user checks more
    events for a citation already in Gramps ("this church record also
    evidences that fact"). A target already carrying the citation is
    reported was_existing instead of being attached twice.
    """
    citation = db.get_citation_from_handle(citation_handle)  # 404 on miss
    targets = payload.get("targets") or []
    if not targets:
        raise InvalidPayload("'targets' must be a non-empty list")
    for target in targets:
        if not isinstance(target, dict) or not target.get("handle"):
            raise InvalidPayload("each target needs 'type' and 'handle'")

    label = ("MatrikelHelfer: Zitat %s anhängen"
             % (citation.get_page() or citation.get_gramps_id()))[:100]
    attached_to = []
    with DbTxn(label, db) as trans:
        for target in targets:
            target_type = target.get("type")
            obj = _get_target(db, target_type, target["handle"])
            was_existing = citation_handle in obj.get_citation_list()
            if not was_existing:
                obj.add_citation(citation_handle)
                _commit_target(db, target_type, obj, trans)
            attached_to.append({"type": target_type,
                                "handle": obj.get_handle(),
                                "gramps_id": obj.get_gramps_id(),
                                "was_existing": was_existing})

    response = {
        "request_id": payload.get("request_id"),
        "citation": {"handle": citation_handle,
                     "gramps_id": citation.get_gramps_id()},
        "attached_to": attached_to,
        "transaction_label": label,
    }
    LOG.info("attach: %s (%d target(s))", label, len(attached_to))
    return response
