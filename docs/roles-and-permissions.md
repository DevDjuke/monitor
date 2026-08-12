# Roles, permissions, and operator management

P12 adds an explicit multi-user authorization boundary to Monitor's existing ASP.NET Core Identity authentication. It governs human/operator cookie sessions only. Component ingestion credentials, OTLP authentication, heartbeats, telemetry ingestion, and component command polling keep their existing machine-authentication contracts.

## Roles

Monitor seeds four roles at application startup:

| Role | View operational data | View audit trail | Change configuration | Issue/cancel control commands | Manage operator accounts |
| --- | --- | --- | --- | --- | --- |
| Owner | Yes | Yes | Yes | Yes | Yes |
| Operator | Yes | No | Yes | Yes | No |
| Viewer | Yes | No | No | No | No |
| Auditor | Yes | Yes | No | No | No |

Each managed account has one effective Monitor role. `Owner` is the administrative superset. `Operator` is intended for people actively operating agents and control-plane policy. `Viewer` is investigation-only. `Auditor` is investigation plus immutable audit-history access without configuration or control privileges.

## Authorization policies

The UI uses named policies rather than scattering role names through handlers:

- `Monitor.View`: Owner, Operator, Viewer, Auditor.
- `Monitor.Audit`: Owner, Auditor.
- `Monitor.Configure`: Owner, Operator.
- `Monitor.Control`: Owner, Operator.
- `Monitor.ManageOperators`: Owner only.

Authenticated SignalR connections require `Monitor.View`. Configuration edit pages such as alert-rule and budget editors require `Monitor.Configure`. The Audit page requires `Monitor.Audit`. `/operators` requires `Monitor.ManageOperators`.

## Mutation boundary

P12 adds a global Razor Page handler filter for state-changing POST requests. This is intentionally fail-closed:

- personal Saved View mutations require only `Monitor.View` because they affect the current user's private workspace and not shared control-plane state;
- component command issue/cancel and the central Commands mutation surface require `Monitor.Control`;
- operator-account mutations require `Monitor.ManageOperators`;
- component ingestion credential issue/rotate/revoke and all other control-plane POST handlers require `Monitor.Configure`;
- a future POST handler that is not explicitly classified therefore starts as `Monitor.Configure`, rather than accidentally inheriting read-only access.

GET authorization and POST authorization are independent. Hiding a button is never the security boundary; server-side policy evaluation is.

## Operator management

Owners manage local Identity accounts from `/operators`:

- create a local account with an initial password and one role;
- change an account's role;
- reset an account password;
- delete an account.

Guardrails:

- an Owner cannot demote their own account;
- an Owner cannot delete their own account;
- Monitor refuses any role change or deletion that would remove the final Owner;
- passwords are never copied into `AuditEvent` snapshots or metadata;
- role changes and password resets rotate the Identity security stamp;
- cookie security stamps are revalidated at most one minute later, so an already signed-in user's old authorization claims are short-lived after a role or password change.

Account creation, role changes, password resets, and deletion are written to the existing append-only audit trail using `operator-account.*` actions.

## Upgrade compatibility

The Identity schema already included `AspNetRoles` and `AspNetUserRoles`, so P12 requires no database migration.

Before P12, authenticated users had no Monitor role and effectively had unrestricted UI access. On startup, P12 creates any missing Monitor roles and assigns `Owner` to every existing account that has no recognized Monitor role. This preserves access for existing installations and prevents an upgrade from locking the administrator out.

Fresh production bootstrap administrators and the Development `/account/setup` first user are assigned `Owner` immediately.

## Navigation and access denied behavior

The sidebar shows the signed-in account's effective role. Audit is shown only to Owner/Auditor, and Operators is shown only to Owner. Other operational read surfaces stay visible because all four roles have `Monitor.View`; state-changing requests remain policy-protected even where a read page contains controls.

Interactive cookie requests that fail authorization are sent to `/account/access-denied`. Machine endpoints continue returning HTTP 401/403 rather than HTML redirects.

## Deliberate boundaries

P12 is a local-account authorization slice. It does not add:

- invitations or email delivery;
- self-service registration;
- SSO/OIDC/SAML;
- external identity-provider group-to-role mapping;
- custom per-user permission grants;
- team/tenant isolation.

A future external identity provider can map its users/groups onto these same named policies without rewriting the operational authorization model.
