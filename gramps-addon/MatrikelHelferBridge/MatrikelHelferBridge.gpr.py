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
    version="0.4.0",
    gramps_target_version="6.0",
    gramplet="MatrikelHelferBridgeGramplet",
    gramplet_title="MatrikelHelfer Bridge",
    authors=["luni64"],
    height=260,
    expand=True,
)
