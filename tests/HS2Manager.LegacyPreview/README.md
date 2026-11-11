# Frozen reference host

This test-only project preserves the old player transport and export adapter for
offline comparisons. The associated `HS2Manager.LegacyApp` and
`HS2Manager.LegacyUiChecks` projects preserve the comparison UI. They are excluded
from the production solution, startup and publishing workflow and cannot publish.
They require an explicitly selected reference player. The native application has
no fallback to this host.
