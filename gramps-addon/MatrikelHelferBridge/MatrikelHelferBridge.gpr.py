# ------------------------------------------------------------------
# MatrikelHelfer Bridge - registration
#
# gramps_target_version must exactly match the installed Gramps
# major.minor version (Help -> About), otherwise the addon is silently
# ignored. On Gramps 6.1 change it to "6.1".
# ------------------------------------------------------------------
register(
    GRAMPLET,
    id="MatrikelHelferBridge",
    name="MatrikelHelfer Bridge",
    description=(
        "Local HTTP bridge that lets MatrikelHelfer read the open family "
        "tree and attach sources/citations to it. Listens on 127.0.0.1 "
        "only; every request except /ping requires the session token "
        "from the discovery file."
    ),
    status=STABLE,
    fname="MatrikelHelferBridge.py",
    version="0.10.0",
    gramps_target_version="6.0",
    gramplet="MatrikelHelferBridgeGramplet",
    gramplet_title="MatrikelHelfer Bridge",
    authors=["luni64"],
    height=260,
    expand=True,
)

register(
    GRAMPLET,
    id="MHBridgeLinks",
    name="MatrikelHelfer Digitalisate",
    description=(
        "Clickable links to the church-book scans (MH_Permalink) of the "
        "active person's citations, grouped by event. The same scan "
        "appears under every event it backs."
    ),
    status=STABLE,
    fname="mhbridge_links.py",
    version="0.10.0",
    gramps_target_version="6.0",
    gramplet="MHBridgeLinksGramplet",
    gramplet_title="Digitalisate",
    authors=["luni64"],
    navtypes=["Person"],
    height=200,
    expand=True,
)
