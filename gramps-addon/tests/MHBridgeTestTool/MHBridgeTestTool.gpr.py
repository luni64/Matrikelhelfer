# ------------------------------------------------------------------
# MH Bridge Tests - registration
# Headless integration test suite for the MatrikelHelfer Bridge addon.
# CLI only:
#   grampsd.exe -O MHBridgeTest -a tool -p name=mhbridgetests -y
# Refuses to run on any tree not named "MHBridgeTest".
# ------------------------------------------------------------------
register(
    TOOL,
    id="mhbridgetests",
    name="MH Bridge Tests",
    description="Integration tests for the MatrikelHelfer Bridge addon "
                "(wipes and reseeds the MHBridgeTest tree).",
    status=STABLE,
    fname="MHBridgeTestTool.py",
    version="0.1.0",
    gramps_target_version="6.0",
    authors=["luni64"],
    category=TOOL_UTILS,  # TOOL_DEBUG is hidden unless Gramps runs with DEBUG
    toolclass="MHBridgeTestTool",
    optionclass="MHBridgeTestOptions",
    tool_modes=[TOOL_MODE_CLI],
)
