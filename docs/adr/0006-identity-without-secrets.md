# 0006. Identity instead of secrets everywhere

**Status:** Accepted

## Context
A portfolio repository is public in spirit even when private, and a leaked key is the most common cloud incident. The goal
was a system where there is nothing in the repository, or in a pod's environment, that grants access to Azure.

## Decision
- **Service Bus has shared access keys disabled** (`local_auth_enabled = false`). Services authenticate as managed
  identities and hold only the *Sender* / *Receiver* roles they need. Locally, the emulator's well-known development
  connection string is used.
- **Pods use AKS Workload Identity.** Each service has its own user-assigned identity, federated with its Kubernetes
  service account. There is no secret to rotate.
- **Runtime secrets live in Key Vault** and are mounted by the CSI driver (database connection strings, the webhook
  signing secret, the Application Insights connection string). They are read-only to the workloads.
- **CI signs in to Azure with OIDC federation**, trusting tokens from this repository's `main` branch only. No client secret
  is stored in GitHub, only three identifiers.
- Containers run as non-root on a chiseled image with a read-only filesystem; the namespace enforces the `restricted` pod
  security standard.

## Consequences
- No secret appears in the repository, the pipeline configuration or a pod's environment variables.
- **The `sub` claim surprised me:** this repository presents the OIDC subject with immutable ids
  (`repo:OWNER@ownerId/NAME@repoId:ref:...`), so trusting the plain `repo:OWNER/NAME` form failed with `AADSTS700213`. The
  setup script now asks GitHub which prefix it will present.
- The CI identity has broad rights (Contributor and User Access Administrator on the subscription), because Terraform creates
  resources and role assignments. Acceptable for a disposable personal subscription, not for production, where plan and
  apply would use separate identities.
- PostgreSQL still uses password authentication, reachable through the "allow Azure services" firewall rule. A private
  endpoint with Entra authentication is the production answer.
