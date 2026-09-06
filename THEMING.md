# Theming

`VenueTheme` is stored on each profile. It has a built-in base ID, semantic color tokens, metrics and optional branding paths/opacity. Starter themes are Dark, Light, Neon and Midnight. A profile can replace any token or metric without affecting another profile.

Use `VenueUi` semantic styles (`Button`, `Card`, `StatusBadge`, and `NavItem`) or later renderer components. Tokens include primary/accent/background/surface/raised surface/border/text/success/warning/error/disabled/selected, plus rounding, padding, spacing and density. The active profile theme is read on every draw; a switch therefore takes effect immediately.

Logo/background paths are profile-owned metadata. Future asset import must copy physical files into a profile-specific asset directory during duplication and delete only that directory after confirmation.
