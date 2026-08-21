# -*- coding: utf-8 -*-
"""
Test cases for the MatrikelHelfer Bridge, driven over live HTTP from a
worker thread while the GLib main loop runs (see MHBridgeTestTool.py).

CTX is filled by the tool before the suite runs: dbstate, service,
handles (seeded objects), port, token.

Seeded data (after wipe):
  persons:  Hans Meier  *1780 (Baptism in Testheim), child of Georg+Maria
            Anna Meier  *1810 (Baptism, no place)
            Georg Meier *1750 (Baptism, no place)
            Maria Huber *1755 (Baptism, no place)
  1 place "Testheim", 1 family (Georg + Maria -> Hans, Marriage 1775)
  no sources / citations / repositories / notes

Tests are order-dependent (numbered): capture tests build on each other.
"""

import json
import unittest
import urllib.error
import urllib.request

from mhbridge_links import collect_scan_links
from mhbridge_service import run_in_main

CTX = {}


def call(method, path, token=None, body=None, headers=None):
    """Return (status, parsed json) for an API call."""
    url = "http://127.0.0.1:%d/api/v1%s" % (CTX["port"], path)
    data = None
    request = urllib.request.Request(url, method=method)
    if token:
        request.add_header("X-MatrikelHelfer-Token", token)
    for key, value in (headers or {}).items():
        request.add_header(key, value)
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        if "Content-Type" not in (headers or {}):
            request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, data, timeout=60) as response:
            return response.status, json.loads(response.read())
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read())


def get(path, **kw):
    return call("GET", path, token=CTX["token"], **kw)


def post(path, body, **kw):
    return call("POST", path, token=CTX["token"], body=body, **kw)


def in_main(func):
    return run_in_main(func)


def db():
    return CTX["dbstate"].db


CAPTURE_TEMPLATE = {
    "repository": {
        "match": {"by": "name", "value": "Matricula Online"},
        "create_if_missing": {
            "name": "Matricula Online",
            "type": "Website",
            "url": "https://data.matricula-online.eu/",
        },
    },
    "source": {
        "match": {"by": "attribute", "key": "MH_SourceKey",
                  "value": "test-taufen-001"},
        "create_if_missing": {
            "title": "Testpfarrei, Taufbuch Bd. 1 (1770-1790)",
            "author": "Kath. Pfarramt Testpfarrei",
            "attributes": [{"key": "MH_SourceKey",
                            "value": "test-taufen-001"}],
            "repository_ref": {"call_number": "T 1/1",
                               "media_type": "Book"},
        },
    },
    "citation": {
        "page": "S. 42, Eintrag 7",
        "date": {"type": "regular", "year": 1810, "month": 3, "day": 12},
        "confidence": "normal",
        "attributes": [{"key": "MH_Permalink",
                        "value": "https://data.matricula-online.eu/de/x"}],
        "notes": [{"type": "Citation", "text": "Transkription: Anna ..."}],
    },
}


def capture_payload(request_id, **overrides):
    payload = json.loads(json.dumps(CAPTURE_TEMPLATE))  # deep copy
    payload["request_id"] = request_id
    payload.update(overrides)
    return payload


class BridgeApiTests(unittest.TestCase):
    maxDiff = None

    # -- connection and security ------------------------------------

    def test_010_ping(self):
        status, body = call("GET", "/ping")
        self.assertEqual(status, 200)
        self.assertEqual(body["api_version"], 1)
        self.assertTrue(body["tree_open"])
        self.assertEqual(body["tree_name"], "MHBridgeTest")
        self.assertTrue(body["session_id"])

    def test_020_missing_token(self):
        status, body = call("GET", "/persons?q=meier")
        self.assertEqual(status, 401)
        self.assertEqual(body["error"]["code"], "UNAUTHORIZED")

    def test_021_wrong_token(self):
        status, body = call("GET", "/persons?q=meier", token="wrong")
        self.assertEqual(status, 401)
        self.assertEqual(body["error"]["code"], "UNAUTHORIZED")

    def test_030_origin_rejected(self):
        status, body = call("GET", "/ping",
                            headers={"Origin": "https://evil.example"})
        self.assertEqual(status, 403)
        self.assertEqual(body["error"]["code"], "ORIGIN_FORBIDDEN")

    def test_040_unknown_endpoint(self):
        status, body = get("/nonsense")
        self.assertEqual(status, 404)
        self.assertEqual(body["error"]["code"], "NOT_FOUND")

    def test_041_post_requires_json_content_type(self):
        status, body = call("POST", "/capture", token=CTX["token"],
                            body={"x": 1},
                            headers={"Content-Type": "text/plain"})
        self.assertEqual(status, 400)
        self.assertEqual(body["error"]["code"], "INVALID_REQUEST")

    # -- person search (5.4) ----------------------------------------

    def test_050_search_q(self):
        status, body = get("/persons?q=meier")
        self.assertEqual(status, 200)
        self.assertEqual(body["total"], 3)

    def test_051_search_surname_given(self):
        status, body = get("/persons?surname=meier&given=hans")
        self.assertEqual(status, 200)
        self.assertEqual(body["total"], 1)
        self.assertEqual(body["results"][0]["given"], "Hans")
        self.assertEqual(body["results"][0]["birth"]["sort_year"], 1780)

    def test_052_search_birth_window(self):
        status, body = get(
            "/persons?surname=meier&birth_year_from=1770&birth_year_to=1790")
        self.assertEqual(status, 200)
        self.assertEqual(body["total"], 1)
        self.assertEqual(body["results"][0]["given"], "Hans")

    def test_053_search_place(self):
        status, body = get("/persons?place=testheim")
        self.assertEqual(status, 200)
        self.assertEqual(body["total"], 1)
        self.assertEqual(body["results"][0]["given"], "Hans")

    def test_054_search_paging(self):
        status, body = get("/persons?q=meier&limit=1&offset=1")
        self.assertEqual(status, 200)
        self.assertEqual(body["total"], 3)
        self.assertEqual(len(body["results"]), 1)

    def test_055_parents_included(self):
        status, body = get("/persons?given=hans")
        self.assertEqual(status, 200)
        parents = body["results"][0]["parents"]
        self.assertEqual(len(parents), 2)
        names = " / ".join(p["primary_name"] for p in parents)
        self.assertIn("Georg", names)
        self.assertIn("Maria", names)

    def test_056_bad_int_param(self):
        status, body = get("/persons?birth_year_from=abc")
        self.assertEqual(status, 400)
        self.assertEqual(body["error"]["code"], "INVALID_REQUEST")

    def test_060_person_detail(self):
        status, body = get("/persons/" + CTX["handles"]["hans"])
        self.assertEqual(status, 200)
        self.assertEqual(body["gender"], "M")
        self.assertEqual(body["parent_family_handle"],
                         CTX["handles"]["family"])
        self.assertEqual(len(body["events"]), 1)
        event = body["events"][0]
        self.assertEqual(event["type"], "Baptism")
        self.assertEqual(event["citation_count"], 0)
        self.assertEqual(event["place"], "Testheim")
        self.assertEqual(len(body["parents"]), 2)

    def test_061_person_detail_unknown(self):
        status, body = get("/persons/doesnotexist")
        self.assertEqual(status, 404)
        self.assertEqual(body["error"]["code"], "NOT_FOUND")

    # -- sources before any capture ---------------------------------

    def test_070_sources_empty(self):
        status, body = get("/sources")
        self.assertEqual(status, 200)
        self.assertEqual(body["total"], 0)

    def test_071_sources_attribute_needs_both_params(self):
        status, body = get("/sources?attribute_key=MH_SourceKey")
        self.assertEqual(status, 400)
        self.assertEqual(body["error"]["code"], "INVALID_REQUEST")

    # -- capture (5.7) ----------------------------------------------

    def test_080_capture_creates_everything(self):
        payload = capture_payload("req-080", create_event_if_missing={
            "person_handle": CTX["handles"]["anna"],
            "event_type": "Baptism",
            "role": "Primary",
            "date": {"type": "regular", "year": 1810, "month": 3, "day": 12},
            "description": "Taufe laut Matrikel",
        })
        status, body = post("/capture", payload)
        self.assertEqual(status, 200, body)
        created = body["created"]
        self.assertFalse(created["repository"]["was_existing"])
        self.assertFalse(created["source"]["was_existing"])
        self.assertFalse(created["citation"]["was_existing"])
        self.assertFalse(created["event"]["was_existing"])
        self.assertEqual(len(created["notes"]), 1)
        self.assertEqual(body["attached_to"][0]["type"], "event")
        CTX["event_080"] = created["event"]["handle"]
        CTX["citation_080"] = created["citation"]["handle"]
        CTX["response_080"] = body

        # verify in the database, on the main thread
        event_citations = in_main(lambda: db().get_event_from_handle(
            CTX["event_080"]).get_citation_list())
        self.assertEqual(event_citations, [CTX["citation_080"]])
        anna_events = in_main(lambda: len(db().get_person_from_handle(
            CTX["handles"]["anna"]).get_event_ref_list()))
        self.assertEqual(anna_events, 2)  # seeded baptism + new one

    def test_081_capture_reuses_source_attaches_to_event(self):
        payload = capture_payload("req-081", targets=[
            {"type": "event", "handle": CTX["event_080"]}])
        payload["citation"]["page"] = "S. 43, Eintrag 1"
        status, body = post("/capture", payload)
        self.assertEqual(status, 200, body)
        self.assertTrue(body["created"]["repository"]["was_existing"])
        self.assertTrue(body["created"]["source"]["was_existing"])
        self.assertFalse(body["created"]["citation"]["was_existing"])
        self.assertNotIn("event", body["created"])
        citations = in_main(lambda: db().get_event_from_handle(
            CTX["event_080"]).get_citation_list())
        self.assertEqual(len(citations), 2)
        CTX["response_081"] = body

    def test_082_idempotent_replay(self):
        payload = capture_payload("req-081", targets=[
            {"type": "event", "handle": CTX["event_080"]}])
        payload["citation"]["page"] = "S. 43, Eintrag 1"
        status, body = post("/capture", payload)
        self.assertEqual(status, 200)
        self.assertEqual(body, CTX["response_081"])
        citations = in_main(lambda: db().get_event_from_handle(
            CTX["event_080"]).get_citation_list())
        self.assertEqual(len(citations), 2)  # unchanged

    def test_083_rollback_on_bad_target(self):
        before = in_main(lambda: (db().get_number_of_citations(),
                                  db().get_number_of_sources()))
        payload = capture_payload("req-083", targets=[
            {"type": "event", "handle": "nosuchhandle"}])
        status, body = post("/capture", payload)
        self.assertEqual(status, 404)
        after = in_main(lambda: (db().get_number_of_citations(),
                                 db().get_number_of_sources()))
        self.assertEqual(before, after)  # T-11: nothing half-written

    def test_084_citation_urls_rejected(self):
        payload = capture_payload("req-084")
        payload["citation"]["urls"] = [{"path": "https://x"}]
        status, body = post("/capture", payload)
        self.assertEqual(status, 400)
        self.assertIn("urls", body["error"]["message"])

    def test_085_bad_date_rejected(self):
        payload = capture_payload("req-085")
        payload["citation"]["date"] = {"type": "regular"}  # year missing
        status, body = post("/capture", payload)
        self.assertEqual(status, 400)
        self.assertEqual(body["error"]["code"], "INVALID_REQUEST")

    def test_086_source_attribute_lookup(self):
        status, body = get("/sources?attribute_key=MH_SourceKey"
                           "&attribute_value=test-taufen-001")
        self.assertEqual(status, 200)
        self.assertEqual(body["total"], 1)
        source = body["results"][0]
        self.assertEqual(source["repositories"][0]["call_number"], "T 1/1")

    def test_087_search_sees_new_event(self):
        # index was invalidated by the capture signals; Anna's new
        # baptism (Testheim-less) must be visible via detail
        status, body = get("/persons/" + CTX["handles"]["anna"])
        self.assertEqual(status, 200)
        counts = [e["citation_count"] for e in body["events"]]
        # seeded baptism has 0; the event from test_080 carries the
        # citations from test_080 and test_081 at this point
        self.assertEqual(sorted(counts), [0, 2])

    # -- port fallback (FA-2) ----------------------------------------

    def test_095_second_service_falls_to_next_port(self):
        """Windows SO_REUSEADDR regression: a second server must NOT
        silently double-bind the occupied port (perma-401 split brain);
        it must fall through to the next free port."""
        import os
        import tempfile
        from mhbridge_service import BridgeService

        def start_second():
            second = BridgeService(
                CTX["dbstate"],
                discovery_file=os.path.join(
                    tempfile.gettempdir(), "mhbridge_test_endpoint2.json"))
            second.refresh_session(new_session=True)
            second.start(CTX["port"])   # base port is already taken
            port, running = second.port, second.running
            second.stop()
            return port, running

        port, running = in_main(start_second)
        self.assertTrue(running)
        self.assertEqual(port, CTX["port"] + 1)

    # -- undo (T-06) -------------------------------------------------

    def test_090_undo_reverts_last_capture(self):
        before = in_main(lambda: db().get_number_of_citations())
        self.assertEqual(before, 2)
        in_main(lambda: db().undo())
        after = in_main(lambda: db().get_number_of_citations())
        self.assertEqual(after, 1)
        citations = in_main(lambda: db().get_event_from_handle(
            CTX["event_080"]).get_citation_list())
        self.assertEqual(citations, [CTX["citation_080"]])

    # -- family events (Marriage etc.) -------------------------------

    def test_105_detail_includes_family_events(self):
        status, body = get("/persons/" + CTX["handles"]["georg"])
        self.assertEqual(status, 200)
        family_events = [e for e in body["events"] if e["scope"] == "family"]
        self.assertEqual(len(family_events), 1)
        self.assertEqual(family_events[0]["type"], "Marriage")
        self.assertEqual(family_events[0]["family_handle"],
                         CTX["handles"]["family"])
        # personal events keep their scope too
        self.assertTrue(all(e["scope"] == "person"
                            for e in body["events"] if e["type"] == "Baptism"))

    def test_110_capture_creates_family_event(self):
        payload = capture_payload("req-110", create_event_if_missing={
            "family_handle": CTX["handles"]["family"],
            "event_type": "Marriage",
            "date": {"type": "regular", "year": 1775, "month": 5, "day": 2},
            "description": "Trauung laut Matrikel",
        })
        status, body = post("/capture", payload)
        self.assertEqual(status, 200, body)
        self.assertEqual(body["attached_to"][0]["type"], "event")
        event_handle = body["created"]["event"]["handle"]
        refs = in_main(lambda: [
            (ref.ref, str(ref.get_role())) for ref in
            db().get_family_from_handle(
                CTX["handles"]["family"]).get_event_ref_list()])
        self.assertIn((event_handle, "Family"), refs)
        self.assertEqual(len(refs), 2)  # seeded marriage + captured one

    def test_111_create_event_rejects_ambiguous_owner(self):
        payload = capture_payload("req-111", create_event_if_missing={
            "person_handle": CTX["handles"]["anna"],
            "family_handle": CTX["handles"]["family"],
            "event_type": "Marriage",
        })
        status, body = post("/capture", payload)
        self.assertEqual(status, 400)
        self.assertEqual(body["error"]["code"], "INVALID_REQUEST")

    def test_112_targets_combined_with_new_event(self):
        """One capture: citation attached to an existing event AND a
        newly created event (one record evidences several facts)."""
        payload = capture_payload("req-112", targets=[
            {"type": "event", "handle": CTX["event_080"]}],
            create_event_if_missing={
                "person_handle": CTX["handles"]["anna"],
                "event_type": "Residence",
                "date": {"type": "regular", "year": 1810},
                "description": "Wohnort laut Matrikel",
        })
        status, body = post("/capture", payload)
        self.assertEqual(status, 200, body)
        self.assertEqual(len(body["attached_to"]), 2)
        self.assertIn("event", body["created"])
        citation_handle = body["created"]["citation"]["handle"]
        on_existing = in_main(lambda: db().get_event_from_handle(
            CTX["event_080"]).get_citation_list())
        on_new = in_main(lambda: db().get_event_from_handle(
            body["created"]["event"]["handle"]).get_citation_list())
        self.assertIn(citation_handle, on_existing)
        self.assertEqual(on_new, [citation_handle])

    # -- permalink on the person's Internet tab (person_url) ---------

    def test_120_person_url_added_to_involved_persons(self):
        payload = capture_payload("req-120", create_event_if_missing={
            "family_handle": CTX["handles"]["family"],
            "event_type": "Marriage",
            "date": {"type": "regular", "year": 1775},
        }, person_url={
            "path": "https://data.matricula-online.eu/de/permalink-120",
            "description": "Trauung 1775",
            "type": "Digitalisat",
        })
        status, body = post("/capture", payload)
        self.assertEqual(status, 200, body)
        results = body["created"]["person_urls"]
        # BOTH partners of the family get the link (bride and groom)
        self.assertEqual([r["was_existing"] for r in results],
                         [False, False])
        names = " / ".join(r["name"] for r in results)
        self.assertIn("Georg", names)
        self.assertIn("Maria", names)
        for person_key in ("georg", "maria"):
            urls = in_main(lambda key=person_key: [
                (u.get_path(), str(u.get_type()), u.get_description())
                for u in db().get_person_from_handle(
                    CTX["handles"][key]).get_url_list()])
            self.assertIn(
                ("https://data.matricula-online.eu/de/permalink-120",
                 "Digitalisat", "Trauung 1775"), urls)

    def test_121_person_url_not_duplicated(self):
        payload = capture_payload("req-121", targets=[
            {"type": "event", "handle": CTX["event_080"]}],
            person_url={
                "path": "https://data.matricula-online.eu/de/permalink-121",
                "description": "Taufe 1810",
                "type": "Digitalisat",
        })
        status, body = post("/capture", payload)
        self.assertEqual(status, 200, body)
        first = body["created"]["person_urls"]
        self.assertEqual([r["was_existing"] for r in first], [False])

        payload2 = capture_payload("req-121b", targets=[
            {"type": "event", "handle": CTX["event_080"]}],
            person_url={
                "path": "https://data.matricula-online.eu/de/permalink-121",
                "description": "Taufe 1810",
                "type": "Digitalisat",
        })
        status, body2 = post("/capture", payload2)
        self.assertEqual(status, 200, body2)
        self.assertEqual([r["was_existing"]
                          for r in body2["created"]["person_urls"]], [True])
        count = in_main(lambda: sum(
            1 for u in db().get_person_from_handle(
                CTX["handles"]["anna"]).get_url_list()
            if u.get_path().endswith("permalink-121")))
        self.assertEqual(count, 1)

    def test_122_same_link_new_event_gets_own_row(self):
        """One scan backs several events: the SAME url with a DIFFERENT
        description (= different event) must create a second Internet
        row - dedup is per event (path+description), not per url."""
        payload = capture_payload("req-122", targets=[
            {"type": "event", "handle": CTX["event_080"]}],
            person_url={
                "path": "https://data.matricula-online.eu/de/permalink-121",
                "description": "Wohnort 1810",
                "type": "Digitalisat",
        })
        status, body = post("/capture", payload)
        self.assertEqual(status, 200, body)
        self.assertEqual([r["was_existing"]
                          for r in body["created"]["person_urls"]], [False])
        rows = in_main(lambda: [
            (u.get_path(), u.get_description())
            for u in db().get_person_from_handle(
                CTX["handles"]["anna"]).get_url_list()
            if u.get_path().endswith("permalink-121")])
        self.assertEqual(sorted(r[1] for r in rows),
                         ["Taufe 1810", "Wohnort 1810"])

    # -- Digitalisate gramplet data (citation-driven, NOT deduplicated)

    def test_130_scan_links_listed_per_event(self):
        # Anna: seeded baptism (no citations) is skipped; the event from
        # test_080 carries five MH_Permalink citations by now (080, two
        # from 121, one each from 112 and 122), and test_112 added a
        # Residence event with one more - all must be listed, same-URL
        # repeats included, because one record can back several
        # attachments.
        rows = in_main(lambda: collect_scan_links(
            db(), CTX["handles"]["anna"]))
        self.assertEqual(len(rows), 2)
        links = [link for row in rows for link in row["links"]]
        # every citation carries the template's MH_Permalink (/de/x):
        # the SAME url must be listed once per citation, not collapsed
        self.assertEqual([link["url"].endswith("/de/x") for link in links],
                         [True] * 6)
        self.assertIn("S. 42, Eintrag 7",
                      [link["label"] for link in links])

        # Georg: marriage citations live on FAMILY events (tests 110/120)
        rows = in_main(lambda: collect_scan_links(
            db(), CTX["handles"]["georg"]))
        family_links = [link for row in rows for link in row["links"]]
        self.assertGreaterEqual(len(family_links), 2)
        self.assertTrue(all(link["url"].endswith("/de/x")
                            for link in family_links))

    # -- POST /citations/{handle}/attach (5.8) -----------------------

    def test_140_attach_existing_citation(self):
        # target: Hans' seeded baptism, which has no citations yet
        status, body = get("/persons/" + CTX["handles"]["hans"])
        self.assertEqual(status, 200)
        baptism = [e for e in body["events"] if e["type"] == "Baptism"][0]
        self.assertEqual(baptism["citation_count"], 0)
        CTX["hans_baptism"] = baptism["handle"]

        status, body = post("/citations/%s/attach" % CTX["citation_080"],
                            {"targets": [{"type": "event",
                                          "handle": baptism["handle"]}]})
        self.assertEqual(status, 200, body)
        self.assertEqual(body["citation"]["handle"], CTX["citation_080"])
        self.assertFalse(body["attached_to"][0]["was_existing"])
        citations = in_main(lambda: db().get_event_from_handle(
            CTX["hans_baptism"]).get_citation_list())
        self.assertIn(CTX["citation_080"], citations)

    def test_141_attach_repeat_reports_existing(self):
        status, body = post("/citations/%s/attach" % CTX["citation_080"],
                            {"targets": [{"type": "event",
                                          "handle": CTX["hans_baptism"]}]})
        self.assertEqual(status, 200, body)
        self.assertTrue(body["attached_to"][0]["was_existing"])
        count = in_main(lambda: db().get_event_from_handle(
            CTX["hans_baptism"]).get_citation_list()
            .count(CTX["citation_080"]))
        self.assertEqual(count, 1)  # never attached twice

    def test_142_attach_unknown_citation(self):
        status, body = post("/citations/nosuchhandle/attach",
                            {"targets": [{"type": "event",
                                          "handle": CTX["hans_baptism"]}]})
        self.assertEqual(status, 404)
        self.assertEqual(body["error"]["code"], "NOT_FOUND")

    def test_143_attach_requires_targets(self):
        status, body = post("/citations/%s/attach" % CTX["citation_080"], {})
        self.assertEqual(status, 400)
        self.assertEqual(body["error"]["code"], "INVALID_REQUEST")

    # -- citations block in person detail (Gramps-Modus link view) ----

    def test_150_detail_citations_carry_source(self):
        status, body = get("/persons/" + CTX["handles"]["hans"])
        self.assertEqual(status, 200)
        baptism = [e for e in body["events"]
                   if e["handle"] == CTX["hans_baptism"]][0]
        self.assertEqual(baptism["citation_count"], 1)
        self.assertEqual(len(baptism["citations"]), 1)
        ref = baptism["citations"][0]
        self.assertEqual(ref["handle"], CTX["citation_080"])
        self.assertEqual(ref["page"], "S. 42, Eintrag 7")
        self.assertEqual(ref["source_title"],
                         "Testpfarrei, Taufbuch Bd. 1 (1770-1790)")
        self.assertTrue(ref["source_handle"])
        self.assertIn("source_abbrev", ref)   # None here - not captured
        # the person-level citation list is delivered the same way
        self.assertEqual(body["citations"], [])

    # -- GET /event-types (Gramps event-editor catalog) ---------------

    def test_160_event_types_catalog(self):
        status, body = get("/event-types")
        self.assertEqual(status, 200)
        self.assertIsInstance(body["custom"], list)
        types = {t["xml"]: t for group in body["groups"]
                 for t in group["types"]}
        # locale-independent xml names; labels are localized
        self.assertIn("Baptism", types)
        self.assertIn("Burial", types)
        self.assertFalse(types["Birth"]["is_family"])
        self.assertTrue(types["Marriage"]["is_family"])
        self.assertTrue(types["Marriage Banns"]["is_family"])
        self.assertTrue(all(t["label"] for t in types.values()))
        # the group labels mirror the Gramps event editor's structure
        self.assertGreaterEqual(len(body["groups"]), 8)

    # -- create_person in capture (spec 7.3 v2) -----------------------

    def test_170_create_person_as_child_with_event(self):
        """Person + family link + event + citation in ONE transaction -
        the sibling-series workflow (new child from a baptism entry)."""
        payload = capture_payload("req-170", create_person={
            "given": "Lena", "surname": "Meier", "gender": "F",
            "child_of_family": CTX["handles"]["family"],
        }, create_event_if_missing={
            "person_handle": "@new",
            "event_type": "Baptism",
            "date": {"type": "regular", "year": 1783, "month": 2, "day": 2},
        })
        status, body = post("/capture", payload)
        self.assertEqual(status, 200, body)
        person = body["created"]["person"]
        self.assertFalse(person["was_existing"])
        self.assertNotIn("family", body["created"])   # joined existing one
        event_handle = body["created"]["event"]["handle"]
        citation_handle = body["created"]["citation"]["handle"]

        def check():
            new_person = db().get_person_from_handle(person["handle"])
            family = db().get_family_from_handle(CTX["handles"]["family"])
            children = [ref.ref for ref in family.get_child_ref_list()]
            event_citations = db().get_event_from_handle(
                event_handle).get_citation_list()
            return (new_person.get_gender(),
                    new_person.get_main_parents_family_handle(),
                    person["handle"] in children,
                    event_citations)
        gender, parent_family, is_child, event_citations = in_main(check)
        self.assertEqual(parent_family, CTX["handles"]["family"])
        self.assertTrue(is_child)
        self.assertEqual(event_citations, [citation_handle])

    def test_171_create_person_as_spouse_with_family_event(self):
        """New spouse creates a new family; '@new' as family_handle puts
        the Marriage event on exactly that family."""
        payload = capture_payload("req-171", create_person={
            "given": "Eva", "surname": "Huber", "gender": "F",
            "spouse_of": CTX["handles"]["hans"],
        }, create_event_if_missing={
            "family_handle": "@new",
            "event_type": "Marriage",
            "date": {"type": "regular", "year": 1805},
        })
        status, body = post("/capture", payload)
        self.assertEqual(status, 200, body)
        person = body["created"]["person"]
        family = body["created"]["family"]
        self.assertFalse(family["was_existing"])

        def check():
            fam = db().get_family_from_handle(family["handle"])
            events = [ref.ref for ref in fam.get_event_ref_list()]
            return (fam.get_father_handle(), fam.get_mother_handle(), events)
        father, mother, events = in_main(check)
        self.assertEqual(father, CTX["handles"]["hans"])
        self.assertEqual(mother, person["handle"])
        self.assertEqual(events, [body["created"]["event"]["handle"]])

    def test_172_create_person_as_parent(self):
        """New parent of a person without parents: family is created,
        the person becomes its child."""
        payload = capture_payload("req-172", create_person={
            "given": "Josef", "surname": "Meier", "gender": "M",
            "parent_of": CTX["handles"]["anna"],
        })
        status, body = post("/capture", payload)
        self.assertEqual(status, 200, body)
        person = body["created"]["person"]
        family = body["created"]["family"]

        def check():
            fam = db().get_family_from_handle(family["handle"])
            children = [ref.ref for ref in fam.get_child_ref_list()]
            anna = db().get_person_from_handle(CTX["handles"]["anna"])
            return (fam.get_father_handle(), children,
                    anna.get_main_parents_family_handle())
        father, children, anna_parents = in_main(check)
        self.assertEqual(father, person["handle"])
        self.assertEqual(children, [CTX["handles"]["anna"]])
        self.assertEqual(anna_parents, family["handle"])

    def test_173_create_person_without_citation(self):
        """Bare person creation: the citation block may be omitted -
        nothing is created besides person + family link."""
        citations_before = in_main(lambda: db().get_number_of_citations())
        status, body = post("/capture", {
            "request_id": "req-173",
            "create_person": {
                "given": "Max", "surname": "Meier", "gender": "M",
                "child_of_family": CTX["handles"]["family"],
            },
        })
        self.assertEqual(status, 200, body)
        self.assertIn("person", body["created"])
        self.assertNotIn("citation", body["created"])
        citations_after = in_main(lambda: db().get_number_of_citations())
        self.assertEqual(citations_before, citations_after)

    def test_174_targets_require_citation(self):
        status, body = post("/capture", {
            "request_id": "req-174",
            "create_person": {
                "given": "X", "child_of_family": CTX["handles"]["family"],
            },
            "targets": [{"type": "event", "handle": CTX["event_080"]}],
        })
        self.assertEqual(status, 400)
        self.assertEqual(body["error"]["code"], "INVALID_REQUEST")

    def test_175_at_new_requires_create_person(self):
        payload = capture_payload("req-175", create_event_if_missing={
            "person_handle": "@new",
            "event_type": "Baptism",
        })
        status, body = post("/capture", payload)
        self.assertEqual(status, 400)
        self.assertEqual(body["error"]["code"], "INVALID_REQUEST")

    # -- POST /capture-batch (whole session in ONE transaction) -------

    BATCH_TEMPLATE = {
        "request_id": "req-180",
        "persons": [
            {"tmp": "new:p1", "given": "Berta", "surname": "Gruber",
             "gender": "F"},
            {"tmp": "new:p2", "given": "Alois", "surname": "Gruber",
             "gender": "M"},
            {"tmp": "new:p3", "given": "Zenzi", "surname": "Meier",
             "gender": "F"},
        ],
        "events": [
            {"tmp": "evt:e1", "type": "Marriage", "family": "new:f1",
             "date": {"type": "regular", "year": 1806, "month": 5, "day": 6}},
            {"tmp": "evt:e2", "type": "Baptism", "person": "new:p1",
             "date": {"type": "regular", "year": 1784}},
        ],
        "citations": [{
            "repository": CAPTURE_TEMPLATE["repository"],
            "source": {
                "match": {"by": "attribute", "key": "MH_SourceKey",
                          "value": "batch-heiraten-001"},
                "create_if_missing": {
                    "title": "Testpfarrei, Heiratsbuch Bd. 1",
                    "attributes": [{"key": "MH_SourceKey",
                                    "value": "batch-heiraten-001"}],
                },
            },
            "citation": {"page": "S. 99, Eintrag 1"},
            "targets": [{"type": "event", "ref": "evt:e1"},
                        {"type": "event", "ref": "evt:e2"}],
        }],
    }

    def batch_payload(self, request_id):
        payload = json.loads(json.dumps(self.BATCH_TEMPLATE))  # deep copy
        payload["request_id"] = request_id
        payload["families"] = [
            # Berta marries Hans (new family), Alois is Berta's father
            # (new family), Zenzi joins the SEEDED georg+maria family
            {"tmp": "new:f1", "father": CTX["handles"]["hans"],
             "mother": "new:p1"},
            {"tmp": "new:f2", "father": "new:p2", "children": ["new:p1"]},
            {"handle": CTX["handles"]["family"], "children": ["new:p3"]},
        ]
        return payload

    def test_180_batch_full_chain(self):
        """Persons -> families -> events -> citations in one call: the
        virtual-spouse-with-parents chain that deadlocked the per-person
        upload, expressed with zero client-side ordering logic."""
        before = in_main(lambda: db().get_number_of_people())
        status, body = post("/capture-batch", self.batch_payload("req-180"))
        self.assertEqual(status, 200, body)
        created = body["created"]
        self.assertEqual(len(created["persons"]), 3)
        p1 = created["persons"]["new:p1"]["handle"]
        f1 = created["families"]["new:f1"]["handle"]
        f2 = created["families"]["new:f2"]["handle"]
        e1 = created["events"]["evt:e1"]["handle"]
        e2 = created["events"]["evt:e2"]["handle"]
        citation = created["citations"][0]
        self.assertFalse(citation["source"]["was_existing"])
        CTX["response_180"] = body
        CTX["batch_before_people"] = before

        def check():
            fam1 = db().get_family_from_handle(f1)
            berta = db().get_person_from_handle(p1)
            fam2 = db().get_family_from_handle(f2)
            seeded = db().get_family_from_handle(CTX["handles"]["family"])
            return (fam1.get_father_handle(), fam1.get_mother_handle(),
                    [r.ref for r in fam1.get_event_ref_list()],
                    berta.get_main_parents_family_handle(),
                    fam2.get_father_handle(),
                    created["persons"]["new:p3"]["handle"]
                    in [r.ref for r in seeded.get_child_ref_list()],
                    db().get_event_from_handle(e1).get_citation_list(),
                    db().get_event_from_handle(e2).get_citation_list())
        (father1, mother1, fam1_events, berta_parents, father2,
         zenzi_is_child, cites1, cites2) = in_main(check)
        self.assertEqual(father1, CTX["handles"]["hans"])
        self.assertEqual(mother1, p1)
        self.assertEqual(fam1_events, [e1])
        self.assertEqual(berta_parents, f2)
        self.assertEqual(father2, created["persons"]["new:p2"]["handle"])
        self.assertTrue(zenzi_is_child)
        # ONE citation shared by both events
        self.assertEqual(cites1, [citation["handle"]])
        self.assertEqual(cites2, [citation["handle"]])
        after = in_main(lambda: db().get_number_of_people())
        self.assertEqual(after, before + 3)

    def test_181_batch_single_undo(self):
        """The entire batch is ONE transaction: a single undo removes
        all three persons again."""
        in_main(lambda: db().undo())
        after = in_main(lambda: db().get_number_of_people())
        self.assertEqual(after, CTX["batch_before_people"])

    def test_182_batch_bad_ref_rolls_back(self):
        before = in_main(lambda: (db().get_number_of_people(),
                                  db().get_number_of_citations()))
        payload = self.batch_payload("req-182")
        payload["events"][1]["person"] = "new:missing"
        status, body = post("/capture-batch", payload)
        self.assertEqual(status, 404)
        after = in_main(lambda: (db().get_number_of_people(),
                                 db().get_number_of_citations()))
        self.assertEqual(before, after)   # nothing half-written

    def test_183_batch_idempotent_replay(self):
        before = in_main(lambda: db().get_number_of_people())
        status, body = post("/capture-batch", self.batch_payload("req-180"))
        self.assertEqual(status, 200)
        self.assertEqual(body, CTX["response_180"])  # cached, no re-write
        after = in_main(lambda: db().get_number_of_people())
        self.assertEqual(after, before)

    def test_184_batch_event_place_by_name(self):
        """Event place by NAME: created once, then REUSED (casefolded
        match) instead of duplicated - church-book work cycles through
        the same parish/village names."""
        before = in_main(lambda: db().get_number_of_places())
        status, body = post("/capture-batch", {
            "request_id": "req-184",
            "events": [
                {"tmp": "evt:pl1", "type": "Baptism",
                 "person": CTX["handles"]["hans"],
                 "place": {"title": "Pollenfeld"},
                 "date": {"type": "regular", "year": 1800}},
            ],
        })
        self.assertEqual(status, 200, body)
        e1 = body["created"]["events"]["evt:pl1"]["handle"]
        status, body2 = post("/capture-batch", {
            "request_id": "req-184b",
            "events": [
                {"tmp": "evt:pl2", "type": "Burial",
                 "person": CTX["handles"]["hans"],
                 "place": {"title": "pollenfeld"}},  # different case
            ],
        })
        self.assertEqual(status, 200, body2)
        e2 = body2["created"]["events"]["evt:pl2"]["handle"]

        def check():
            h1 = db().get_event_from_handle(e1).get_place_handle()
            h2 = db().get_event_from_handle(e2).get_place_handle()
            place = db().get_place_from_handle(h1)
            return (h1, h2, place.get_name().get_value(),
                    db().get_number_of_places())
        h1, h2, name, places_after = in_main(check)
        self.assertEqual(h1, h2)                 # reused, not duplicated
        self.assertEqual(name, "Pollenfeld")
        self.assertEqual(places_after, before + 1)
