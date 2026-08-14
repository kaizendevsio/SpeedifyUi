# uLink Multi-Client And Multi-Region Platform Plan

Date: 2026-07-24  
Status: Proposed for review  
Scope: Evolve the current single-client, single-server uLink/XBond deployment into a secure account-based service supporting multiple devices and server regions.

## Objective

Allow independent uLink routers to share uLink servers safely, let users register and manage their own devices, and eventually offer healthy server locations in regions such as Asia, the US, and Europe.

The current Vultr server remains the compatible first data-plane location. A regional server process may continue binding one UDP socket/port such as `8444`. UDP is connectionless: this is only a shared listening endpoint, not a shared tunnel session. Every client has an independent authenticated transport association identified by client identity, session epoch, and observed remote endpoint. The server demultiplexes packets into isolated per-client runtime state and sends replies to that client's current remote endpoint.

## Current Limitations

- The server supports one active client session. A new session replaces the current session and clears its forwarding state.
- Path, schedule, recovery, reorder/FEC/repair, queue, and status state are global rather than per client.
- Every client would use `10.250.0.2/30`, so return routing cannot distinguish devices.
- One shared PSK authorizes all clients and provides no account ownership or per-device revocation.
- The client has no user registration, login, account, or device-enrollment model.
- The deployment has one server and no registry, regional health/capacity model, or server-selection process.

Connecting a second client to the current production process can evict the existing router and is not a valid concurrency test.

## Target Architecture

### Control Plane

A small HTTPS service owns product and operational metadata:

- user registration, verification, login, recovery, sessions, and account lifecycle;
- account-to-device ownership and device enrollment/revocation;
- server, location, and region registry;
- server health, maintenance state, and usable capacity;
- client region preference and server assignment;
- operator portal APIs, roles, audit records, and safeguards.

The control plane never forwards tunnel packets. It issues short-lived enrollment/assignment material and durable per-device identity, while storing secrets in protected server-side storage.

### Data Plane

Regional Rust `xbond-server` instances continue to carry encrypted tunnel traffic:

- one process can bind one UDP listening socket/port and serve packets from many clients;
- every client retains an independent authenticated transport association, remote endpoint, tunnel address, forwarding state, and runtime state;
- authenticated client/session identity demultiplexes inbound datagrams, and responses use that client's current authenticated remote endpoint;
- each authenticated client has isolated runtime state and a unique regional tunnel IP;
- server TUN return traffic is dispatched by destination tunnel IP;
- per-client and server-wide resource limits prevent noisy-neighbor failures;
- data-plane servers validate control-plane-issued device authorization without receiving user passwords.

### Client Web App

The uLink app provides:

- sign-up, verification, login, logout, recovery, and account management;
- device naming, enrollment status, revocation, and current server assignment;
- automatic server selection by default, with optional user-visible region preference;
- clear connection, maintenance, capacity, and reassignment states.

Infrastructure provisioning, server secrets, capacity overrides, and destructive server operations remain operator-only.

### Blazor Server Management Portal

Build the new management portal as a dedicated **Blazor Server** application in the separate multi-client branch/worktree. It manages servers, regions, clients/devices, users, enrollment, assignments, maintenance, and operations without replacing or modifying the existing deployed router dashboard during development.

- Use server-side interactive components and secure HTTP-only cookie sessions for browser users.
- Keep durable state in control-plane services/database, not in Blazor circuit memory.
- Put control-plane business rules behind application-service/API boundaries. Blazor components must not manipulate Rust sockets, TUN devices, systemd units, server files, or database tables directly.
- Use one initial portal host with clearly separated end-user and operator route areas, layouts, navigation, API scopes, and authorization policies. A later physical split is optional if operational or security needs justify it.
- Deploy the portal independently from the Pi uLink dashboard and regional Rust data-plane servers.

### XFramework Identity Integration

Reuse the existing XFramework IdentityServer instead of creating a second user-password or account-session system for uLink. The confirmed integration surface is XFramework's typed Bolt RPC stack, not OpenID Connect: the current repository has no active OIDC/OpenIddict provider configuration.

- Reference versioned packages for `IdentityServer.Domain.Shared`, `IdentityServer.Integration`, and `XFramework.Integration`. These provide the identity request/response contracts, generated `IIdentityServerServiceWrapper`, and Bolt client registration used by the existing Blazor Server ControlPanel.
- Keep Bolt and its service credentials entirely on the portal/control-plane backend. Browsers authenticate to the Blazor Server portal through secure application cookies and never receive a Bolt credential, shared service token, refresh token, or direct IdentityServer transport access.
- Follow the proven ControlPanel pattern: the portal backend calls `IIdentityServerServiceWrapper`, validates the returned identity/session, maps only required claims into a local portal cookie, and enforces uLink policies with ASP.NET Core authorization.
- Reuse existing XFramework IdentityServer capabilities for authentication, token refresh, logout/session invalidation, password change, password verification, forgot/reset password, identity verification, credentials, contacts, roles, and authorization logs where their behavior meets uLink requirements.
- Add a cohesive uLink account-registration application operation to XFramework IdentityServer before public or invite registration is enabled. Do not port the older `XFramework.Blazor` client-side registration behavior that performs multiple independent identity/contact/role mutations; registration must be atomic, server-authorized, rate-limited, and assign only the seeded uLink user role.
- Resolve the uLink tenant and permitted role on the portal backend. Never accept an arbitrary tenant ID or role ID supplied by the browser during login or registration.
- Keep XFramework account sessions separate from uLink device enrollment credentials and Rust tunnel authorization. A user session may create or revoke device enrollment, but user access/refresh tokens are never used as data-plane keys.
- Validate the portal cookie against the backing XFramework session on sensitive actions and at a bounded interval so IdentityServer logout/revocation propagates promptly. Store any required access/refresh token material only in protected server-side storage; do not place it in Blazor circuit state or browser storage.
- Publish the current identity package set to an approved private NuGet feed with aligned package IDs and versions before uLink integration. The checked-in `nupkgs` contains older `1.0.0` artifacts while current source declares `1.0.1` and `XFramework.*` package IDs, so those local artifacts are evidence of packability, not the production dependency source.
- Treat XFramework IdentityServer and Bolt availability as portal readiness dependencies. Existing authenticated tunnel sessions continue through a bounded cached authorization period when the identity/control plane is unavailable.

## Key Decisions

1. **Keep one UDP listening port per data server.** UDP is connectionless, so the listening socket can receive datagrams from many remote endpoints. Each client still has an independent authenticated transport association, session identity, remote endpoint, address, and forwarding/runtime state. Do not model this as one shared tunnel session, and do not allocate one public port per client.
2. **Separate user, device, session, and path identity.** A user owns devices; a device has a stable ID and credential; a tunnel session is a short-lived epoch; paths belong to that session.
3. **Use unique per-device credentials.** Eliminate the shared client PSK after migration. Support rotation and revocation without restarting servers.
4. **Isolate runtime state per client.** Each client owns its paths, schedule, recovery, reorder/FEC/repair state, queues, counters, and assigned address.
5. **Allocate addresses per region.** Each regional server or server pool receives a non-overlapping IPv4 range. A client receives a unique address while assigned to that region.
6. **Keep server selection simple initially.** Start with one Asia location and explicit assignment. Add health/capacity-aware automatic selection after the registry is proven.
7. **Prefer region stability over frequent switching.** Automatic selection considers availability, capacity, and measured latency but uses hysteresis; it does not move a healthy session for small latency differences.
8. **Treat regional failover as a reconnect initially.** Seamless session migration and stable public IP across regions are deferred.
9. **Keep identity out of Rust authentication UI concerns.** The control plane handles user authentication; data servers consume narrowly scoped, signed device authorization.
10. **IPv4 first.** Multi-client IPv6 is a separate milestone.
11. **Use Blazor Server for the portal.** Start as one independently deployed portal/control-plane UI with separate end-user and operator areas; do not rebuild the current router dashboard as part of this program.
12. **Reuse XFramework IdentityServer through its server-side Bolt SDK.** Do not create a parallel password/session store and do not expose Bolt to browsers. A future standards-based OIDC provider is optional and not required for the first uLink portal.

## Blazor Server Management Portal Capabilities

The portal is a dedicated Blazor Server application with separate end-user and operator experiences. Both use the same authenticated control-plane boundary but have different routes, navigation, policies, API permissions, and audit requirements. Operators must never see user passwords or raw reusable device secrets.

### Deliberately Minimal Initial Release

The first portal release includes only the capabilities needed to migrate the current router safely and operate the first Asia server:

- verified, invite-only user registration; email/password login, logout, recovery, and session revocation;
- one account type where a user manages only personally owned devices;
- single-use device enrollment token, device naming, configuration/assignment retrieval, status, and revocation;
- one visible Asia region and current server assignment; `Automatic` resolves to that eligible region;
- operator roles `Viewer`, `Operator`, and `Administrator`;
- user/device lookup, server inventory, server health/capacity, maintenance/drain, credential revocation, and audit history;
- guarded operator actions with confirmation, reason capture, concurrency checks, and idempotent execution;
- no billing, subscriptions, organizations/teams, SSO, public self-service sign-up, seamless cross-region session migration, or user-selected individual server.

Later releases may add public sign-up, passkeys/MFA for users, organizations/teams, delegated support roles, richer usage history, automated credential rotation, multiple regions, latency-aware selection, regional failover, and higher-scale portal deployment. These are not prerequisites for the first multi-client pilot unless separately approved.

### User Portal

#### Registration, Identity, And Account

- Accept an invite, register with email/password, and verify the email before enrollment or tunnel assignment.
- Log in and out using secure server-side cookie sessions.
- Request password recovery through a short-lived, single-use verification flow.
- View basic account identity, verification state, creation date, and security notices.
- List active account sessions with created/last-active context and revoke one or all other sessions.
- Change password after recent authentication and invalidate appropriate existing sessions.
- Request account closure through a guarded workflow that explains device and service impact.
- View user-facing account, login, recovery, enrollment, device, and region-assignment history without exposing internal secrets.

#### Device Enrollment And Management

- Create a short-lived, single-use enrollment token/code for an authenticated account.
- Display clear steps for entering/scanning the token on a uLink router; never display the resulting reusable device credential.
- Bind the enrolled device ID to the authenticated account and issue its initial regional assignment/configuration.
- List owned devices with name, device ID suffix, software/protocol version, enrollment date, last seen, current assignment, and online/offline/attention state.
- Rename a device and view its non-secret effective configuration.
- Show concise device health and usage: tunnel state, current region/location, last contact, recent connection quality, and bounded traffic totals suitable for support—not packet contents or browsing history.
- Revoke a device with an impact warning. Revocation blocks future assignment and data-plane authentication and terminates the device according to the documented revocation propagation target.
- Request credential rotation through a controlled flow that proves current device/account authorization and reports completion or required re-enrollment.
- Prevent users from viewing, modifying, or revoking devices belonging to another account.

#### Regions And Assignments

- Show available user-facing regions and locations with friendly name, broad geographic location, availability, maintenance state, and capacity status such as `Available`, `Busy`, or `Unavailable`.
- Show the device's current assignment, assignment time, selected preference, and why a fallback was chosen.
- Offer `Automatic` and allowed region-level preferences; do not expose individual server selection initially.
- Show reassignment/failover progress and clearly distinguish device connectivity, regional maintenance, capacity rejection, and service outage.
- Preserve the last valid assignment for a bounded offline-control-plane window and indicate when assignment information is cached.

#### Support And Audit Visibility

- Provide a user-visible event history for sign-in, recovery, device enrollment/revocation/rotation, preference changes, and region reassignments.
- Provide a support reference/correlation ID for failed enrollment, assignment, and device actions.
- Allow the user to export or copy a bounded non-secret diagnostic summary for support.
- Do not expose raw IP inventories, server credentials, other users, internal operator notes, or high-cardinality infrastructure telemetry.

### Operator Portal

#### Roles And Access

- `Viewer`: read-only dashboards, inventory, health, capacity, and audit records; no user/device/server mutations.
- `Operator`: approved operational workflows such as user/device support lookup, device revocation, server drain/maintenance, and incident annotations; no operator-role or signing-key administration.
- `Administrator`: server enrollment/retirement, address pools, assignment policy, operator access, signing/credential lifecycle, and other sensitive configuration.
- Enforce authorization in APIs/application services as well as UI visibility; hiding a button is never the security boundary.
- Require stronger authentication and shorter sessions for operator areas, with recent-authentication checks for sensitive actions.

#### User And Device Operations

- Search users by exact email/account ID and devices by device ID/name/account, subject to role and audit policy.
- View verification, account/session security state, owned devices, assignments, last seen, software compatibility, and relevant audit events.
- Resend verification or initiate a supported recovery flow without setting or viewing the user's password.
- Revoke account sessions, suspend/reactivate an account, revoke a device, force device re-enrollment, or initiate credential rotation where the role permits.
- Require explicit scope, reason, and impact confirmation for suspension, revocation, re-enrollment, or disconnect operations.
- Do not allow an operator to retrieve reusable credentials after issuance.

#### Region And Server Inventory

- List regions, locations, servers, public UDP endpoints, protocol versions, address pools, assignment eligibility, lifecycle state, and last health report.
- Enroll a server using a short-lived bootstrap credential, then exchange it for a unique server identity credential.
- Verify endpoint ownership/reachability and compatible protocol/configuration before making a server assignment-eligible.
- Display active/maximum clients, assignment headroom, CPU, memory, queues, packet/socket errors, address-pool use, and recent health trends.
- Maintain lifecycle states such as `Provisioning`, `Active`, `Draining`, `Maintenance`, `Disabled`, and `Retired` with valid guarded transitions.

#### Capacity, Maintenance, And Drain Workflows

- Set reviewed capacity limits and reserve thresholds; health reporting may reduce eligibility but cannot silently raise operator-approved maximums.
- Enter maintenance with reason, start/end window, and expected user impact.
- Drain a server by stopping new assignments, showing remaining sessions, and enforcing an explicit deadline/action for sessions that do not leave naturally.
- Cancel a drain safely before retirement where state permits.
- Disable or retire a server only after assignments, address leases, credential state, and rollback impact are reviewed.
- Surface regional aggregate capacity and prevent assignment policies from concentrating clients on an unhealthy or nearly full server.

#### Assignment Policy And Credentials

- Configure which servers/regions are eligible for automatic assignment and the minimum health/capacity reserve.
- View assignment decisions and reasons without allowing arbitrary per-user packet routing edits.
- Rotate/revoke server identity and control-plane signing material through guarded workflows with overlap/rollout status where required.
- Rotate or revoke a device credential without exposing its raw value.
- Keep address-pool, signing-key, and server-credential changes administrator-only.

#### Operations, Dashboards, Alerts, And Audit

- Dashboard regional/server availability, assignment failures, active clients, capacity/headroom, authentication abuse, queue pressure, resource use, and control-plane/portal health.
- Show actionable alerts with severity, source, first/last occurrence, affected region/server/client count, acknowledgement, and operator notes.
- Link alerts to relevant inventory and audit events without exposing secrets.
- Record every operator mutation with actor, role, target, before/after summary, reason, correlation ID, time, and outcome.
- Distinguish read-only inspection, routine reversible actions, sensitive actions, and destructive/irreversible actions visually and through policy.
- Require typed or equivalent deliberate confirmation plus recent authentication for destructive actions; bulk operations require preview and affected-object count.

### Platform, Security, And Operations

#### Blazor Server Session And UI Behavior

- Use secure HTTP-only, `Secure`, appropriate `SameSite` cookie authentication and anti-forgery protection.
- Persist ASP.NET Core Data Protection keys outside the process.
- Treat circuits as disposable presentation state. Durable commands execute through application services and persist status independently of the circuit.
- Use idempotency keys, optimistic concurrency, cancellation, and clear pending/success/failure states so reconnects and double-clicks cannot repeat sensitive operations.
- Apply bounded circuit/session limits and instrument circuit counts, reconnects, exceptions, and resource use.

#### API And Domain Boundaries

- **Identity domain:** users, verification, credentials, recovery, account sessions, operator identities, and roles.
- **Device domain:** account ownership, enrollment tokens, device identity, device credentials, versions, state, and revocation.
- **Region/server domain:** regions, locations, servers, endpoints, lifecycle, health, capacity, and address pools.
- **Assignment domain:** preferences, eligibility, leases, assignments, cached authorization, failover reason, and assignment history.
- **Operations domain:** maintenance, drains, incidents, alerts, guarded commands, and audit events.
- Blazor components call typed application services/API clients; they do not access database tables, Rust sockets, TUN interfaces, systemd, or server files directly.
- Data-plane servers receive narrowly scoped signed device authorization and assignments; they never receive user passwords, browser sessions, or portal role claims.
- Use explicit versioned contracts between portal/control plane, client, and data servers so each deployable can roll forward or back within the declared compatibility matrix.

#### Security And Operational Safeguards

- Rate-limit registration, verification, login, recovery, enrollment, lookup, assignment, and mutation APIs.
- Prevent account/device enumeration through user-facing responses.
- Encrypt protected secrets at rest; hash password verifiers and non-retrievable tokens using maintained platform primitives.
- Separate signing keys, server credentials, device credentials, and browser sessions by purpose and scope.
- Require audit reasons and recent authentication for privileged mutations.
- Back up identity, device ownership, assignments, server inventory, address leases, and audit records; test restore before pilot migration.
- Keep portal/control-plane health independent of regional data-plane health so portal overload cannot directly block tunnel packet forwarding.

#### Data-Retention Boundaries

- Store account and device ownership records while active and through the approved deletion/recovery window.
- Store verification, recovery, enrollment, and assignment tokens only until use or short expiry; do not retain their raw values afterward.
- Retain security and operator audit records longer than high-cardinality operational telemetry according to an approved policy.
- Keep detailed server/path metrics for a bounded operational window and aggregate older data where needed.
- Do not store packet contents, browsing destinations, user traffic logs, passwords, raw reusable credentials, or unnecessary precise location history.
- Make exact retention periods, deletion delay, legal requirements, and user export/deletion behavior explicit before production launch.

### Later Portal Capabilities

The following are deferred until the initial account, device, and single-region flows meet their acceptance criteria:

- public self-service sign-up, passkeys, user MFA, organizations/teams, delegated administrators, SSO, and billing;
- richer per-device usage reporting, notification preferences, automated credential rotation, and support case workflows;
- multiple regions, candidate latency comparison, automatic failover, regional incident messaging, and controlled fleet migration;
- advanced operator automation, maintenance scheduling, bulk operations, custom alert routing, and horizontally scaled portal instances.

### Portal Acceptance Criteria

- A verified user can authenticate, recover access, manage sessions, enroll/name/view/revoke only owned devices, and see current region/assignment state.
- Enrollment and recovery tokens are short-lived and single-use; no reusable credential appears in UI, logs, audit records, or browser-accessible storage.
- Revoked users/devices/sessions lose access within documented propagation targets.
- Viewer, Operator, and Administrator tests cover every portal route and backing command/query; direct API calls cannot bypass UI restrictions.
- Server enrollment, health/capacity, maintenance, drain, disable, and retirement workflows enforce valid state transitions and protect active assignments.
- Sensitive/destructive operations require role, recent authentication, explicit reason/confirmation, idempotency, concurrency checking, and an auditable outcome.
- Blazor circuit loss/reconnect does not lose accepted work or execute a command twice.
- End-user pages expose no operator inventory, other-account data, raw credentials, or internal telemetry.
- Portal/control-plane failure does not terminate healthy authorized tunnels during the bounded cached-assignment window.
- Retention/deletion tests prove expired raw tokens and prohibited traffic data are not retained.

## Phased Milestones

### Phase 0: Protocol And Data Model Decisions

- Define IDs and relationships for users, accounts, devices, credentials, regions, locations, servers, assignments, and sessions.
- Version authentication and assignment messages so incompatible clients fail clearly.
- Define credential storage, signing-key rotation, and duplicate-device-session behavior.
- Reserve non-overlapping regional IPv4 pools and an initial Asia pool.
- Define control-plane availability expectations and cached-assignment behavior if it is temporarily unreachable.

**Acceptance criteria**

- Threat model and trust boundaries are reviewed.
- Protocol/data migrations and rollback compatibility are documented.
- No user password or control-plane master secret is required on a data server.

### Phase 1: Per-Device Identity And Multi-Client Data Plane

- Add stable `ClientId`/device identity to the authenticated handshake.
- Replace the single shared PSK with unique device credentials.
- Replace global server forwarding state with a bounded client-runtime map.
- Isolate peer paths, schedules, recovery, reorder, FEC, repair, queues, and counters per client.
- Make a new session for the same device replace only that device's older session.
- Add idle expiry and deterministic state cleanup.

**Acceptance criteria**

- Two device identities authenticate concurrently without evicting or affecting each other.
- Identical path IDs from different devices do not collide.
- Revoking or restarting one device affects only that device.
- Replay, spoofing, and cross-client isolation tests pass.

### Phase 2: Regional Address Pool, TUN Routing, And NAT

- Replace the `/30` pair with a configurable regional IPv4 pool.
- Assign and persist a unique address per device assignment.
- Route server-TUN return packets to the owning client runtime by destination address.
- NAT the regional pool and reject unassigned or spoofed tunnel source addresses.
- Record lease ownership, expiry, conflict, and cleanup events.

**Acceptance criteria**

- Multiple clients use the internet concurrently with correct bidirectional return routing.
- No client can send with or receive another client's tunnel address.
- Reconnects do not leave duplicate leases or stale NAT/conntrack behavior.

### Phase 3: Accounts, Login, And Device Enrollment

- Integrate the portal backend with the existing XFramework IdentityServer through the versioned IdentityServer contracts/integration packages and server-side Bolt client.
- Add the missing cohesive uLink registration operation to XFramework IdentityServer, including tenant/role enforcement, credential/contact creation, verification initiation, duplicate checks, and rollback on failure.
- Expose an HTTPS Blazor Server authentication boundary for registration, verification, login, logout, recovery, and account sessions; browser requests never call Bolt directly.
- Reuse XFramework's maintained password-verifier and account-session implementation; never store plaintext credentials or create a second uLink password database.
- Add email verification, rate-limited login/recovery, session revocation, and security-event logging.
- Use short-lived, single-use enrollment codes or tokens to bind a new uLink device to an authenticated account.
- Issue device credentials after enrollment and store them using OS-protected storage where available.
- Add account APIs distinct from data-plane server APIs.
- Integrate the Blazor Server portal with the control-plane identity boundary using secure cookie authentication, anti-forgery protection, session revocation, and policy-based authorization.
- Persist data-protection keys outside the process so portal restarts do not invalidate every valid session unexpectedly.
- Validate that the packaged SDK versions, Bolt protocol version, IdentityServer service version, and portal version are compatible before deployment.

**Acceptance criteria**

- A user can create and recover an account and revoke active account sessions.
- A user can enroll, name, reconnect, and revoke only their own devices.
- Enrollment tokens expire, are single-use, and reveal no reusable secret in logs or UI.
- A revoked device cannot obtain a new server assignment or authenticate to a data server.
- Portal authentication survives a normal application restart and revoked sessions stop working promptly.
- Registration is atomic and cannot assign an arbitrary tenant or privileged role supplied by a client.
- IdentityServer/Bolt unavailability fails new login or account mutations clearly without terminating already-authorized healthy tunnels.

### Phase 4: Server Registry And Single-Region Control Plane

- Register the current Vultr server as the first Asia location.
- Track region/location, endpoints, protocol version, status, maintenance mode, capacity, and last heartbeat.
- Have servers report signed health/capacity heartbeats to the control plane.
- Issue clients a short-lived server assignment for an eligible server.
- Cache the last valid assignment so existing clients can reconnect during a brief control-plane outage.
- Add drain behavior: no new assignments while existing sessions complete or reach a deadline.

**Acceptance criteria**

- The current server remains usable through the new assignment flow.
- Offline, incompatible, full, or maintenance servers receive no new assignments.
- A control-plane outage does not immediately disconnect an already authorized healthy tunnel.
- Assignment and drain decisions are auditable.

### Phase 5: Management Portal

- Implement the approved minimal capabilities from **Blazor Server Management Portal Capabilities** in a dedicated portal project and deployment unit over explicit control-plane application services/APIs.
- Preserve the existing Pi-hosted uLink dashboard unchanged while the new portal is developed and piloted.
- Separate end-user account/device/enrollment experiences from operator infrastructure/operations experiences through distinct route areas, layouts, navigation, policies, and API permissions.
- Start with roles: `Viewer`, `Operator`, and `Administrator`.
- Show region/server inventory, health, capacity, protocol version, maintenance/drain state, and recent incidents.
- Support guarded add, edit, drain, maintenance, disable, credential rotation, and retirement operations.
- Provide scoped device lookup/revocation for support without exposing secrets.
- Require stronger authentication for operators, short sessions, and audit logs for every mutation.
- Treat Blazor circuits as disposable UI sessions: reconnect or circuit loss must not lose an accepted operation, repeat a destructive command, or become the source of truth.
- Use cancellation, optimistic concurrency, idempotency keys for sensitive commands, and clear pending/success/failure states for long-running operations.

**Acceptance criteria**

- Role tests prevent viewers/operators from performing unauthorized actions.
- Destructive changes require confirmation and an audit reason.
- Server lifecycle changes cannot silently orphan active assignments.
- End users cannot access infrastructure inventory or operator APIs.
- Component and end-to-end tests prove route, UI, service, and API authorization for every role and ownership boundary.
- Circuit reconnect and duplicate-submit tests prove destructive operations are not executed twice.

### Phase 6: Multi-Region Selection And Failure Handling

- Add additional locations only after the Asia flow is stable.
- Let clients measure candidate endpoints using lightweight probes without establishing full tunnels to every server.
- Default selection filters by authorization, protocol compatibility, health, maintenance, and available capacity, then prefers low measured latency.
- Use hysteresis and a minimum hold period to prevent region flapping.
- Allow a user to prefer an available region; the service may override it when unavailable or full with a clear reason.
- On server/region failure, obtain a new assignment and perform a clean authenticated reconnect.
- Distinguish server failure, regional impairment, client-path impairment, capacity rejection, and control-plane outage.

**Acceptance criteria**

- A client normally selects a healthy low-latency server without oscillating.
- A failed or drained server receives no new sessions and clients reconnect to an eligible alternative.
- Failure of one region does not corrupt sessions or addressing in another.
- Manual region preference behaves predictably and never bypasses eligibility/security checks.

### Phase 7: Fairness, Capacity, And Observability

- Add per-client limits for paths, queued packets/bytes, reorder/repair memory, control traffic, and idle lifetime.
- Add server-wide caps for active clients, buffered memory, file descriptors, and assignment headroom.
- Preserve control and health traffic under pressure; shed optional duplicate/FEC work before primary traffic.
- Publish aggregate regional/server metrics and scoped per-client metrics.
- Alert on capacity, assignment failures, authentication abuse, queue pressure, memory, CPU, socket errors, and regional health.
- Define telemetry retention and cardinality limits.
- Instrument Blazor Server circuit counts, reconnects, exceptions, authentication failures, request latency, SignalR transport health, and per-instance resource use separately from tunnel/data-plane metrics.

**Acceptance criteria**

- An overloaded client cannot materially degrade another healthy client.
- Servers stop accepting assignments before unsafe saturation.
- Memory, queues, and file descriptors remain bounded during churn.
- Operators can distinguish user/device, server, region, and client-path faults without exposing secrets.
- Portal UI load or circuit churn cannot exhaust the data plane because the portal and Rust servers are independently deployed and capacity-limited.

### Phase 8: Migration And Regional Rollout

- Back up the current server/client binaries, unit files, NAT rules, `/30` configuration, and shared-secret configuration.
- Migrate the existing Pi to an account/device identity and the first Asia assignment.
- Remove the shared PSK only after every supported client is migrated or explicitly retired.
- Add regions one at a time: Asia pilot, second Asia/US pilot, then Europe or other demand-driven locations.
- Expand enrollment in controlled batches with health and capacity gates.

**Acceptance criteria**

- The existing Pi migrates without losing path configuration or user settings.
- Legacy clients receive an explicit upgrade-required response rather than undefined behavior.
- Regional rollout can pause without disrupting healthy existing assignments.
- Shared credentials and legacy compatibility are removed by a documented deadline.

## Staged Validation

1. **Two-client isolation:** concurrent ping/browsing; verify no session eviction or cross-client traffic.
2. **Two-client load/failure:** simultaneous bidirectional throughput; restart and impair one client while the other remains stable.
3. **Account/enrollment security:** registration, recovery, token expiry/reuse, ownership checks, revocation, and session invalidation.
4. **Single-region pilot:** current Asia server through registry/assignment, including maintenance and drain behavior.
5. **Small fleet:** 5-10 clients with mixed idle, real-time, bulk, reconnect, and path-churn workloads.
6. **Two-region test:** latency selection, manual preference, capacity rejection, drain, region outage, and clean reassignment.
7. **Capacity test:** increase synthetic clients on `xeon-dev` until agreed CPU, memory, latency, loss, queue, or socket thresholds are reached.
8. **Controlled production expansion:** add real devices and regions in batches with rollback checkpoints.

Portal validation includes component tests, service/API integration tests, role/ownership authorization tests, browser end-to-end tests, anti-forgery/session-revocation tests, circuit reconnect tests, accessibility checks, and a bounded concurrent-circuit/load test.

Functional and security isolation must pass before any capacity or scale claim is made.

## Security And Tenancy Requirements

- Authenticate users only through the control plane over HTTPS.
- Use unique revocable device credentials; never distribute one server-wide client secret.
- Authenticate device identity before allocating substantial data-plane state.
- Prevent source-address spoofing and cross-client data/control messages.
- Rate-limit registration, login, recovery, enrollment, assignment, and unauthenticated UDP traffic.
- Separate end-user, support, operator, and administrator authorization.
- Require stronger authentication for privileged portal roles.
- Encrypt protected secrets at rest and support signing/device-key rotation.
- Exclude passwords, tokens, keys, and packet contents from logs, metrics, and status JSON.
- Record account, device, assignment, server lifecycle, credential, and operator mutations in audit logs.

## Compatibility, Rollout, And Rollback

### Compatibility

- Version protocol negotiation and reject unsupported versions explicitly.
- Prefer a coordinated paired client/server cutover for the first device.
- If a legacy window is required, isolate legacy single-client behavior rather than mixing it with the multi-client runtime map.

### Rollout

- Validate on `xeon-dev` before touching production.
- Deploy the control plane and portal without changing production routing.
- Upgrade the first Asia data server and existing Pi together during a controlled window.
- Verify the migrated single-device state before enrolling a second device.
- Add devices and regions gradually using capacity and health gates.

### Rollback

- Preserve the current single-client binary/configuration and database/config backups.
- Stop new enrollments and assignments before rollback.
- Drain or disconnect pilot clients explicitly.
- Restore the previous paired server/client binaries, `/30` routes, NAT, and shared credential only for the original Pi.
- Verify tunnel routing and internet access before declaring rollback complete.
- Roll back one region independently where possible; control-plane schema changes must have tested backward migrations or forward-fix procedures.

## Implementation Governance

- Develop all multi-client, multi-region, account/authentication, and management-portal work in a dedicated Git branch and separate worktree created for this program.
- Do not create that branch or worktree until implementation is explicitly approved. This document is plan-only.
- Keep the current single-client production branch, binaries, service definitions, configuration, routes, NAT rules, and deployed versions stable and unchanged during development.
- Do not use the production Pi or Vultr data server as an iterative development environment. Use local tests and isolated `xeon-dev` services first, followed by an isolated pilot server/client environment.
- Develop the Blazor Server portal in that same dedicated program branch/worktree as a separate application/deployment unit. Do not add experimental portal routes or identity changes to the deployed Pi app during development.
- Treat client/server protocol changes as one coordinated versioned change:
  - define the supported protocol-version matrix before implementation;
  - build matching client and server artifacts from the same reviewed commit;
  - reject incompatible peers explicitly instead of attempting undefined fallback;
  - never deploy only one side of a required paired protocol change.
- Keep control-plane/API and database changes versioned with forward and rollback migration notes. Data-plane servers must continue using their last valid cached authorization/assignment during a bounded control-plane interruption.
- Require focused review for authentication, credential storage, authorization, address isolation, routing/NAT, and cross-client resource isolation before integration.
- Require Blazor-specific review for cookie/session security, anti-forgery, circuit authorization, data-protection key storage, duplicate command handling, operator safeguards, and end-user/operator boundary enforcement.
- Merge only after the milestone's tests and acceptance criteria are met, the two-client isolation test passes, migration/rollback has been rehearsed, and the user explicitly approves promotion.
- Use a release candidate tag or immutable build identifier for every paired pilot deployment so binaries, protocol version, schema version, and test evidence can be traced together.
- If a pilot fails, stop enrollment, restore the previously validated paired client/server artifacts and network configuration, and leave the current single-client production release as the fallback until the defect is corrected and revalidated on the development branch.

**Governance acceptance criteria**

- No multi-client program commit is made directly on the current production branch.
- Production remains on its last validated single-client release throughout development and isolated testing.
- Every protocol-affecting release has matching client/server artifacts, a compatibility declaration, review evidence, and a tested rollback procedure.
- Portal releases are independently versioned and deployable, and a portal rollback does not require rolling back a healthy data-plane release unless an explicitly versioned API contract requires it.
- Promotion to production occurs only through an approved merge/release after all agreed functional, security, isolation, migration, and rollback gates pass.

## Prerequisites

- A reliable HTTPS hostname/certificate and persistent database for the control plane.
- An independent Blazor Server hosting target behind TLS/reverse proxy, with health checks, structured logs, and deployment separate from the Pi and Rust data servers.
- Persistent ASP.NET Core Data Protection keys and protected configuration/secrets for portal authentication.
- For initial scale, one portal instance is acceptable. Before horizontal scale, add shared data-protection keys, distributed cache/backplane where required, compatible SignalR routing/stickiness, and load tests proving circuit behavior across instances.
- Transactional storage for users, devices, credentials, assignments, regions, servers, address leases, and audit records.
- Email delivery for verification and recovery, unless initial enrollment is intentionally operator-only.
- Protected storage and rotation procedures for control-plane signing keys and device credential material.
- Non-overlapping tunnel address pools for every planned region.
- Health/capacity definitions and initial safe limits for the current Vultr size.
- Backups and a tested restore path before account or credential migration.
- An approved private NuGet feed containing aligned, current builds of `IdentityServer.Domain.Shared`, `IdentityServer.Integration`, `XFramework.Integration`, and their Bolt/domain dependencies; do not consume stale checked-in `1.0.0` packages in production.
- A seeded uLink tenant, end-user role, and operator roles in XFramework IdentityServer, plus a reviewed atomic uLink registration operation.
- Protected Bolt service credentials and network reachability from the portal backend to the XFramework Bolt hub; no browser or Rust data server receives these credentials.

## Open Questions For Review

1. Confirm the proposed invite-only, email-verified initial registration; public self-service sign-up remains deferred.
2. Is email/password sufficient for the first release, with passkeys/MFA later, or is MFA required immediately?
3. Should device enrollment use a code displayed by the router, a QR flow, or an operator-generated file for the first release?
4. When the same device ID reconnects, should it replace its older session immediately or only after proving the old session is stale?
5. Should tunnel addresses remain stable per device within a region?
6. What initial Asia address-pool size and supported concurrent-client target should be used?
7. Should users select only a region (`Asia`) or a specific location/server (`Singapore 1`)? Recommendation: region only.
8. Should automatic selection optimize latency only after applying a minimum capacity reserve? Recommendation: yes.
9. How long may a client use a cached server assignment while the control plane is unreachable?
10. Are user-facing bandwidth plans/limits required, or only technical fairness limits initially?
11. Should support operators be allowed to revoke devices, or only administrators?
12. Which region follows Asia first, based on actual users and measured demand?
13. Should the first Blazor Server portal and control-plane API run in one deployable process for simplicity, or as separate processes? Recommendation: one deployable application with strict internal service/API boundaries initially.
14. Where will the versioned XFramework identity/Bolt packages be published for the isolated uLink worktree and CI? Recommendation: an authenticated private NuGet feed with immutable versions.
15. Should uLink extend the current XFramework IdentityServer session model for portal session validation immediately, or first use the proven ControlPanel cookie pattern plus validation on sensitive actions? Recommendation: add bounded periodic validation before the external pilot so central revocation has a clear propagation target.

## Revised Definition Of Done

- Multiple real client devices use independent authenticated transport associations through one server UDP listening socket/port concurrently, without session eviction, shared addresses/forwarding state, or cross-client leakage.
- Users can securely register, authenticate, recover accounts, and manage only their own devices and sessions.
- Devices enroll with unique credentials, receive unique tunnel addresses, and can be revoked independently.
- Operators can safely register, drain, maintain, observe, and retire regional servers through audited role-based controls.
- A separately deployed Blazor Server portal provides authorized end-user and operator experiences without coupling UI circuits to tunnel runtime state or changing the existing router dashboard during development.
- Clients can select or be assigned to healthy, compatible, sufficiently provisioned regions and fail over by clean reconnect.
- One bad client, server, or region does not disrupt unrelated healthy clients beyond documented capacity limits.
- Security, migration, rollback, impairment, churn, multi-client, multi-region, and capacity tests pass.
- Measured server and regional capacity with safe operating headroom is documented; no unsupported scale claim is made.
