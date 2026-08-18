# -*- coding: utf-8 -*-
"""
MatrikelHelfer Bridge - GET /event-types.

Serves the same structured event-type catalog the Gramps event editor
offers (EventType._MENU: Life Events / Family / Religious / ...), so the
client can show the familiar grouped list instead of a hardcoded one:

  {"groups": [{"name": "<localized>", "types": [
      {"xml": "Baptism", "label": "<localized>", "is_family": false},
      ...]}],
   "custom": ["<custom types used in this tree>"]}

"xml" is the locale-independent name the capture endpoint interprets
via set_from_xml_str(); "label" is what the user sees. "is_family"
mirrors membership in the "Family" menu group - those events belong on
the Family object, not the person (spec 5.7).

Main thread only (callers go through mhbridge_service.run_in_main).
"""

from gramps.gen.const import GRAMPS_LOCALE as glocale
from gramps.gen.lib import EventType

_ = glocale.translation.sgettext

_FAMILY_GROUP_KEY = "Family"     # untranslated _MENU group label


def event_type_catalog(db):
    family_values = set()
    for label, values in EventType._MENU:
        if label == _FAMILY_GROUP_KEY:
            family_values.update(values)

    groups = []
    for label, values in EventType._MENU:
        groups.append({
            "name": _(label),
            "types": [{
                "xml": EventType(value).xml_str(),
                "label": str(EventType(value)),
                "is_family": value in family_values,
            } for value in values],
        })

    known = {t["xml"] for g in groups for t in g["types"]}
    known |= {t["label"] for g in groups for t in g["types"]}
    custom = sorted(name for name in db.get_event_types()
                    if name and name not in known)
    return {"groups": groups, "custom": custom}
