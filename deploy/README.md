# Deploying PayFlow to Azure

Infrastructure is Terraform (`infra/terraform`), workloads are Kubernetes manifests (`deploy/kubernetes`) built from
one Dockerfile (`deploy/docker/Dockerfile`). Nothing in this repository contains a secret: the identity running
Terraform is your Azure login, pods authenticate with workload identity, and every runtime secret lives in Key Vault.

## What Terraform creates

| Resource | Notes |
|---|---|
| Service Bus (Standard) | queue `payment-commands`, topic `domain-events`, four subscriptions with SQL filters, DLQ after 5 deliveries, duplicate detection. **Entra ID only** (shared keys disabled) |
| PostgreSQL Flexible Server | `B1ms`, databases `orders` and `payments`, random admin password |
| Key Vault (RBAC) | connection strings, webhook secret, Application Insights connection string |
| Container Registry (Basic) | admin user disabled |
| Application Insights + Log Analytics | workspace-based, **0.5 GB/day ingestion cap** |
| User-assigned identities | one per workload, least-privilege roles (Service Bus sender/receiver, Key Vault reader) |
| Function App (Consumption) | system identity, receives from Service Bus without a connection string |
| AKS (optional, `deploy_aks`) | Free tier, one node, OIDC issuer + workload identity, Key Vault CSI driver |

## One-time setup

```powershell
az login
./infra/scripts/bootstrap-state.ps1                    # state storage account + infra/terraform/backend.hcl
cd infra/terraform
cp environments/dev.tfvars.example dev.tfvars          # put your subscription id in it
terraform init -backend-config=backend.hcl
```

## Working session (and what it costs)

```powershell
terraform apply -var-file=dev.tfvars -var deploy_aks=true     # AKS is ~30 USD/month, so only for a session
# ... build, deploy, test, take screenshots ...
terraform apply -var-file=dev.tfvars -var deploy_aks=false    # removes the cluster, keeps everything else
terraform destroy -var-file=dev.tfvars                        # removes everything
```

Without AKS the monthly cost is mostly PostgreSQL (free for 12 months on a new account) and the registry (~5 USD).
Set a budget with alerts before the first `apply`.

## Deploy the services

```powershell
$env:ACR = terraform output -raw acr_name
az acr login --name $env:ACR
$login = terraform output -raw acr_login_server
$tag = git rev-parse --short HEAD
foreach ($svc in 'order-service','payment-service','provider-simulator') {
  docker build -f ../../deploy/docker/Dockerfile --target $svc -t "$login/payflow/${svc}:$tag" ../..
  docker push "$login/payflow/${svc}:$tag"
}

az aks get-credentials --resource-group (terraform output -raw resource_group_name) --name (terraform output -raw aks_name)
$ids = terraform output -json workload_client_ids | ConvertFrom-Json
$env:ORDER_SERVICE_CLIENT_ID = $ids.'order-service'
$env:PAYMENT_SERVICE_CLIENT_ID = $ids.'payment-service'
$env:PROVIDER_SIMULATOR_CLIENT_ID = $ids.'provider-simulator'
$env:KEY_VAULT_NAME = terraform output -raw key_vault_name
$env:TENANT_ID = terraform output -raw tenant_id
$env:ACR_LOGIN_SERVER = $login
$env:IMAGE_TAG = $tag
$env:SERVICE_BUS_FQDN = terraform output -raw service_bus_fqdn
kubectl kustomize ../../deploy/kubernetes | envsubst | kubectl apply -f -   # envsubst: Git Bash / WSL
```

Check it works without exposing anything publicly:

```powershell
kubectl -n payflow get pods
kubectl -n payflow port-forward svc/order-service 8080:80
curl -X POST localhost:8080/api/orders -H "Content-Type: application/json" -d '{"customerId":"<guid>","amount":10,"currency":"EUR","paymentToken":"tok_success"}'
```

## Things to verify after the first `apply`

These behave as documented but could not be exercised without an Azure subscription:

- **Subscription filters.** Terraform replaces each subscription's default accept-all rule through an ARM
  deployment. Confirm with
  `az servicebus topic subscription rule list -g <rg> --namespace-name <ns> --topic-name domain-events --subscription-name payment-service`:
  there must be exactly one rule, `$Default`, with a SQL filter and not `1=1`.
- **Function runtime.** The Function App uses `dotnet_version = "10.0"` on a Linux Consumption plan. If the region or
  provider version rejects that combination, switch to the Flex Consumption plan.
- **Region capacity.** `westeurope` is the default; pick another region if the PostgreSQL SKU or the AKS node size
  is unavailable for your subscription (`terraform apply` reports it immediately).

## Deliberate simplifications (and the production answer)

| Here | In production |
|---|---|
| PostgreSQL reachable through the "allow Azure services" firewall rule, password auth | private endpoint + Entra ID authentication |
| Services migrate their own database on startup (`Database__MigrateOnStartup`) | a separate migration job in the pipeline |
| One-node Free tier cluster, no ingress | multi-node pool across zones, ingress + TLS, network policies |
| Purge protection off on Key Vault | on |
