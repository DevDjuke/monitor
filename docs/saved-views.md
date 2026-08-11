# Saved views

Saved views are personal operator-workspace preferences for repeatedly useful filter combinations. They are not telemetry, control commands, or forensic audit evidence.

## Scope

The first saved-view slice supports these operational surfaces:

- Runs
- Logs
- Usage
- Alerts
- Budgets
- Audit
- Commands

A saved view belongs to exactly one ASP.NET Core Identity user and one surface. Shared/team views are deliberately deferred until Monitor has a real multi-user permission model.

## Stored model

`SavedView` stores:

- `Id`
- `UserId`
- `Surface`
- display `Name`
- normalized `NameKey`
- canonical `QueryString`
- `IsPinned`
- `CreatedAt`
- `UpdatedAt`

Names are unique per user and surface. The database enforces that invariant with a unique `(UserId, Surface, NameKey)` index.

The `UserId` foreign key points to `AspNetUsers` and cascades on user deletion. This is intentional: saved views are preference state and do not need to outlive the account that owns them.

## Query-string contract

Monitor does not duplicate every page's filter properties into the saved-view schema. Each supported surface instead has an explicit allow-list of query-string keys.

When a view is saved, `SavedViewQueryPolicy`:

1. parses the supplied query string;
2. discards every key that is not allow-listed for that surface;
3. ignores empty values;
4. normalizes the key order to the surface definition;
5. re-encodes the result as a canonical query string;
6. rejects unreasonably large individual values or query strings.

This prevents arbitrary query data, transient navigation state, and future unrelated parameters from being persisted merely because they happened to be present in the browser URL.

Runs is the important special case. Its filter state is now represented in `/runs?...` so it can be bookmarked and saved, while keyset pagination state (`before`) remains API-only and is never persisted in a saved view.

## Ownership and authorization

Every read and mutation is scoped by the authenticated Identity user's `ClaimTypes.NameIdentifier`.

An operator cannot list, rename, pin, or delete another user's saved views, even if the other view id is known. The management handlers resolve rows with both `Id` and the current `UserId`; a cross-user mutation therefore returns `404` rather than exposing whether another user's preference exists.

## Pinning

A user may pin at most six saved views. Pinned views appear as fast links in the main sidebar and may belong to different surfaces.

The cap is enforced by the application before a create or pin operation. The sidebar independently limits its projection to six entries as a defensive UI bound.

The current product limit is:

- maximum 100 saved views per user;
- maximum 6 pinned views per user.

These are ergonomic bounds, not tenancy/security quotas.

## UI integration

Supported operational pages receive the same saved-view toolbar after their page header. A header TagHelper resolves the current route to a supported `SavedViewSurface` and invokes one reusable ViewComponent; individual pages do not duplicate saved-view markup or persistence logic.

The toolbar supports:

- applying a saved view;
- recognizing when the current canonical filters exactly match a saved view;
- saving the current filters under a name;
- optionally pinning at creation time;
- linking to the central `/saved-views` management surface.

`/saved-views` supports applying, renaming, pinning/unpinning, and deleting personal views.

## Audit policy

Saved-view operations intentionally do **not** emit `AuditEvent` rows.

The audit trail is reserved for operational/control-plane state changes: credentials, alert policy, budgets, commands, acknowledgements, delivery actions, and similar evidence. A user renaming or pinning a personal filter preset does not alter autonomous workload behavior or security posture, so auditing it would reduce the signal-to-noise ratio of the forensic stream.

The SQL-backed saved-view integration gate explicitly verifies that preference mutations leave the `AuditEvents` count unchanged.

## Future direction

Shared/team views should not be implemented by simply making `SavedView.UserId` nullable or adding a global flag. If Monitor becomes multi-user, shared views should be introduced together with explicit roles/permissions, ownership/administration rules, and a clear answer to who may publish or modify team-wide operational shortcuts.
