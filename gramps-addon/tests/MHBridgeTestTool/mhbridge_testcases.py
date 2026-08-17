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
