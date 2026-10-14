# ADR-0007: JWT authentication, dev tokens that cannot reach production, fail-fast configuration

**Status:** accepted

## Context
A reviewer must be able to call the API in seconds without an identity provider, but development shortcuts (local token minting, failure injection, free money via simulated funding) must be impossible to leave enabled in a real deployment.

## Decision
* **One scheme: JWT bearer.** In production it validates tokens from an external OpenID Connect provider (`Authentication:Jwt:Authority` + `Audience`). In Development/Testing an HMAC key from configuration validates tokens minted by `POST /api/dev/tokens`.
* Inbound claim mapping is disabled. `sub` identifies the caller, `role` carries `customer` or `ledger-operator`, and customer subjects must be GUIDs.
* **Secure by default:** the authorization fallback policy requires an authenticated user, so only health checks, OpenAPI (dev) and the dev-token endpoint opt out.
* **Segregation of duties:** customers move only their own money. Operators can inspect, reconcile, fund (dev) and reverse, but can never debit a customer wallet.
* **Startup validation** ([EnvironmentGuards.cs](../../src/LedgerCore.Api/Hosting/EnvironmentGuards.cs)) runs after the host is built and before traffic is served. It refuses to start when `DevAuthentication`, `FailureInjection`, `Features:SimulatedFunding` or demo seeding are enabled outside Development/Testing, or when no authority is configured. `SecurityConfigurationTests` cover every one of these cases.
* No secret is in code. The dev signing key and dev DB password live in `appsettings.Development.json` / compose defaults, clearly labelled as throwaway local values. Real values come from environment variables or a secret store.

## Consequences
* A misconfigured production deployment fails loudly at startup instead of running with a known signing key.
* Not tested against a real OIDC provider (documented limitation).
