# -*- coding: utf-8 -*-
"""
MH Bridge Tests - headless integration suite for the MatrikelHelfer
Bridge addon, run inside the real Gramps runtime via CLI:

    grampsd.exe -O MHBridgeTest -a tool -p name=mhbridgetests -y

The tool:
  1. refuses to run unless the open tree is named exactly "MHBridgeTest"
     (it WIPES the tree at the start of every run),
  2. wipes and seeds deterministic test data,
  3. starts the real BridgeService on a test port with a temp discovery
     file (so a concurrently running production bridge is untouched),
  4. runs a GLib main loop on this thread while a worker thread drives
     the unittest suite over live HTTP,
  5. prints "MHBRIDGE-TESTS RESULT: ..." as the machine-readable verdict.
"""

import os
import sys
import tempfile
import threading
import unittest

from gi.repository import GLib

from gramps.gen.db import DbTxn
from gramps.gen.lib import (ChildRef, Date, Event, EventRef, EventRoleType,
                            EventType, Family, Name, Person, Place, PlaceName,
                            Surname)
from gramps.gui.plug import tool

# the bridge addon's directory is only put on sys.path when Gramps loads
# that addon itself - in CLI runs it may not be, so add it explicitly
_BRIDGE_DIR = os.path.join(os.path.dirname(os.path.dirname(__file__)),
                           "MatrikelHelferBridge")
if _BRIDGE_DIR not in sys.path:
    sys.path.insert(0, _BRIDGE_DIR)

from mhbridge_service import BridgeService
import mhbridge_testcases

TEST_TREE_NAME = "MHBridgeTest"
TEST_PORT = 8811
LOOP_TIMEOUT_S = 180


class MHBridgeTestTool(tool.Tool):

    def __init__(self, dbstate, user, options_class, name, callback=None):
        tool.Tool.__init__(self, dbstate, options_class, name)
        db = dbstate.db
        if db.get_dbname() != TEST_TREE_NAME:
            print("MHBRIDGE-TESTS RESULT: REFUSED (tree is %r, expected %r)"
                  % (db.get_dbname(), TEST_TREE_NAME))
            return
        self._run_suite(dbstate)

    # -- data setup --------------------------------------------------

    @staticmethod
    def _wipe(db):
        with DbTxn("MHBridgeTests: wipe", db) as trans:
            for get_handles, remove in (
                    (db.get_person_handles, db.remove_person),
                    (db.get_family_handles, db.remove_family),
                    (db.get_event_handles, db.remove_event),
                    (db.get_citation_handles, db.remove_citation),
                    (db.get_source_handles, db.remove_source),
                    (db.get_repository_handles, db.remove_repository),
                    (db.get_note_handles, db.remove_note),
                    (db.get_place_handles, db.remove_place),
                    (db.get_media_handles, db.remove_media)):
                for handle in list(get_handles()):
                    remove(handle, trans)

    @staticmethod
    def _seed(db):
        """Three Meiers, one place, one parent family; see testcases."""
        handles = {}
        with DbTxn("MHBridgeTests: seed", db) as trans:
            place = Place()
            place_name = PlaceName()
            place_name.set_value("Testheim")
            place.set_name(place_name)
            place.set_title("Testheim")
            db.add_place(place, trans)
            handles["place"] = place.get_handle()

            def make_person(given, surname_text, gender, birth_year,
                            place_handle=None):
                person = Person()
                name = Name()
                name.set_first_name(given)
                surname = Surname()
                surname.set_surname(surname_text)
                name.add_surname(surname)
                person.set_primary_name(name)
                person.set_gender(gender)
                db.add_person(person, trans)

                event = Event()
                event.set_type(EventType(EventType.BAPTISM))
                date = Date()
                date.set(quality=Date.QUAL_NONE, modifier=Date.MOD_NONE,
                         calendar=Date.CAL_GREGORIAN,
                         value=(0, 0, birth_year, False))
                event.set_date_object(date)
                if place_handle:
                    event.set_place_handle(place_handle)
                db.add_event(event, trans)

                ref = EventRef()
                ref.set_reference_handle(event.get_handle())
                person.add_event_ref(ref)
                db.commit_person(person, trans)
                handles[given.lower() + "_event"] = event.get_handle()
                return person

            hans = make_person("Hans", "Meier", Person.MALE, 1780,
                               place.get_handle())
            anna = make_person("Anna", "Meier", Person.FEMALE, 1810)
            georg = make_person("Georg", "Meier", Person.MALE, 1750)
            maria = make_person("Maria", "Huber", Person.FEMALE, 1755)

            family = Family()
            family.set_father_handle(georg.get_handle())
            family.set_mother_handle(maria.get_handle())
            child_ref = ChildRef()
            child_ref.set_reference_handle(hans.get_handle())
            family.add_child_ref(child_ref)

            # family event: marriage 1775 (family events live on the
            # Family object with role "Family", like the Gramps GUI does)
            marriage = Event()
            marriage.set_type(EventType(EventType.MARRIAGE))
            marriage_date = Date()
            marriage_date.set(quality=Date.QUAL_NONE, modifier=Date.MOD_NONE,
                              calendar=Date.CAL_GREGORIAN,
                              value=(0, 0, 1775, False))
            marriage.set_date_object(marriage_date)
            db.add_event(marriage, trans)
            marriage_ref = EventRef()
            marriage_ref.set_reference_handle(marriage.get_handle())
            marriage_ref.set_role(EventRoleType(EventRoleType.FAMILY))
            family.add_event_ref(marriage_ref)
            handles["marriage_event"] = marriage.get_handle()

            db.add_family(family, trans)
            georg.add_family_handle(family.get_handle())
            db.commit_person(georg, trans)
            maria.add_family_handle(family.get_handle())
            db.commit_person(maria, trans)
            hans.add_parent_family_handle(family.get_handle())
            db.commit_person(hans, trans)

            handles["hans"] = hans.get_handle()
            handles["anna"] = anna.get_handle()
            handles["georg"] = georg.get_handle()
            handles["maria"] = maria.get_handle()
            handles["family"] = family.get_handle()
        return handles

    # -- suite -------------------------------------------------------

    def _run_suite(self, dbstate):
        db = dbstate.db
        self._wipe(db)
        handles = self._seed(db)

        discovery = os.path.join(tempfile.gettempdir(),
                                 "mhbridge_test_endpoint.json")
        service = BridgeService(dbstate, discovery_file=discovery)
        service.refresh_session(new_session=True)
        service.start(TEST_PORT)
        if not service.running:
            print("MHBRIDGE-TESTS RESULT: FAILED (server did not start: %s)"
                  % service.last_error)
            return

        mhbridge_testcases.CTX.update(
            dbstate=dbstate, service=service, handles=handles,
            port=service.port, token=service.token)

        loop = GLib.MainLoop()
        outcome = {}

        def worker():
            try:
                suite = unittest.defaultTestLoader.loadTestsFromModule(
                    mhbridge_testcases)
                runner = unittest.TextTestRunner(stream=sys.stdout,
                                                 verbosity=2)
                outcome["result"] = runner.run(suite)
            except BaseException as exc:  # noqa: BLE001
                outcome["crash"] = exc
            finally:
                GLib.idle_add(loop.quit)

        thread = threading.Thread(target=worker, name="MHBridgeTests",
                                  daemon=True)
        GLib.timeout_add_seconds(LOOP_TIMEOUT_S, loop.quit)  # failsafe
        thread.start()
        loop.run()
        thread.join(timeout=10)

        service.stop()

        if "crash" in outcome:
            print("MHBRIDGE-TESTS RESULT: FAILED (suite crashed: %r)"
                  % outcome["crash"])
        elif "result" not in outcome:
            print("MHBRIDGE-TESTS RESULT: FAILED (timeout after %ds)"
                  % LOOP_TIMEOUT_S)
        else:
            result = outcome["result"]
            if result.wasSuccessful():
                print("MHBRIDGE-TESTS RESULT: OK (%d tests)"
                      % result.testsRun)
            else:
                print("MHBRIDGE-TESTS RESULT: FAILED "
                      "(failures=%d errors=%d of %d)"
                      % (len(result.failures), len(result.errors),
                         result.testsRun))


class MHBridgeTestOptions(tool.ToolOptions):
    pass
