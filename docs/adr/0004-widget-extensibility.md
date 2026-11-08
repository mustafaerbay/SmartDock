# ADR 0004 — Widget registry and future external widgets

**Status:** accepted

## Decision
- Built-in widgets are registered as `WidgetDescriptor` singletons (`BuiltInWidgets.AddBuiltInWidgets`);
  `WidgetRegistry` validates unique type ids and feeds the widget manager.
- A descriptor carries metadata (id, name, description, category, glyph, default region,
  `AllowMultiple`) and a factory, so third-party widgets need nothing more than a descriptor.
- **No external code is loaded today.** A future plugin model must require: explicit user opt-in per
  package, Authenticode/signature verification against an allow-list, a manifest declaring permissions
  (network hosts, secrets), and loading into a collectible `AssemblyLoadContext`. Untrusted, web-style
  widgets would instead run in an optional WebView2 host without file-system access.

## Refresh model
One `PeriodicTimer` (1 s) in `WidgetHost` drives every widget according to its own `RefreshInterval`.
Each refresh runs on the thread pool with a 20 s timeout. Failures set the widget's error state and back
off exponentially (interval × 2^failures, capped at 5 minutes), so a broken widget cannot crash the dock
or spin in a retry loop. Event-driven widgets (media) use a long safety-net interval.
