# ------------------------------------------------------------------
# Bridge Spike - stage 0 (throwaway addon)
# Registration. IMPORTANT: gramps_target_version must exactly match the
# installed Gramps major.minor version (Help -> About), otherwise the
# addon is silently ignored. On Gramps 6.1 change it to "6.1".
# ------------------------------------------------------------------
register(
    GRAMPLET,
    id="BridgeSpike",
    name="Bridge Spike",
    description=(
        "Stage-0 spike for the MatrikelHelfer bridge: HTTP server inside "
        "the Gramps process, write test via thread marshalling. "
        "Throwaway code - use against a test tree only."
    ),
    status=STABLE,
    fname="BridgeSpike.py",
    version="0.1.0",
    gramps_target_version="6.0",
    gramplet="BridgeSpikeGramplet",
    gramplet_title="Bridge Spike",
    height=200,
    expand=True,
)
