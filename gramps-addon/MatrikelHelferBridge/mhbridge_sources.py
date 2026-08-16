# -*- coding: utf-8 -*-
"""
MatrikelHelfer Bridge - source and repository search (stage 5, spec 5.6).

Main-thread only (callers go through mhbridge_service.run_in_main).
Unlike persons there is no index: even large trees hold only a few
hundred sources, so a direct scan is well below the performance targets.

The attribute lookup (attribute_key/attribute_value) is the client's
primary dedup mechanism: MatrikelHelfer stamps every source it creates
with an MH_SourceKey attribute (spec 7.2) and looks it up here before
creating a new one.
"""

from gramps.gen.errors import HandleError

DEFAULT_LIMIT = 50
MAX_LIMIT = 500


def _attributes(obj):
    return [{"key": str(attr.get_type()), "value": attr.get_value()}
            for attr in obj.get_attribute_list()]


def search_sources(db, q=None, attribute_key=None, attribute_value=None,
                   limit=DEFAULT_LIMIT, offset=0):
    q_cf = q.casefold() if q else None
    hits = []
    for source in db.iter_sources():
        if q_cf and q_cf not in (source.get_title() or "").casefold():
            continue
        if attribute_key is not None:
            values = {a["key"]: a["value"] for a in _attributes(source)}
            if values.get(attribute_key) != attribute_value:
                continue
        hits.append(source)
    hits.sort(key=lambda s: (s.get_title() or "").casefold())
    page = hits[offset:offset + limit]
    return {"total": len(hits),
            "results": [_source_result(db, s) for s in page]}


def _source_result(db, source):
    repositories = []
    for ref in source.get_reporef_list():
        handle = ref.get_reference_handle()
        name = None
        try:
            name = db.get_repository_from_handle(handle).get_name()
        except HandleError:
            pass
        repositories.append({
            "handle": handle,
            "name": name,
            "call_number": ref.get_call_number() or None,
            "media_type": str(ref.get_media_type()) or None,
        })
    return {
        "handle": source.get_handle(),
        "gramps_id": source.get_gramps_id(),
        "title": source.get_title() or None,
        "author": source.get_author() or None,
        "publication_info": source.get_publication_info() or None,
        "abbreviation": source.get_abbreviation() or None,
        "attributes": _attributes(source),
        "repositories": repositories,
    }


def search_repositories(db, q=None, limit=DEFAULT_LIMIT, offset=0):
    q_cf = q.casefold() if q else None
    hits = []
    for repository in db.iter_repositories():
        if q_cf and q_cf not in (repository.get_name() or "").casefold():
            continue
        hits.append(repository)
    hits.sort(key=lambda r: (r.get_name() or "").casefold())
    page = hits[offset:offset + limit]
    return {"total": len(hits),
            "results": [_repository_result(r) for r in page]}


def _repository_result(repository):
    return {
        "handle": repository.get_handle(),
        "gramps_id": repository.get_gramps_id(),
        "name": repository.get_name() or None,
        "type": str(repository.get_type()) or None,
        "urls": [{
            "path": url.get_path() or None,
            "description": url.get_description() or None,
            "type": str(url.get_type()) or None,
        } for url in repository.get_url_list()],
    }
